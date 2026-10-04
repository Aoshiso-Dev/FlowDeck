namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private readonly Dictionary<string, DeviceConnectionLightingPermission> connectionLighting = [];
    private PhysicalDeviceBinding? ConnectionLightingBinding(string id)
    {
        if (session is null || releasing || stopping || configurationChanging ||
            !connectionLighting.TryGetValue(id, out var permission) || !permission.IsCurrent ||
            !states.TryGetValue(permission.Target, out var state) || state.Generation != permission.Generation ||
            drivers.GetValueOrDefault(id) is not IDeviceConnectionLightingDriver { UsesConnectionLighting: true } driver ||
            driver.BindingCandidate != permission.Binding) return null;
        return permission.Binding;
    }
    private void RevokeConnectionLightingCore()
    {
        foreach (var permission in connectionLighting.Values) permission.Revoke();
        connectionLighting.Clear();
    }
    // Caller holds the configuration authority lock. All supplied data comes from the adopted service configuration.
    public int PrepareConnectionLighting(string owner, IReadOnlySet<string> excludedDevices, DeviceControlUsage savedUsage,
        Func<bool> configurationCurrent)
    {
        lock (gate)
        {
            ExpireCore(); RefreshCore();
            if (session != owner || sessionLifetime is null || releasing || stopping || configurationChanging ||
                lifecycleError is not null || !configurationCurrent()) return 0;
            foreach (var id in connectionLighting.Keys.ToArray())
                if (excludedDevices.Contains(id) || ConnectionLightingBinding(id) is null)
                { connectionLighting[id].Revoke(); connectionLighting.Remove(id); }
            var prepared = 0;
            var usage = savedUsage with { Lighting = savedUsage.Lighting + connectionLighting.Count, Leds = savedUsage.Leds + 8 * connectionLighting.Count };
            foreach (var device in registry.Snapshot.Devices)
            {
                var id = device.Device.Id;
                if (!device.Connected || device.Device.Provider != "ene-memory" || device.Device.Kind != DeviceKind.Memory ||
                    device.Device.IdentityConfidence != IdentityConfidence.ConnectionBound || excludedDevices.Contains(id) ||
                    connectionLighting.ContainsKey(id) || drivers.GetValueOrDefault(id) is not IDeviceConnectionLightingDriver driver ||
                    driver is not IDeviceSetupDriver setup || driver.BindingCandidate is not { } binding) continue;
                var descriptors = setup.DescribeSetup();
                if (device.Device.Endpoints is not [{ Kind: EndpointKind.Lighting } endpoint] || descriptors is not [var descriptor] ||
                    descriptor.EndpointId != endpoint.Id || descriptor.DetectedLeds != 8 || descriptor.MinimumLeds != 8 || descriptor.MaximumLeds != 8 ||
                    descriptor.CoolingExitActions.Length != 0 || !descriptor.LightingExitActions.Contains(LightingExitAction.RestoreOriginal)) continue;
                var target = new EndpointReference(id, endpoint.Id);
                if (!states.TryGetValue(target, out var state) || state.Generation != device.Generation || state.Owned ||
                    state.Lighting?.Running == true || state.Lighting?.Fault is not null || DeviceLightingIdentification.Running(state.Identification) ||
                    recoveryRequired.Any(t => t.DeviceId == id) || lightingRecoveryRequired.Any(t => t.DeviceId == id)) continue;
                var nextUsage = usage with { Lighting = usage.Lighting + 1, Leds = usage.Leds + 8 };
                if (DeviceControlCapacity.Problem(nextUsage) is not null) continue;
                var permission = new DeviceConnectionLightingPermission(target, binding, device.Generation, sessionLifetime.Token, configurationCurrent);
                try
                {
                    if (!driver.TryPrepareConnectionLighting(permission) || !permission.IsCurrent || driver.BindingCandidate != binding)
                    { permission.Revoke(); continue; }
                    connectionLighting.Add(id, permission); usage = nextUsage; prepared++;
                    states[target] = state with { PolicyRevision = checked(state.PolicyRevision + 1),
                        Message = "今回の接続中だけ照明を使用できます", RequestedLighting = null, ReportedLighting = null, Persistence = null };
                }
                catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
                { permission.Revoke(); }
            }
            RefreshCore(); return prepared;
        }
    }
}
