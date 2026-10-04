using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceConfigurationAuthorityTests
{
    private static DeviceServiceRequest Trial(Rig r)=>r.Duty(0) with { Operation=DeviceOperation.CalibrateCooling,Duty=null,
        Calibration=new(r.Driver.BindingCandidate!,100,45,true) };
    [Fact] public async Task StartupCannotBeReenabledWhileCalibrationIsWaitingBeforeDriverOwnership()
    {
        await using var r=await Rig.Create(); var config=r.Startup.Configuration; var endpoint=config.Cooling.Endpoints[0]; var state=r.Startup.Runtime.State();
        var bindings=r.Startup.Runtime.PhysicalCandidates(); Assert.Null(DeviceCoolingStartup.Problem(endpoint,config.Bindings,state,bindings));
        state=state with { Endpoints=state.Endpoints.Select(e=>e.Target==endpoint.Target?e with { Outcome=Outcome.Pending,RequestedDuty=100,Owned=false }:e).ToArray() };
        Assert.NotNull(DeviceCoolingStartup.Problem(endpoint,config.Bindings,state,bindings));
    }
    [Fact] public async Task CalibrationDurablyPausesStartupBeforeAnyWriteAndNeverChangesTheSavedFloorOrPolicy()
    {
        await using var r=await Rig.Create(); var before=r.Startup.Configuration.Cooling.Endpoints[0]; Assert.True(before.ResumeOnStart);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        r.Hub.BeforeFanWrite=ct=>
        {
            var saved=DeviceConfigurationCodec.Read(r.Store.ReadActive()!.Files).Cooling.Endpoints[0];
            Assert.False(saved.ResumeOnStart); Assert.Equal(65,saved.SavedPolicy!.Duty); Assert.Equal(30,saved.Safety.Minimum);
            entered.TrySetResult(); return Task.CompletedTask;
        };
        var request=Trial(r); var pending=r.Startup.Runtime.ExecuteAsync(request); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var cancel=r.Duty(0) with { Operation=DeviceOperation.CancelCoolingCalibration,Duty=null,Targets=null,TopologyRevision=0,ResultId=request.Id };
        await r.Startup.Runtime.ExecuteAsync(cancel); Assert.True((await pending).Result!.Targets[0].Calibration!.Restored);
        var after=DeviceConfigurationCodec.Read(r.Store.ReadActive()!.Files).Cooling.Endpoints[0];
        Assert.False(after.ResumeOnStart); Assert.True(DeviceConfigurationRules.SameSafety(before.Safety,after.Safety)); Assert.Equal(65,after.SavedPolicy!.Duty);
        Assert.Equal(45,(await r.Hub.ReadAsync()).Channels[0].Duty);
        Assert.False((await r.Startup.Runtime.ExecuteAsync(r.Duty(0) with { Operation=DeviceOperation.RestoreSavedCooling,Duty=null })).Success);
    }
    [Fact] public async Task CalibrationPausePersistenceFailurePreventsHardwareAccess()
    {
        await using var r=await Rig.Create(); var generation=r.Startup.Generation;
        r.Store.Checkpoint=phase=>{ if(phase=="before-switch") throw new IOException("disk unavailable"); };
        var result=await r.Startup.Runtime.ExecuteAsync(Trial(r));
        Assert.Equal(Outcome.Rejected,result.Result!.Outcome); Assert.Equal(0,r.Hub.Writes); Assert.Equal(generation,r.Startup.Generation);
        Assert.True(r.Startup.Configuration.Cooling.Endpoints[0].ResumeOnStart);
    }
    [Fact] public async Task CalibrationPauseInvalidatesAnOldConfigurationPreview()
    {
        await using var r=await Rig.Create(); var transfer=await r.Upload(r.Changed());
        var request=Trial(r); var pending=r.Startup.Runtime.ExecuteAsync(request);
        await Task.Run(async()=>{ while(r.Hub.Writes==0) await Task.Delay(5); }).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False((await r.Send(r.Apply(transfer))).Success);
        await r.Startup.Runtime.ExecuteAsync(r.Duty(0) with { Operation=DeviceOperation.CancelCoolingCalibration,Duty=null,Targets=null,TopologyRevision=0,
            ResultId=request.Id });
        var report=(await pending.WaitAsync(TimeSpan.FromSeconds(8))).Result!.Targets[0].Calibration!;
        Assert.True(report.Restored);
        Assert.Equal(45,(await r.Hub.ReadAsync()).Channels[0].Duty); Assert.Equal(30,r.Startup.Configuration.Cooling.Endpoints[0].Safety.Minimum);
        Assert.False(r.Startup.Configuration.Cooling.Endpoints[0].ResumeOnStart);
    }
    [Fact] public async Task RuntimeReconfigurationWaitsForCalibrationToRestoreTheOriginalControl()
    {
        await using var r=await Rig.Create(); var pending=r.Startup.Runtime.ExecuteAsync(Trial(r));
        await Task.Run(async()=>{ while(r.Hub.Writes==0) await Task.Delay(5); }).WaitAsync(TimeSpan.FromSeconds(3));
        var applied=false;
        var reconfigure=r.Startup.Runtime.ReconfigureAsync(r.Hello.Session,r.Startup.Runtime.State().Inventory.TopologyRevision,async (_,ct)=>
        {
            Assert.Equal(45,(await r.Hub.ReadAsync(ct)).Channels[0].Duty); applied=true;
            return r.Startup.Runtime.PhysicalCandidates();
        });
        var report=(await pending.WaitAsync(TimeSpan.FromSeconds(3))).Result!.Targets[0].Calibration!;
        await reconfigure.WaitAsync(TimeSpan.FromSeconds(3)); Assert.True(applied); Assert.True(report.Restored); Assert.False(report.Completed);
        Assert.False(r.Startup.Runtime.State().Endpoints.Single(e=>e.Target.EndpointId=="fan1").Owned);
    }
}

