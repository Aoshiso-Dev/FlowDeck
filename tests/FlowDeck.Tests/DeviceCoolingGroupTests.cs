using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceCoolingGroupTests
{
    private static async Task<string> Group(DeviceWorkspaceClient client,params EndpointReference[] targets)
    {
        await client.RefreshAsync(true); var draft=new DeviceCoolingGroupEditorDraft(client.Snapshot!){Name="ケース冷却"};
        foreach(var target in targets) draft.Select(target,true);
        Assert.Equal(Outcome.Confirmed,(await client.SaveCoolingGroupAsync(draft.Capture())).Outcome); return draft.Id;
    }
    private static async Task<DeviceOperationResult> Apply(DeviceWorkspaceClient client,string id,int duty,CoolingTemperatureProfile? profile=null)
    {
        await client.RefreshAsync(true); var draft=new DeviceCoolingGroupDraft(client.Snapshot!,id); draft.Change(duty,profile is not null,profile);
        return await client.ApplyCoolingGroupAsync(draft.Capture());
    }
    [Fact] public async Task GroupsAcrossControllersClampIndependentlyAndPersistTheRequestedPolicy()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path); await using var client=demo.CreateClient(); await client.ConnectAsync();
        var a=new EndpointReference(demo.DeviceIds[0],"fan1"); var b=new EndpointReference(demo.DeviceIds[1],"fan1");
        var safety=new DeviceCoolingSettingsDraft(client.Snapshot!,b); safety.Change(safety.Safety with { Minimum=60 },safety.Profiles); safety.Confirmed=true;
        await client.ApplySetupAsync(await client.PreviewSetupAsync(safety.Capture()));
        var id=await Group(client,a,b); Assert.All(demo.Hubs,h=>Assert.Equal(0,h.Writes));
        var result=await Apply(client,id,50); Assert.Equal(Outcome.Confirmed,result.Outcome); Assert.All(result.Targets,t=>Assert.Equal(50,t.RequestedDuty));
        Assert.Equal(50,result.Targets.Single(t=>t.Target==a).ReportedDuty); Assert.Equal(60,result.Targets.Single(t=>t.Target==b).ReportedDuty);
        Assert.All(client.Snapshot!.Configuration.Configuration.Cooling.Endpoints.Where(e=>e.Target==a||e.Target==b),e=>Assert.Equal(50,e.SavedPolicy!.Duty));
        var draft=new DeviceCoolingGroupDraft(client.Snapshot,id); Assert.Equal(Outcome.Confirmed,(await client.ApplyCoolingGroupAsync(draft.Capture(DeviceOperation.ReleaseCooling))).Outcome);
        Assert.Equal(70,(await demo.Hubs[0].ReadAsync()).Channels[0].Duty); Assert.Equal(70,(await demo.Hubs[1].ReadAsync()).Channels[0].Duty);
    }
    [Fact] public async Task MembershipEditsAndDeletionDoNotApplyReleaseOrExtendRunningCooling()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path); await using var client=demo.CreateClient(); await client.ConnectAsync();
        var a=new EndpointReference(demo.DeviceIds[0],"fan1"); var b=new EndpointReference(demo.DeviceIds[1],"fan1"); var id=await Group(client,a);
        await Apply(client,id,66); var before=client.Snapshot!.State.Endpoints.Single(e=>e.Target==a); var writes=demo.Hubs[0].Writes;
        var draft=new DeviceCoolingGroupEditorDraft(client.Snapshot,id){Name="ラジエーター"}; draft.Select(b,true); await client.SaveCoolingGroupAsync(draft.Capture());
        Assert.Equal(writes,demo.Hubs[0].Writes); Assert.Equal(0,demo.Hubs[1].Writes); Assert.Equal(before.PolicyRevision,client.Snapshot!.State.Endpoints.Single(e=>e.Target==a).PolicyRevision);
        Assert.Equal(2,client.Snapshot.Configuration.Configuration.Cooling.Groups.Single().Revision);
        draft=new(client.Snapshot,id); await client.SaveCoolingGroupAsync(draft.Capture(true));
        Assert.Empty(client.Snapshot!.Configuration.Configuration.Cooling.Groups); Assert.Equal(writes,demo.Hubs[0].Writes); Assert.True(client.Snapshot.State.Endpoints.Single(e=>e.Target==a).Owned);
    }
    [Fact] public async Task AutomaticControlUsesTheSameSelectedCurveAndOverlappingGroupsKeepOnePolicyPerEndpoint()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path); await using var client=demo.CreateClient(); await client.ConnectAsync();
        var a=new EndpointReference(demo.DeviceIds[0],"fan1"); var b=new EndpointReference(demo.DeviceIds[1],"fan1");
        var one=await Group(client,a,b); var two=await Group(client,b); var profile=client.Snapshot!.Configuration.Configuration.Cooling.Endpoints.First(e=>e.Target==a).Profiles.Single();
        Assert.Equal(Outcome.Confirmed,(await Apply(client,one,0,profile)).Outcome);
        Assert.All(client.Snapshot!.State.Endpoints.Where(e=>e.Target==a||e.Target==b),e=>Assert.Equal(FanMode.Auto,e.Cooling!.Mode));
        Assert.Equal(Outcome.Confirmed,(await Apply(client,two,78)).Outcome); await demo.Runtime.TickCoolingAsync(); await client.RefreshAsync(true);
        Assert.Equal(FanMode.Auto,client.Snapshot!.State.Endpoints.Single(e=>e.Target==a).Cooling!.Mode);
        Assert.Equal(FanMode.Manual,client.Snapshot.State.Endpoints.Single(e=>e.Target==b).Cooling!.Mode); Assert.Equal(78,(await demo.Hubs[1].ReadAsync()).Channels[0].Duty);
    }
    [Fact] public async Task APartialWriteIsReportedPerTargetAndDoesNotClaimGroupSuccess()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path); await using var client=demo.CreateClient(); await client.ConnectAsync();
        var a=new EndpointReference(demo.DeviceIds[0],"fan3"); var b=new EndpointReference(demo.DeviceIds[1],"fan1"); var id=await Group(client,a,b);
        demo.Hubs[0].FailNextFanWrite=true; var result=await Apply(client,id,66);
        Assert.Equal(Outcome.Partial,result.Outcome); Assert.NotEqual(Outcome.Confirmed,result.Targets.Single(t=>t.Target==a).Outcome); Assert.Equal(Outcome.Confirmed,result.Targets.Single(t=>t.Target==b).Outcome);
        Assert.Equal(66,(await demo.Hubs[1].ReadAsync()).Channels[0].Duty);
    }
    [Theory] [InlineData("revision")] [InlineData("targets")] [InlineData("generation")]
    public async Task TheServiceRejectsAlteredGroupIdentityMembershipAndGeneration(string reason)
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        var raw=DeviceServicePipeClient.ForDemo(demo.PipeName,Environment.ProcessId); await using var client=new DeviceWorkspaceClient(raw); await client.ConnectAsync();
        var a=new EndpointReference(demo.DeviceIds[0],"fan1"); var b=new EndpointReference(demo.DeviceIds[1],"fan1"); var id=await Group(client,a,b);
        var draft=new DeviceCoolingGroupDraft(client.Snapshot!,id); draft.Change(60,false,null); var edit=draft.Capture();
        var request=raw.Request(DeviceOperation.SetDuty,topology:edit.Topology,targets:reason=="targets"?[edit.Targets[0]]:edit.Targets,duty:60) with {
            CoolingGroup=edit.Group with { ExpectedRevision=reason=="revision"?42:edit.Group.ExpectedRevision,ExpectedGeneration=reason=="generation"?Guid.NewGuid().ToString("N"):edit.Group.ExpectedGeneration } };
        Assert.False((await raw.SendAsync(request)).Success); Assert.All(demo.Hubs,h=>Assert.Equal(0,h.Writes));
    }
    [Theory] [InlineData("configuration")] [InlineData("endpoint")]
    public async Task EditsSurvivePollingButBlockConflictingApplication(string change)
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path); await using var client=demo.CreateClient(); await client.ConnectAsync();
        var a=new EndpointReference(demo.DeviceIds[0],"fan1"); var id=await Group(client,a); var snapshot=client.Snapshot!;
        var draft=new DeviceCoolingGroupDraft(snapshot,id); draft.Change(66,false,null); await client.RefreshAsync(); draft.Update(client.Snapshot!);
        Assert.False(draft.Conflict); Assert.Equal(66,draft.Duty);
        var changed=snapshot with { Configuration=snapshot.Configuration with { Generation=change=="configuration"?Guid.NewGuid().ToString("N"):snapshot.Configuration.Generation },
            State=snapshot.State with { Endpoints=snapshot.State.Endpoints.Select(e=>change=="endpoint"&&e.Target==a?e with { PolicyRevision=e.PolicyRevision+1 }:e).ToArray() } };
        draft.Update(changed); Assert.True(draft.Conflict); Assert.Throws<InvalidOperationException>(()=>draft.Capture()); Assert.Equal(66,draft.Duty);
    }
    [Fact] public async Task GroupEditorRetainsJapaneseNameAndChecksButCannotSaveAnObsoleteGeneration()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path); await using var client=demo.CreateClient(); await client.ConnectAsync();
        var a=new EndpointReference(demo.DeviceIds[0],"fan1"); var draft=new DeviceCoolingGroupEditorDraft(client.Snapshot!){Name="天面ファン"}; draft.Select(a,true);
        await client.RefreshAsync(); draft.Update(client.Snapshot!); Assert.Equal("天面ファン",draft.Name); Assert.True(draft.Selected(a));
        var stale=draft.Capture(); await Group(client,a); draft.Update(client.Snapshot!); Assert.True(draft.Conflict);
        Assert.Throws<InvalidOperationException>(()=>draft.Capture()); await Assert.ThrowsAsync<IOException>(()=>client.SaveCoolingGroupAsync(stale));
    }
    [Fact] public async Task MissingSensorsAndDisconnectedMembersBlockAllOutputsBeforeTheBatchStarts()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path); await using var client=demo.CreateClient(); await client.ConnectAsync();
        var a=new EndpointReference(demo.DeviceIds[0],"fan1"); var b=new EndpointReference(demo.DeviceIds[1],"fan1"); var id=await Group(client,a,b);
        var snapshot=client.Snapshot!; var stale=snapshot with { State=snapshot.State with { Telemetry=snapshot.State.Telemetry!.Select(t=>t.Feature==DeviceFeature.Temperature?t with { Stale=true }:t).ToArray() } };
        var draft=new DeviceCoolingGroupDraft(stale,id); draft.Change(60,false,null); Assert.Throws<InvalidOperationException>(()=>draft.Capture());
        demo.Hubs[1].Disconnected=true;
        // PollAsync can join an observation that began before Disconnected changed. Wait for the
        // disconnected observation, not merely for that older read to finish; no outputs are sent.
        using(var observed = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while(true)
            {
                await demo.Runtime.PollAsync().WaitAsync(observed.Token);
                if(!demo.Runtime.State().Inventory.Devices.Single(d=>d.Device.Id==b.DeviceId).Connected) break;
                await Task.Delay(10,observed.Token);
            }
        await client.RefreshAsync(true); draft=new(client.Snapshot!,id); draft.Change(60,false,null);
        Assert.Throws<InvalidOperationException>(()=>draft.Capture()); Assert.All(demo.Hubs,h=>Assert.Equal(0,h.Writes)); demo.Hubs[1].Disconnected=false;
    }
    [Theory] [InlineData("led1")] [InlineData("unknown")]
    public async Task GroupValidationRejectsLightingAndUnknownReferencesWithoutOutput(string endpoint)
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path); await using var client=demo.CreateClient(); await client.ConnectAsync();
        var snapshot=client.Snapshot!; var id=Guid.NewGuid().ToString("N"); var group=new EndpointGroup(id,"不正",EndpointKind.Cooling,[new(demo.DeviceIds[0],endpoint)]);
        await Assert.ThrowsAsync<IOException>(()=>client.SaveCoolingGroupAsync(new(snapshot.State.Inventory.Epoch,snapshot.Configuration.Generation,new(id,0,group))));
        Assert.All(demo.Hubs,h=>Assert.Equal(0,h.Writes));
    }
    [Fact] public async Task GroupLimitIsCheckedByTheServiceAndFailedSavePreservesTheCatalog()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        var raw=DeviceServicePipeClient.ForDemo(demo.PipeName,Environment.ProcessId); await using var client=new DeviceWorkspaceClient(raw); await client.ConnectAsync();
        var a=new EndpointReference(demo.DeviceIds[0],"fan1"); var snapshot=client.Snapshot!;
        var config=snapshot.Configuration.Configuration; config=config with { Cooling=config.Cooling with { Groups=Enumerable.Range(0,32).Select(i=>new EndpointGroup(Guid.NewGuid().ToString("N"),"Group "+i,EndpointKind.Cooling,[a])).ToArray() } };
        var preview=await raw.PreviewConfigurationAsync(snapshot.Configuration.Generation,config);
        await raw.ApplyConfigurationAsync(preview,snapshot.State.Inventory.TopologyRevision); await client.RefreshAsync(true);
        var editor=new DeviceCoolingGroupEditorDraft(client.Snapshot!){Name="33番目"}; editor.Select(a,true); Assert.Throws<InvalidOperationException>(()=>editor.Capture());
        var group=new EndpointGroup(editor.Id,"33番目",EndpointKind.Cooling,[a]);
        await Assert.ThrowsAsync<IOException>(()=>client.SaveCoolingGroupAsync(new(client.Snapshot!.State.Inventory.Epoch,client.Snapshot.Configuration.Generation,new(group.Id,0,group))));
        Assert.Equal(32,client.Snapshot!.Configuration.Configuration.Cooling.Groups.Length); Assert.All(demo.Hubs,h=>Assert.Equal(0,h.Writes));
    }
    [Fact] public async Task MembershipChangeCancelsAQueuedMemberAndNeverAddsNewMembersToTheInFlightRequest()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        var raw=DeviceServicePipeClient.ForDemo(demo.PipeName,Environment.ProcessId); await using var client=new DeviceWorkspaceClient(raw); await client.ConnectAsync();
        var a=new EndpointReference(demo.DeviceIds[0],"fan1"); var b=new EndpointReference(demo.DeviceIds[0],"fan2"); var c=new EndpointReference(demo.DeviceIds[1],"fan1"); var id=await Group(client,a,b);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var finish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        demo.Hubs[0].BeforeFanWrite=async ct=>{ entered.TrySetResult(); await finish.Task.WaitAsync(ct); };
        var draft=new DeviceCoolingGroupDraft(client.Snapshot!,id); draft.Change(66,false,null); var edit=draft.Capture();
        var applying=raw.SendAsync(raw.Request(DeviceOperation.SetDuty,topology:edit.Topology,targets:edit.Targets,duty:66) with { CoolingGroup=edit.Group });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            DeviceServiceReply? saved=null;
            for(var retry=0;retry<4;retry++)
            {
                var current=await raw.ReadConfigurationAsync(); var group=current.Configuration.Cooling.Groups.Single(g=>g.Id==id);
                saved=await raw.SendAsync(raw.Request(DeviceOperation.Configuration,configuration:new(DeviceConfigurationOperation.SaveCoolingGroup,
                    ExpectedGeneration:current.Generation,CoolingGroup:new(id,group.Revision,group with { Targets=[a,c] }))));
                if(saved.Success) break;
            }
            Assert.True(saved?.Success,saved?.Error);
        }
        finally { demo.Hubs[0].BeforeFanWrite=null; finish.TrySetResult(); }
        var result=(await applying).Result!; Assert.Equal(Outcome.Partial,result.Outcome);
        Assert.Single(result.Targets,t=>t.Outcome==Outcome.Confirmed); Assert.Single(result.Targets,t=>t.Outcome==Outcome.Rejected);
        Assert.Equal(1,demo.Hubs[0].Writes); Assert.Equal(0,demo.Hubs[1].Writes);
        Assert.Equal(45,(await demo.Hubs[0].ReadAsync()).Channels.Single(ch=>ch.Id==result.Targets.Single(t=>t.Outcome==Outcome.Rejected).Target.EndpointId).Duty);
    }
    [Fact] public void MalformedOrCrossOperationGroupPayloadsAreRejected()
    {
        var id=Guid.NewGuid().ToString("N"); var target=new EndpointReference("hub","fan1"); var group=new EndpointGroup(id,"ファン",EndpointKind.Cooling,[target]);
        Assert.True(DeviceCoolingGroups.Valid(group)); Assert.False(DeviceCoolingGroups.Valid(group with { Targets=[target,target] }));
        Assert.False(DeviceCoolingGroups.Valid(group with { Kind=EndpointKind.Lighting }));
        var config=new DeviceConfigurationRequest(DeviceConfigurationOperation.SaveCoolingGroup,ExpectedGeneration:id,CoolingGroup:new(id,0,group));
        Assert.True(DeviceConfigurationProtocol.Valid(config)); Assert.False(DeviceConfigurationProtocol.Valid(config with { Operation=DeviceConfigurationOperation.SaveDashboard }));
        var request=new DeviceServiceRequest(2,id,id,id,1,"PC",DeviceOperation.State,CoolingGroup:new(id,id,1));
        Assert.Throws<IOException>(()=>DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(request,Contract.Json)));
    }
}
