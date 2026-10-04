using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceEndpointSetupTests
{
    private static async Task ConfigureHubLights(DeviceWorkspaceClient client, string device)
    {
        foreach (var endpoint in new[] { "led2", "led3" })
            Assert.Equal(Outcome.Confirmed, (await client.ApplySetupAsync(await client.PreviewSetupAsync(Light(client, new(device, endpoint)).Capture()))).Outcome);
    }

    [Theory]
    [InlineData("fixed", false)] [InlineData("fixed", true)]
    [InlineData("breathing", false)] [InlineData("breathing", true)]
    public async Task LightingPortSaveAndRemovalRetainSiblingOutputsOwnersSyncAndRecovery(string effect, bool remove)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, lightingPorts: 3);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var device = demo.DeviceIds[0]; await ConfigureHubLights(client, device);
        var selected = new EndpointReference(device, "led1");
        var siblings = new[] { new EndpointReference(device, "led2"), new EndpointReference(device, "led3"), new EndpointReference(demo.DeviceIds[1], "led1") };
        var fan = new EndpointReference(device, "fan1"); await Duty(client, fan, 62);
        await LightProgram(client, [selected], "fixed");
        await LightProgram(client, siblings, effect);
        await demo.Runtime.TickLightingAsync(); await client.RefreshAsync(true);
        var before = siblings.ToDictionary(t => t, t => client.Snapshot!.State.Endpoints.Single(e => e.Target == t));
        var fanWrites = demo.Hubs.Select(h => h.Writes).ToArray();
        var edit = remove ? DeviceControlSetupRemoval.Capture(client.Snapshot!, selected) : Light(client, selected).Capture();
        Assert.Equal(Outcome.Confirmed, (await client.ApplySetupAsync(await client.PreviewSetupAsync(edit))).Outcome);
        Assert.False(client.Snapshot!.State.Endpoints.Single(e => e.Target == selected).Owned);
        Assert.Equal("off", demo.Hubs[0].LightEffects["led1"].Mode); // Previously saved exit, not newly entered white.
        await Task.Delay(140); await demo.Runtime.TickLightingAsync(); await client.RefreshAsync(true);
        foreach (var sibling in siblings)
        {
            var after = client.Snapshot!.State.Endpoints.Single(e => e.Target == sibling);
            Assert.True(after.Owned); Assert.Equal(before[sibling].PolicyRevision, after.PolicyRevision);
            Assert.True(DeviceLighting.SameProgram(before[sibling].RequestedLighting!, after.RequestedLighting!));
            if (effect == "breathing") { Assert.True(after.Lighting!.Running); Assert.Null(after.Lighting.Fault); Assert.Equal(before[sibling].Lighting!.GroupId, after.Lighting.GroupId); Assert.True(after.Lighting.Frames > before[sibling].Lighting!.Frames); }
            else Assert.True(DeviceLighting.SameProgram(before[sibling].ReportedLighting!, after.ReportedLighting!));
        }
        Assert.Equal(fanWrites, demo.Hubs.Select(h => h.Writes));
        Assert.Equal(62, client.Snapshot!.State.Endpoints.Single(e => e.Target == fan).ReportedDuty);
        Assert.Equal(new[] { "led2", "led3" }, new HubLightingRecoveryJournal(dir.Path, device).Pending.Select(p => p.Channel).Order().ToArray());
        Assert.True((await client.ReleaseAsync()).Success);
        Assert.Empty(new HubLightingRecoveryJournal(dir.Path, device).Pending);
        Assert.Equal(new RgbColor(255,255,255), demo.Hubs[0].LightEffects["led2"].Colors.Single());
        Assert.Equal(new RgbColor(255,255,255), demo.Hubs[0].LightEffects["led3"].Colors.Single());
    }

    [Fact]
    public async Task SynchronizedSiblingSetupRejectsBeforeOutputAndSelectedDetachAllowsSaving()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, lightingPorts: 3);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var device=demo.DeviceIds[0]; await ConfigureHubLights(client,device);
        var selected=new EndpointReference(device,"led1"); var sibling=new EndpointReference(device,"led2");
        await LightProgram(client,[selected,sibling],"breathing");
        var writes=demo.Hubs[0].LightingNonFrameWrites;
        var error=await Assert.ThrowsAsync<IOException>(()=>client.PreviewSetupAsync(Light(client,selected).Capture()));
        Assert.Contains("照明同期中",error.Message); Assert.Equal(writes,demo.Hubs[0].LightingNonFrameWrites);
        await client.RefreshAsync(true); var snapshot=client.Snapshot!; var draft=new DeviceLightingDraft();
        draft.Update(snapshot.State,snapshot.Configuration.Generation,snapshot.Configuration.Configuration.Lighting,0,true); draft.Select([selected]);
        Assert.True((await client.ApplyLightingAsync(draft.CaptureStop())).Outcome is Outcome.Confirmed or Outcome.WrittenUnverified);
        Assert.Equal(Outcome.Confirmed,(await client.ApplySetupAsync(await client.PreviewSetupAsync(Light(client,selected).Capture()))).Outcome);
        await Task.Delay(140); await demo.Runtime.TickLightingAsync(); await client.RefreshAsync(true);
        var after=client.Snapshot!.State.Endpoints.Single(e=>e.Target==sibling);
        Assert.True(after.Owned); Assert.True(after.Lighting!.Running); Assert.Null(after.Lighting.Fault);
    }

    [Fact]
    public async Task FailedSelectedLightingExitRetainsSiblingRecoveryAndRetriesOnlySelectedPort()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, lightingPorts: 3);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var device = demo.DeviceIds[0]; await ConfigureHubLights(client, device);
        var selected = new EndpointReference(device, "led1"); var sibling = new EndpointReference(device, "led2");
        await LightProgram(client, [selected, sibling], "fixed");
        var before = client.Snapshot!.State.Endpoints.Single(e => e.Target == sibling);
        var preview = await client.PreviewSetupAsync(Light(client, selected).Capture());
        demo.Hubs[0].FailNextLightingWrite = true;
        await Assert.ThrowsAsync<IOException>(() => client.ApplySetupAsync(preview));
        var after = client.Snapshot!.State.Endpoints.Single(e => e.Target == sibling);
        Assert.True(after.Owned); Assert.Equal(before.PolicyRevision, after.PolicyRevision);
        Assert.True(DeviceLighting.SameProgram(before.ReportedLighting!, after.ReportedLighting!));
        Assert.Equal(new[] { "led1", "led2" }, new HubLightingRecoveryJournal(dir.Path, device).Pending.Select(p => p.Channel).Order());
        Assert.Equal(Outcome.Confirmed, (await client.ApplySetupAsync(await client.PreviewSetupAsync(Light(client, selected).Capture()))).Outcome);
        Assert.Null(client.Snapshot!.State.LifecycleError); Assert.True(client.Snapshot.State.Endpoints.Single(e => e.Target == sibling).Owned);
        Assert.Equal("led2", Assert.Single(new HubLightingRecoveryJournal(dir.Path, device).Pending).Channel);
        Assert.True((await client.ReleaseAsync()).Success); Assert.Empty(new HubLightingRecoveryJournal(dir.Path, device).Pending);
    }
}