public class DeviceCoolingCalibrationPipeTests
{
    [Fact] public async Task AuthenticatedDemoPipeSupportsTrialProgressCancellationAndFinalReportsFromAnEmptySetup()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path,monitoringOnly:true);
        await using var raw=DeviceServicePipeClient.ForDemo(demo.PipeName,Environment.ProcessId); await raw.ConnectAsync();
        Assert.Contains("cooling-calibration-v2",raw.Hello!.Features);
        var state=(await raw.SendAsync(raw.Request(DeviceOperation.State))).State!; var target=state.Endpoints.Single(e=>e.Target==new EndpointReference(demo.DeviceIds[0],"fan1"));
        var binding=demo.Runtime.PhysicalCandidates().Single(b=>b.DeviceId==target.Target.DeviceId);
        var request=raw.Request(DeviceOperation.CalibrateCooling,topology:state.Inventory.TopologyRevision,targets:[new(target.Target,target.Generation,target.PolicyRevision)]) with {
            Calibration=new(binding,100,45,true) };
        var pending=raw.SendAsync(request);
        await Task.Run(async()=>{ while(demo.Hubs[0].Writes==0) await Task.Delay(5); }).WaitAsync(TimeSpan.FromSeconds(3));
        var progress=await raw.SendAsync(raw.Request(DeviceOperation.Result,resultId:request.Id)); Assert.Equal(Outcome.Pending,progress.Result!.Outcome);
        Assert.Equal(100,progress.Result.Targets[0].RequestedDuty);
        var cancel=await raw.SendAsync(raw.Request(DeviceOperation.CancelCoolingCalibration,resultId:request.Id)); Assert.Equal(Outcome.Accepted,cancel.Result!.Outcome);
        var report=Assert.Single((await pending).Result!.Targets).Calibration!; Assert.True(report.Restored); Assert.False(report.Completed);
        state=(await raw.SendAsync(raw.Request(DeviceOperation.State))).State!;
        Assert.Equal(report,state.Endpoints.Single(e=>e.Target==target.Target).Calibration); Assert.False(state.Endpoints.Single(e=>e.Target==target.Target).Owned);
        Assert.Equal(45,(await demo.Hubs[0].ReadAsync()).Channels[0].Duty); Assert.Equal(0,demo.Hubs[1].Writes);
    }
}
