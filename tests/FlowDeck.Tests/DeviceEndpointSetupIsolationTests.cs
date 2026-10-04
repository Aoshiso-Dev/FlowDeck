using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceEndpointSetupTests
{
    private static async Task Automatic(DemoDeviceWorkspace demo, DeviceWorkspaceClient client, EndpointReference target)
    {
        await demo.Runtime.PollAsync(); await client.RefreshAsync(true);
        var snapshot = client.Snapshot!; var draft = new DeviceCoolingDraft(target);
        draft.Update(snapshot.State, snapshot.Configuration.Generation, snapshot.Configuration.Configuration.Cooling.Endpoints.Single(e => e.Target == target), 0, true);
        draft.SetAutomatic(true); draft.SelectProfile("balanced");
        Assert.Equal(Outcome.Confirmed, (await client.ApplyCoolingAsync(draft.Capture())).Outcome);
    }

    [Theory] [InlineData("setup")] [InlineData("safety")] [InlineData("remove")]
    public async Task FanEditsRetainOtherAutomaticPoliciesAndTheirTemperatureResponse(string operation)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var target = new EndpointReference(demo.DeviceIds[0], "fan1");
        var others = new[] { new EndpointReference(target.DeviceId, "fan2"), new EndpointReference(demo.DeviceIds[1], "fan1") };
        await Duty(client, target, 62);
        foreach (var other in others) await Automatic(demo, client, other);
        var before = others.Select(t => client.Snapshot!.State.Endpoints.Single(e => e.Target == t)).ToArray();
        DeviceEndpointSetupEdit edit;
        if (operation == "remove") edit = DeviceControlSetupRemoval.Capture(client.Snapshot!, target);
        else if (operation == "safety")
        {
            var draft = new DeviceCoolingSettingsDraft(client.Snapshot!, target);
            draft.Change(draft.Safety with { Minimum = 35 }, draft.Profiles); draft.Confirmed = true; edit = draft.Capture();
        }
        else edit = Fan(client, target.DeviceId).Capture();
        Assert.Equal(Outcome.Confirmed, (await client.ApplySetupAsync(await client.PreviewSetupAsync(edit))).Outcome);
        for (var i = 0; i < others.Length; i++)
        {
            var after = client.Snapshot!.State.Endpoints.Single(e => e.Target == others[i]);
            Assert.True(after.Owned); Assert.Equal(before[i].PolicyRevision, after.PolicyRevision);
            Assert.Equal(FanMode.Auto, after.Cooling!.Mode); Assert.False(after.Cooling.Faulted);
        }
        demo.CpuTemperature = 88;
        await demo.Runtime.PollAsync(); await demo.Runtime.TickCoolingAsync(); await client.RefreshAsync(true);
        foreach (var other in others)
        {
            var after = client.Snapshot!.State.Endpoints.Single(e => e.Target == other);
            Assert.True(after.Owned); Assert.True(after.ReportedDuty >= 90, after.Message);
        }
        await client.ReleaseAsync();
        foreach (var other in others) Assert.Equal(70, (await demo.Hubs[Array.IndexOf(demo.DeviceIds, other.DeviceId)].ReadAsync()).Channels.Single(c => c.Id == other.EndpointId).Duty);
    }

    [Theory] [InlineData("fixed")] [InlineData("breathing")]
    public async Task SavingAFanPreservesLightingOnTheSameHubAndAcrossHubs(string effect)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var snapshot = client.Snapshot!;
        var lights = snapshot.State.Endpoints.Where(e => e.Target.EndpointId == "led1").Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray();
        var result = await client.ApplyLightingAsync(new(new(snapshot.State.Inventory.Epoch, snapshot.State.Inventory.TopologyRevision, snapshot.Configuration.Generation, lights),
            DeviceOperation.SetLighting, new(effect, [new(10, 20, 30)])));
        Assert.True(result.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified, result.Message);
        await demo.Runtime.TickLightingAsync(); await client.RefreshAsync(true);
        var before = lights.Select(t => client.Snapshot!.State.Endpoints.Single(e => e.Target == t.Target)).ToArray();
        Assert.Equal(Outcome.Confirmed, (await client.ApplySetupAsync(await client.PreviewSetupAsync(Fan(client, demo.DeviceIds[0]).Capture()))).Outcome);
        await demo.Runtime.TickLightingAsync(); await client.RefreshAsync(true);
        for (var i = 0; i < lights.Length; i++)
        {
            var after = client.Snapshot!.State.Endpoints.Single(e => e.Target == lights[i].Target);
            Assert.True(after.Owned); Assert.Equal(before[i].PolicyRevision, after.PolicyRevision);
            Assert.True(DeviceLighting.SameProgram(before[i].RequestedLighting!, after.RequestedLighting!));
            if (effect == "breathing") Assert.True(after.Lighting!.Running);
        }
    }

    [Theory] [InlineData("other-fan")] [InlineData("preferences")] [InlineData("lighting")]
    public async Task IndividualFanPreviewRejectsHiddenChangesBeforeAnyOutput(string extra)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var target = new EndpointReference(demo.DeviceIds[0], "fan1"); await Duty(client, target, 62);
        var edit = Fan(client, target.DeviceId).Capture(); var config = edit.Configuration;
        config = extra switch {
            "other-fan" => config with { Cooling = config.Cooling with { Endpoints = config.Cooling.Endpoints.Select(e => e.Target.EndpointId == "fan2" ? e with { Safety = e.Safety with { Minimum = 40 } } : e).ToArray() } },
            "lighting" => config with { Lighting = config.Lighting with { Endpoints = config.Lighting.Endpoints!.Select(e => e with { Exit = new(LightingExitAction.FixedColor, new(80, 80, 80)) }).ToArray() } },
            _ => config with { Dashboard = config.Dashboard with { Items = [] } }
        };
        var writes = demo.Hubs.Select(h => h.Writes).ToArray();
        await Assert.ThrowsAsync<IOException>(() => client.PreviewSetupAsync(edit with { Configuration = config }));
        Assert.Equal(writes, demo.Hubs.Select(h => h.Writes)); Assert.True(client.Snapshot!.State.Endpoints.Single(e => e.Target == target).Owned);
    }

    [Fact] public async Task FailedFanExitPreservesOtherFanAndItsRecoveryObligation()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var target = new EndpointReference(demo.DeviceIds[0], "fan3"); var other = new EndpointReference(target.DeviceId, "fan2");
        await Duty(client, target, 62); await Duty(client, other, 56);
        var before = client.Snapshot!.State.Endpoints.Single(e => e.Target == other);
        var preview = await client.PreviewSetupAsync(Fan(client, target.DeviceId, target.EndpointId).Capture());
        demo.Hubs[0].FailNextFanWrite = true;
        await Assert.ThrowsAsync<IOException>(() => client.ApplySetupAsync(preview));
        var after = client.Snapshot!.State.Endpoints.Single(e => e.Target == other);
        Assert.True(after.Owned); Assert.Equal(56, after.ReportedDuty); Assert.Equal(before.PolicyRevision, after.PolicyRevision);
        Assert.Contains(new HubEndpointRecoveryJournal(dir.Path, other.DeviceId).Pending, p => p.Channel == other.EndpointId && p.ExitDuty == 70);
        Assert.Equal(Outcome.Confirmed, (await client.ApplySetupAsync(await client.PreviewSetupAsync(Fan(client, target.DeviceId, target.EndpointId).Capture()))).Outcome);
        Assert.True(client.Snapshot!.State.Endpoints.Single(e => e.Target == other).Owned);
        Assert.Null(client.Snapshot.State.LifecycleError);
        await client.ReleaseAsync();
        Assert.Equal(70, (await demo.Hubs[0].ReadAsync()).Channels.Single(c => c.Id == other.EndpointId).Duty);
        Assert.Empty(new HubEndpointRecoveryJournal(dir.Path, other.DeviceId).Pending);
    }

    [Fact] public void IndividualCoolingSetupWireRequestRequiresAnExactTargetAndRejectsUnrelatedBodies()
    {
        var request = new DeviceConfigurationRequest(DeviceConfigurationOperation.ValidateCoolingSetup, Guid.NewGuid().ToString("N"), CoolingSetupTarget: new("device", "fan1"));
        Assert.True(DeviceConfigurationProtocol.Valid(request));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { CoolingSetupTarget = null }));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { Operation = DeviceConfigurationOperation.Validate }));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { TopologyRevision = 3 }));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { Data = [1] }));
    }

    [Fact] public async Task RemovingAnIdleDisconnectedFanDoesNotReleaseOtherDevicesOrReenableOnReconnect()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var target = new EndpointReference(demo.DeviceIds[0], "fan1"); var other = new EndpointReference(demo.DeviceIds[1], "fan2");
        await Duty(client, other, 56);
        demo.Hubs[0].Disconnected = true; await demo.Runtime.PollAsync(); await client.RefreshAsync(true);
        Assert.Equal(Outcome.Confirmed, (await client.ApplySetupAsync(await client.PreviewSetupAsync(DeviceControlSetupRemoval.Capture(client.Snapshot!, target)))).Outcome);
        Assert.True(client.Snapshot!.State.Endpoints.Single(e => e.Target == other).Owned);
        demo.Hubs[0].Disconnected = false; await demo.Runtime.PollAsync(); await demo.Runtime.PollAsync(); await client.RefreshAsync(true);
        Assert.NotEqual(Outcome.Confirmed, (await Duty(client, target, 62)).Outcome);
        Assert.Equal(0, demo.Hubs[0].Writes); Assert.Equal(1, demo.Hubs[1].Writes);
    }
}
