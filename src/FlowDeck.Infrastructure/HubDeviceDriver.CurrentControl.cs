using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class HubDeviceDriver : IDeviceCurrentConnectionControlDriver
{
    private PhysicalDeviceBinding? currentControlBinding;
    private string? currentControlLocator;
    private bool CurrentControlConnectionChanged => currentControlBinding is not null && currentControlLocator != locator;
    public PhysicalDeviceBinding? CurrentControlBinding
    {
        get { lock (gate) return !disposed && connected && !CurrentControlConnectionChanged && currentControlBinding == candidate &&
            binding == currentControlBinding && lightingBinding == currentControlBinding ? currentControlBinding : null; }
    }

    public bool AllowsCurrentConnectionControl(string endpointId, DeviceFeature feature)
    {
        lock (gate)
        {
            if (CurrentControlBinding is null) return false;
            return feature switch
            {
                DeviceFeature.FixedDuty or DeviceFeature.AutomaticDuty =>
                    DefinitionData.Cooling.Endpoints.Contains(endpointId) && Ready(endpointId, policy: true) is null,
                DeviceFeature.FixedColor or DeviceFeature.NativeEffect =>
                    DefinitionData.Lighting.Endpoints.Contains(endpointId) && LightingReady(endpointId) is null,
                DeviceFeature.ColorFrames =>
                    DefinitionData.Lighting.Endpoints.Contains(endpointId) && LightingReady(endpointId, streaming: true) is null,
                // Ending an owned stream must remain possible after a failed
                // write. The current connection and exact endpoint are still
                // required; release verifies its saved journal and locator.
                DeviceFeature.RestoreLighting => LightingAdapter is { SupportsLightingEndpoints: true } &&
                    status?.Lighting.Any(p => p.Id == endpointId) == true,
                _ => false
            };
        }
    }

    // Called under gate/io after the selected endpoint's old ownership has ended.
    // Forgetting saved preferences must not disable a known attached accessory.
    // Only its current detected size may supply a new, unwritten exit policy.
    private void RestoreKnownCurrentLightingDefaults(PhysicalDeviceBinding expected, string endpointId, RegisteredEndpoint endpoint)
    {
        if (CurrentControlBinding != expected || status?.Lighting.SingleOrDefault(p => p.Id == endpointId) is not
                { LedCount: >= 1 } port || port.LedCount > DefinitionData.Lighting.MaximumLedsPerEndpoint) return;
        lightingSetup[endpointId] = (new(new(DeviceId, endpointId), false, new(LightingExitAction.Off)),
            new(port.Accessory, port.LedCount, false));
    }

    public void ConfigureCurrentConnection(PhysicalDeviceBinding currentBinding, PhysicalDeviceBinding? savedBinding,
        IEnumerable<CoolingEndpointConfiguration> cooling, IEnumerable<LightingEndpointConfiguration> lighting,
        IEnumerable<RegisteredEndpoint> endpoints)
    {
        // Templates are operational values, not evidence of a current connection.
        // Publication is atomic and performs no adapter, output or journal writes.
        var fans = cooling.Take(DefinitionData.Cooling.Endpoints.Count + 1).Select(Contract.Copy).ToArray();
        var lights = lighting.Take(DefinitionData.Lighting.Endpoints.Count + 1).Select(Contract.Copy).ToArray();
        var registered = endpoints.Take(DeviceRegistry.MaximumEndpoints + 1).Select(Contract.Copy).ToArray();
        if (fans.Length > DefinitionData.Cooling.Endpoints.Count || fans.Any(c => c is null || c.Target is null ||
                c.Target.DeviceId != DeviceId || !DefinitionData.Cooling.Endpoints.Contains(c.Target.EndpointId) ||
                !DeviceConfigurationRules.ValidSafety(c.Safety)) || fans.DistinctBy(c => c.Target).Count() != fans.Length ||
            lights.Length > DefinitionData.Lighting.Endpoints.Count || lights.Any(c => !DeviceLightingConfiguration.ValidEndpoint(c) ||
                c.Target.DeviceId != DeviceId || !DefinitionData.Lighting.Endpoints.Contains(c.Target.EndpointId)) ||
            lights.DistinctBy(c => c.Target).Count() != lights.Length ||
            registered.Length > DeviceRegistry.MaximumEndpoints || registered.Any(e => e is null || !DeviceConfigurationRules.Key(e.Id)) ||
            registered.DistinctBy(e => e.Id).Count() != registered.Length)
            throw new ArgumentException("Hubの実設定が不正です");
        if (!io.Wait(0)) throw new IOException("Hubの操作が完了してから実設定を更新してください");
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                CheckDiscovery();
                if (!connected || status is null || candidate != currentBinding || sampleTimestamp is not { } sampled ||
                    clock.GetElapsedTime(sampled).TotalSeconds is < 0 or >= 3)
                    throw new IOException("Hubの現在の個体・接続情報を再取得してください");
                if (lightingPreparing || calibrating.Count != 0 || journal.Pending.Length != 0 ||
                    lightingJournal.Pending.Length != 0 || lightingStreams.Count != 0 || lightingFaulted.Count != 0)
                    throw new IOException("Hubの前回の操作を終了・復旧してから実設定を更新してください");
                var resolved = new Dictionary<string, (LightingEndpointConfiguration Config, EndpointSetup? Setup)>();
                foreach (var port in status.Lighting)
                {
                    var preference = lights.SingleOrDefault(c => c.Target.EndpointId == port.Id);
                    var template = registered.SingleOrDefault(e => e.Id == port.Id && e.Kind == EndpointKind.Lighting)?.Setup;
                    // Only the pinned worker's actual accessory detection supplies
                    // this count. Unknown accessories need an exact-signature saved
                    // count; the encoder limit must never be mistaken for detection.
                    var count = port.LedCount ?? (template?.Accessory == port.Accessory ? template.LedCount : null);
                    var exit = preference?.Exit ?? new LightingExitPolicy(LightingExitAction.Off);
                    resolved.Add(port.Id, (new(new(DeviceId, port.Id), false, Contract.Copy(exit)),
                        new EndpointSetup(port.Accessory, count, false)));
                }
                // A different physical binding can reuse actual safety values,
                // never the old policy's execution intent. Matching bindings
                // retain the existing explicit startup opt-in; this method does
                // not execute it. The runtime still owns startup validation.
                this.cooling = fans.ToDictionary(c => c.Target.EndpointId,
                    c => savedBinding == currentBinding ? c : c with { SavedPolicy = null, ResumeOnStart = false });
                lightingSetup = resolved;
                binding = lightingBinding = currentControlBinding = currentBinding;
                currentControlLocator = locator;
            }
        }
        finally { io.Release(); }
    }
}
