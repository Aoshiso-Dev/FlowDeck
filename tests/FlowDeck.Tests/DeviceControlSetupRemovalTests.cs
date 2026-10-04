using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceControlSetupRemovalTests
{
    private static Task<DeviceOperationResult> Duty(DeviceWorkspaceClient client, EndpointReference target, int duty)
    {
        var snapshot = client.Snapshot!; var state = snapshot.State.Endpoints.Single(e => e.Target == target);
        return client.ApplyCoolingAsync(new(new(snapshot.State.Inventory.Epoch, snapshot.State.Inventory.TopologyRevision, snapshot.Configuration.Generation,
            new(target, state.Generation, state.PolicyRevision)), DeviceOperation.SetDuty, duty));
    }

    [Fact] public async Task RemovingLiveControlUsesOldExitPreservesRpmAndRequiresFreshSetupWithoutAutomaticRestart()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var target = new EndpointReference(demo.DeviceIds[0], "fan1");
        Assert.Equal(Outcome.Confirmed, (await Duty(client, target, 62)).Outcome);
        var config = client.Snapshot!.Configuration.Configuration; var writes = demo.Hubs[0].Writes;
        var edit = DeviceControlSetupRemoval.Capture(client.Snapshot, target); var preview = await client.PreviewSetupAsync(edit);
        Assert.Equal(writes, demo.Hubs[0].Writes); Assert.True(client.Snapshot!.State.Endpoints.Single(e => e.Target == target).Owned);
        Assert.Equal(Outcome.Confirmed, (await client.ApplySetupAsync(preview)).Outcome);
        Assert.Equal(writes + 1, demo.Hubs[0].Writes); Assert.Equal(70, (await demo.Hubs[0].ReadAsync()).Channels.Single(c => c.Id == "fan1").Duty);
        var after = client.Snapshot!.Configuration.Configuration;
        Assert.DoesNotContain(after.Cooling.Endpoints, e => e.Target == target); Assert.Equal(config.Dashboard.Items, after.Dashboard.Items);
        Assert.Contains(after.Devices.Devices.Single(d => d.Id == target.DeviceId).Endpoints, e => e.Id == target.EndpointId);
        Assert.NotEqual(Outcome.Confirmed, (await Duty(client, target, 60)).Outcome);
        Assert.Equal(writes + 1, demo.Hubs[0].Writes);
        var setup = new DeviceEndpointSetupDraft(client.Snapshot!, target); Assert.False(setup.Values.Confirmed);
        setup.Change(setup.Values with { Confirmed = true, Safety = new(35, 100, 75, CoolingExitAction.FixedDuty, []) });
        Assert.Equal(Outcome.Confirmed, (await client.ApplySetupAsync(await client.PreviewSetupAsync(setup.Capture()))).Outcome);
        Assert.Equal(writes + 1, demo.Hubs[0].Writes); Assert.False(client.Snapshot!.State.Endpoints.Single(e => e.Target == target).Owned);
        var saved = client.Snapshot.Configuration.Configuration.Cooling.Endpoints.Single(e => e.Target == target);
        Assert.Null(saved.SavedPolicy); Assert.False(saved.ResumeOnStart);
        Assert.Equal(Outcome.Confirmed, (await Duty(client, target, 61)).Outcome);
        Assert.Equal(writes + 2, demo.Hubs[0].Writes); Assert.Equal(0, demo.Hubs[1].Writes);
    }

    [Fact] public async Task DisconnectedLightingCanBeRemovedAndReconnectionDoesNotRestoreWriteAuthority()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var target = new EndpointReference(demo.DeviceIds[1], "led1");
        var stale = await client.PreviewSetupAsync(DeviceControlSetupRemoval.Capture(client.Snapshot!, target));
        demo.Hubs[1].Disconnected = true; await demo.Runtime.PollAsync(); await client.RefreshAsync(true);
        await Assert.ThrowsAsync<IOException>(() => client.ApplySetupAsync(stale));
        var snapshot = client.Snapshot!; Assert.DoesNotContain(snapshot.Configuration.Setup!, c => c.Target == target);
        var edit = DeviceControlSetupRemoval.Capture(snapshot, target);
        Assert.Equal(Outcome.Confirmed, (await client.ApplySetupAsync(await client.PreviewSetupAsync(edit))).Outcome);
        var config = client.Snapshot!.Configuration.Configuration;
        Assert.DoesNotContain(config.Lighting.Endpoints!, e => e.Target == target);
        Assert.False(config.Devices.Devices.Single(d => d.Id == target.DeviceId).Endpoints.Single(e => e.Id == target.EndpointId).Setup!.LedCountConfirmed);
        Assert.Equal(24, DeviceControlCapacity.Count(config.Devices, config.Cooling, config.Lighting).Leds);
        demo.Hubs[1].Disconnected = false; await demo.Runtime.PollAsync(); await demo.Runtime.PollAsync(); await client.RefreshAsync(true);
        var capability = client.Snapshot!.State.Inventory.Devices.Single(d => d.Device.Id == target.DeviceId).Device.Endpoints.Single(e => e.Id == target.EndpointId)
            .Capabilities.Single(c => c.Feature == DeviceFeature.FixedColor);
        Assert.False(capability.Usable); Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
        var setup = new DeviceEndpointSetupDraft(client.Snapshot, target); Assert.False(setup.Values.Confirmed);
        Assert.Throws<InvalidOperationException>(() => setup.Capture());
    }

    [Fact] public async Task FailedExitKeepsControlSettingsAndRemovalCannotBeReportedAsSuccessful()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var target = new EndpointReference(demo.DeviceIds[0], "fan3");
        await Duty(client, target, 62); var before = client.Snapshot!.Configuration.Generation;
        var preview = await client.PreviewSetupAsync(DeviceControlSetupRemoval.Capture(client.Snapshot, target));
        demo.Hubs[0].FailNextFanWrite = true; await Assert.ThrowsAsync<IOException>(() => client.ApplySetupAsync(preview));
        Assert.Equal(before, client.Snapshot!.Configuration.Generation);
        Assert.Contains(client.Snapshot.Configuration.Configuration.Cooling.Endpoints, e => e.Target == target);
        Assert.NotNull(client.Snapshot.State.LifecycleError);
    }
}
