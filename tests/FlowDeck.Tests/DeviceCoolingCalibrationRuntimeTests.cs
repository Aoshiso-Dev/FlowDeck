using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceCoolingCalibrationRuntimeTests
{
    private const string Installation="0123456789abcdef0123456789abcdef";
    private sealed class Rig : IAsyncDisposable
    {
        public readonly ServiceTestDirectory Directory=new();
        public readonly DemoHub Hub=new(){DelayMs=0};
        public readonly DeviceResourceScheduler Scheduler=new();
        public HubDeviceDriver Driver=null!;
        public DeviceControlRuntime Runtime=null!;
        public DeviceServiceHello Hello=null!;
        private long sequence;
        public static async Task<Rig> Create(bool configured=false,TimeProvider? clock=null)
        {
            var r=new Rig(); r.Driver=new(Installation,DemoHub.Identity.Id,r.Hub,r.Directory.Path,clock); await r.Driver.InitializeAsync();
            if(configured) r.Driver.Configure(r.Driver.BindingCandidate,[r.Config("fan1"),r.Config("fan2")]);
            r.Runtime=new([r.Driver],["user"],new TestLog(),clock,r.Scheduler,physicalBindings:configured?[r.Driver.BindingCandidate!]:[]);
            r.Hello=r.Runtime.OpenSession("user"); return r;
        }
        public CoolingEndpointConfiguration Config(string id)=>new(new(Driver.DeviceId,id),true,new(30,100,70,CoolingExitAction.FixedDuty,[]),[]);
        public DeviceServiceRequest Request(DeviceOperation operation=DeviceOperation.CalibrateCooling,int duty=100,string endpoint="fan1",string? resultId=null)
        {
            var state=Runtime.State(); var target=state.Endpoints.Single(e=>e.Target==new EndpointReference(Driver.DeviceId,endpoint));
            var targeted=DeviceServiceProtocol.IsWrite(operation);
            return new(2,Hello.Epoch,Hello.Session,Guid.NewGuid().ToString("N"),++sequence,"test",operation,targeted?state.Inventory.TopologyRevision:0,
                targeted?[new(target.Target,target.Generation,target.PolicyRevision)]:null,operation==DeviceOperation.SetDuty?duty:null,ResultId:resultId,
                Calibration:operation==DeviceOperation.CalibrateCooling?new(Driver.BindingCandidate!,duty,(int)Math.Round(state.Telemetry!.Single(t=>t.Target==target.Target&&t.Feature==DeviceFeature.DutyRead).Value!.Value),true):null);
        }
        public async Task Begun()=>await Task.Run(async()=>{ while(Hub.Writes==0) await Task.Delay(5); }).WaitAsync(TimeSpan.FromSeconds(3));
        public HubEndpointRecoveryJournal Journal()=>new(Directory.Path,Driver.DeviceId);
        public async ValueTask DisposeAsync() { Hub.BeforeFanWrite=null; Hub.Disconnected=false; await Runtime.DisposeAsync(); Directory.Dispose(); }
    }
    [Fact] public async Task UnconfiguredEndpointRequiresFullOutputProofAndDoesNotAcquirePermanentSetup()
    {
        await using var r=await Rig.Create(); Assert.False((await r.Runtime.ExecuteAsync(r.Request(duty:30))).Success); Assert.Equal(0,r.Hub.Writes);
        var request=r.Request(); var reply=await r.Runtime.ExecuteAsync(request); var report=Assert.Single(reply.Result!.Targets).Calibration!;
        Assert.Equal(Outcome.Confirmed,report.Outcome); Assert.True(report.Completed&&report.Restored); Assert.InRange(report.Samples,1,100);
        Assert.Equal(45,report.Before!.Duty); Assert.Equal(45,(await r.Hub.ReadAsync()).Channels[0].Duty); Assert.Empty(r.Journal().Pending);
        Assert.Null(r.Driver.CoolingConfiguration("fan1")); Assert.NotNull(r.Driver.ValidateCoolingSetup("fan1"));
        Assert.Equal(2,r.Hub.Writes); await r.Runtime.ExecuteAsync(request); Assert.Equal(2,r.Hub.Writes);
        await r.Runtime.PollAsync();
        var reduced=await r.Runtime.ExecuteAsync(r.Request(duty:20)); Assert.Equal(Outcome.Confirmed,reduced.Result!.Outcome);
        Assert.Equal(20,reduced.Result.Targets[0].Calibration!.Duty); Assert.Null(r.Driver.CoolingConfiguration("fan1"));
        Assert.Equal(45,(await r.Hub.ReadAsync()).Channels[0].Duty);
        await r.Driver.InitializeAsync(); Assert.False((await r.Runtime.ExecuteAsync(r.Request(duty:20))).Success);
        await r.Runtime.DisconnectAsync(r.Hello.Session); r.Hello=r.Runtime.OpenSession("user");
        Assert.False((await r.Runtime.ExecuteAsync(r.Request(duty:20))).Success);
    }
    [Theory] [InlineData("binding")] [InlineData("generation")] [InlineData("revision")] [InlineData("topology")] [InlineData("before")]
    public async Task ChangedTrialTargetsAreRejectedWithoutHardwareWrites(string change)
    {
        await using var r=await Rig.Create(); var request=r.Request();
        request=change switch {
            "binding"=>request with { Calibration=request.Calibration! with { Binding=request.Calibration.Binding with { Fingerprint=new('f',64) } } },
            "generation"=>request with { Targets=[request.Targets![0] with { Generation=request.Targets[0].Generation+1 }] },
            "revision"=>request with { Targets=[request.Targets![0] with { ExpectedPolicyRevision=5 }] },
            "topology"=>request with { TopologyRevision=request.TopologyRevision+1 },
            _=>request with { Calibration=request.Calibration! with { BeforeDuty=99 } }
        };
        Assert.False((await r.Runtime.ExecuteAsync(request)).Success); Assert.Equal(0,r.Hub.Writes);
    }
    [Fact] public async Task TheTrialReservesOnlyItsEndpointAndWaitsWithoutHoldingTheHubResource()
    {
        await using var r=await Rig.Create(configured:true); var request=r.Request(); var pending=r.Runtime.ExecuteAsync(request); await r.Begun();
        try
        {
            Assert.False((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetDuty,60))).Success);
            Assert.False((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.ReleaseCooling))).Success);
            Assert.False((await r.Runtime.ExecuteAsync(r.Request())).Success);
            var other=await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetDuty,60,"fan2")).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(Outcome.Confirmed,other.Result!.Outcome); Assert.False(pending.IsCompleted);
            await r.Runtime.PollAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.CancelCoolingCalibration,resultId:request.Id)); }
        var done=await pending; Assert.True(done.Result!.Targets[0].Calibration!.Restored); Assert.False(done.Result.Targets[0].Calibration!.Completed);
        Assert.Equal(45,(await r.Hub.ReadAsync()).Channels[0].Duty); Assert.Equal(60,(await r.Hub.ReadAsync()).Channels[1].Duty);
        Assert.Equal(30,r.Driver.CoolingConfiguration("fan1")!.Safety.Minimum);
    }
    [Fact] public async Task ExistingCoolingMustBeReleasedBeforeCalibrationAndARejectedTrialCannotReleaseIt()
    {
        await using var r=await Rig.Create(configured:true); await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetDuty,62));
        var writes=r.Hub.Writes; Assert.False((await r.Runtime.ExecuteAsync(r.Request())).Success); Assert.Equal(writes,r.Hub.Writes);
        Assert.Equal(62,(await r.Hub.ReadAsync()).Channels[0].Duty); Assert.Single(r.Journal().Pending);
    }
    [Fact] public async Task CancellingAQueuedTrialNeverWritesOrReleasesAnUnrelatedOwner()
    {
        await using var r=await Rig.Create(); var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold=r.Scheduler.RunAsync(r.Driver.Observe()!.ResourceDomains,HardwarePriority.Manual,async ct=>{ entered.SetResult(); await proceed.Task; return true; });
        await entered.Task; var request=r.Request(); var trial=r.Runtime.ExecuteAsync(request);
        try
        {
            await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.CancelCoolingCalibration,resultId:request.Id));
            // Recovery waits for the occupied domain even though no output was issued.
        }
        finally { proceed.SetResult(); await hold; }
        var report=(await trial).Result!.Targets[0].Calibration!; Assert.False(report.Completed); Assert.True(report.Restored); Assert.Equal(0,r.Hub.Writes);
        Assert.Empty(r.Journal().Pending);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DisconnectAndServiceShutdownWaitForUncancelledTrialRestoration(bool shutdown)
    {
        await using var r=await Rig.Create(); var pending=r.Runtime.ExecuteAsync(r.Request()); await r.Begun();
        if(shutdown) await r.Runtime.DisposeAsync(); else await r.Runtime.DisconnectAsync(r.Hello.Session);
        var report=(await pending).Result!.Targets[0].Calibration!; Assert.False(report.Completed); Assert.True(report.Restored); Assert.Empty(r.Journal().Pending);
        Assert.Equal(45,(await r.Hub.ReadAsync()).Channels[0].Duty); Assert.False(r.Runtime.State().Endpoints.Single(e=>e.Target.EndpointId=="fan1").Owned);
    }
    [Fact] public async Task LeaseExpiryRestoresTrialOutputBeforeAdmittingANewSession()
    {
        var clock=new TestClock(); await using var r=await Rig.Create(clock:clock); var pending=r.Runtime.ExecuteAsync(r.Request()); await r.Begun();
        clock.Advance(13); await r.Runtime.SweepAsync(); Assert.True((await pending).Result!.Targets[0].Calibration!.Restored);
        r.Hello=r.Runtime.OpenSession("user"); await r.Runtime.PollAsync(); Assert.False((await r.Runtime.ExecuteAsync(r.Request(duty:30))).Success);
        Assert.Equal(45,(await r.Hub.ReadAsync()).Channels[0].Duty);
    }
    [Fact] public async Task AFailedRestoreRetainsRecoveryAndCanBeRetriedWithoutPermanentSetup()
    {
        await using var r=await Rig.Create(); var request=r.Request(); var pending=r.Runtime.ExecuteAsync(request); await r.Begun();
        r.Hub.BeforeFanWrite=ct=>throw new IOException("restore unavailable");
        await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.CancelCoolingCalibration,resultId:request.Id));
        var report=(await pending).Result!.Targets[0].Calibration!; Assert.Equal(Outcome.Unknown,report.Outcome); Assert.False(report.Restored); Assert.Single(r.Journal().Pending);
        Assert.False((await r.Runtime.ExecuteAsync(r.Request())).Success); r.Hub.BeforeFanWrite=null; await r.Runtime.PollAsync();
        Assert.Equal(Outcome.Confirmed,(await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.ReleaseCooling))).Result!.Outcome);
        Assert.Empty(r.Journal().Pending); Assert.Null(r.Driver.ValidateCalibrationStart("fan1",r.Driver.BindingCandidate!));
    }
    [Theory] [InlineData("confirm")] [InlineData("zero")] [InlineData("multiple")] [InlineData("smuggled")] [InlineData("mismatch")]
    public async Task CalibrationUsesTheExplicitOperationAndPreservesTargetPayloadAndFullOutputGuards(string kind)
    {
        await using var r=await Rig.Create(); var request=r.Request();
        request=kind switch {
            "confirm"=>request with { Calibration=request.Calibration! with { Confirmed=false } },
            "zero"=>request with { Calibration=request.Calibration! with { Duty=0 } },
            "multiple"=>request with { Targets=[request.Targets![0],request.Targets[0] with { Target=new(r.Driver.DeviceId,"fan2") }] },
            "smuggled"=>request with { Operation=DeviceOperation.SetDuty,Duty=100 },
            _=>request with { Calibration=request.Calibration! with { Binding=request.Calibration.Binding with { DeviceId="different-device" } } }
        };
        if(kind=="confirm")
        {
            var parsed=DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(request,Contract.Json));
            Assert.False(parsed.Calibration!.Confirmed);
            // The obsolete acknowledgement cannot replace the actual successful full-output trial.
            Assert.False((await r.Runtime.ExecuteAsync(parsed with { Calibration=parsed.Calibration with { Duty=30 } })).Success);
            Assert.Equal(0,r.Hub.Writes); return;
        }
        Assert.Throws<IOException>(()=>DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(request,Contract.Json))); Assert.Equal(0,r.Hub.Writes);
    }
}
