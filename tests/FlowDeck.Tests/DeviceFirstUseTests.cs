using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceEndpointSetupTests
{
    [Fact] public async Task NormalFirstUsePreparesAllKnownLightsWithoutPortInteractionOrAnyOutput()
    {
        using var dir = new ServiceTestDirectory();
        await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true, lightingPorts: 3);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var before = client.Snapshot!.Configuration.Configuration;
        var result = await client.PrepareFirstUseAsync();
        Assert.Null(result.Deferred); Assert.Equal(6, result.PreparedLighting);
        var snapshot = client.Snapshot!; var after = snapshot.Configuration.Configuration;
        Assert.Equal(SettingsTransfer.Hash(before.Cooling), SettingsTransfer.Hash(after.Cooling));
        Assert.Equal(SettingsTransfer.Hash(before.Preferences), SettingsTransfer.Hash(after.Preferences));
        Assert.Equal(SettingsTransfer.Hash(before.Dashboard), SettingsTransfer.Hash(after.Dashboard));
        Assert.All(after.Lighting.Endpoints!, e => {
            Assert.True(e.WiringConfirmed); Assert.Equal(LightingExitAction.Off, e.Exit!.Action);
            var descriptor = snapshot.State.Inventory.Devices.Single(d => d.Device.Id == e.Target.DeviceId).Device.Endpoints.Single(p => p.Id == e.Target.EndpointId);
            Assert.True(descriptor.Capabilities.Single(c => c.Feature == DeviceFeature.FixedColor).Usable);
        });
        Assert.Empty(after.Cooling.Endpoints);
        Assert.All(snapshot.State.Endpoints, e => Assert.False(e.Owned));
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
        var generation = snapshot.Configuration.Generation;
        Assert.Equal(0, (await client.PrepareFirstUseAsync()).PreparedLighting);
        await client.RefreshAsync(true);
        Assert.Equal(generation, client.Snapshot!.Configuration.Generation);
    }

    [Fact] public async Task AutomaticLightingPreparationPreservesAnAlreadyRunningFan()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var fan = new EndpointReference(demo.DeviceIds[0], "fan1");
        await client.ApplySetupAsync(await client.PreviewSetupAsync(Fan(client, fan.DeviceId).Capture()));
        await Duty(client, fan, 62);
        var cooling = SettingsTransfer.Hash(client.Snapshot!.Configuration.Configuration.Cooling); var writes = demo.Hubs[0].Writes;
        Assert.Equal(2, (await client.PrepareFirstUseAsync()).PreparedLighting);
        Assert.Equal(cooling, SettingsTransfer.Hash(client.Snapshot!.Configuration.Configuration.Cooling));
        Assert.True(client.Snapshot.State.Endpoints.Single(e => e.Target == fan).Owned);
        Assert.Equal(62, client.Snapshot.State.Endpoints.Single(e => e.Target == fan).RequestedDuty);
        Assert.Equal(writes, demo.Hubs[0].Writes); Assert.All(demo.Hubs, h => Assert.Equal(0, h.LightingWrites));
    }

    [Fact] public async Task AutomaticPreparationDoesNotReapproveRemovedLighting()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var target = new EndpointReference(demo.DeviceIds[0], "led1");
        await client.ApplySetupAsync(await client.PreviewSetupAsync(DeviceControlSetupRemoval.Capture(client.Snapshot!, target)));
        var before = SettingsTransfer.Hash(client.Snapshot!.Configuration.Configuration);
        Assert.Equal(0, (await client.PrepareFirstUseAsync()).PreparedLighting);
        Assert.Equal(before, SettingsTransfer.Hash(client.Snapshot!.Configuration.Configuration));
        Assert.False(new DeviceEndpointSetupDraft(client.Snapshot, target).Values.Confirmed);
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
    }

    [Theory] [InlineData("connection-only")] [InlineData("unknown-count")] [InlineData("mismatched-binding")]
    public async Task AutomaticLaunchLeavesUncertainLightingOptional(string problem)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var snapshot = client.Snapshot!;
        Assert.Equal(2, DeviceDetectedLightingSetup.AutomaticTargets(snapshot).Length);
        if (problem == "connection-only") snapshot = snapshot with { State = snapshot.State with { Inventory = snapshot.State.Inventory with {
            Devices = snapshot.State.Inventory.Devices.Select(d => d with { Device = d.Device with { IdentityConfidence = IdentityConfidence.ConnectionBound } }).ToArray() } } };
        if (problem == "unknown-count") snapshot = snapshot with { Configuration = snapshot.Configuration with {
            Setup = snapshot.Configuration.Setup!.Select(c => c with { Options = c.Options with { DetectedLeds = null } }).ToArray() } };
        if (problem == "mismatched-binding") snapshot = snapshot with { Configuration = snapshot.Configuration with { Configuration = snapshot.Configuration.Configuration with {
            Bindings = snapshot.Configuration.Configuration.Bindings with { Devices = snapshot.Configuration.Candidates.Select(b => b with { Fingerprint = new('f', 64) }).ToArray() } } } };
        Assert.Empty(DeviceDetectedLightingSetup.AutomaticTargets(snapshot));
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
    }

    [Fact] public async Task EmptyDashboardShowsFreshReadingsWithoutSavingOrAuthorizingControl()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var snapshot = client.Snapshot!;
        var config = snapshot.Configuration.Configuration; var before = SettingsTransfer.Hash(config);
        var readings = DeviceDashboard.VisibleItems(config.Devices, config.Dashboard, snapshot.State, snapshot.ElapsedSeconds, true);
        Assert.Equal(12, readings.Length); Assert.All(readings, i => Assert.True(i.Visible));
        Assert.Equal(before, SettingsTransfer.Hash(config)); Assert.Empty(config.Bindings.Devices); Assert.Empty(config.Cooling.Endpoints);
        Assert.Empty(DeviceDashboard.VisibleItems(config.Devices, config.Dashboard, snapshot.State, 3, true));
        Assert.Empty(DeviceDashboard.VisibleItems(config.Devices, config.Dashboard, snapshot.State, 0, false));
        var hidden = new DeviceDashboardConfiguration(2, [readings[0] with { Visible = false }]);
        Assert.Empty(DeviceDashboard.VisibleItems(config.Devices, hidden, snapshot.State, 0, true));
        var saved = new DeviceDashboardConfiguration(2, [readings[0]]);
        Assert.Single(DeviceDashboard.VisibleItems(config.Devices, saved, snapshot.State, 0, true));
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
    }
}
