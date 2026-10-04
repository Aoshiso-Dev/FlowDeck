using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed record OpenRgbDeviceIdentity(string Native, IdentityConfidence Confidence, bool Bindable);
internal sealed record OpenRgbBoundDevice(OpenRgbSdkConnection Connection, OpenRgbSdkSnapshot Snapshot, OpenRgbDeviceIdentity Identity);

internal static class OpenRgbDeviceIdentities
{
    internal static string Locator(OpenRgbSdkTarget target) => $"{target.Connection:N}:{target.InventoryRevision}:{target.ControllerId}:{target.Structure}";
    // The SDK's ID locates the current connection only. It is never a manufactured serial or persistent physical identity.
    public static Dictionary<uint, OpenRgbDeviceIdentity> Resolve(OpenRgbSdkSnapshot[] snapshots, IDictionary<OpenRgbSdkTarget, string> unresolved)
    {
        string? Serial(OpenRgbSdkController d) => HardwareSensorCapture.UsefulSerial(d.Serial) &&
            !new[] { "serial", "serial number", "not supported" }.Contains(d.Serial.Trim(), StringComparer.OrdinalIgnoreCase)
            ? SettingsTransfer.Hash(new { d.Vendor, d.Name, d.Type, Serial = d.Serial.Trim() }) : null;
        string? Location(OpenRgbSdkController d) => DeviceConfigurationRules.Text(d.Location, 1024) &&
            !new[] { "unknown", "none", "n/a", "not supported" }.Contains(d.Location.Trim(), StringComparer.OrdinalIgnoreCase)
            ? SettingsTransfer.Hash(new { d.Vendor, d.Name, d.Type, Location = d.Location.Trim() }) : null;
        var values = snapshots.Select(s => (s.Target, Serial: Serial(s.Controller), Location: Location(s.Controller))).ToArray();
        var result = new Dictionary<uint, OpenRgbDeviceIdentity>();
        foreach (var value in values)
        {
            OpenRgbDeviceIdentity identity;
            if (value.Serial is { } serial && values.Count(v => v.Serial == serial) == 1) identity = new("serial:" + serial, IdentityConfidence.SerialBound, true);
            else if (value.Location is { } location && values.Count(v => v.Location == location) == 1) identity = new("location:" + location, IdentityConfidence.ConnectionBound, true);
            else
            {
                if (!unresolved.TryGetValue(value.Target, out var local)) unresolved.Add(value.Target, local = Guid.NewGuid().ToString("N"));
                identity = new("unbound:" + local, IdentityConfidence.ConnectionBound, false);
            }
            result.Add(value.Target.ControllerId, identity);
        }
        foreach (var old in unresolved.Keys.Except(values.Select(v => v.Target)).ToArray()) unresolved.Remove(old);
        return result;
    }
    public static PhysicalDeviceBinding? Binding(string id, OpenRgbBoundDevice? device) => device is { Identity.Bindable: true } d
        ? new(id, d.Identity.Native, OpenRgbDeviceProvider.Definition, SettingsTransfer.Hash(new
        {
            d.Identity.Native, d.Snapshot.Target.Structure,
            Session = d.Identity.Confidence == IdentityConfidence.SerialBound ? null : d.Snapshot.Target
        })) : null;

    public static OpenRgbLightingSurface[] Surfaces(OpenRgbSdkController device)
    {
        var result = new List<OpenRgbLightingSurface> { OpenRgbLightingProfile.Surface(device, null) };
        // Zone names/type identify SDK-local endpoints; list positions are only transient transport selectors.
        // Ambiguous zone names remain accessible through the whole device, never assigned guessed physical ports.
        if (OpenRgbLightingProfile.Partitioned(device) && device.Zones.Length < DeviceRegistry.MaximumEndpoints)
        {
            var keys = device.Zones.Select(z => SettingsTransfer.Hash(new { z.Name, z.Type })).ToArray();
            for (var i = 0; i < keys.Length; i++)
                if (!string.IsNullOrWhiteSpace(device.Zones[i].Name) && keys.Count(k => k == keys[i]) == 1)
                    result.Add(OpenRgbLightingProfile.Surface(device, i) with { Endpoint = "zone:" + keys[i].ToLowerInvariant() });
        }
        var shared = result[0].DirectMode is not null && result.Any(s => s.Zone is not null && s.DirectMode is null);
        return result.Where(s => s.LedCount > 0).Select(s => s with { ControllerDirect = shared && s.Zone is not null }).ToArray();
    }
}
