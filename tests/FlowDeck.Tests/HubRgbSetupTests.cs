using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class HubRgbSetupTests
{
    [Fact] public async Task Three24LedPortsCanBeConfiguredDuringFanControlThenRestoreWhiteWithoutLosingFanExit()
    {
        using var dir = new ServiceTestDirectory();
        var hub = new HubLightingDriverTests.Hub {
            Transform = s => s with { Lighting = [new("led1", "RGB", 24), new("led2", "RGB", 24), new("led3", "RGB", 24)] }
        };
        await using var driver = await HubLightingDriverTests.Ready(hub, dir.Path);
        Assert.Equal(Outcome.Confirmed, (await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, Duty: 62), default)).Outcome);
        var fanWrites = hub.Fans.Writes;
        var ports = Enumerable.Range(1, 3).Select(n => new RegisteredEndpoint("led" + n, "LED" + n, EndpointKind.Lighting,
            Setup: new("RGB", 24, true))).ToArray();
        var settings = ports.Select(p => new LightingEndpointConfiguration(new(driver.DeviceId, p.Id), true,
            new(LightingExitAction.FixedColor, new(255, 255, 255)))).ToArray();
        driver.ConfigureLighting(driver.BindingCandidate, settings, ports);
        Assert.Equal(fanWrites, hub.Fans.Writes);
        var colors = new[] { new RgbColor(255, 0, 0), new RgbColor(0, 255, 0), new RgbColor(0, 0, 255) };
        for (var i = 0; i < ports.Length; i++)
        {
            Assert.True(driver.Observe()!.Endpoints.Single(e => e.Id == ports[i].Id).Capabilities.Single(c => c.Feature == DeviceFeature.FixedColor).Usable);
            var result = await driver.ApplyAsync(new(ports[i].Id, DeviceOperation.SetLighting, Lighting: new("fixed", [colors[i]], Brightness: 50)), default);
            Assert.Equal(Outcome.WrittenUnverified, result.Outcome);
            Assert.Equal(colors[i].Scale(.5), hub.Colors[ports[i].Id].Colors.Single());
        }
        Assert.Equal(fanWrites, hub.Fans.Writes); Assert.Equal(3, hub.Writes.Count);
        // The saved fan obligation survives the lighting-only configuration call.
        Assert.Single(new HubEndpointRecoveryJournal(dir.Path, driver.DeviceId).Pending);
        await driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default);
        Assert.Equal(65, (await hub.Fans.ReadAsync()).Channels.Single(c => c.Id == "fan1").Duty);
        Assert.All(ports, p => Assert.Equal(new RgbColor(255, 255, 255), hub.Colors[p.Id].Colors.Single()));
        Assert.Empty(new HubEndpointRecoveryJournal(dir.Path, driver.DeviceId).Pending);
        Assert.Empty(new HubLightingRecoveryJournal(dir.Path, driver.DeviceId).Pending);
    }
}
