using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class HubDeviceDriver : IIndividualLightingConfiguration
{
    public void ConfigureLightingEndpoint(PhysicalDeviceBinding binding, string endpointId,
        LightingEndpointConfiguration? configuration, RegisteredEndpoint endpoint)
    {
        DeviceLightingConfiguration.CheckIndividual(DeviceId, endpointId, configuration, endpoint);
        if (!DefinitionData.Lighting.Endpoints.Contains(endpointId)) throw new ArgumentException("未対応のHub照明端子です");
        var value = configuration is null ? null : Contract.Copy(configuration); var registered = Contract.Copy(endpoint);
        if (!io.Wait(0)) throw new IOException("Hubの操作完了後に設定してください");
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (binding is null || binding != lightingBinding) throw new IOException("Hub照明の関連付けが一致しません");
                if (lightingPreparing || lightingStreams.ContainsKey(endpointId) || lightingFaulted.Contains(endpointId) ||
                    lightingJournal.Pending.Any(p => p.Channel == endpointId)) throw new IOException("対象端子の照明を終了・復旧してから設定してください");
                if (value is null)
                {
                    lightingSetup.Remove(endpointId);
                    RestoreKnownCurrentLightingDefaults(binding, endpointId, registered);
                }
                else lightingSetup[endpointId] = (value, registered.Setup);
            }
        }
        finally { io.Release(); }
    }
}

internal sealed partial class AsrockDeviceDriver : IIndividualLightingConfiguration
{
    public void ConfigureLightingEndpoint(PhysicalDeviceBinding binding, string endpointId,
        LightingEndpointConfiguration? configuration, RegisteredEndpoint endpoint)
    {
        DeviceLightingConfiguration.CheckIndividual(DeviceId, endpointId, configuration, endpoint);
        var zone = Zone(endpointId); var value = configuration is null ? null : Contract.Copy(configuration); var registered = Contract.Copy(endpoint);
        if (!io.Wait(0)) throw new IOException("照明処理の完了後に設定してください");
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (journalError is not null) throw new IOException(journalError);
                if (binding is null || binding != this.binding || binding != Candidate()) throw new IOException("照明の関連付けが一致しません");
                if (active?.Targets.Contains(zone) == true || journal!.Pending?.Targets.Contains(zone) == true ||
                    nativeJournal!.Pending?.Entries.Any(e => e.Zone == zone) == true || nativeOwned.Contains(zone) || nativeFaulted.Contains(zone))
                    throw new IOException("対象端子の照明を終了・復旧してから設定してください");
                if (value is null) RestoreKnownCurrentLightingDefaults(zone);
                else settings[zone] = (value, registered.Setup);
            }
        }
        finally { io.Release(); }
    }
}

internal sealed partial class EneMemoryDeviceDriver : IIndividualLightingConfiguration
{
    public void ConfigureLightingEndpoint(PhysicalDeviceBinding binding, string endpointId,
        LightingEndpointConfiguration? configuration, RegisteredEndpoint endpoint)
    {
        DeviceLightingConfiguration.CheckIndividual(DeviceId, endpointId, configuration, endpoint);
        ArgumentNullException.ThrowIfNull(binding);
        // Removing preferences keeps the known controller usable with its actual eight
        // LEDs and a fresh-original exit policy; it does not remove current model support.
        ConfigureCurrentConnection(binding, null, [], configuration is null ? [] : [configuration], [endpoint]);
    }
}

internal sealed partial class OpenRgbDeviceDriver : IIndividualLightingConfiguration
{
    public void ConfigureLightingEndpoint(PhysicalDeviceBinding binding, string endpointId,
        LightingEndpointConfiguration? configuration, RegisteredEndpoint endpoint)
    {
        DeviceLightingConfiguration.CheckIndividual(DeviceId, endpointId, configuration, endpoint);
        var value = configuration is null ? null : Contract.Copy(configuration); var registered = Contract.Copy(endpoint);
        if (!io.Wait(0)) throw new IOException("OpenRGBの操作完了後に設定してください");
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closing || disposed, this);
                if (journalError is not null) throw new IOException(journalError);
                if (nativeShutdown) throw new IOException("Native復旧と記録整理を待っています");
                if (binding is null || binding != this.binding || binding != Candidate()) throw new IOException("OpenRGBの関連付けが一致しません");
                if (!ports.TryGetValue(endpointId, out var port)) throw new IOException("OpenRGBの未検出端子です");
                var slot = slots[Key(port)];
                if (streams.ContainsKey(endpointId) || shared?.Controlled.ContainsKey(endpointId) == true ||
                    slot.Journal.Pending is not null || slot.Owned || slot.Faulted ||
                    port.ControllerDirect && shared is null && slots[-1].Journal.Pending is not null)
                    throw new IOException("対象端子の照明を終了・復旧してから設定してください");
                // Shared Direct keeps its whole-controller mode, background and recovery record for other zones.
                if (value is null) RestoreKnownCurrentLightingDefaults(port);
                else settings[endpointId] = (value, registered.Setup);
            }
        }
        finally { io.Release(); }
    }
}
