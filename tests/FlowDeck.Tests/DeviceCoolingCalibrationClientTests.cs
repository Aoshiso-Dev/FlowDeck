using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceCoolingCalibrationClientTests
{
    private static DeviceCoolingCalibrationDraft Draft(DeviceWorkspaceClient client,DemoDeviceWorkspace demo)=>new(client.Snapshot!,new(demo.DeviceIds[0],"fan1"));
    private static async Task Begun(DemoDeviceWorkspace demo)=>await Task.Run(async()=>{ while(demo.Hubs[0].Writes==0) await Task.Delay(5); }).WaitAsync(TimeSpan.FromSeconds(3));
    [Fact] public async Task SuccessfulTrialsEnableLowerOutputAndExplicitAdoptionUsesMeasuredMinimumWithoutAcknowledgement()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path,monitoringOnly:true);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var draft=Draft(client,demo);
        Assert.True(client.SupportsCoolingCalibration); Assert.False(draft.CanReduce); Assert.NotNull(draft.Capture());
        Assert.Throws<ArgumentOutOfRangeException>(()=>draft.ChangeDuty(40));
        var edit=draft.Capture(); draft.Begin(); var result=await client.StartCoolingCalibrationAsync(edit); draft.Complete(result,client.Snapshot!);
        Assert.Equal(Outcome.Confirmed,result.Outcome); Assert.True(draft.CanReduce); Assert.False(draft.Confirmed); Assert.True(draft.CanAdopt);
        Assert.Equal(45,(await demo.Hubs[0].ReadAsync()).Channels[0].Duty); Assert.Empty(client.Snapshot!.Configuration.Configuration.Cooling.Endpoints);
        draft.ChangeDuty(35); Assert.False(draft.Confirmed); edit=draft.Capture(); draft.Begin();
        result=await client.StartCoolingCalibrationAsync(edit); draft.Complete(result,client.Snapshot!); Assert.Equal(Outcome.Confirmed,result.Outcome);
        Assert.True(draft.CanAdopt); Assert.False(draft.MinimumConfirmed);
        var setup=draft.AdoptMinimum(); Assert.Equal(35,setup.Values.Safety!.Minimum); Assert.Null(setup.Values.Safety.Fallback); Assert.Null(setup.Values.Safety.Exit);
        Assert.False(setup.Values.Confirmed); Assert.Empty(client.Snapshot!.Configuration.Configuration.Cooling.Endpoints); Assert.Equal(0,demo.Hubs[1].Writes);
        Assert.Throws<InvalidOperationException>(()=>setup.Capture());
        setup.Change(setup.Values with { Safety=setup.Values.Safety! with { Fallback=100,Exit=70 } }); await client.ApplySetupAsync(await client.PreviewSetupAsync(setup.Capture()));
        Assert.Equal(35,Assert.Single(client.Snapshot!.Configuration.Configuration.Cooling.Endpoints).Safety.Minimum); Assert.Equal(4,demo.Hubs[0].Writes);
    }
    [Theory] [InlineData("session")] [InlineData("epoch")] [InlineData("configuration")] [InlineData("topology")] [InlineData("generation")] [InlineData("policy")] [InlineData("binding")]
    public async Task AChangedDeviceOrSettingsVersionPreventsCaptureWhileTheOutputChoiceIsPreserved(string change)
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path,monitoringOnly:true);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var snapshot=client.Snapshot!; var draft=Draft(client,demo); draft.Confirmed=true;
        snapshot=change switch {
            "session"=>snapshot with { Session=Guid.NewGuid().ToString("N") },
            "epoch"=>snapshot with { State=snapshot.State with { Inventory=snapshot.State.Inventory with { Epoch=Guid.NewGuid().ToString("N") } } },
            "configuration"=>snapshot with { Configuration=snapshot.Configuration with { Generation=Guid.NewGuid().ToString("N") } },
            "topology"=>snapshot with { State=snapshot.State with { Inventory=snapshot.State.Inventory with { TopologyRevision=99 } } },
            "generation"=>snapshot with { State=snapshot.State with { Inventory=snapshot.State.Inventory with { Devices=snapshot.State.Inventory.Devices.Select(d=>d.Device.Id==draft.Target.DeviceId?d with { Generation=d.Generation+1 }:d).ToArray() } } },
            "policy"=>snapshot with { State=snapshot.State with { Endpoints=snapshot.State.Endpoints.Select(e=>e.Target==draft.Target?e with { PolicyRevision=e.PolicyRevision+1 }:e).ToArray() } },
            _=>snapshot with { Configuration=snapshot.Configuration with { Candidates=[] } }
        };
        draft.Update(snapshot); Assert.True(draft.Conflict); Assert.Equal(100,draft.Duty); Assert.Throws<InvalidOperationException>(()=>draft.Capture());
        Assert.All(demo.Hubs,h=>Assert.Equal(0,h.Writes));
    }
    [Theory] [InlineData("zero")] [InlineData("stale")] [InlineData("disconnected")] [InlineData("exit")] [InlineData("pending")] [InlineData("owned")]
    public async Task UnavailableRotationOrOwnershipPreventsTrialCapture(string reason)
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path,monitoringOnly:true);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var snapshot=client.Snapshot!; var draft=Draft(client,demo); draft.Confirmed=true;
        snapshot=reason switch {
            "zero"=>snapshot with { State=snapshot.State with { Telemetry=snapshot.State.Telemetry!.Select(t=>t.Target==draft.Target&&t.Feature==DeviceFeature.Rpm?t with { Value=0 }:t).ToArray() } },
            "stale"=>snapshot with { ElapsedSeconds=4 },
            "disconnected"=>snapshot with { Connected=false },
            "exit"=>snapshot with { ExitAttempted=true },
            _=>snapshot with { State=snapshot.State with { Endpoints=snapshot.State.Endpoints.Select(e=>e.Target==draft.Target?e with { Owned=reason=="owned",Outcome=reason=="pending"?Outcome.Pending:e.Outcome }:e).ToArray() } }
        };
        draft.Update(snapshot); Assert.NotNull(draft.Problem); Assert.Throws<InvalidOperationException>(()=>draft.Capture());
    }
    [Fact] public async Task PollingAndAnotherEndpointRemainResponsiveAndCancelRestoresOnlyTheTrial()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var draft=Draft(client,demo); draft.Confirmed=true;
        var edit=draft.Capture(); draft.Begin(); var pending=client.StartCoolingCalibrationAsync(edit); await Begun(demo); Assert.True(client.CalibrationRunning);
        await client.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(2)); Assert.True(client.Snapshot!.ElapsedSeconds<3);
        Assert.Equal(100,client.Snapshot.State.Telemetry!.Single(t=>t.Target==draft.Target&&t.Feature==DeviceFeature.DutyRead).Value);
        draft.Update(client.Snapshot); Assert.Equal(100,draft.CurrentDuty); Assert.Contains("45%",draft.Restoration);
        var other=new EndpointReference(demo.DeviceIds[1],"fan1"); var row=client.Snapshot.State.Endpoints.Single(e=>e.Target==other);
        var result=await client.ApplyCoolingAsync(new(new(client.Snapshot.State.Inventory.Epoch,client.Snapshot.State.Inventory.TopologyRevision,client.Snapshot.Configuration.Generation,
            new(other,row.Generation,row.PolicyRevision)),DeviceOperation.SetDuty,62)).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(Outcome.Confirmed,result.Outcome); Assert.False(pending.IsCompleted);
        await client.CancelCoolingCalibrationAsync(); result=await pending; Assert.True(result.Targets[0].Calibration!.Restored); Assert.False(client.CalibrationRunning);
        Assert.Equal(45,(await demo.Hubs[0].ReadAsync()).Channels[0].Duty); Assert.Equal(62,(await demo.Hubs[1].ReadAsync()).Channels[0].Duty);
    }
    [Fact] public async Task NormalExitCancelsAnActiveCalibrationAndWaitsForItsRestoration()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path,monitoringOnly:true);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var draft=Draft(client,demo); draft.Confirmed=true;
        var pending=client.StartCoolingCalibrationAsync(draft.Capture()); await Begun(demo);
        Assert.True((await client.ReleaseAsync().WaitAsync(TimeSpan.FromSeconds(3))).Success);
        Assert.True((await pending).Targets[0].Calibration!.Restored); Assert.False(client.CalibrationRunning); Assert.Equal(45,(await demo.Hubs[0].ReadAsync()).Channels[0].Duty);
    }
    [Fact] public async Task AChangedHubReturnDutyUsesTheFreshRestorationValueWithoutAcknowledgement()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path,monitoringOnly:true);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var snapshot=client.Snapshot!; var draft=Draft(client,demo); draft.Confirmed=true;
        draft.Update(snapshot with { State=snapshot.State with { Telemetry=snapshot.State.Telemetry!.Select(t=>t.Target==draft.Target&&t.Feature==DeviceFeature.DutyRead?t with { Value=46 }:t).ToArray() } });
        draft.Confirmed=false; Assert.Contains("46%",draft.Restoration); Assert.False(draft.Conflict);
        Assert.Equal(46,draft.Capture().Trial.BeforeDuty);
    }
    [Fact] public async Task OldTrialProofAndReportsAreNotReusedAfterAClientReconnect()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path,monitoringOnly:true);
        await using(var first=demo.CreateClient())
        {
            await first.ConnectAsync(); var draft=Draft(first,demo); draft.Confirmed=true; Assert.Equal(Outcome.Confirmed,(await first.StartCoolingCalibrationAsync(draft.Capture())).Outcome);
            Assert.True(first.Snapshot!.State.Endpoints.Single(e=>e.Target==draft.Target).CalibrationReady); await first.ReleaseAsync();
        }
        await using var second=demo.CreateClient(); await second.ConnectAsync(); var reopened=Draft(second,demo); reopened.MinimumConfirmed=true;
        Assert.NotNull(reopened.Result); Assert.False(reopened.CanReduce); Assert.False(reopened.CanAdopt); Assert.Throws<ArgumentOutOfRangeException>(()=>reopened.ChangeDuty(30));
    }
}
