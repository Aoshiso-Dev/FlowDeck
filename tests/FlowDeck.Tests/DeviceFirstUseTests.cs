using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceEndpointSetupTests
{
    [Fact] public async Task NormalFirstUseReadsAllKnownLightsWithoutSavingApprovalOrAnyOutput()
    {
        using var dir = new ServiceTestDirectory();
        await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true, lightingPorts: 3);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var before = client.Snapshot!.Configuration.Configuration; var generation = client.Snapshot.Configuration.Generation;
        var result = await client.PrepareFirstUseAsync();
        Assert.Null(result.Deferred); Assert.Equal(6, result.PreparedLighting);
        var snapshot = client.Snapshot!; var after = snapshot.Configuration.Configuration;
        Assert.Equal(SettingsTransfer.Hash(before), SettingsTransfer.Hash(after));
        Assert.Equal(generation, snapshot.Configuration.Generation);
        Assert.Empty(after.Bindings.Devices); Assert.Empty(after.Lighting.Endpoints ?? []); Assert.Empty(after.Cooling.Endpoints);
        var lights = snapshot.State.Inventory.Devices.SelectMany(d => d.Device.Endpoints).Where(e => e.Kind == EndpointKind.Lighting).ToArray();
        Assert.Equal(6, lights.Length);
        Assert.All(lights, e => Assert.True(e.Capabilities.Single(c => c.Feature == DeviceFeature.FixedColor).Usable));
        Assert.All(snapshot.State.Endpoints, e => Assert.False(e.Owned));
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
        Assert.Equal(0, (await client.PrepareFirstUseAsync()).PreparedLighting);
        await client.RefreshAsync(true); Assert.Equal(generation, client.Snapshot!.Configuration.Generation);
    }

    [Fact] public async Task ReadingAutomaticLightingReadinessPreservesAnAlreadyRunningFan()
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

    [Fact] public async Task FirstUseDoesNotRestoreRemovedSettingsOrWriteKnownLighting()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var target = new EndpointReference(demo.DeviceIds[0], "led1");
        await client.ApplySetupAsync(await client.PreviewSetupAsync(DeviceControlSetupRemoval.Capture(client.Snapshot!, target)));
        var before = SettingsTransfer.Hash(client.Snapshot!.Configuration.Configuration);
        Assert.Equal(2, (await client.PrepareFirstUseAsync()).PreparedLighting);
        Assert.Equal(before, SettingsTransfer.Hash(client.Snapshot!.Configuration.Configuration));
        Assert.False(new DeviceEndpointSetupDraft(client.Snapshot, target).Values.Confirmed);
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
    }

    [Fact] public async Task FirstUseRefreshAfterDisconnectionDoesNotCreateConfigurationOrControlCooling()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var before = SettingsTransfer.Hash(client.Snapshot!.Configuration.Configuration);
        demo.Hubs[0].Disconnected = true; await demo.Runtime.PollAsync();
        Assert.Equal(1, (await client.PrepareFirstUseAsync()).PreparedLighting);
        Assert.Equal(before, SettingsTransfer.Hash(client.Snapshot!.Configuration.Configuration));
        Assert.All(client.Snapshot.State.Inventory.Devices.SelectMany(d => d.Device.Endpoints).Where(e => e.Kind == EndpointKind.Cooling),
            e => Assert.False(e.Capabilities.Single(c => c.Feature == DeviceFeature.FixedDuty).Usable));
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
    }

    [Fact] public async Task EmptyDashboardShowsFreshReadingsWithoutSavingOrControllingHardware()
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
        Assert.Single(DeviceDashboard.VisibleItems(config.Devices, new(2, [readings[0]]), snapshot.State, 0, true));
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
    }
}
