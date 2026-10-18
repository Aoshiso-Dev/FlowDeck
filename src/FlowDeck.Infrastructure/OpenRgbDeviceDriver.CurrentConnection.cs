using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed partial class OpenRgbDeviceDriver
{
    private void RestoreKnownCurrentLightingDefaults(OpenRgbLightingSurface port)
    {
        // Only the selected released surface changes. A sibling's shared Direct
        // background, first original, exit policy and journal remain untouched.
        var exit = port.ControllerDirect && port.Native.Length == 0 ? LightingExitAction.Off : LightingExitAction.RestoreOriginal;
        settings[port.Endpoint] = (new(new(DeviceId, port.Endpoint), false, new(exit)), new EndpointSetup(LedCount: port.LedCount));
    }

    public PhysicalDeviceBinding? CurrentControlBinding
    { get { lock (gate) return binding == Candidate() ? binding : null; } }

    private bool IsAsrockConfiguredExtent() => current?.Snapshot.Controller is
        { Type: 0, Vendor: "ASRock", Description: "ASRock Polychrome USB Device" };

    public bool AllowsCurrentConnectionControl(string endpointId, DeviceFeature feature)
    {
        lock (gate)
        {
            if (feature == DeviceFeature.RestoreLighting)
                return !disposed && !nativeShutdown && current is not null && journalError is null && ports.ContainsKey(endpointId);
            if (!ports.TryGetValue(endpointId, out var port) || Ready(port) is not null) return false;
            return feature switch
            {
                DeviceFeature.FixedColor => port.FixedMode is not null || port.ControllerDirect,
                DeviceFeature.NativeEffect => port.Native.Length > 0,
                DeviceFeature.ColorFrames => port.DirectMode is not null || port.ControllerDirect,
                _ => false
            };
        }
    }

    public void ConfigureCurrentConnection(PhysicalDeviceBinding currentBinding, PhysicalDeviceBinding? savedBinding,
        IEnumerable<CoolingEndpointConfiguration> cooling, IEnumerable<LightingEndpointConfiguration> lighting,
        IEnumerable<RegisteredEndpoint> endpoints)
    {
        var values = lighting.Where(v => v.Target.DeviceId == DeviceId).Take(257).Select(Contract.Copy).ToArray();
        var registered = endpoints.Take(257).Select(Contract.Copy).ToArray();
        if (values.Length > 256 || registered.Length > 256 || values.Any(v => !DeviceLightingConfiguration.ValidEndpoint(v)) ||
            values.DistinctBy(v => v.Target).Count() != values.Length || registered.DistinctBy(e => e.Id).Count() != registered.Length)
            throw new ArgumentException("OpenRGBの照明設定が不正です");
        if (!io.Wait(0)) throw new IOException("OpenRGB操作の完了を待っています");
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closing || disposed, this);
                if (currentBinding != Candidate()) throw new IOException("OpenRGBの現在接続が変更されています");
                // Route selection and packet authorization remain in Ready/Guard. This only
                // resolves operational parameters and never resets an active owner or journal.
                if (streams.Count != 0 || slots.Values.Any(s => s.Owned)) return;
                settings = ports.Values.ToDictionary(p => p.Endpoint, p =>
                {
                    var saved = values.SingleOrDefault(v => v.Target.EndpointId == p.Endpoint);
                    var layout = registered.SingleOrDefault(e => e.Kind == EndpointKind.Lighting && e.Id == p.Endpoint)?.Setup;
                    var setup = layout?.LedCount == p.LedCount ? layout : new EndpointSetup(LedCount: p.LedCount);
                    var cannotRestore = p.ControllerDirect && p.Native.Length == 0;
                    var validExit = saved is not null && DeviceLightingConfiguration.ValidExit(saved.Exit) &&
                        (!cannotRestore || saved.Exit!.Action != LightingExitAction.RestoreOriginal) &&
                        (p.FixedMode is not null || p.ControllerDirect || saved.Exit!.Action == LightingExitAction.RestoreOriginal);
                    var config = validExit ? saved! : new LightingEndpointConfiguration(new(DeviceId, p.Endpoint), false,
                        new(cannotRestore ? LightingExitAction.Off : LightingExitAction.RestoreOriginal));
                    return (Config: config, Setup: (EndpointSetup?)setup);
                });
                binding = currentBinding;
            }
        }
        finally { io.Release(); }
    }
}
