using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed partial class AsrockDeviceDriver
{
    private void RestoreKnownCurrentLightingDefaults(byte zone)
    {
        var port = observed?.Zones.SingleOrDefault(z => z.Address == zone)
            ?? throw new IOException("照明ゾーンの通信範囲を取得できません");
        // Called only for a released endpoint under gate/io. Keep sibling owners,
        // active streams and recovery records exactly as they are.
        settings[zone] = (new(new(DeviceId, "zone:" + zone), false, new(LightingExitAction.RestoreOriginal)),
            new EndpointSetup(LedCount: port.Count));
    }

    public PhysicalDeviceBinding? CurrentControlBinding
    { get { lock (gate) return binding == Candidate() ? binding : null; } }

    public bool AllowsCurrentConnectionControl(string endpointId, DeviceFeature feature)
    {
        lock (gate)
        {
            if (feature == DeviceFeature.RestoreLighting)
            {
                try { return !disposed && connection is not null && journalError is null && observed?.Zones.Any(z => z.Address == Zone(endpointId)) == true; }
                catch (ArgumentException) { return false; }
            }
            if (feature is not (DeviceFeature.FixedColor or DeviceFeature.NativeEffect or DeviceFeature.ColorFrames)) return false;
            try { return Ready(Zone(endpointId)) is null; }
            catch (ArgumentException) { return false; }
        }
    }

    public void ConfigureCurrentConnection(PhysicalDeviceBinding currentBinding, PhysicalDeviceBinding? savedBinding,
        IEnumerable<CoolingEndpointConfiguration> cooling, IEnumerable<LightingEndpointConfiguration> lighting,
        IEnumerable<RegisteredEndpoint> endpoints)
    {
        var values = lighting.Where(v => v.Target.DeviceId == DeviceId).Take(9).Select(Contract.Copy).ToArray();
        var registered = endpoints.Take(9).Select(Contract.Copy).ToArray();
        if (values.Length > 8 || registered.Length > 8 || values.Any(v => !DeviceLightingConfiguration.ValidEndpoint(v)) ||
            values.DistinctBy(v => v.Target).Count() != values.Length || registered.DistinctBy(e => e.Id).Count() != registered.Length)
            throw new ArgumentException("マザー照明の設定が不正です");
        if (!io.Wait(0)) throw new IOException("照明操作の完了を待っています");
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (currentBinding != Candidate() || observed is null) throw new IOException("照明の現在接続が変更されています");
                // A configuration refresh must never replace the exit plan or original-state
                // journal of an operation already owned by this connection.
                if (active is not null || nativeOwned.Count != 0) return;
                settings = observed.Zones.ToDictionary(z => z.Address, z =>
                {
                    var id = "zone:" + z.Address;
                    var saved = values.SingleOrDefault(v => v.Target.EndpointId == id);
                    var layout = registered.SingleOrDefault(e => e.Kind == EndpointKind.Lighting && e.Id == id)?.Setup;
                    // Counts describe firmware packet extents, including unused headers. They
                    // are sufficient for this compiled protocol, not evidence of attached LEDs.
                    var setup = layout?.LedCount == z.Count ? layout : new EndpointSetup(LedCount: z.Count);
                    var config = saved is not null && DeviceLightingConfiguration.ValidExit(saved.Exit)
                        ? saved : new LightingEndpointConfiguration(new(DeviceId, id), false, new(LightingExitAction.RestoreOriginal));
                    return (Config: config, Setup: (EndpointSetup?)setup);
                });
                binding = currentBinding;
            }
        }
        finally { io.Release(); }
    }
}
