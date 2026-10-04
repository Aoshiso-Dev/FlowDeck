namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    // Called under gate before accepting a complete provider cache. No I/O or stored configuration changes.
    private void EnsureRegistryCapacityCore(IEnumerable<string> additions)
    {
        var admission = additions.Concat(drivers.Keys).ToHashSet(StringComparer.Ordinal);
        if (admission.Count > DeviceRegistry.MaximumDevices) throw new IOException("登録する機器数が32台の上限を超えています");
        var snapshot = registry.Snapshot;
        var needed = admission.Union(snapshot.Devices.Select(d => d.Device.Id)).Count() - DeviceRegistry.MaximumDevices;
        if (needed <= 0) return;
        var reclaimable = snapshot.Devices.Where(d => !d.Connected && closedRegistryDevices.Contains(d.Device.Id) &&
            !admission.Contains(d.Device.Id) && !retiredDrivers.Any(r => r.DeviceId == d.Device.Id) &&
            !states.Values.Any(s => s.Target.DeviceId == d.Device.Id && s.Owned) &&
            !recoveryRequired.Any(t => t.DeviceId == d.Device.Id) && !lightingRecoveryRequired.Any(t => t.DeviceId == d.Device.Id) &&
            !coolingPolicies.Keys.Any(t => t.DeviceId == d.Device.Id))
            .OrderBy(d => d.Generation).ThenBy(d => d.Device.Id, StringComparer.Ordinal).Take(needed).ToArray();
        // Insufficient evidence cannot cause a partial eviction or hide an unfinished obligation.
        if (reclaimable.Length != needed) throw new IOException("切断機器を含む登録が32台の上限です。旧経路の制御終了と通信の解放を確認できません");
        foreach (var device in reclaimable)
        {
            if (!registry.ForgetDisconnected(device.Device.Id)) throw new InvalidOperationException("旧経路の登録状態が変わりました");
            foreach (var target in states.Keys.Where(t => t.DeviceId == device.Device.Id).ToArray()) states.Remove(target);
            closedRegistryDevices.Remove(device.Device.Id);
        }
    }
}
