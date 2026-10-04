namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private readonly HashSet<string> registryCapacityBlockedProviders = [];
    private (IDeviceControlDriver Driver, IDeviceCacheRetirement Provider) CacheRetirementCore(string deviceId, long generation)
    {
        if (!DeviceConfigurationRules.Key(deviceId) || generation <= 0) throw new IOException("整理する機器の識別が不正です");
        var instance = registry.Snapshot.Devices.SingleOrDefault(d => d.Device.Id == deviceId);
        if (instance is null || instance.Connected || instance.Generation != generation)
            throw new IOException("対象は切断済みの機器ではないか、接続状態が変わりました");
        if (!drivers.TryGetValue(deviceId, out var driver) || !driverProviders.TryGetValue(deviceId, out var providerId) ||
            providers.SingleOrDefault(p => p.ProviderId == providerId) is not IDeviceCacheRetirement provider ||
            !provider.CachedDrivers.Any(d => ReferenceEquals(d, driver)))
            throw new IOException("この取得経路は個別の検出履歴整理に対応していません");
        if (observationErrors.ContainsKey(deviceId) || (observationErrors.ContainsKey("provider/" + providerId) && !registryCapacityBlockedProviders.Contains(providerId)) ||
            !provider.IsConfirmedAbsent(driver))
            throw new IOException("取得失敗と切断を区別できません。取得状態を確認してください");
        if (states.Values.Any(s => s.Target.DeviceId == deviceId && s.Owned) ||
            recoveryRequired.Any(t => t.DeviceId == deviceId) || lightingRecoveryRequired.Any(t => t.DeviceId == deviceId) ||
            coolingPolicies.Keys.Any(t => t.DeviceId == deviceId) || retiredDrivers.Contains(driver))
            throw new IOException("対象機器の制御終了・復旧が未完了です");
        return (driver, provider);
    }

    // Observes cached metadata only. It neither disposes a driver nor changes saved settings.
    public string? CacheRetirementProblem(string deviceId, long generation)
    {
        lock (gate)
        {
            try
            {
                if (stopping || releasing || configurationChanging || providerTransitionError is not null)
                    return "制御終了・設定反映の状態を確認してください";
                RefreshCore(); _ = CacheRetirementCore(deviceId, generation); return null;
            }
            catch (Exception e) { return e.Message; }
        }
    }

    public void CheckCacheRetirement(long expectedTopology, DeviceCacheRetirementTarget target)
    {
        lock (gate)
        {
            if (stopping || releasing || configurationChanging || providerTransitionError is not null)
                throw new IOException("制御終了・設定反映の状態を確認してください");
            RefreshCore();
            if (registry.Snapshot.TopologyRevision != expectedTopology) throw new IOException("機器構成が変わりました");
            _ = CacheRetirementCore(target.DeviceId, target.Generation);
        }
    }

    // Administrative compiled API. The wire/UI caller must review that all control is released.
    // Existing authenticated reconfiguration owns draining, lease expiry, release, and shutdown waiting.
    public async Task RetireDisconnectedAsync(string owner, long expectedTopology, string deviceId, long generation, Action? validateConfiguration = null)
    {
        lock (gate) { RefreshCore(); _ = CacheRetirementCore(deviceId, generation); }
        await ReconfigureAsync(owner, expectedTopology, async (_, ct) =>
        {
            validateConfiguration?.Invoke();
            IDeviceControlDriver driver; IDeviceCacheRetirement provider;
            lock (gate)
            {
                CheckProviderChange(owner, ct); RefreshCore();
                (driver, provider) = CacheRetirementCore(deviceId, generation);
            }
            return await scheduler.RunPreparedAsync(provider.ResourceDomains, HardwarePriority.Recovery, provider,
                DevicePreparationKind.Release, async token =>
                {
                    CheckProviderChange(owner, token);
                    await provider.RetireDisconnectedAsync(driver, token).ConfigureAwait(false);
                    lock (gate)
                    {
                        // Disposal has completed. Reconcile this fact even if the owner expired meanwhile.
                        // A failed/partial provider implementation cannot make the runtime forget its obligation.
                        if (provider.CachedDrivers.Any(d => d.DeviceId == deviceId) || driver.Observe() is not null ||
                            !drivers.TryGetValue(deviceId, out var registered) || !ReferenceEquals(registered, driver))
                            throw new IOException("機器の通信解放とキャッシュからの除去を確認できません");
                        if (!registry.ForgetDisconnected(deviceId)) throw new IOException("整理する機器の接続状態が変わりました");
                        drivers.Remove(deviceId); driverProviders.Remove(deviceId);
                        polling.Remove("driver/" + deviceId); pollingErrors.Remove(deviceId); observationErrors.Remove(deviceId);
                        foreach (var target in states.Keys.Where(t => t.DeviceId == deviceId).ToArray()) states.Remove(target);
                        closedRegistryDevices.Remove(deviceId);
                        // Keep disk registrations intact; a rediscovered driver receives no cached write authority.
                        return (physicalBindings ?? []).Values.Where(b => b.DeviceId != deviceId).Select(Contract.Copy).ToArray();
                    }
                }, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}
