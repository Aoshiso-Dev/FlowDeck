using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceEndpointSetupTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static DeviceEndpointSetupDraft Fan(DeviceWorkspaceClient client, string id, string endpoint = "fan1")
    {
        var draft = new DeviceEndpointSetupDraft(client.Snapshot!, new(id, endpoint));
        draft.Change(draft.Values with { Confirmed = true, Safety = new(30,100,70,CoolingExitAction.FixedDuty,[]) }); return draft;
    }
    private static Task<DeviceOperationResult> Duty(DeviceWorkspaceClient client, EndpointReference target, int duty)
    {
        var snapshot = client.Snapshot!; var row = snapshot.State.Endpoints.Single(e => e.Target == target);
        return client.ApplyCoolingAsync(new(new(snapshot.State.Inventory.Epoch, snapshot.State.Inventory.TopologyRevision, snapshot.Configuration.Generation,
            new(target, row.Generation, row.PolicyRevision)), DeviceOperation.SetDuty, duty));
    }
    [Fact] public void DescriptorRejectsAmbiguousExitKindsInvalidEnumsAndInconsistentLedRanges()
    {
        var candidate = new DeviceSetupCandidate(new("a", "fan1"), 1, new("fan1", [CoolingExitAction.FixedDuty], []));
        Assert.True(DeviceSetup.Valid(candidate));
        Assert.False(DeviceSetup.Valid(candidate with { Generation = 0 }));
        Assert.False(DeviceSetup.Valid(candidate with { Options = candidate.Options with { LightingExitActions = [LightingExitAction.Off] } }));
        Assert.False(DeviceSetup.Valid(candidate with { Options = candidate.Options with { CoolingExitActions = [(CoolingExitAction)99] } }));
        var light = candidate with { Options = new("fan1", [], [LightingExitAction.Off], 1, 24, 24) };
        Assert.True(DeviceSetup.Valid(light));
        Assert.False(DeviceSetup.Valid(light with { Options = light.Options with { DetectedLeds = 25 } }));
        Assert.False(DeviceSetup.Valid(light with { Options = light.Options with { MinimumLeds = 0 } }));
        var reply = new DeviceConfigurationReply(Outcome.Confirmed, "ok", Guid.NewGuid().ToString("N"), Setup: [candidate]);
        Assert.True(DeviceConfigurationProtocol.Valid(reply)); Assert.False(DeviceConfigurationProtocol.Valid(reply with { Setup = [candidate,candidate] }));
    }
    [Fact] public async Task InitialDiscoveryDescribesSetupWithoutBindingDevicesOrWritingOutputs()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        Assert.True(client.SupportsEndpointSetup); var snapshot = client.Snapshot!;
        Assert.Equal(12, snapshot.Configuration.Setup!.Length); Assert.Empty(snapshot.Configuration.Configuration.Bindings.Devices);
        Assert.All(snapshot.Configuration.Setup, c => Assert.True(DeviceSetup.Valid(c)));
        var light = snapshot.Configuration.Setup.First(c => c.Options.LightingExitActions.Length > 0);
        Assert.Equal("DEMO RGB", light.Options.Accessory); Assert.Equal(24, light.Options.DetectedLeds);
        Assert.DoesNotContain(LightingExitAction.RestoreOriginal, light.Options.LightingExitActions);
        Assert.All(demo.Hubs, h => { Assert.Equal(0,h.Writes); Assert.Equal(0,h.LightingWrites); });
    }
    [Fact] public async Task ConfirmationAndDeviceSpecificBoundsAreRequiredBeforePreview()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var draft = new DeviceEndpointSetupDraft(client.Snapshot!, new(demo.DeviceIds[0], "fan1"));
        Assert.Throws<InvalidOperationException>(() => draft.Capture());
        draft.Change(draft.Values with { Confirmed = true, Safety = new(50,40,70,CoolingExitAction.FixedDuty,[]) });
        Assert.Throws<InvalidOperationException>(() => draft.Capture());
        draft.Change(draft.Values with { Safety = new(30,100,null,CoolingExitAction.ReleaseToDevice,[]) });
        Assert.Throws<InvalidOperationException>(() => draft.Capture());
        var light = new DeviceEndpointSetupDraft(client.Snapshot!, new(demo.DeviceIds[0], "led1"));
        light.Change(light.Values with { Confirmed = true, LedCount = 23 }); Assert.Throws<InvalidOperationException>(() => light.Capture());
        light.Change(light.Values with { LedCount = 24, LightingExit = new(LightingExitAction.RestoreOriginal) }); Assert.Throws<InvalidOperationException>(() => light.Capture());
        light.Change(light.Values with { LightingExit = new(LightingExitAction.Off) }); Assert.NotNull(light.Capture());
    }
    [Theory] [InlineData("epoch")] [InlineData("configuration")] [InlineData("topology")] [InlineData("device")] [InlineData("binding")]
    public async Task PollingPreservesEditsAndAnyNewVersionRequiresExplicitReload(string change)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var snapshot = client.Snapshot!;
        var draft = Fan(client, demo.DeviceIds[0]); draft.Change(draft.Values with { Name = "ケース背面" });
        draft.Update(snapshot); Assert.Null(draft.Problem); Assert.Equal("ケース背面", draft.Values.Name);
        var inventory = snapshot.State.Inventory;
        var next = snapshot with { State = snapshot.State with { Inventory = inventory with {
            Epoch = change == "epoch" ? Guid.NewGuid().ToString("N") : inventory.Epoch,
            TopologyRevision = inventory.TopologyRevision + (change == "topology" ? 1 : 0),
            Devices = inventory.Devices.Select(d => change == "device" && d.Device.Id == draft.Candidate.Target.DeviceId ? d with { Generation = d.Generation + 1 } : d).ToArray() } },
            Configuration = snapshot.Configuration with { Generation = change == "configuration" ? Guid.NewGuid().ToString("N") : snapshot.Configuration.Generation,
                Candidates = change == "binding" ? [] : snapshot.Configuration.Candidates } };
        draft.Update(next); Assert.True(draft.Conflict); Assert.Equal("ケース背面", draft.Values.Name); Assert.Throws<InvalidOperationException>(() => draft.Capture());
        draft.Update(snapshot); Assert.True(draft.Conflict);
    }
    [Fact] public async Task AdoptingFirstFanEnablesOnlyThatEndpointWithoutStartingIt()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var target = new EndpointReference(demo.DeviceIds[0], "fan1");
        var before = client.Snapshot!.Configuration.Generation;
        var preview = await client.PreviewSetupAsync(Fan(client, target.DeviceId).Capture());
        Assert.Equal(before, client.Snapshot!.Configuration.Generation); Assert.All(demo.Hubs, h => Assert.Equal(0,h.Writes));
        Assert.Equal(Outcome.Confirmed, (await client.ApplySetupAsync(preview)).Outcome);
        var config = client.Snapshot!.Configuration.Configuration;
        Assert.Single(config.Bindings.Devices); Assert.Equal(target, Assert.Single(config.Cooling.Endpoints).Target);
        Assert.False(config.Cooling.Endpoints[0].ResumeOnStart); Assert.Null(config.Cooling.Endpoints[0].SavedPolicy);
        Assert.All(demo.Hubs, h => { Assert.Equal(0,h.Writes); Assert.Equal(0,h.LightingWrites); });
        Assert.Equal(Outcome.Confirmed, (await Duty(client, target, 62)).Outcome);
        Assert.NotEqual(Outcome.Confirmed, (await Duty(client, new(target.DeviceId, "fan2"), 62)).Outcome);
        Assert.Equal(0,demo.Hubs[1].Writes);
    }
    [Fact] public async Task SetupPreviewLeavesOwnedOutputRunningAndApplyUsesOldExitBeforeSavingNewLimits()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var target = new EndpointReference(demo.DeviceIds[0], "fan1");
        Assert.Equal(Outcome.Confirmed,(await Duty(client,target,62)).Outcome);
        var draft = Fan(client,target.DeviceId); draft.Change(draft.Values with { Safety = draft.Values.Safety! with { Exit = 80 } });
        var writes = demo.Hubs[0].Writes; var preview = await client.PreviewSetupAsync(draft.Capture());
        Assert.Equal(writes,demo.Hubs[0].Writes); Assert.True(client.Snapshot!.State.Endpoints.Single(e=>e.Target==target).Owned);
        await client.ApplySetupAsync(preview);
        Assert.False(client.Snapshot!.State.Endpoints.Single(e=>e.Target==target).Owned);
        Assert.Equal(70,(await demo.Hubs[0].ReadAsync()).Channels.Single(c=>c.Id=="fan1").Duty);
        Assert.Equal(80,client.Snapshot.Configuration.Configuration.Cooling.Endpoints.Single(e=>e.Target==target).Safety.Exit);
        Assert.Equal(writes+1,demo.Hubs[0].Writes); Assert.Equal(0,demo.Hubs[1].Writes);
    }
    [Fact] public async Task SavingOneFanKeepsOtherFansOwnedAtTheirExistingDuty()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var target = new EndpointReference(demo.DeviceIds[0], "fan1");
        var retained = new[] { new EndpointReference(target.DeviceId, "fan2"), new EndpointReference(demo.DeviceIds[1], "fan1") };
        await Duty(client, target, 62);
        foreach (var other in retained) Assert.Equal(Outcome.Confirmed, (await Duty(client, other, 56)).Outcome);
        var before = retained.Select(t => client.Snapshot!.State.Endpoints.Single(e => e.Target == t)).ToArray();
        var saved = retained.Select(t => client.Snapshot!.Configuration.Configuration.Cooling.Endpoints.Single(e => e.Target == t)).ToArray();
        var writes = demo.Hubs.Select(h => h.Writes).ToArray();
        await client.ApplySetupAsync(await client.PreviewSetupAsync(Fan(client, target.DeviceId).Capture()));
        Assert.False(client.Snapshot!.State.Endpoints.Single(e => e.Target == target).Owned);
        for (var i = 0; i < retained.Length; i++)
        {
            var after = client.Snapshot.State.Endpoints.Single(e => e.Target == retained[i]);
            Assert.True(after.Owned); Assert.Equal(56, after.RequestedDuty);
            Assert.Equal(before[i].PolicyRevision, after.PolicyRevision);
            Assert.Equal(SettingsTransfer.Hash(saved[i]), SettingsTransfer.Hash(client.Snapshot.Configuration.Configuration.Cooling.Endpoints.Single(e => e.Target == retained[i])));
        }
        Assert.Equal(writes[0] + 1, demo.Hubs[0].Writes); Assert.Equal(writes[1], demo.Hubs[1].Writes);
    }
    [Fact] public async Task ChangedConfigurationOrHotplugInvalidatesPreviewWithoutWritingNewSetup()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var target = new EndpointReference(demo.DeviceIds[0],"fan1");
        var preview = await client.PreviewSetupAsync(Fan(client,target.DeviceId).Capture());
        await Duty(client,target,63); var writes=demo.Hubs[0].Writes;
        await Assert.ThrowsAsync<IOException>(()=>client.ApplySetupAsync(preview)); Assert.Equal(writes,demo.Hubs[0].Writes);
        Assert.Equal(63,client.Snapshot!.State.Endpoints.Single(e=>e.Target==target).RequestedDuty);
        preview = await client.PreviewSetupAsync(Fan(client,target.DeviceId).Capture());
        demo.Hubs[1].Disconnected=true; await demo.Runtime.PollAsync();
        await Assert.ThrowsAsync<IOException>(()=>client.ApplySetupAsync(preview)); Assert.Equal(writes,demo.Hubs[0].Writes);
        demo.Hubs[1].Disconnected=false;
    }
    [Fact] public async Task FirstLightingSetupDoesNotLightAnythingAndUsesOnlyReportedAccessoryAndCount()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path,monitoringOnly:true);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var target = new EndpointReference(demo.DeviceIds[0],"led1");
        var draft = new DeviceEndpointSetupDraft(client.Snapshot!,target); draft.Change(draft.Values with { Confirmed=true });
        await client.ApplySetupAsync(await client.PreviewSetupAsync(draft.Capture()));
        var config=client.Snapshot!.Configuration.Configuration;
        var port=Assert.Single(Assert.Single(config.Devices.Devices).Endpoints);
        Assert.Equal(new EndpointSetup("DEMO RGB",24,true),port.Setup); Assert.Equal(target,Assert.Single(config.Lighting.Endpoints!).Target);
        Assert.Empty(config.Cooling.Endpoints); Assert.All(demo.Hubs,h=> { Assert.Equal(0,h.Writes); Assert.Equal(0,h.LightingWrites); });
        var actual=client.Snapshot.State.Inventory.Devices.Single(d=>d.Device.Id==target.DeviceId).Device;
        Assert.True(actual.Endpoints.Single(e=>e.Id==target.EndpointId).Capabilities.Single(c=>c.Feature==DeviceFeature.FixedColor).Usable);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RebindingADeviceDoesNotSilentlyReapproveItsOtherCoolingAndLightingPorts(bool missingBinding)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var snapshot=client.Snapshot!; var target=new EndpointReference(demo.DeviceIds[0],"fan1");
        var updated=snapshot with { Configuration=snapshot.Configuration with {
            Configuration=missingBinding ? snapshot.Configuration.Configuration with { Bindings=snapshot.Configuration.Configuration.Bindings with {
                Devices=snapshot.Configuration.Configuration.Bindings.Devices.Where(b=>b.DeviceId!=target.DeviceId).ToArray() } } : snapshot.Configuration.Configuration,
            Candidates=missingBinding ? snapshot.Configuration.Candidates : snapshot.Configuration.Candidates.Select(b=>b.DeviceId==target.DeviceId ? b with { Fingerprint=new('f',64) } : b).ToArray() } };
        var draft=new DeviceEndpointSetupDraft(updated,target); Assert.False(draft.Values.Confirmed); draft.Change(draft.Values with { Confirmed=true });
        var result=draft.Capture();
        Assert.True(result.Configuration.Cooling.Endpoints.Single(e=>e.Target==target).WiringConfirmed);
        Assert.All(result.Configuration.Cooling.Endpoints.Where(e=>e.Target.DeviceId==target.DeviceId&&e.Target!=target),e=> { Assert.False(e.WiringConfirmed); Assert.False(e.ResumeOnStart); Assert.Null(e.SavedPolicy); });
        Assert.All(result.Configuration.Lighting.Endpoints!.Where(e=>e.Target.DeviceId==target.DeviceId),e=>Assert.False(e.WiringConfirmed));
        Assert.All(result.Configuration.Devices.Devices.Single(d=>d.Id==target.DeviceId).Endpoints.Where(e=>e.Kind==EndpointKind.Lighting),e=>Assert.False(e.Setup!.LedCountConfirmed));
        Assert.All(result.Configuration.Cooling.Endpoints.Where(e=>e.Target.DeviceId==demo.DeviceIds[1]),e=>Assert.True(e.WiringConfirmed));
        Assert.Contains(result.Changes,s=>s.Contains("再確認"));
    }
    [Fact] public async Task FailedReleaseDoesNotAdoptSetupAndClientReadsBackTheUnconfirmedState()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var target=new EndpointReference(demo.DeviceIds[0],"fan3");
        await Duty(client,target,62); var before=client.Snapshot!.Configuration.Generation;
        var preview=await client.PreviewSetupAsync(Fan(client,target.DeviceId,target.EndpointId).Capture()); demo.Hubs[0].FailNextFanWrite=true;
        await Assert.ThrowsAsync<IOException>(()=>client.ApplySetupAsync(preview));
        Assert.Equal(before,client.Snapshot!.Configuration.Generation);
        Assert.NotNull(client.Snapshot.State.LifecycleError);
        Assert.DoesNotContain(client.Snapshot.State.Endpoints,e=>e.Target==target&&e.Outcome==Outcome.Confirmed&&e.ReportedDuty==70);
    }
}
