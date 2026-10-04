using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>Projects existing, already acquired inventory into common device capabilities. Performs no I/O.</summary>
public static class LegacyDeviceCatalog
{
    public static DeviceObservation[] Build(string installation, AppSettings settings, ControlSnapshot control,
        BoardSnapshot board, RgbInventory rgb, bool privileged, TimeProvider? time = null)
    {
        var result = new List<DeviceObservation>();
        string Id(string provider, string native) => DeviceIdentity.Create(installation, provider, native);
        static string Short(string text, int max = 160) => MotherboardDisplay.ShortText(text, max);
        static DeviceCapability Feature(DeviceFeature feature, DeviceAvailability availability, bool supported = true,
            VerificationEvidence evidence = VerificationEvidence.MatchingHardware, string? reason = null)
            => new(feature, supported ? ImplementationSupport.Supported : ImplementationSupport.Unsupported, evidence, availability, reason);
        foreach (var group in control.Sensors.GroupBy(s => (s.Kind, HardwareId(s.Id))))
        {
            var first = group.First();
            result.Add(new(Id("lhm", group.Key.Item2), "lhm", Short(first.Name.Split(" / ")[0]),
                group.Key.Kind switch { "cpu" => DeviceKind.Processor, "gpu" => DeviceKind.Graphics, _ => DeviceKind.Other }, IdentityConfidence.ConnectionBound,
                group.Key.Item2, null, ["sensors:lhm"], group.Select(s => new DeviceEndpoint(Id("sensor", s.Id), Short(s.Name), EndpointKind.Sensor,
                    [Feature(DeviceFeature.Temperature, DashboardDisplay.Fresh(s.Timestamp, time) && MotherboardDisplay.IsUsable("temperature", s.Celsius) ? DeviceAvailability.Ready : DeviceAvailability.Failed,
                        evidence: VerificationEvidence.SourceReview)], s.Id)).ToArray()));
        }
        if (control.HubReady && settings.Hardware.Hub is { } hub)
        {
            var telemetryFresh = control.TelemetryTimestamp is { } t && DashboardDisplay.Fresh(t, time);
            var fanReady = settings.Hardware.WiringConfirmed && FanPolicyEngine.ValidateLimits(settings.Limits) is null;
            var autoReady = settings.Profiles.Any(p => ProfileAvailable(p, settings.Limits, control.Sensors, time));
            var writeState = !telemetryFresh ? DeviceAvailability.Failed : fanReady ? DeviceAvailability.Ready : DeviceAvailability.NeedsSetup;
            var endpoints = control.Channels.Select(c => new DeviceEndpoint(c.Id, "FAN " + c.Id[3..], EndpointKind.Cooling,
                [Feature(DeviceFeature.Rpm, telemetryFresh && MotherboardDisplay.IsUsable("fan", c.Rpm) ? DeviceAvailability.Ready : DeviceAvailability.Failed),
                 Feature(DeviceFeature.DutyRead, telemetryFresh && MotherboardDisplay.IsUsable("control", c.Duty) ? DeviceAvailability.Ready : DeviceAvailability.Failed),
                 Feature(DeviceFeature.FixedDuty, c.Presence == Presence.Present ? writeState : DeviceAvailability.NeedsSetup),
                 Feature(DeviceFeature.AutomaticDuty, c.Presence == Presence.Present && autoReady ? writeState : DeviceAvailability.NeedsSetup)], Role: "fan")).ToList();
            endpoints.AddRange((settings.Hardware.LedDetails ?? []).Select(l => new DeviceEndpoint(l.Id, l.Id.ToUpperInvariant(), EndpointKind.Lighting,
                [Feature(DeviceFeature.FixedColor, settings.Hardware.WiringConfirmed && l.LedCount is > 0 ? DeviceAvailability.Ready : DeviceAvailability.NeedsSetup),
                 Feature(DeviceFeature.NativeEffect, settings.Hardware.WiringConfirmed && l.LedCount is > 0 ? DeviceAvailability.Ready : DeviceAvailability.NeedsSetup),
                 Feature(DeviceFeature.ColorFrames, settings.Hardware.WiringConfirmed && l.LedCount is > 0 ? DeviceAvailability.Ready : DeviceAvailability.NeedsSetup)])));
            result.Add(new(Id("nzxt-control-hub", hub.Id), "nzxt-control-hub", Short(hub.Description), DeviceKind.Controller,
                string.IsNullOrWhiteSpace(hub.Serial) ? IdentityConfidence.ConnectionBound : IdentityConfidence.SerialBound,
                hub.Path, "nzxt-control-hub-1e71-2022-v1", ["usb:" + hub.Id], endpoints.ToArray()));
        }
        if (board.Timestamp is not null || board.Sensors.Length > 0)
        {
            var definition = MotherboardDefinitionCatalog.BuiltIn.Resolve(board.Name, allowDemo: true);
            var observationState = board.IsFresh(time) ? DeviceAvailability.Ready : DeviceAvailability.Failed;
            var endpoints = new List<DeviceEndpoint>();
            foreach (var sensor in board.Sensors.Where(s => s.Kind is "temperature" or "fan"))
            {
                var available = MotherboardDisplay.IsUsable(sensor.Kind, sensor.Value) && sensor.Problem is null ? observationState : DeviceAvailability.Failed;
                if (sensor.Kind == "temperature")
                {
                    endpoints.Add(new(Id("sensor", sensor.Id), Short(sensor.SourceName), EndpointKind.Sensor,
                        [Feature(DeviceFeature.Temperature, available, evidence: VerificationEvidence.SourceReview)], sensor.Id));
                    continue;
                }
                var port = MotherboardDefinitionCatalog.BuiltIn.PortForSensor(board, sensor.Id);
                var setup = port is null ? null : MotherboardFans.Settings(settings, port.Id);
                var canWrite = !privileged ? DeviceAvailability.NeedsPermission : !board.IsFresh(time) ? DeviceAvailability.Failed :
                    setup is { WiringConfirmed: true, Minimum: >= 1 and <= 100 } ? DeviceAvailability.Ready : DeviceAvailability.NeedsSetup;
                var sensors = control.Sensors.Concat(board.Sensors.Where(s => s.Kind == "temperature").Select(s =>
                    new SensorReading(s.Id, s.SourceName, "motherboard", s.Problem is null && board.Error is null ? s.Value : null, board.Timestamp ?? 0))).ToArray();
                var autoReady = setup?.Profile is { } profile && ProfileAvailable(profile, new(setup.Minimum, 100, 100), sensors, time);
                endpoints.Add(new(port?.Id ?? Id("fan", sensor.Id), port?.Name ?? Short(sensor.SourceName), EndpointKind.Cooling,
                    [Feature(DeviceFeature.Rpm, available, evidence: VerificationEvidence.SourceReview),
                     Feature(DeviceFeature.FixedDuty, canWrite, port is not null, VerificationEvidence.FamilyHardware),
                     Feature(DeviceFeature.AutomaticDuty, autoReady ? canWrite : DeviceAvailability.NeedsSetup, port is not null, VerificationEvidence.FamilyHardware),
                     Feature(DeviceFeature.ReleaseCooling, !privileged ? DeviceAvailability.NeedsPermission : observationState, port is not null, VerificationEvidence.FamilyHardware)], sensor.Id, "fan"));
            }
            result.Add(new(Id("motherboard", board.Name), "motherboard", Short(board.Name), DeviceKind.Motherboard,
                IdentityConfidence.ConnectionBound, "system:motherboard", definition?.Id, ["sio:motherboard"], endpoints.ToArray()));
        }
        foreach (var device in rgb.Devices)
        {
            var native = device.ControlIdentity is not null && (device.IsMemory || !settings.Motherboard.OpenRgbEnabled && device.IsAsrock);
            var provider = native ? device.IsMemory ? "ene-memory" : "asrock-usb" : "openrgb";
            // An SDK index is never identity. Ambiguous same-name/location observations are rejected by the registry.
            var nativeId = device.ControlIdentity ?? string.Join('|', device.Vendor, device.Name, device.Location);
            var endpoints = device.Zones.Select((zone, i) =>
            {
                var available = rgb.Error is not null ? DeviceAvailability.Failed : zone.Configuration is null || zone.LedCount == 0 ? DeviceAvailability.NeedsSetup : DeviceAvailability.Ready;
                var id = native && zone.HardwareZone is { } address ? "zone:" + address : "zone:" + zone.Name;
                return new DeviceEndpoint(id, Short(zone.Name), EndpointKind.Lighting,
                    [Feature(DeviceFeature.FixedColor, available, native), Feature(DeviceFeature.NativeEffect, available, native),
                     Feature(DeviceFeature.ColorFrames, available, native), Feature(DeviceFeature.RestoreLighting, available, native)]);
            }).ToArray();
            result.Add(new(Id(provider, nativeId), provider, Short(device.Name), device.IsMemory ? DeviceKind.Memory : DeviceKind.Controller,
                IdentityConfidence.ConnectionBound, nativeId, native ? device.IsMemory ? "ene-auda0-e6k5-0101-v1" : "asrock-usb-26ce-01a2-v1" : null,
                [native ? device.IsMemory ? "smbus:amd-port0" : "usb:" + Id(provider, nativeId) : "openrgb:localhost:" + settings.Motherboard.OpenRgbPort], endpoints));
        }
        return result.ToArray();
    }
    private static bool ProfileAvailable(AutoProfile profile, FanLimits limits, SensorReading[] sensors, TimeProvider? time)
        => FanPolicyEngine.ValidateProfile(profile, limits) is null && new[] { profile.Cpu, profile.Gpu }.OfType<SensorCurve>().All(curve =>
            sensors.Count(s => s.Id == curve.SensorId) == 1 && sensors.Single(s => s.Id == curve.SensorId) is { } sensor &&
            DashboardDisplay.Fresh(sensor.Timestamp, time) && MotherboardDisplay.IsUsable("temperature", sensor.Celsius) && sensor.Celsius < curve.Overheat);
    private static string HardwareId(string sensorId)
    {
        var separator = sensorId.LastIndexOf("/temperature/", StringComparison.Ordinal);
        return separator > 0 ? sensorId[..separator] : sensorId;
    }
}
