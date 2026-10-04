using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceControlCapacityTests
{
    internal static DeviceConfigurationPackage Package(int cooling, int lighting, int leds = 1)
    {
        var package = ServiceRuntimeBootstrap.MonitoringConfiguration(new('a', 32));
        var fans = Enumerable.Range(0, cooling).Select(i => new EndpointReference("controls", "fan-" + i)).ToArray();
        var lights = Enumerable.Range(0, lighting).Select(i => new EndpointReference("controls", "led-" + i)).ToArray();
        return package with {
            Devices = new(2, [new("controls", "制御機器", "test", DeviceKind.Controller,
                [.. fans.Select(t => new RegisteredEndpoint(t.EndpointId, t.EndpointId, EndpointKind.Cooling)),
                 .. lights.Select(t => new RegisteredEndpoint(t.EndpointId, t.EndpointId, EndpointKind.Lighting, Setup: new(LedCount: leds, LedCountConfirmed: true)))])]),
            Cooling = package.Cooling with { Endpoints = fans.Select(t => new CoolingEndpointConfiguration(t, true, new(30, 100, 70, CoolingExitAction.FixedDuty, []), [])).ToArray() },
            Lighting = package.Lighting with { Endpoints = lights.Select(t => new LightingEndpointConfiguration(t, true, new(LightingExitAction.Off))).ToArray() }
        };
    }

    [Theory]
    [InlineData(64, 128, 32, null)] [InlineData(65, 0, 1, "64")]
    [InlineData(0, 129, 1, "128")] [InlineData(0, 2, 2049, "4096")]
    [InlineData(0, 1, 4096, null)] [InlineData(0, 0, 1, null)]
    public void CompleteConfigurationEnforcesSeparateReservationsAndAggregateLeds(int cooling, int lighting, int leds, string? error)
    {
        var package = Package(cooling, lighting, leds);
        if (error is not null)
        {
            Assert.Contains(error, Assert.Throws<IOException>(() => DeviceConfigurationCodec.Validate(package)).Message);
            return;
        }
        DeviceConfigurationCodec.Validate(package);
        var restored = DeviceConfigurationCodec.DeserializePackage(DeviceConfigurationCodec.SerializePackage(package));
        Assert.Equal(new DeviceControlUsage(cooling, lighting, lighting * leds, 0), DeviceControlCapacity.Count(restored.Devices, restored.Cooling, restored.Lighting));
        foreach (var portable in new[] { false, true })
        {
            var archive = DeviceSettingsArchives.Read(DeviceSettingsArchives.Write(DeviceSettingsArchives.Create(package, portable)));
            Assert.Equal(lighting * leds, DeviceControlCapacity.Count(archive.Configuration.Devices, archive.Configuration.Cooling, archive.Configuration.Lighting).Leds);
        }
    }

    [Fact] public void MonitoringAndDormantReferencesDoNotReserveControlSlotsOrLeds()
    {
        var package = Package(100, 140, 4096);
        package = package with { Cooling = package.Cooling with { Endpoints = [] }, Lighting = package.Lighting with { Endpoints = [] },
            Dashboard = new(2, package.Devices.Devices[0].Endpoints.Where(e => e.Kind == EndpointKind.Cooling).Select(e =>
                new DeviceDashboardItem(new("controls", e.Id), DeviceFeature.Rpm, e.Name, "fan")).ToArray()) };
        DeviceConfigurationCodec.Validate(package);
        Assert.Equal(new DeviceControlUsage(0, 0, 0, 0), DeviceControlCapacity.Count(package.Devices, package.Cooling, package.Lighting));
        Assert.Equal(100, package.Dashboard.Items.Length);
    }

    [Fact] public void UnconfirmedAndUnboundSettingsStillReserveCapacityWithoutInventingUnknownLedCounts()
    {
        var package = Package(64, 128, 32);
        package = package with {
            Cooling = package.Cooling with { Endpoints = package.Cooling.Endpoints.Select(e => e with { WiringConfirmed = false }).ToArray() },
            Lighting = package.Lighting with { Endpoints = package.Lighting.Endpoints!.Select(e => e with { WiringConfirmed = false }).ToArray() },
            Devices = package.Devices with { Devices = package.Devices.Devices.Select(d => d with { Endpoints = d.Endpoints.Select(e =>
                e.Setup is { } setup ? e with { Setup = setup with { LedCountConfirmed = false } } : e).ToArray() }).ToArray() }
        };
        Assert.Equal(new DeviceControlUsage(64, 128, 4096, 0), DeviceControlCapacity.Count(package.Devices, package.Cooling, package.Lighting));
        var missing = package with { Devices = package.Devices with { Devices = package.Devices.Devices.Select(d => d with {
            Endpoints = d.Endpoints.Select(e => e.Id == "led-0" ? e with { Setup = null } : e).ToArray() }).ToArray() } };
        DeviceConfigurationCodec.Validate(missing);
        Assert.Equal(new DeviceControlUsage(64, 128, 4064, 1), DeviceControlCapacity.Count(missing.Devices, missing.Cooling, missing.Lighting));
    }

    private static DeviceWorkspaceSnapshot Snapshot(DeviceConfigurationPackage package, EndpointReference target, bool cooling)
    {
        var binding = new PhysicalDeviceBinding("controls", "physical-controls", "test", new('b', 64));
        return new(new(new(new('c', 32), 1, [new(new("controls", "test", "制御機器", DeviceKind.Controller, IdentityConfidence.SerialBound,
            "physical-controls", "test", ["controls"], [new(target.EndpointId, "端子", cooling ? EndpointKind.Cooling : EndpointKind.Lighting, [])]), 1, true)], 1), [], false, null),
            new(new('d', 32), package, [binding], [new(target, 1, cooling ? new(target.EndpointId, [CoolingExitAction.FixedDuty], []) :
                new(target.EndpointId, [], [LightingExitAction.Off], 1, 4096))]), 0, true, null, Session: new('e', 32));
    }

    [Fact] public void SetupDraftRejectsOnlyAdditionsBeyondLimitAndReplacementDoesNotDoubleCount()
    {
        var config = Package(64, 128, 32);
        var fan = new DeviceEndpointSetupDraft(Snapshot(config, new("controls", "fan-new"), true), new("controls", "fan-new"));
        fan.Change(fan.Values with { Confirmed = true }); Assert.Contains("64", fan.Problem);
        Assert.Throws<InvalidOperationException>(() => fan.Capture());
        var existingFan = new DeviceEndpointSetupDraft(Snapshot(config, new("controls", "fan-0"), true), new("controls", "fan-0"));
        existingFan.Change(existingFan.Values with { Confirmed = true }); Assert.Null(existingFan.Problem); Assert.NotNull(existingFan.Capture());
        var light = new DeviceEndpointSetupDraft(Snapshot(config, new("controls", "led-new"), false), new("controls", "led-new"));
        light.Change(light.Values with { Confirmed = true, LedCount = 1 }); Assert.Contains("128", light.Problem);
        var replacement = new DeviceEndpointSetupDraft(Snapshot(config, new("controls", "led-0"), false), new("controls", "led-0"));
        replacement.Change(replacement.Values with { Confirmed = true, LedCount = 33 }); Assert.Contains("4096", replacement.Problem);
        replacement.Change(replacement.Values with { LedCount = 32 }); Assert.Null(replacement.Problem);
        Assert.Equal(4096, DeviceControlCapacity.Count(replacement.Capture().Configuration.Devices, config.Cooling, config.Lighting).Leds);
        replacement.Change(replacement.Values with { LedCount = 16 }); Assert.NotNull(replacement.Capture());
    }

    [Fact] public void RemovingDisconnectedConfigurationFreesReservationButPreservesNamesGroupsScenesAndMonitoring()
    {
        var config = Package(64, 128, 32); var target = new EndpointReference("controls", "led-0");
        var scene = new DeviceLightingScene("blue", "ブルー", [new(target, new("fixed", [new(0, 0, 255)]))], []);
        config = config with { Lighting = config.Lighting with { Scenes = [scene], Groups = [new("case", "ケース", EndpointKind.Lighting, [target])], Names = [new(target, "正面")] } };
        var snapshot = Snapshot(config, target, false);
        snapshot = snapshot with { State = snapshot.State with { Inventory = snapshot.State.Inventory with { Devices = [] } } };
        var edit = DeviceControlSetupRemoval.Capture(snapshot, target);
        Assert.True(edit.Removing); Assert.True(edit.Matches(snapshot)); Assert.Equal(0, edit.DeviceGeneration);
        Assert.Equal(new DeviceControlUsage(64, 127, 4064, 0), DeviceControlCapacity.Count(edit.Configuration.Devices, edit.Configuration.Cooling, edit.Configuration.Lighting));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(config.Lighting.Scenes, Contract.Json), System.Text.Json.JsonSerializer.Serialize(edit.Configuration.Lighting.Scenes, Contract.Json));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(config.Lighting.Groups, Contract.Json), System.Text.Json.JsonSerializer.Serialize(edit.Configuration.Lighting.Groups, Contract.Json));
        Assert.Equal(config.Lighting.Names, edit.Configuration.Lighting.Names); Assert.Equal(config.Dashboard.Items, edit.Configuration.Dashboard.Items);
        Assert.Equal(config.Devices.Devices[0].Endpoints.Length, edit.Configuration.Devices.Devices[0].Endpoints.Length);
        var saved = edit.Configuration.Devices.Devices[0].Endpoints.Single(e => e.Id == target.EndpointId);
        Assert.Equal(32, saved.Setup!.LedCount); Assert.False(saved.Setup.LedCountConfirmed);
        Assert.True(config.Devices.Devices[0].Endpoints.Single(e => e.Id == target.EndpointId).Setup!.LedCountConfirmed);
        Assert.Contains(edit.Changes, s => s.Contains("この端子の照明だけ") && s.Contains("ファンとほかの照明は継続"));
        Assert.Contains("128", DeviceControlCapacity.ReplacementProblem(config.Devices, config.Cooling, config.Lighting, new("controls", "new"), false, 32));
        Assert.Null(DeviceControlCapacity.ReplacementProblem(edit.Configuration.Devices, edit.Configuration.Cooling, edit.Configuration.Lighting, new("controls", "new"), false, 32));
        Assert.False(edit.Matches(snapshot with { Configuration = snapshot.Configuration with { Generation = new('f', 32) } }));
        Assert.False(edit.Matches(snapshot with { State = snapshot.State with { Inventory = snapshot.State.Inventory with { TopologyRevision = 2 } } }));
    }
}
