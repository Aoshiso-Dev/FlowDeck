using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class LegacyDeviceCatalogTests
{
    private const string Scope = "b9b8f0e5454d4b8396cd0c0d8d230c50";
    [Fact]
    public async Task HubAbsenceDoesNotHideSensorsOrBoardLighting()
    {
        await using var f = new Fixture(DemoHub.CreateSettings() with { Hardware = new() }); await f.Core.TickAsync();
        var devices = LegacyDeviceCatalog.Build(Scope, f.Core.Settings, f.Core.Snapshot, new DemoMotherboard().Board, DemoMotherboard.Rgb, true);
        Assert.DoesNotContain(devices, d => d.Provider == "nzxt-control-hub");
        Assert.Contains(devices, d => d.Kind == DeviceKind.Processor);
        Assert.Contains(devices, d => d.Kind == DeviceKind.Motherboard);
        Assert.Contains(devices, d => d.Endpoints.Any(e => e.Kind == EndpointKind.Lighting));
        new DeviceRegistry().Reconcile(devices);
    }
    [Fact]
    public async Task UnknownMotherboardHasRpmButNoCoolingWriteCapability()
    {
        await using var f = new Fixture(); var board = new DemoMotherboard().Board with { Name = "Unknown board" };
        var device = Assert.Single(LegacyDeviceCatalog.Build(Scope, f.Core.Settings, f.Core.Snapshot, board, new([]), true));
        var fans = device.Endpoints.Where(e => e.Kind == EndpointKind.Cooling).ToArray(); Assert.NotEmpty(fans);
        Assert.All(fans, e => Assert.Equal(ImplementationSupport.Supported, e.Capabilities.Single(c => c.Feature == DeviceFeature.Rpm).Support));
        Assert.All(fans, e => Assert.All(e.Capabilities.Where(c => c.Feature != DeviceFeature.Rpm), c => Assert.False(c.Usable)));
    }
    [Fact]
    public async Task ZeroRpmIsValidButStaleTelemetryAndIncompleteCurvesAreNotReady()
    {
        await using var f = new Fixture(); await f.Core.OpenAsync(DemoHub.Identity); await f.Core.TickAsync();
        var snapshot = f.Core.Snapshot with { Channels = [new("fan1", Presence.Present, 0, 0, "PWM")] };
        DeviceObservation Hub(AppSettings settings) => LegacyDeviceCatalog.Build(Scope, settings, snapshot, BoardSnapshot.Empty, new([]), true, f.Clock)
            .Single(d => d.Provider == "nzxt-control-hub");
        Assert.True(Hub(f.Core.Settings).Endpoints[0].Capabilities.Single(c => c.Feature == DeviceFeature.Rpm).Usable);
        Assert.True(Hub(f.Core.Settings).Endpoints[0].Capabilities.Single(c => c.Feature == DeviceFeature.AutomaticDuty).Usable);
        Assert.False(Hub(f.Core.Settings with { Profiles = [new("incomplete", "Incomplete")] }).Endpoints[0].Capabilities.Single(c => c.Feature == DeviceFeature.AutomaticDuty).Usable);
        f.Clock.Advance(4);
        Assert.False(Hub(f.Core.Settings).Endpoints[0].Capabilities.Single(c => c.Feature == DeviceFeature.Rpm).Usable);
        Assert.False(Hub(f.Core.Settings).Endpoints[0].Capabilities.Single(c => c.Feature == DeviceFeature.FixedDuty).Usable);
    }
    [Fact]
    public async Task OpenRgbInventoryCannotAdvertiseDirectWrites()
    {
        await using var f = new Fixture();
        var inventory = DemoMotherboard.Rgb;
        var devices = LegacyDeviceCatalog.Build(Scope, f.Core.Settings with { Motherboard = new(OpenRgbEnabled: true) }, f.Core.Snapshot, BoardSnapshot.Empty, inventory, true);
        var device = Assert.Single(devices); Assert.Equal("openrgb", device.Provider);
        Assert.All(device.Endpoints, e => Assert.All(e.Capabilities, c => Assert.False(c.Usable)));
    }
    [Fact]
    public async Task RgbOnlyMemoryDoesNotInventTemperatureSensorsOrPhysicalSlotNames()
    {
        await using var f = new Fixture();
        var rgb = new RgbInventory([new(0, "Memory 1", "G.SKILL", "", "", "SMBus 0x71", ["Static"], 0,
            [new("Module", 1, 8, 8, 8, "Static", 0, new(1, 0, 0, 255))], MemoryLighting.Identity(0x71))]);
        var device = Assert.Single(LegacyDeviceCatalog.Build(Scope, f.Core.Settings, f.Core.Snapshot, BoardSnapshot.Empty, rgb, true));
        Assert.Equal(DeviceKind.Memory, device.Kind);
        Assert.All(device.Endpoints, e => Assert.Equal(EndpointKind.Lighting, e.Kind));
        Assert.DoesNotContain(device.Endpoints.SelectMany(e => e.Capabilities), c => c.Feature == DeviceFeature.Temperature);
        Assert.DoesNotContain("A2", device.Name); Assert.DoesNotContain("B2", device.Name);
        var withSdk = Assert.Single(LegacyDeviceCatalog.Build(Scope, f.Core.Settings with { Motherboard = new(OpenRgbEnabled: true) },
            f.Core.Snapshot, BoardSnapshot.Empty, rgb, true));
        Assert.Equal("ene-memory", withSdk.Provider);
    }
}
