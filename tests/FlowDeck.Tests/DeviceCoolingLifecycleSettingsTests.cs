using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceCoolingLifecycleSettingsTests
{
    private static async Task<DeviceOperationResult> Duty(DeviceWorkspaceClient client,EndpointReference target,int duty)
    {
        await client.RefreshAsync(true); var snapshot=client.Snapshot!; var draft=new DeviceCoolingDraft(target);
        draft.Update(snapshot.State,snapshot.Configuration.Generation,snapshot.Configuration.Configuration.Cooling.Endpoints.Single(e=>e.Target==target),snapshot.ElapsedSeconds,true);
        draft.SetAutomatic(false); draft.SetDuty(duty); return await client.ApplyCoolingAsync(draft.Capture());
    }
    private static async Task Enable(DeviceWorkspaceClient client,params EndpointReference[] targets)
    {
        await client.RefreshAsync(true); var draft=new DeviceCoolingStartupDraft(client.Snapshot!); draft.SetEnabled(true);
        foreach(var target in targets) draft.Select(target,true);
        Assert.Equal(Outcome.Confirmed,(await client.SaveCoolingStartupAsync(draft.Capture())).Outcome);
    }
    [Fact] public async Task StopRulesAreExplicitAndDriveStopThenConfirmedRestartBoostThroughTheService()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var target=new EndpointReference(demo.DeviceIds[0],"fan1");
        var draft=new DeviceCoolingSettingsDraft(client.Snapshot!,target); Assert.True(draft.StopSupported); Assert.Null(draft.Safety.Stop);
        var stop=new CoolingStopRule(draft.Sensors.Select(s=>new StopTemperatureRule(s.Target,60)).ToArray(),80,0.1);
        draft.Change(draft.Safety with { Stop=stop },draft.Profiles); draft.Confirmed=true;
        var preview=await client.PreviewSetupAsync(draft.Capture()); Assert.Contains(preview.Edit.Changes,s=>s.Contains("停止条件（すべて満たす）"));
        Assert.Equal(Outcome.Confirmed,(await client.ApplySetupAsync(preview)).Outcome); Assert.All(demo.Hubs,h=>Assert.Equal(0,h.Writes));
        Assert.Equal(Outcome.Confirmed,(await Duty(client,target,0)).Outcome); Assert.Equal(0,(await demo.Hubs[0].ReadAsync()).Channels[0].Duty);
        var restart=await Duty(client,target,40); Assert.Equal(Outcome.Confirmed,restart.Outcome);
        // The watchdog can finish a 100ms boost before this client receives its reply.
        // Check the command's confirmed read-back, then independently read the settled output.
        Assert.Equal(80,Assert.Single(restart.Targets).ReportedDuty);
        await Task.Delay(150); await demo.Runtime.TickCoolingAsync(); Assert.Equal(40,(await demo.Hubs[0].ReadAsync()).Channels[0].Duty);
        Assert.Equal(0,demo.Hubs[1].Writes);
    }
    [Theory] [InlineData("unsupported")] [InlineData("empty")] [InlineData("threshold")] [InlineData("restart")] [InlineData("duration")] [InlineData("missing")]
    public async Task UnsupportedOrIncompleteStopSettingsCannotBeSaved(string reason)
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var snapshot=client.Snapshot!; var target=new EndpointReference(demo.DeviceIds[0],"fan1");
        if(reason=="unsupported") snapshot=snapshot with { Configuration=snapshot.Configuration with { Setup=snapshot.Configuration.Setup!.Select(s=>s.Target==target?s with {
            Options=s.Options with { CoolingStopSupported=false } }:s).ToArray() } };
        var draft=new DeviceCoolingSettingsDraft(snapshot,target); var sensor=draft.Sensors[0].Target;
        var stop=new CoolingStopRule(reason=="empty"?[]:[new(sensor,reason=="threshold"?150:60)],reason=="restart"?20:80,reason=="duration"?0:1);
        draft.Change(draft.Safety with { Stop=stop },draft.Profiles); draft.Confirmed=true;
        if(reason=="missing") draft.Update(snapshot with { State=snapshot.State with { Telemetry=snapshot.State.Telemetry!.Select(t=>t.Target==sensor?t with { Stale=true }:t).ToArray() } });
        Assert.Throws<InvalidOperationException>(()=>draft.Capture()); Assert.All(demo.Hubs,h=>Assert.Equal(0,h.Writes));
    }
    [Fact] public async Task AConfiguredPumpStartsWithStopDisabledAndRemovingStopRestoresTheMinimum()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var target=new EndpointReference(demo.DeviceIds[1],"fan5");
        var draft=new DeviceCoolingSettingsDraft(client.Snapshot!,target); Assert.True(draft.Pump); Assert.Null(draft.Safety.Stop);
        draft.Change(draft.Safety with { Stop=new([new(draft.Sensors[0].Target,60)],90,1) },draft.Profiles); draft.Confirmed=true;
        await client.ApplySetupAsync(await client.PreviewSetupAsync(draft.Capture()));
        draft=new(client.Snapshot!,target); draft.Change(draft.Safety with { Stop=null },draft.Profiles); draft.Confirmed=true;
        var edit=draft.Capture(); Assert.Contains(edit.Changes,s=>s.StartsWith("停止許可を解除")); await client.ApplySetupAsync(await client.PreviewSetupAsync(edit));
        var snapshot=client.Snapshot!; var state=snapshot.State.Endpoints.Single(e=>e.Target==target); var row=new DeviceCoolingDraft(target);
        row.Update(snapshot.State,snapshot.Configuration.Generation,snapshot.Configuration.Configuration.Cooling.Endpoints.Single(e=>e.Target==target),snapshot.ElapsedSeconds,true);
        row.SetDuty(0); Assert.Equal(30,row.Minimum); Assert.False(row.CanApply);
        var result=await client.ApplyCoolingAsync(new(new(snapshot.State.Inventory.Epoch,snapshot.State.Inventory.TopologyRevision,snapshot.Configuration.Generation,
            new(target,state.Generation,state.PolicyRevision)),DeviceOperation.SetDuty,0));
        Assert.Equal(Outcome.Confirmed,result.Outcome); Assert.Equal(30,(await demo.Hubs[1].ReadAsync()).Channels[4].Duty);
    }
    [Fact] public async Task StartupSelectionPersistsWithoutReleasingOrChangingTheCurrentOutputs()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); Assert.True(client.SupportsCoolingStartup);
        var target=new EndpointReference(demo.DeviceIds[0],"fan1"); var absent=new EndpointReference(demo.DeviceIds[1],"fan1");
        var empty=new DeviceCoolingStartupDraft(client.Snapshot!); Assert.Empty(empty.Candidates); empty.SetEnabled(true); Assert.Throws<ArgumentException>(()=>empty.Select(target,true));
        await Duty(client,target,64); var before=client.Snapshot!.State.Endpoints.Single(e=>e.Target==target); var writes=demo.Hubs[0].Writes;
        await Enable(client,target); var config=client.Snapshot!.Configuration.Configuration;
        Assert.True(config.Preferences.Startup.ResumeControl); Assert.True(config.Cooling.Endpoints.Single(e=>e.Target==target).ResumeOnStart);
        Assert.False(config.Cooling.Endpoints.Single(e=>e.Target==absent).ResumeOnStart); Assert.Equal(writes,demo.Hubs[0].Writes);
        Assert.Equal(before.PolicyRevision,client.Snapshot.State.Endpoints.Single(e=>e.Target==target).PolicyRevision); Assert.True(client.Snapshot.State.Endpoints.Single(e=>e.Target==target).Owned);
        var draft=new DeviceCoolingStartupDraft(client.Snapshot); draft.SetEnabled(false); await client.SaveCoolingStartupAsync(draft.Capture());
        Assert.Equal(writes,demo.Hubs[0].Writes); Assert.Equal(64,(await demo.Hubs[0].ReadAsync()).Channels[0].Duty);
        await Duty(client,target,65); Assert.All(client.Snapshot!.Configuration.Configuration.Cooling.Endpoints,e=>Assert.False(e.ResumeOnStart));
    }
    [Fact] public async Task AColdServiceNeedsAnExplicitLaunchAndTheLaunchRestoresOnlySelectedPoliciesOnce()
    {
        using var dir=new ServiceTestDirectory(); EndpointReference target;
        await using(var first=await DemoDeviceWorkspace.CreateAsync(dir.Path,persistent:true))
        {
            await using var client=first.CreateClient(); await client.ConnectAsync(); target=new(first.DeviceIds[0],"fan1");
            await Duty(client,target,64); await Duty(client,new(first.DeviceIds[1],"fan1"),72); await Enable(client,target);
            Assert.True((await client.ReleaseAsync()).Success);
        }
        await using var second=await DemoDeviceWorkspace.CreateAsync(dir.Path,persistent:true); await using var launch=second.CreateClient(); await launch.ConnectAsync();
        Assert.All(second.Hubs,h=>Assert.Equal(0,h.Writes));
        var restored=await launch.RestoreStartupAsync(); Assert.Equal(Outcome.Confirmed,Assert.Single(restored).Outcome);
        Assert.Equal(64,(await second.Hubs[0].ReadAsync()).Channels[0].Duty); Assert.Equal(0,second.Hubs[1].Writes);
        Assert.True(launch.Snapshot!.Configuration.Configuration.Cooling.Endpoints.Single(e=>e.Target==target).ResumeOnStart);
        var writes=second.Hubs[0].Writes; await launch.RefreshAsync(); Assert.Empty(await launch.RestoreStartupAsync()); await launch.ReconnectAsync();
        Assert.Equal(writes,second.Hubs[0].Writes);
    }
    [Fact] public async Task AnUnconfirmedStartupWriteDurablyDisablesFutureAutomaticAttempts()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path); var target=new EndpointReference(demo.DeviceIds[0],"fan3");
        await using(var setup=demo.CreateClient()) { await setup.ConnectAsync(); await Duty(setup,target,64); await Enable(setup,target); await setup.ReleaseAsync(); }
        await using(var launch=demo.CreateClient())
        {
            await launch.ConnectAsync(); demo.Hubs[0].FailNextFanWrite=true; var result=Assert.Single(await launch.RestoreStartupAsync());
            Assert.NotEqual(Outcome.Confirmed,result.Outcome); Assert.False(launch.Snapshot!.Configuration.Configuration.Cooling.Endpoints.Single(e=>e.Target==target).ResumeOnStart);
            var writes=demo.Hubs[0].Writes; Assert.Empty(await launch.RestoreStartupAsync()); Assert.Equal(writes,demo.Hubs[0].Writes); await launch.ReleaseAsync();
        }
        await using var next=demo.CreateClient(); await next.ConnectAsync(); var before=demo.Hubs[0].Writes;
        Assert.Empty(await next.RestoreStartupAsync()); Assert.Equal(before,demo.Hubs[0].Writes);
    }
    [Theory] [InlineData("configuration")] [InlineData("topology")] [InlineData("binding")]
    public async Task StartupEditsKeepTheirSelectionButRejectChangedVersions(string reason)
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var target=new EndpointReference(demo.DeviceIds[0],"fan1"); await Duty(client,target,64);
        var original=client.Snapshot!; var draft=new DeviceCoolingStartupDraft(original); draft.SetEnabled(true); draft.Select(target,true);
        var changed=original with { Configuration=original.Configuration with { Generation=reason=="configuration"?Guid.NewGuid().ToString("N"):original.Configuration.Generation,
            Candidates=original.Configuration.Candidates.Select(b=>reason=="binding"&&b.DeviceId==target.DeviceId?b with { Fingerprint=new('f',64) }:b).ToArray() },
            State=original.State with { Inventory=original.State.Inventory with { TopologyRevision=original.State.Inventory.TopologyRevision+(reason=="topology"?1:0) } } };
        draft.Update(changed); Assert.True(draft.Conflict); Assert.True(draft.Selected(target)); Assert.Throws<InvalidOperationException>(()=>draft.Capture());
        draft.Update(original); Assert.True(draft.Conflict);
    }
    [Fact] public async Task OneStartupFailureDoesNotPreventAnIndependentSelectedFanFromRestoring()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        var a=new EndpointReference(demo.DeviceIds[0],"fan3"); var b=new EndpointReference(demo.DeviceIds[1],"fan1");
        await using(var setup=demo.CreateClient()) { await setup.ConnectAsync(); await Duty(setup,a,64); await Duty(setup,b,74); await Enable(setup,a,b); await setup.ReleaseAsync(); }
        await using var launch=demo.CreateClient(); await launch.ConnectAsync(); demo.Hubs[0].FailNextFanWrite=true;
        var results=await launch.RestoreStartupAsync(); Assert.Equal(2,results.Length);
        Assert.NotEqual(Outcome.Confirmed,results.Single(r=>r.Target==a).Outcome); Assert.Equal(Outcome.Confirmed,results.Single(r=>r.Target==b).Outcome);
        Assert.Equal(74,(await demo.Hubs[1].ReadAsync()).Channels[0].Duty);
        var config=launch.Snapshot!.Configuration.Configuration; Assert.False(config.Cooling.Endpoints.Single(e=>e.Target==a).ResumeOnStart); Assert.True(config.Cooling.Endpoints.Single(e=>e.Target==b).ResumeOnStart);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ClosingDuringStartupFinishesTheInFlightTargetButSkipsRemainingTargets(bool dispose)
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        var a=new EndpointReference(demo.DeviceIds[0],"fan1"); var b=new EndpointReference(demo.DeviceIds[1],"fan1");
        await using(var setup=demo.CreateClient()) { await setup.ConnectAsync(); await Duty(setup,a,64); await Duty(setup,b,74); await Enable(setup,a,b); await setup.ReleaseAsync(); }
        await using var launch=demo.CreateClient(); await launch.ConnectAsync(); demo.Hubs[0].DelayMs=800;
        var firstWrites=demo.Hubs[0].Writes; var secondWrites=demo.Hubs[1].Writes; var restoring=launch.RestoreStartupAsync();
        var deadline=Environment.TickCount64+5000;
        while(demo.Runtime.State().Endpoints.Single(e=>e.Target==a).Outcome!=Outcome.Pending&&Environment.TickCount64<deadline) await Task.Delay(10);
        Assert.Equal(Outcome.Pending,demo.Runtime.State().Endpoints.Single(e=>e.Target==a).Outcome);
        if(dispose) await launch.DisposeAsync(); else Assert.True((await launch.ReleaseAsync()).Success);
        var result=Assert.Single(await restoring); Assert.Equal(a,result.Target); Assert.Equal(Outcome.Confirmed,result.Outcome);
        deadline=Environment.TickCount64+5000;
        while((await demo.Hubs[0].ReadAsync()).Channels[0].Duty!=70&&Environment.TickCount64<deadline) await Task.Delay(10);
        Assert.Equal(70,(await demo.Hubs[0].ReadAsync()).Channels[0].Duty);
        Assert.Equal(firstWrites+2,demo.Hubs[0].Writes); Assert.Equal(secondWrites,demo.Hubs[1].Writes);
        Assert.Empty(await launch.RestoreStartupAsync());
    }
    [Fact] public void StartupProtocolRejectsMissingDuplicateAndCrossOperationPayloads()
    {
        var target=new EndpointReference("controller","fan1"); var generation=Guid.NewGuid().ToString("N");
        var request=new DeviceConfigurationRequest(DeviceConfigurationOperation.SaveCoolingStartup,ExpectedGeneration:generation,TopologyRevision:1,CoolingStartup:new(true,[target]));
        Assert.True(DeviceConfigurationProtocol.Valid(request)); Assert.False(DeviceConfigurationProtocol.Valid(request with { CoolingStartup=null }));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { CoolingStartup=new(true,[target,target]) }));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { CoolingStartup=new(false,[target]) }));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { Operation=DeviceConfigurationOperation.SaveDashboard }));
        Assert.False(DeviceSetup.Valid(new(target,1,new("fan1",[],[LightingExitAction.Off],1,24,24,CoolingStopSupported:true))));
    }
    [Fact] public async Task TheServiceRejectsUnavailableSavedPoliciesAndStaleSelectionEvenWhenTheGuiIsBypassed()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var target=new EndpointReference(demo.DeviceIds[0],"fan1"); var state=client.Snapshot!;
        await Assert.ThrowsAsync<IOException>(()=>client.SaveCoolingStartupAsync(new(state.State.Inventory.Epoch,state.Configuration.Generation,state.State.Inventory.TopologyRevision,new(true,[target]))));
        Assert.Equal(0,demo.Hubs[0].Writes); await Duty(client,target,64); state=client.Snapshot!;
        var edit=new CoolingStartupEdit(state.State.Inventory.Epoch,state.Configuration.Generation,state.State.Inventory.TopologyRevision,new(true,[target]));
        await Duty(client,target,65); await Assert.ThrowsAsync<IOException>(()=>client.SaveCoolingStartupAsync(edit));
        Assert.False(client.Snapshot!.Configuration.Configuration.Preferences.Startup.ResumeControl);
    }
}
