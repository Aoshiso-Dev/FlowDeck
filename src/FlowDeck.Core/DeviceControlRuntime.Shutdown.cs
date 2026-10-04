namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private Task? shutdownWork, shutdownPreparation;
    private readonly HashSet<Task> configurationWork = [];
    private readonly HashSet<IDeviceControlDriver> shutdownClosedDrivers = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IDeviceDriverProvider> shutdownClosedProviders = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IDeviceControlDriver> shutdownReleaseAttempted = new(ReferenceEqualityComparer.Instance);

    /// <summary>All registered/retired handles reported successful disposal. Restoration results remain in State().</summary>
    public bool ResourcesClosed { get { lock (gate) return disposed; } }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            // Concurrent callers share one attempt, including its error. A later call retries unfinished closures.
            if (disposed || shutdownWork is { IsCompleted: false }) return new(shutdownWork ?? Task.CompletedTask);
            stopping = true;
            shutdownPreparation ??= Task.Run(PrepareShutdownAsync);
            return new(shutdownWork = Task.Run(FinishShutdownAsync));
        }
    }

    private static string? JoinLifecycleErrors(string? first, string? second) =>
        first is null ? second : second is null || second == first ? first : first + " / " + second;

    private static async Task CaptureShutdownTaskAsync(Func<Task> action, List<Exception> errors)
    {
        try { await action().ConfigureAwait(false); } catch (Exception e) { errors.Add(e); }
    }

    private async Task PrepareShutdownAsync()
    {
        var errors = new List<Exception>();
        try { lifetime.Cancel(); } catch (Exception e) { errors.Add(e); }
        Task finish; Task[] background;
        lock (gate)
        {
            finish = BeginReleaseCore(DeviceReleaseReason.ServiceStopping);
            background = [watchdog, lightingWatchdog, notificationWatchdog, .. polling.Values, .. coolingWork.Values];
        }
        // A fault is still a completed task. Observe every task before closing its transport.
        await CaptureShutdownTaskAsync(() => Task.WhenAll(background.Append(finish)), errors).ConfigureAwait(false);
        Task[] commands;
        lock (gate) commands = commandWork.Concat(configurationWork).ToArray();
        await CaptureShutdownTaskAsync(() => Task.WhenAll(commands), errors).ConfigureAwait(false);

        IDeviceControlDriver[] discoveredLate;
        lock (gate)
        {
            try { RefreshCore(); } catch (Exception e) { errors.Add(e); }
            // An in-flight provider poll may have registered another device after the first release snapshot.
            discoveredLate = drivers.Values.Where(d => !shutdownReleaseAttempted.Contains(d)).ToArray();
        }
        if (discoveredLate.Length > 0)
        {
            await CaptureShutdownTaskAsync(() => ReleaseDriversAsync(DeviceReleaseReason.ServiceStopping, discoveredLate), errors).ConfigureAwait(false);
        }
        if (errors.Count > 0) throw new AggregateException("終了前の処理待機に失敗しました", errors);
    }

    private async Task FinishShutdownAsync()
    {
        var errors = new List<Exception>();
        await CaptureShutdownTaskAsync(() => shutdownPreparation!, errors).ConfigureAwait(false);
        IDeviceControlDriver[] closingDrivers; IDeviceDriverProvider[] closingProviders;
        var driverErrors = new Dictionary<IDeviceControlDriver, Exception>(ReferenceEqualityComparer.Instance);
        lock (gate)
        {
            closingDrivers = drivers.Values.ToArray(); closingProviders = providers.ToArray();
            var inventory = registry.Snapshot;
            foreach (var driver in closingDrivers)
                if (inventory.Devices.SingleOrDefault(d => d.Device.Id == driver.DeviceId) is { } instance)
                    shutdownOrigins.TryAdd(driver, instance);
        }
        foreach (var driver in closingDrivers)
        {
            lock (gate) { if (shutdownClosedDrivers.Contains(driver)) continue; }
            try { await driver.DisposeAsync().ConfigureAwait(false); lock (gate) shutdownClosedDrivers.Add(driver); }
            catch (Exception e) { driverErrors[driver] = new IOException(driver.DeviceId + ": 機器の接続を閉じられません", e); }
        }
        foreach (var provider in closingProviders)
        {
            IDeviceControlDriver[] unfinished;
            lock (gate)
            {
                if (shutdownClosedProviders.Contains(provider)) continue;
                unfinished = closingDrivers.Where(d => driverProviders.GetValueOrDefault(d.DeviceId) == provider.ProviderId && !shutdownClosedDrivers.Contains(d)).ToArray();
            }
            try
            {
                // Ordinary providers retain the transport for child recovery. A compiled coordinator
                // can instead finish through its independently checked native path.
                if (unfinished.Length > 0 && (provider is not IDeviceProviderShutdownCoordinator coordinator ||
                    !coordinator.CanCompleteDriverShutdown(unfinished))) continue;
                await provider.DisposeAsync().ConfigureAwait(false);
                lock (gate) shutdownClosedProviders.Add(provider);
                foreach (var driver in unfinished)
                {
                    try
                    {
                        await driver.DisposeAsync().ConfigureAwait(false);
                        lock (gate) shutdownClosedDrivers.Add(driver);
                        driverErrors.Remove(driver);
                    }
                    catch (Exception e) { driverErrors[driver] = new IOException(driver.DeviceId + ": 機器の接続を閉じられません", e); }
                }
            }
            catch (Exception e) { errors.Add(new IOException(provider.ProviderId + ": 取得経路を閉じられません", e)); }
        }
        errors.AddRange(driverErrors.Values);
        await CaptureShutdownTaskAsync(CloseRetiredProvidersAsync, errors).ConfigureAwait(false);
        lock (gate)
        {
            try { RefreshCore(); } catch (Exception e) { errors.Add(e); }
            foreach (var provider in closingProviders.OfType<IDeviceProviderShutdownCoordinator>())
            {
                if (!shutdownClosedProviders.Contains(provider) || shutdownRecoveryReported.Contains(provider)) continue;
                if (closingDrivers.Any(d => driverProviders.GetValueOrDefault(d.DeviceId) == provider.ProviderId && !shutdownClosedDrivers.Contains(d))) continue;
                try { ApplyShutdownRecoveriesCore(provider); }
                catch (Exception e) { errors.Add(new IOException(provider.ProviderId + ": 最終復旧結果を確認できません", e)); }
            }
            disposed = closingDrivers.All(shutdownClosedDrivers.Contains) && closingProviders.All(shutdownClosedProviders.Contains) &&
                retiredDrivers.Count == 0 && retiredProviders.Count == 0 &&
                closingProviders.OfType<IDeviceProviderShutdownCoordinator>().All(shutdownRecoveryReported.Contains);
            if (!disposed && errors.Count == 0) errors.Add(new IOException("未完了の機器終了処理があります"));
            lifecycleError = JoinLifecycleErrors(ShutdownReleaseErrorCore(), errors.Count == 0 ? null :
                "機器の終了処理が未完了です: " + string.Join(" / ", errors.Select(e => e.Message)));
        }
        if (disposed) lifetime.Dispose();
        if (errors.Count > 0) throw new AggregateException("機器の終了処理に失敗しました", errors);
    }
}
