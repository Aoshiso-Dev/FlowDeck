namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private bool providerChangesAllowed;
    private Task<IDeviceControlDriver[]>? providerReplacementWork;
    private CancellationTokenSource? providerReplacementLifetime;
    private readonly HashSet<IDeviceControlDriver> retiredDrivers = [];
    private readonly HashSet<IDeviceDriverProvider> retiredProviders = [];
    private readonly HashSet<string> closedRegistryDevices = new(StringComparer.Ordinal);
    private string? providerTransitionError;

    // Read only the runtime's registered ownership map. Display/provider labels on observations
    // are not proof of which handles will close. No factory, polling, or release occurs here.
    public DeviceProviderReplacementScope InspectProviderReplacement(string[] remove, string[] add)
    {
        if (!DeviceProviderRouteReview.Ids(remove, 8) || !DeviceProviderRouteReview.Ids(add, 8))
            throw new IOException("取得経路の切替計画が不正です");
        lock (gate)
        {
            if (providerTransitionError is not null) throw new IOException(providerTransitionError);
            var current = providers.Select(p => p.ProviderId).ToHashSet(StringComparer.Ordinal);
            if (remove.Any(id => !current.Contains(id))) throw new IOException("終了する取得経路が現在の登録と一致しません");
            var retained = current.Except(remove).Order(StringComparer.Ordinal).ToArray();
            if (add.Intersect(retained).Any() || retained.Length + add.Length > 8)
                throw new IOException("新しい取得経路が保持する経路と重複するか、上限を超えます");
            return new(registry.Snapshot.TopologyRevision,
                driverProviders.Where(p => remove.Contains(p.Value)).Select(p => p.Key).Order(StringComparer.Ordinal).ToArray(), retained);
        }
    }

    // This is a compiled configuration callback API, not a wire command or an assembly loader.
    // The caller must persist route intent/selection and validate which providers share hardware.
    // ReconfigureAsync has already drained commands/polls, ended effects, and verified release.
    public Task<IDeviceControlDriver[]> ReplaceProvidersAsync(string owner, string[] remove,
        Func<IDeviceDriverProvider[]> create, CancellationToken ct, string[]? expectedProviders = null)
    {
        ArgumentNullException.ThrowIfNull(create);
        if (remove is not { Length: <= 8 } || remove.Any(id => !DeviceConfigurationRules.Key(id)) || remove.Distinct().Count() != remove.Length)
            throw new ArgumentException("切り替える取得経路が不正です");
        remove = remove.ToArray();
        if (expectedProviders is not null && (expectedProviders.Length > 8 || expectedProviders.Any(id => !DeviceConfigurationRules.Key(id)) ||
            expectedProviders.Distinct().Count() != expectedProviders.Length)) throw new ArgumentException("新経路の予定一覧が不正です");
        expectedProviders = expectedProviders?.ToArray();
        lock (gate)
        {
            CheckProviderChange(owner, ct);
            CheckIsolatedReplacement(remove, expectedProviders);
            if (providerReplacementWork is { IsCompleted: false }) throw new IOException("取得経路の切替中です");
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, providerReplacementLifetime!.Token);
            return providerReplacementWork = Task.Run(async () =>
            {
                try { return await ReplaceProvidersCoreAsync(owner, remove, create, cancellation.Token, expectedProviders).ConfigureAwait(false); }
                finally { cancellation.Dispose(); }
            }, CancellationToken.None);
        }
    }
    private void CheckProviderChange(string owner, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            ExpireCore(); ct.ThrowIfCancellationRequested();
            if (providerTransitionError is not null) throw new IOException(providerTransitionError);
            if (!configurationChanging || !providerChangesAllowed || stopping || releasing || session != owner ||
                states.Values.Any(s => s.Owned && ConfigurationBlocksDevice(s.Target.DeviceId)) ||
                polling.Any(p => !p.Value.IsCompleted && ConfigurationBlocksPoll(p.Key)) ||
                coolingWork.Any(p => !p.Value.IsCompleted && ConfigurationBlocksDevice(p.Key)))
                throw new IOException("制御と取得の終了を確認した設定反映中にだけ、経路を切り替えられます");
        }
    }
    private static bool ValidProvider(IDeviceDriverProvider? p) => p is not null && DeviceConfigurationRules.Key(p.ProviderId) &&
        p.ResourceDomains is { Length: > 0 and <= 16 } && p.ResourceDomains.All(d => DeviceConfigurationRules.Text(d, 256)) &&
        p.ResourceDomains.Distinct().Count() == p.ResourceDomains.Length;

    private async Task<IDeviceControlDriver[]> ReplaceProvidersCoreAsync(string owner, string[] remove,
        Func<IDeviceDriverProvider[]> create, CancellationToken ct, string[]? expectedProviders)
    {
        // A second replacement may be a rollback after the first route failed startup recovery.
        // Its pending obligations must be released before its handles can be closed as well.
        lock (gate) { CheckProviderChange(owner, ct); RefreshCore(); }
        IDeviceControlDriver[]? selected;
        lock (gate) { CheckIsolatedReplacement(remove, expectedProviders); selected = ConfigurationReleaseDrivers(); }
        await ReleaseDriversAsync(DeviceReleaseReason.ConfigurationChanged, selected).ConfigureAwait(false);
        IDeviceDriverProvider[] retained;
        string[] retiredIds;
        lock (gate)
        {
            CheckProviderChange(owner, ct); RefreshCore();
            if (lifecycleError is not null) throw new IOException(lifecycleError);
            var retiring = providers.Where(p => remove.Contains(p.ProviderId)).ToArray();
            foreach (var provider in retiring)
            {
                if (provider.CachedDrivers.Any(d => driverProviders.GetValueOrDefault(d.DeviceId) != provider.ProviderId ||
                    !drivers.TryGetValue(d.DeviceId, out var known) || !ReferenceEquals(d, known)))
                    throw new IOException("旧経路の全機器の登録・解除を確認できません");
            }
            retiredProviders.UnionWith(retiring);
            retiredIds = driverProviders.Where(p => remove.Contains(p.Value)).Select(p => p.Key).ToArray();
            foreach (var id in retiredIds)
            {
                retiredDrivers.Add(drivers[id]); drivers.Remove(id); driverProviders.Remove(id);
                polling.Remove("driver/" + id); pollingErrors.Remove(id); observationErrors.Remove(id);
            }
            retained = providers.Where(p => !remove.Contains(p.ProviderId)).ToArray(); providers = retained;
            foreach (var id in remove)
            { polling.Remove("provider/" + id); pollingErrors.Remove("provider/" + id); observationErrors.Remove("provider/" + id); }
            // Reconcile a real disconnected generation even if the next provider reuses the same IDs.
            RefreshCore();
        }
        try { await CloseRetiredProvidersAsync().ConfigureAwait(false); }
        catch (Exception e)
        {
            lock (gate) providerTransitionError = "旧経路のハンドル解放を確認できません。サービスの再起動が必要です: " + e.Message;
            throw new IOException(providerTransitionError, e);
        }
        lock (gate)
        {
            CheckProviderChange(owner, ct);
            // Release and both child/parent disposal have completed. Only these historical
            // runtime records may later give up a slot; stored names/settings are independent.
            var remembered = registry.Snapshot.Devices.Select(d => d.Device.Id).ToHashSet(StringComparer.Ordinal);
            closedRegistryDevices.UnionWith(retiredIds.Where(remembered.Contains));
        }
        // No new factory, enumeration, or SDK connection runs before old disposal has completed.
        var additions = create();
        try
        {
            CheckProviderChange(owner, ct);
            if (additions is null || additions.Length + retained.Length > 8 || additions.Any(p => !ValidProvider(p)) ||
                additions.Concat(retained).DistinctBy(p => p.ProviderId).Count() != additions.Length + retained.Length ||
                expectedProviders is not null && !additions.Select(p => p.ProviderId).ToHashSet().SetEquals(expectedProviders))
                throw new IOException("新しい取得経路の登録が不正または重複しています");
            CheckIsolatedAdditions(additions);
            lock (gate)
            {
                var cached = additions.SelectMany(p => p.CachedDrivers ?? throw new IOException("取得経路の機器一覧がありません")).ToArray();
                if (cached.Length > DeviceRegistry.MaximumDevices || cached.Any(d => d is null || !DeviceConfigurationRules.Key(d.DeviceId)) ||
                    cached.DistinctBy(d => d.DeviceId).Count() != cached.Length || cached.Any(d => drivers.ContainsKey(d.DeviceId)) ||
                    cached.Select(d => d.DeviceId).Union(drivers.Keys).Count() > DeviceRegistry.MaximumDevices)
                    throw new IOException("新しい経路の機器登録が重複または上限を超えています");
                EnsureRegistryCapacityCore(cached.Select(d => d.DeviceId));
            }
        }
        catch
        {
            lock (gate)
                foreach (var provider in additions ?? [])
                    if (provider is not null && !retained.Contains(provider))
                    {
                        retiredProviders.Add(provider);
                        try { retiredDrivers.UnionWith(provider.CachedDrivers.Where(d => d is not null && !drivers.Values.Contains(d))); }
                        catch { /* Provider disposal must still be attempted if its cache is malformed. */ }
                    }
            try { await CloseRetiredProvidersAsync().ConfigureAwait(false); }
            catch (Exception e) { lock (gate) providerTransitionError = "新しい取得経路の解放を確認できません: " + e.Message; }
            throw;
        }
        lock (gate) { CheckProviderChange(owner, ct); providers = [.. retained, .. additions]; }
        var addedIds = additions.Select(p => p.ProviderId).ToHashSet();
        for (var round = 0; round < 2; round++)
        {
            CheckProviderChange(owner, ct);
            IDevicePollingDriver[] polls;
            lock (gate) polls = drivers.Values.OfType<IDevicePollingDriver>().Where(d => addedIds.Contains(driverProviders.GetValueOrDefault(d.DeviceId) ?? "")).ToArray();
            // Each task is awaited before leaving the replacement scope, including failures.
            var reads = additions.Select(p => scheduler.RunPreparedAsync(p.ResourceDomains, HardwarePriority.Monitoring, p,
                DevicePreparationKind.Polling, async token => { CheckProviderChange(owner, token); await p.PollAsync(token).ConfigureAwait(false); return true; }, ct,
                () => CheckProviderChange(owner, ct))).Concat(polls.Select(d =>
            {
                string[] domains; lock (gate) domains = registry.Snapshot.Devices.Single(i => i.Device.Id == d.DeviceId).Device.ResourceDomains;
                return scheduler.RunPreparedAsync(domains, HardwarePriority.Monitoring, d, DevicePreparationKind.Polling,
                    async token => { CheckProviderChange(owner, token); await d.PollAsync(token).ConfigureAwait(false); return true; }, ct,
                    () => CheckProviderChange(owner, ct));
            })).ToArray();
            await Task.WhenAll(reads).ConfigureAwait(false);
            lock (gate)
            {
                CheckProviderChange(owner, ct); RefreshCore();
                if (providerIsolation is not null)
                {
                    isolatedDeviceIds.UnionWith(driverProviders.Where(p => providerIsolation.ProviderIds.Contains(p.Value)).Select(p => p.Key));
                    ValidateProviderIsolationCore(providerIsolation);
                }
                if (addedIds.Any(id => observationErrors.ContainsKey("provider/" + id)) ||
                    driverProviders.Any(p => addedIds.Contains(p.Value) && observationErrors.ContainsKey(p.Key)))
                    throw new IOException("新しい経路の機器一覧を登録できません");
            }
        }
        IDeviceControlDriver[] discovered;
        lock (gate) discovered = drivers.Values.Where(d => addedIds.Contains(driverProviders.GetValueOrDefault(d.DeviceId) ?? "")).ToArray();
        await ReleaseDriversAsync(DeviceReleaseReason.StartupRecovery, discovered).ConfigureAwait(false);
        lock (gate)
        {
            CheckProviderChange(owner, ct);
            if (lifecycleError is not null) throw new IOException(lifecycleError);
            return drivers.Values.ToArray();
        }
    }

    private async Task CloseRetiredProvidersAsync()
    {
        IDeviceControlDriver[] closingDrivers; IDeviceDriverProvider[] closingProviders;
        lock (gate) { closingDrivers = retiredDrivers.ToArray(); closingProviders = retiredProviders.ToArray(); }
        var errors = new List<Exception>();
        var driverErrors = new Dictionary<IDeviceControlDriver, Exception>(ReferenceEqualityComparer.Instance);
        foreach (var driver in closingDrivers)
            try { await driver.DisposeAsync().ConfigureAwait(false); lock (gate) retiredDrivers.Remove(driver); }
            catch (Exception e) { driverErrors[driver] = e; }
        foreach (var provider in closingProviders)
        {
            try
            {
                IDeviceControlDriver[] remaining;
                lock (gate) remaining = retiredDrivers.ToArray();
                IDeviceControlDriver[] unfinished = [];
                if (remaining.Length > 0)
                {
                    // The active ID map is already retired; require exact cached driver objects.
                    // Other providers' failures may never be claimed by this coordinator.
                    if (provider is not IDeviceProviderShutdownCoordinator coordinator) continue;
                    var owned = provider.CachedDrivers;
                    unfinished = remaining.Where(d => owned.Any(candidate => ReferenceEquals(candidate, d))).ToArray();
                    if (unfinished.Length == 0 || !coordinator.CanCompleteDriverShutdown(unfinished)) continue;
                }
                await provider.DisposeAsync().ConfigureAwait(false);
                lock (gate) retiredProviders.Remove(provider);
                foreach (var driver in unfinished)
                {
                    try { await driver.DisposeAsync().ConfigureAwait(false); lock (gate) retiredDrivers.Remove(driver); driverErrors.Remove(driver); }
                    catch (Exception e) { driverErrors[driver] = e; }
                }
            }
            catch (Exception e) { errors.Add(e); }
        }
        errors.AddRange(driverErrors.Values);
        if (errors.Count > 0) throw new AggregateException("取得経路を解放できません", errors);
    }
}
