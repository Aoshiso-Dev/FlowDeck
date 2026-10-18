using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceConfigurationAuthority : IDeviceIndividualCoolingSetupHandler, IDeviceLightingSetupHandler
{
    private static DeviceEndpointSetupScope ReviewEndpointSetup(DeviceConfigurationPackage old, DeviceConfigurationPackage next, EndpointReference target, bool lighting = false)
    {
        void Require(bool ok) { if (!ok) throw new IOException("対象端子の設定以外が変更されています。差分を再確認してください"); }
        Require(next.Bindings.InstallationId == old.Bindings.InstallationId && Same(old.Preferences, next.Preferences) &&
            Same(old.Dashboard, next.Dashboard) && Same(old.Migration, next.Migration) &&
            Same(old.Cooling with { Endpoints = [] }, next.Cooling with { Endpoints = [] }) &&
            Same(old.Lighting with { Endpoints = [], Revision = 0 }, next.Lighting with { Endpoints = [], Revision = 0 }));
        Require(next.Devices.Devices.SingleOrDefault(d => d.Id == target.DeviceId)?.Endpoints.Any(e => e.Id == target.EndpointId && e.Kind == (lighting ? EndpointKind.Lighting : EndpointKind.Cooling)) == true);
        var rebound = old.Bindings.Devices.Concat(next.Bindings.Devices).Select(b => b.DeviceId).Distinct()
            .Where(id => old.Bindings.Devices.SingleOrDefault(b => b.DeviceId == id) != next.Bindings.Devices.SingleOrDefault(b => b.DeviceId == id)).ToArray();
        Require(!lighting || rebound.All(id => id == target.DeviceId));
        Require(Same(old.Devices with { Devices = [] }, next.Devices with { Devices = [] }) &&
            Same(old.Bindings with { Devices = [] }, next.Bindings with { Devices = [] }));
        var scope = new DeviceEndpointSetupScope(target, rebound, lighting ? [target.EndpointId] : null);
        foreach (var device in old.Devices.Devices)
        {
            var after = next.Devices.Devices.SingleOrDefault(d => d.Id == device.Id);
            Require(after is not null && Same(device with { Endpoints = [] }, after with { Endpoints = [] }));
            foreach (var endpoint in device.Endpoints)
            {
                var reference = new EndpointReference(device.Id, endpoint.Id);
                if (reference == target) continue;
                var updated = after!.Endpoints.SingleOrDefault(e => e.Id == endpoint.Id);
                var expected = rebound.Contains(device.Id) && endpoint.Setup is { } setup ? endpoint with { Setup = setup with { LedCountConfirmed = false } } : endpoint;
                Require(Same(endpoint, updated) || Same(expected, updated));
            }
        }
        foreach (var device in next.Devices.Devices)
        {
            Require(!lighting || device.Id == target.DeviceId || old.Devices.Devices.Any(d => d.Id == device.Id));
            foreach (var endpoint in device.Endpoints)
                if (old.Devices.Devices.SingleOrDefault(d => d.Id == device.Id)?.Endpoints.Any(e => e.Id == endpoint.Id) != true)
                    Require(new EndpointReference(device.Id, endpoint.Id) == target || !lighting && endpoint.Kind == EndpointKind.Sensor);
        }
        foreach (var reference in old.Cooling.Endpoints.Concat(next.Cooling.Endpoints).Select(e => e.Target).Distinct())
        {
            if (!lighting && reference == target) continue;
            var before = old.Cooling.Endpoints.SingleOrDefault(e => e.Target == reference);
            var after = next.Cooling.Endpoints.SingleOrDefault(e => e.Target == reference);
            Require(before is not null && (rebound.Contains(reference.DeviceId) || before.SavedPolicy is { } saved &&
                DeviceCoolingPolicy.Required(saved, before.Safety).Any(s => rebound.Contains(s.DeviceId))) ?
                Same(before with { ResumeOnStart = false }, after) ||
                Same(before with { WiringConfirmed = false, SavedPolicy = null, ResumeOnStart = false }, after) : Same(before, after));
        }
        foreach (var reference in (old.Lighting.Endpoints ?? []).Concat(next.Lighting.Endpoints ?? []).Select(e => e.Target).Distinct())
        {
            if (lighting && reference == target) continue;
            var before = old.Lighting.Endpoints?.SingleOrDefault(e => e.Target == reference);
            var after = next.Lighting.Endpoints?.SingleOrDefault(e => e.Target == reference);
            Require(Same(before, after) || rebound.Contains(reference.DeviceId) && before is not null && Same(before with { WiringConfirmed = false }, after));
        }
        foreach (var endpoint in next.Cooling.Endpoints)
            if (rebound.Contains(endpoint.Target.DeviceId) || endpoint.SavedPolicy is { } saved &&
                DeviceCoolingPolicy.Required(saved, endpoint.Safety).Any(s => rebound.Contains(s.DeviceId)))
                Require(!endpoint.ResumeOnStart);
        return scope;
    }

    private PhysicalDeviceBinding[] ConfigureEndpointSetup(DeviceConfigurationPackage value, IDeviceControlDriver[] drivers, DeviceEndpointSetupScope scope)
    {
        var continuing = drivers.Select(d => d.DeviceId).Except(scope.ReboundDevices).ToHashSet();
        var active = ConfigureDrivers(value, installation, drivers, continuing);
        if (!scope.ReboundDevices.Contains(scope.Target.DeviceId) && drivers.SingleOrDefault(d => d.DeviceId == scope.Target.DeviceId) is { } driver)
        {
            if (driver.Observe() is null && driver is IDeviceCurrentConnectionControlDriver && driver is IDeviceConfigurationRevocation absent)
            {
                // A disconnected current-connection driver has no endpoint authority to update.
                // Revoke only cached settings; pending recovery records remain intact. Its next
                // connection preparation will read the newly saved settings without emitting output.
                absent.RevokeConfiguration();
                return active;
            }
            if (scope.IsLighting)
            {
                if (driver is not IIndividualLightingConfiguration light) throw new IOException("照明端子単位の設定に対応していません");
                var expected = value.Bindings.Devices.SingleOrDefault(b => b.DeviceId == driver.DeviceId);
                if (expected is null) throw new IOException("照明機器の実機関連付けがありません");
                if (driver.Observe() is null && driver is not IConfiguredCoolingDriver && driver is IDeviceConfigurationRevocation revoke)
                {
                    revoke.RevokeConfiguration();
                    return active;
                }
                var lightEntry = value.Lighting.Endpoints?.SingleOrDefault(e => e.Target == scope.Target);
                var endpoint = value.Devices.Devices.Single(d => d.Id == driver.DeviceId).Endpoints.Single(e => e.Id == scope.Target.EndpointId);
                light.ConfigureLightingEndpoint(expected, scope.Target.EndpointId, lightEntry, endpoint);
                return active;
            }
            if (driver is not IIndividualCoolingConfiguration individual) throw new IOException("このドライバーは端子単位の設定更新に対応していません");
            var binding = value.Bindings.Devices.SingleOrDefault(b => b.DeviceId == driver.DeviceId);
            var entry = value.Cooling.Endpoints.SingleOrDefault(e => e.Target == scope.Target);
            if (binding is null) throw new IOException("冷却端子の実機関連付けがありません");
            individual.ConfigureCoolingEndpoint(binding, scope.Target.EndpointId, entry is null ? null : entry with {
                ResumeOnStart = entry.ResumeOnStart && value.Preferences.Startup.ResumeControl });
        }
        return active;
    }
}
