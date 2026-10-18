using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceRegistrationRemovalTests
{
    private static readonly EndpointReference AT=new("a","temp"), AF=new("a","fan"), AL=new("a","led"),
        BT=new("b","temp"), BF=new("b","fan"), BL=new("b","led");
    private static CoolingTemperatureProfile Profile(EndpointReference sensor)=>new("balanced","標準",1,[new(sensor,[new(30,30),new(90,100)],95,85)],3,2);
    private static CoolingSafety Safety(EndpointReference sensor)=>new(30,100,70,CoolingExitAction.FixedDuty,[new(sensor,95,85)]);
    private static DeviceWorkspaceSnapshot Snapshot()
    {
        var config=ServiceRuntimeBootstrap.MonitoringConfiguration(new string('a',32));
        RegisteredDevice Device(string id)=>new(id,"同じ機種 "+id,"fixture",DeviceKind.Motherboard,
            [new("temp","温度",EndpointKind.Sensor),new("fan","ファン",EndpointKind.Cooling),new("led","照明",EndpointKind.Lighting,Setup:new("RGB",24,true))]);
        var devices=new[]{Device("a"),Device("b")}; var program=new LightingProgram("breathing",[new(0,0,255)]);
        DeviceLightingScene Scene(string id,params EndpointReference[] targets)=>DeviceLightingSceneRequirements.Stamp(new(id,id,targets.Select(t=>new EndpointLighting(t,program)).ToArray(),[]));
        var shared=Scene("shared",AL,BL); var onlyA=Scene("only-a",AL); var onlyB=Scene("only-b",BL);
        config=config with {
            Devices=new(2,devices),Bindings=new(2,new string('a',32),devices.Select(d=>new PhysicalDeviceBinding(d.Id,d.Id,"fixture",new string('a',64))).ToArray()),
            Cooling=new(2,[new(AF,true,Safety(AT),[Profile(AT)]),new(BF,true,Safety(BT),[Profile(BT)])],
                [new("shared","両方",EndpointKind.Cooling,[AF,BF],3),new("only-a","片方",EndpointKind.Cooling,[AF])],[Profile(AT)]),
            Lighting=config.Lighting with {
                Scenes=[shared,onlyA,onlyB,DeviceLightingSceneRequirements.Stamp(new("sync","同期",[],[new([AL,BL],program)]))],
                Groups=[new("shared","両方",EndpointKind.Lighting,[AL,BL],3),new("only-a","片方",EndpointKind.Lighting,[AL]),new("only-b","残る",EndpointKind.Lighting,[BL])],
                Names=[new(AL,"前面"),new(BL,"背面")],SyncTargets=[AL,BL],
                Endpoints=[new(AL,true,new(LightingExitAction.Off)),new(BL,true,new(LightingExitAction.Off))],
                Preferences=[new(AL,program),new(BL,program)],SavedSynchronization=[new([AL,BL],program)],SavedFrameGroups=[[AL,BL]],
                Adjustments=[new("all",100,false,shared),new("shared",100,false,shared),new("only-a",100,false,onlyA),new("only-b",100,false,onlyB)],
                TargetAdjustments=[new(AL,program,new(),new(),new()),new(BL,program,new(),new(),new())],Revision=8 },
            Dashboard=new(2,[new(AT,DeviceFeature.Temperature,"A温度","temperature"),new(BF,DeviceFeature.Rpm,"Bファン","fan")]),
            Preferences=config.Preferences with {
                Notifications=new(Rules:[new(new string('a',32),AT,DeviceFeature.Temperature,"A通知",90),new(new string('b',32),BT,DeviceFeature.Temperature,"B通知",90)]) },
            Migration=new(2,"native-v2",new string('a',64),[new("fixture","old-a","temp",AT),new("fixture","old-b","temp",BT)],[])
        };
        DeviceConfigurationCodec.Validate(config);
        var observations=devices.Select(d=>new DeviceInstance(new(d.Id,d.Provider,d.Name,d.Kind,IdentityConfidence.SerialBound,d.Id,"fixture",[d.Id],
            d.Endpoints.Select(e=>new DeviceEndpoint(e.Id,e.Name,e.Kind,[new(e.Kind==EndpointKind.Sensor?DeviceFeature.Temperature:e.Kind==EndpointKind.Cooling?DeviceFeature.FixedDuty:DeviceFeature.FixedColor,
                ImplementationSupport.Supported,VerificationEvidence.Automated,DeviceAvailability.Ready)])).ToArray()),d.Id=="a"?1:2,true)).ToArray();
        return new(new(new("epoch",1,observations,3),[],false,null),new(new string('b',32),config,config.Bindings.Devices),0,true,null);
    }
    private static DeviceWorkspaceSnapshot Config(DeviceWorkspaceSnapshot s,DeviceConfigurationPackage c)=>s with { Configuration=s.Configuration with { Configuration=c } };
    [Fact] public void RemovalTrimsEveryReferenceAndKeepsIndependentSettingsWithoutMutatingInput()
    {
        var s=Snapshot(); var before=DeviceConfigurationCodec.SerializePackage(s.Configuration.Configuration);
        var edit=DeviceRegistrationRemoval.Capture(s,"a"); var c=edit.Configuration;
        Assert.True(edit.Matches(s)); DeviceConfigurationCodec.Validate(c);
        Assert.Equal(before,DeviceConfigurationCodec.SerializePackage(s.Configuration.Configuration));
        Assert.Equal("b",Assert.Single(c.Devices.Devices).Id); Assert.Equal("b",Assert.Single(c.Bindings.Devices).DeviceId);
        Assert.Equal(BF,Assert.Single(c.Cooling.Endpoints).Target); Assert.Empty(c.Cooling.ProfileTemplates!);
        Assert.Equal(new[]{BF},Assert.Single(c.Cooling.Groups).Targets); Assert.Equal(4,c.Cooling.Groups[0].Revision);
        Assert.Equal(3,c.Lighting.Scenes.Length); Assert.DoesNotContain(c.Lighting.Scenes,x=>x.Id=="only-a");
        Assert.Equal(2,c.Lighting.Scenes.Single(x=>x.Id=="shared").Revision); Assert.Equal(1,c.Lighting.Scenes.Single(x=>x.Id=="only-b").Revision);
        Assert.All(c.Lighting.Scenes,x=>Assert.True(DeviceLightingSceneRequirements.Matches(x)));
        Assert.Equal(new[]{BL},Assert.Single(c.Lighting.SavedSynchronization!).Targets); Assert.Equal(new[]{BL},Assert.Single(c.Lighting.SavedFrameGroups!));
        Assert.Equal(BL,Assert.Single(c.Lighting.TargetAdjustments!).Target); Assert.Equal(BL,Assert.Single(c.Lighting.Preferences!).Target);
        Assert.Equal(BL,Assert.Single(c.Lighting.Endpoints!).Target); Assert.Equal(BL,Assert.Single(c.Lighting.Names).Target); Assert.Equal(new[]{BL},c.Lighting.SyncTargets);
        Assert.Equal(3,c.Lighting.Adjustments.Length); Assert.Equal(9,c.Lighting.Revision);
        Assert.Equal(BF,Assert.Single(c.Dashboard.Items).Target); Assert.Equal(BT,Assert.Single(c.Preferences.Notifications.Rules!).Target);
        Assert.Equal(BT,Assert.Single(c.Migration.References).Target);
        Assert.Contains(edit.Changes,x=>x.Contains("すべての制御")); Assert.Contains(edit.Changes,x=>x.Contains("同じ機種 a"));
    }
    [Fact] public void RemovingASelectedTemperatureClearsOnlyItsDialReference()
    {
        var snapshot = Snapshot(); var c = snapshot.Configuration.Configuration;
        c = c with { Devices = c.Devices with { Devices = c.Devices.Devices.Select(d => d with {
            Kind = d.Id == "a" ? DeviceKind.Processor : DeviceKind.Graphics }).ToArray() },
            Preferences = c.Preferences with { DialDisplay = new(AT, BT) } };
        DeviceConfigurationCodec.Validate(c);
        var removed = DeviceRegistrationRemoval.Capture(Config(snapshot, c), "a").Configuration;
        Assert.Null(removed.Preferences.DialDisplay!.Cpu); Assert.Equal(BT, removed.Preferences.DialDisplay.Gpu);
        Assert.Equal(AT, c.Preferences.DialDisplay!.Cpu);
    }
    [Theory] [InlineData("protection")] [InlineData("stop")] [InlineData("profile")] [InlineData("saved-profile")] [InlineData("saved-safety")]
    public void ATemperatureUsedByAnotherFanCannotBeRemovedOrWeakenItsSafety(string source)
    {
        var s=Snapshot(); var c=s.Configuration.Configuration; var b=c.Cooling.Endpoints.Single(e=>e.Target==BF);
        b=source switch {
            "protection"=>b with { Safety=Safety(AT) },
            "stop"=>b with { Safety=b.Safety with { Stop=new([new(AT,40)],60,3) } },
            "profile"=>b with { Profiles=[Profile(AT)] },
            "saved-profile"=>b with { SavedPolicy=new(FanMode.Auto,Profile:Profile(AT)) },
            _=>b with { SavedPolicy=new(FanMode.Manual,50,SafetySnapshot:Safety(AT)) }
        };
        c=c with { Cooling=c.Cooling with { Endpoints=c.Cooling.Endpoints.Select(e=>e.Target==BF?b:e).ToArray() } };s=Config(s,c);
        DeviceConfigurationCodec.Validate(c); var before=DeviceConfigurationCodec.SerializePackage(c);
        Assert.Contains("同じ機種 b",DeviceRegistrationRemoval.Problem(s,"a"));
        Assert.Throws<InvalidOperationException>(()=>DeviceRegistrationRemoval.Capture(s,"a"));
        Assert.Equal(before,DeviceConfigurationCodec.SerializePackage(c));
    }
    [Theory] [InlineData("epoch")] [InlineData("generation")] [InlineData("topology")] [InlineData("device-generation")] [InlineData("connection")] [InlineData("exit")]
    public void ChangedConnectionOrConfigurationInvalidatesTheReviewedRemoval(string change)
    {
        var s=Snapshot(); var edit=DeviceRegistrationRemoval.Capture(s,"a");
        var next=change switch {
            "epoch"=>s with { State=s.State with { Inventory=s.State.Inventory with { Epoch="new" } } },
            "generation"=>s with { Configuration=s.Configuration with { Generation=new string('c',32) } },
            "topology"=>s with { State=s.State with { Inventory=s.State.Inventory with { TopologyRevision=4 } } },
            "device-generation"=>s with { State=s.State with { Inventory=s.State.Inventory with { Devices=s.State.Inventory.Devices.Select(d=>d.Device.Id=="a"?d with { Generation=4 }:d).ToArray() } } },
            "connection"=>s with { Connected=false }, _=>s with { ExitAttempted=true }
        };
        Assert.False(edit.Matches(next));
    }
    [Fact] public void EmptyAndUnseenRegisteredDeviceCanBeRemovedToFreeASavedRegistrationSlot()
    {
        var s=Snapshot(); var c=ServiceRuntimeBootstrap.MonitoringConfiguration(new string('a',32));
        c=c with { Devices=new(2,Enumerable.Range(0,32).Select(i=>new RegisteredDevice("unused-"+i,"保存した機器","fixture",DeviceKind.Controller,[])).ToArray()) };
        s=Config(s,c); var edit=DeviceRegistrationRemoval.Capture(s,"unused-0");Assert.True(edit.Matches(s));
        Assert.Equal(31,edit.Configuration.Devices.Devices.Length); Assert.Equal(c.Lighting.Revision,edit.Configuration.Lighting.Revision);
        DeviceConfigurationCodec.Validate(edit.Configuration with { Devices=new(2,[..edit.Configuration.Devices.Devices,new("new","新規","fixture",DeviceKind.Controller,[])]) });
        Assert.Throws<InvalidOperationException>(()=>DeviceRegistrationRemoval.Capture(s,"unknown"));
    }
    private static Task<DeviceOperationResult> Duty(DeviceWorkspaceClient client,EndpointReference target,int duty)
    {
        var s=client.Snapshot!; var e=s.State.Endpoints.Single(e=>e.Target==target);
        return client.ApplyCoolingAsync(new(new(s.State.Inventory.Epoch,s.State.Inventory.TopologyRevision,s.Configuration.Generation,new(target,e.Generation,e.PolicyRevision)),DeviceOperation.SetDuty,duty));
    }
    [Fact] public async Task ApplyingRemovalUsesSavedExitRevokesControlAndPreservesOtherDeviceConfiguration()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var id=demo.DeviceIds[0]; var target=new EndpointReference(id,"fan1");
        await Duty(client,target,62); var before=client.Snapshot!.Configuration.Configuration; var writes=demo.Hubs[0].Writes;
        var preview=await client.PreviewRegistrationRemovalAsync(DeviceRegistrationRemoval.Capture(client.Snapshot,id));
        Assert.Equal(writes,demo.Hubs[0].Writes); Assert.True(client.Snapshot!.State.Endpoints.Single(e=>e.Target==target).Owned);
        Assert.Equal(Outcome.Confirmed,(await client.ApplyRegistrationRemovalAsync(preview)).Outcome);
        Assert.Equal(writes+1,demo.Hubs[0].Writes); Assert.Equal(70,(await demo.Hubs[0].ReadAsync()).Channels.Single(c=>c.Id=="fan1").Duty);
        var after=client.Snapshot!.Configuration.Configuration; Assert.DoesNotContain(after.Devices.Devices,d=>d.Id==id);
        Assert.Equal(before.Cooling.Endpoints.Count(e=>e.Target.DeviceId!=id),after.Cooling.Endpoints.Length);
        Assert.NotEqual(Outcome.Confirmed,(await Duty(client,target,60)).Outcome); Assert.Equal(writes+1,demo.Hubs[0].Writes);
        Assert.False(new DeviceEndpointSetupDraft(client.Snapshot!,target).Values.Confirmed);
    }
    [Fact] public async Task DisconnectedUnownedRegistrationCanBeRemovedAndReconnectHasNoOldPermission()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient();await client.ConnectAsync();var id=demo.DeviceIds[0];
        var stale=await client.PreviewRegistrationRemovalAsync(DeviceRegistrationRemoval.Capture(client.Snapshot!,id));
        demo.Hubs[0].Disconnected=true;await demo.Runtime.PollAsync();await client.RefreshAsync(true);
        await Assert.ThrowsAsync<IOException>(()=>client.ApplyRegistrationRemovalAsync(stale));
        var preview=await client.PreviewRegistrationRemovalAsync(DeviceRegistrationRemoval.Capture(client.Snapshot!,id));
        Assert.Equal(Outcome.Confirmed,(await client.ApplyRegistrationRemovalAsync(preview)).Outcome);
        demo.Hubs[0].Disconnected=false;await demo.Runtime.PollAsync();await demo.Runtime.PollAsync();await client.RefreshAsync(true);
        Assert.DoesNotContain(client.Snapshot!.Configuration.Configuration.Devices.Devices,d=>d.Id==id);
        Assert.False(new DeviceEndpointSetupDraft(client.Snapshot,new(id,"fan1")).Values.Confirmed);
        Assert.All(demo.Hubs,h=> { Assert.Equal(0,h.Writes);Assert.Equal(0,h.LightingWrites); });
    }
    [Fact] public async Task FailedExitRetainsTheRegistrationGenerationAndRecoveryObligation()
    {
        using var dir=new ServiceTestDirectory();await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient();await client.ConnectAsync();var id=demo.DeviceIds[0];var target=new EndpointReference(id,"fan3");
        await Duty(client,target,62);var before=client.Snapshot!.Configuration.Generation;
        var preview=await client.PreviewRegistrationRemovalAsync(DeviceRegistrationRemoval.Capture(client.Snapshot,id));
        demo.Hubs[0].FailNextFanWrite=true;await Assert.ThrowsAsync<IOException>(()=>client.ApplyRegistrationRemovalAsync(preview));
        Assert.Equal(before,client.Snapshot!.Configuration.Generation);Assert.Contains(client.Snapshot.Configuration.Configuration.Devices.Devices,d=>d.Id==id);
        Assert.True(client.Snapshot.State.Endpoints.Single(e=>e.Target==target).Owned);Assert.NotNull(client.Snapshot.State.LifecycleError);
    }
    [Fact] public async Task RemovingAHubPreservesUnrelatedDialTemperatureSelections()
    {
        using var dir=new ServiceTestDirectory();await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient();await client.ConnectAsync();var s=client.Snapshot!;var c=s.Configuration.Configuration;var id=demo.DeviceIds[0];
        var cpu=c.Dashboard.Items.Single(i=>i.Name=="CPU").Target;var gpu=c.Dashboard.Items.Single(i=>i.Name=="GPU").Target;
        c=c with { Preferences=c.Preferences with { DialDisplay=new(cpu,gpu) } };
        var result=DeviceRegistrationRemoval.Capture(Config(s,c),id).Configuration;
        Assert.Equal(cpu,result.Preferences.DialDisplay!.Cpu);Assert.Equal(gpu,result.Preferences.DialDisplay.Gpu);
    }
    [Fact] public void RemovalSmokeRequiresTheExclusiveEmbeddedDemoAndCannotConnectToAnExternalService()
    {
        string[] common=["--devices-preview","--demo","--data-dir",Path.Combine(Path.GetTempPath(),"flowdeck-removal-options")];
        var valid=DeviceWorkspaceOptions.Parse([..common,"--smoke","--registration-removal"]);
        Assert.True(valid.RegistrationRemoval);Assert.False(valid.External);
        Assert.Throws<ArgumentException>(()=>DeviceWorkspaceOptions.Parse([..common,"--registration-removal"]));
        Assert.Throws<ArgumentException>(()=>DeviceWorkspaceOptions.Parse([..common,"--smoke","--registration-removal","--endpoint-setup"]));
        Assert.Throws<ArgumentException>(()=>DeviceWorkspaceOptions.Parse([..common,"--smoke","--registration-removal","--devices-pipe","FlowDeck.Devices.Demo."+new string('a',32),"--server-pid","1"]));
    }

}