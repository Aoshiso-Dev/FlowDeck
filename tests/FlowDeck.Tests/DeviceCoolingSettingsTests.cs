using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceCoolingSettingsTests
{
    private static CoolingTemperatureProfile Profile(DeviceCoolingSettingsDraft draft) => new("custom","水温・CPU",0,
        draft.Sensors.Select((s,i)=>new TemperatureDemandCurve(s.Target,[new(30,i==0?40:80),new(90,i==0?40:80)],95,85)).ToArray(),0,100);
    private static async Task<DeviceOperationResult> Apply(DemoDeviceWorkspace demo,DeviceWorkspaceClient client,EndpointReference target,bool automatic,int duty=60)
    {
        // Configuration persistence may outlast the sensor freshness window under a full test run.
        // Obtain a new measurement before testing the output; retain the production freshness guard.
        await demo.Runtime.PollAsync();
        await client.RefreshAsync(true); var snapshot=client.Snapshot!; var config=snapshot.Configuration.Configuration.Cooling.Endpoints.Single(e=>e.Target==target);
        var draft=new DeviceCoolingDraft(target); draft.Update(snapshot.State,snapshot.Configuration.Generation,config,snapshot.ElapsedSeconds,true);
        if(automatic) { draft.SetAutomatic(true); draft.SelectProfile("custom"); } else draft.SetDuty(duty);
        return await client.ApplyCoolingAsync(draft.Capture());
    }
    [Fact] public async Task FirstCurveExplicitlyBindsSelectedSensorsWithoutStartingOutputOrAddingDashboardItems()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path,monitoringOnly:true);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var target=new EndpointReference(demo.DeviceIds[0],"fan1");
        Assert.Throws<IOException>(()=>new DeviceCoolingSettingsDraft(client.Snapshot!,target));
        var setup=new DeviceEndpointSetupDraft(client.Snapshot!,target); setup.Change(setup.Values with { Confirmed=true,Safety=new(30,100,70,CoolingExitAction.FixedDuty,[]) });
        Assert.Equal(Outcome.Confirmed,(await client.ApplySetupAsync(await client.PreviewSetupAsync(setup.Capture()))).Outcome);
        var draft=new DeviceCoolingSettingsDraft(client.Snapshot!,target);
        draft.Change(draft.Safety,[Profile(draft)]); Assert.Throws<InvalidOperationException>(()=>draft.Capture()); draft.Confirmed=true;
        var preview=await client.PreviewSetupAsync(draft.Capture()); Assert.All(demo.Hubs,h=>Assert.Equal(0,h.Writes));
        Assert.Equal(Outcome.Confirmed,(await client.ApplySetupAsync(preview)).Outcome);
        var config=client.Snapshot!.Configuration.Configuration;
        Assert.Equal(3,config.Bindings.Devices.Length); Assert.Equal(2,config.Devices.Devices.Sum(d=>d.Endpoints.Count(e=>e.Kind==EndpointKind.Sensor)));
        Assert.Empty(config.Dashboard.Items); Assert.Null(config.Cooling.Endpoints[0].SavedPolicy); Assert.False(config.Cooling.Endpoints[0].ResumeOnStart);
        Assert.All(demo.Hubs,h=>Assert.Equal(0,h.Writes));
        var result = await Apply(demo,client,target,true);
        Assert.True(result.Outcome == Outcome.Confirmed, System.Text.Json.JsonSerializer.Serialize(result));
        Assert.Equal(80,(await demo.Hubs[0].ReadAsync()).Channels[0].Duty); Assert.Equal(0,demo.Hubs[1].Writes);
    }
    [Fact] public async Task PreviewKeepsOutputAndAdoptionReleasesUsingPreviousExitWhilePreservingOtherPorts()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var target=new EndpointReference(demo.DeviceIds[0],"fan1");
        Assert.Equal(Outcome.Confirmed,(await Apply(demo,client,target,false,63)).Outcome); await client.RefreshAsync(true);
        var original=client.Snapshot!.Configuration.Configuration; var draft=new DeviceCoolingSettingsDraft(client.Snapshot,target);
        var gpu=draft.Sensors.Single(s=>s.Name.StartsWith("GPU"));
        draft.Change(draft.Safety with { ManualProtection=[new(gpu.Target,90,80)] },[..draft.Profiles,Profile(draft)]); draft.Confirmed=true;
        var preview=await client.PreviewSetupAsync(draft.Capture()); Assert.Equal(63,(await demo.Hubs[0].ReadAsync()).Channels[0].Duty);
        Assert.Contains(preview.Edit.Changes,s=>s.Contains("GPU")&&s.Contains("90"));
        Assert.Equal(Outcome.Confirmed,(await client.ApplySetupAsync(preview)).Outcome); var config=client.Snapshot!.Configuration.Configuration;
        Assert.Equal(70,(await demo.Hubs[0].ReadAsync()).Channels[0].Duty);
        var saved=config.Cooling.Endpoints.Single(e=>e.Target==target); Assert.Null(saved.SavedPolicy); Assert.False(saved.ResumeOnStart);
        Assert.Equal(1,saved.Profiles.Single(p=>p.Id=="custom").Revision); Assert.Equal(1,saved.Profiles.Single(p=>p.Id=="balanced").Revision);
        Assert.Equal(SettingsTransfer.Hash(original.Cooling.Endpoints.Where(e=>e.Target!=target)),SettingsTransfer.Hash(config.Cooling.Endpoints.Where(e=>e.Target!=target)));
        Assert.All(client.Snapshot.State.Endpoints,e=>Assert.False(e.Owned));
        var edit=new DeviceCoolingSettingsDraft(client.Snapshot,target);
        edit.Change(edit.Safety,edit.Profiles.Select(p=>p.Id=="custom"?p with { Name="静音",Revision=999 }:p).ToArray()); edit.Confirmed=true;
        var changed=edit.Capture().Configuration.Cooling.Endpoints.Single(e=>e.Target==target);
        Assert.Equal(2,changed.Profiles.Single(p=>p.Id=="custom").Revision); Assert.Equal(1,changed.Profiles.Single(p=>p.Id=="balanced").Revision);
    }
    [Theory] [InlineData("configuration")] [InlineData("epoch")] [InlineData("topology")] [InlineData("sensor")] [InlineData("binding")]
    public async Task ChangesToTheOriginalConfigurationOrSensorIdentityInvalidateTheDraft(string change)
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var snapshot=client.Snapshot!;
        var draft=new DeviceCoolingSettingsDraft(snapshot,new(demo.DeviceIds[0],"fan1")); draft.Change(draft.Safety,[Profile(draft)]); draft.Confirmed=true;
        var sensor=draft.Sensors[0].Target; var inventory=snapshot.State.Inventory;
        var changed=snapshot with { Configuration=snapshot.Configuration with {
            Generation=change=="configuration"?Guid.NewGuid().ToString("N"):snapshot.Configuration.Generation,
            Candidates=snapshot.Configuration.Candidates.Select(b=>change=="binding"&&b.DeviceId==sensor.DeviceId?b with { Fingerprint=new('f',64) }:b).ToArray() },
            State=snapshot.State with { Inventory=inventory with { Epoch=change=="epoch"?Guid.NewGuid().ToString("N"):inventory.Epoch,
                TopologyRevision=inventory.TopologyRevision+(change=="topology"?1:0),
                Devices=inventory.Devices.Select(d=>change=="sensor"&&d.Device.Id==sensor.DeviceId?d with { Generation=d.Generation+1 }:d).ToArray() } } };
        draft.Update(changed); Assert.True(draft.Conflict); Assert.Throws<InvalidOperationException>(()=>draft.Capture());
        draft.Update(snapshot); Assert.True(draft.Conflict); Assert.Equal("水温・CPU",Assert.Single(draft.Profiles).Name);
        Assert.All(demo.Hubs,h=>Assert.Equal(0,h.Writes));
    }
    [Theory] [InlineData("duplicate")] [InlineData("empty")] [InlineData("descending")] [InlineData("threshold")] [InlineData("rpm")] [InlineData("protection")]
    public async Task InvalidCurvesOrTemperatureReferencesCannotBeCaptured(string problem)
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var draft=new DeviceCoolingSettingsDraft(client.Snapshot!,new(demo.DeviceIds[0],"fan1"));
        var profile=Profile(draft); var curve=profile.Curves[0]; var safety=draft.Safety;
        profile=problem switch {
            "duplicate"=>profile with { Curves=[curve,curve] },"empty"=>profile with { Curves=[] },
            "descending"=>profile with { Curves=[curve with { Points=[new(30,80),new(90,30)] }] },
            "threshold"=>profile with { Curves=[curve with { ResumeBelow=curve.Overheat }] },
            "rpm"=>profile with { Curves=[curve with { Sensor=new(demo.DeviceIds[0],"fan1") }] },_=>profile };
        if(problem=="protection") safety=safety with { ManualProtection=[new(curve.Sensor,80,85)] };
        draft.Change(safety,[profile]); draft.Confirmed=true; Assert.Throws<InvalidOperationException>(()=>draft.Capture());
        Assert.All(demo.Hubs,h=>Assert.Equal(0,h.Writes));
    }
    [Fact] public async Task MissingTemperatureDisablesSaveUntilFreshReadWithoutReplacingEdits()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var original=client.Snapshot!;
        var draft=new DeviceCoolingSettingsDraft(original,new(demo.DeviceIds[0],"fan1")); draft.Change(draft.Safety,[Profile(draft)]); draft.Confirmed=true;
        draft.Update(original with { State=original.State with { Telemetry=original.State.Telemetry!.Select(t=>t.Feature==DeviceFeature.Temperature?t with { Stale=true }:t).ToArray() } });
        Assert.False(draft.Conflict); Assert.Contains("温度",draft.Problem); Assert.Throws<InvalidOperationException>(()=>draft.Capture());
        draft.Update(original); Assert.Null(draft.Problem); Assert.Equal("custom",draft.Capture().Configuration.Cooling.Endpoints.Single(e=>e.Target==draft.Target).Profiles[0].Id);
        draft.Change(draft.Safety,draft.Profiles); Assert.False(draft.Confirmed);
    }
    [Fact] public async Task RebindingATemperatureCarrierDoesNotReapproveItsCoolingOrLightingPorts()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var snapshot=client.Snapshot!;
        var carrier=demo.DeviceIds[1]; var target=new EndpointReference(demo.DeviceIds[0],"fan1"); var temperature=new EndpointReference(carrier,"local-temperature");
        var sensor=snapshot.State.Inventory.Devices.SelectMany(d=>d.Device.Endpoints).First(e=>e.Kind==EndpointKind.Sensor) with { Id=temperature.EndpointId };
        snapshot=snapshot with { Configuration=snapshot.Configuration with { Candidates=snapshot.Configuration.Candidates.Select(b=>b.DeviceId==carrier?b with { Fingerprint=new('f',64) }:b).ToArray() },
            State=snapshot.State with { Inventory=snapshot.State.Inventory with { Devices=snapshot.State.Inventory.Devices.Select(d=>d.Device.Id==carrier?d with { Device=d.Device with { Endpoints=[..d.Device.Endpoints,sensor] } }:d).ToArray() },
                Telemetry=[..snapshot.State.Telemetry!,new(temperature,DeviceFeature.Temperature,35,0,false)] } };
        var draft=new DeviceCoolingSettingsDraft(snapshot,target);
        draft.Change(draft.Safety,[new("carrier","ケース温度",0,[new(temperature,[new(30,40),new(60,100)],70,60)],3,2)]); draft.Confirmed=true;
        var edit=draft.Capture(); var config=edit.Configuration;
        Assert.All(config.Cooling.Endpoints.Where(e=>e.Target.DeviceId==carrier),e=> { Assert.False(e.WiringConfirmed); Assert.False(e.ResumeOnStart); Assert.Null(e.SavedPolicy); });
        Assert.All(config.Lighting.Endpoints!.Where(e=>e.Target.DeviceId==carrier),e=>Assert.False(e.WiringConfirmed));
        Assert.All(config.Devices.Devices.Single(d=>d.Id==carrier).Endpoints.Where(e=>e.Kind==EndpointKind.Lighting),e=>Assert.False(e.Setup!.LedCountConfirmed));
        Assert.True(config.Cooling.Endpoints.Single(e=>e.Target==target).WiringConfirmed);
        Assert.Equal(new string('f',64),config.Bindings.Devices.Single(b=>b.DeviceId==carrier).Fingerprint);
        Assert.Contains(edit.Changes,c=>c.Contains("再確認")); Assert.All(demo.Hubs,h=>Assert.Equal(0,h.Writes));
    }
    [Fact] public async Task DeletingTheRunningProfileClearsSavedAutomaticPolicyAndDoesNotRestartIt()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var target=new EndpointReference(demo.DeviceIds[0],"fan1");
        var draft=new DeviceCoolingSettingsDraft(client.Snapshot!,target); draft.Change(draft.Safety,[Profile(draft)]); draft.Confirmed=true;
        Assert.Equal(Outcome.Confirmed,(await client.ApplySetupAsync(await client.PreviewSetupAsync(draft.Capture()))).Outcome);
        Assert.Equal(Outcome.Confirmed,(await Apply(demo,client,target,true)).Outcome); await client.RefreshAsync(true);
        draft=new DeviceCoolingSettingsDraft(client.Snapshot!,target); draft.Change(draft.Safety,[]); draft.Confirmed=true;
        var preview=await client.PreviewSetupAsync(draft.Capture()); Assert.Contains(preview.Edit.Changes,c=>c.Contains("カーブ削除"));
        Assert.Equal(Outcome.Confirmed,(await client.ApplySetupAsync(preview)).Outcome);
        var saved=client.Snapshot!.Configuration.Configuration.Cooling.Endpoints.Single(e=>e.Target==target);
        Assert.Empty(saved.Profiles); Assert.Null(saved.SavedPolicy); Assert.False(saved.ResumeOnStart);
        Assert.Equal(70,(await demo.Hubs[0].ReadAsync()).Channels[0].Duty); Assert.All(client.Snapshot.State.Endpoints,e=>Assert.False(e.Owned));
    }
}
