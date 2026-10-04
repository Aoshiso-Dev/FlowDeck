using FlowDeck.Core;
using FlowDeck.Infrastructure;
using System.Text.Json;
using Xunit;

namespace FlowDeck.Tests;

public sealed class DeviceLightingIdentificationTests
{
    private sealed class HoldClock : TimeProvider
    {
        private sealed class HeldTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            public void Fire() { if (!disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
        private HeldTimer? timer;
        public TimeSpan Due;
        public TaskCompletionSource Scheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { Due = dueTime; timer = new(callback, state); Scheduled.TrySetResult(); return timer; }
        public void Finish() => timer!.Fire();
    }
    private sealed class Driver(string id) : IPhysicalDeviceDriver
    {
        public string DeviceId => id;
        public bool Connected = true, FailWhite, ThrowWhite, FailRestore, ThrowValidationAfterWhite;
        public string Locator = "original";
        public int Releases, Duties;
        public List<LightingProgram> Writes = [];
        public TaskCompletionSource WhiteSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PhysicalDeviceBinding BindingCandidate => new(id, "demo:" + Locator, "demo-v1", new('a',64));
        private static bool White(LightingProgram? p) => p?.EffectId == "fixed" && p.Colors.SequenceEqual(new[] { new RgbColor(255,255,255) });
        public DeviceObservation? Observe() => Connected ? new(id, "demo", id, DeviceKind.Controller, IdentityConfidence.SerialBound, Locator, "demo-v1", ["shared-bus"],
            [.. new[] { "light1", "light2" }.Select(e => new DeviceEndpoint(e, e, EndpointKind.Lighting,
                new[] { DeviceFeature.FixedColor, DeviceFeature.NativeEffect, DeviceFeature.RestoreLighting }.Select(f => new DeviceCapability(f, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)).ToArray())),
             new("fan", "fan", EndpointKind.Cooling, [new(DeviceFeature.FixedDuty, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)])]) : null;
        public string? Validate(DeviceEndpointAction action)
        { if (ThrowValidationAfterWhite && Writes.Any(White)) throw new IOException("validation failed"); return null; }
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct)
        {
            if (action.Operation == DeviceOperation.SetDuty) { Duties++; return Task.FromResult(new DeviceDriverResult(Outcome.Confirmed, "duty", ReportedDuty: action.Duty)); }
            if (action.Operation == DeviceOperation.StopLighting) return Task.FromResult(new DeviceDriverResult(Outcome.WrittenUnverified, "released"));
            var p = Contract.Copy(action.Lighting!); Writes.Add(p);
            if (White(p))
            {
                WhiteSent.TrySetResult();
                if (ThrowWhite) throw new IOException("unknown white write");
                if (FailWhite) return Task.FromResult(new DeviceDriverResult(Outcome.Unknown, "white lost"));
            }
            else if (FailRestore && Writes.Count > 1) return Task.FromResult(new DeviceDriverResult(Outcome.Unknown, "restore lost"));
            return Task.FromResult(new DeviceDriverResult(Outcome.WrittenUnverified, "sent", ReportedLighting: p));
        }
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct)
        { Releases++; return Task.FromResult<DeviceDriverReleaseResult[]>([new("light1", new(Outcome.WrittenUnverified,"exit")), new("light2",new(Outcome.WrittenUnverified,"exit")), new("fan",new(Outcome.Confirmed,"exit",ReportedDuty:100))]); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Rig : IAsyncDisposable
    {
        public readonly HoldClock Clock = new();
        public readonly DeviceResourceScheduler Scheduler = new();
        public readonly DeviceControlRuntime Runtime;
        public readonly DeviceServiceHello Hello;
        private long sequence;
        public Rig(params IDeviceControlDriver[] drivers)
        { Runtime = new(drivers, ["user"], new TestLog(), Clock, scheduler: Scheduler, physicalBindings: drivers.OfType<IPhysicalDeviceDriver>().Select(d => d.BindingCandidate!)); Hello = Runtime.OpenSession("user"); }
        public DeviceServiceRequest Request(DeviceOperation operation, string device = "a", string endpoint = "light1", LightingProgram? program = null, string? resultId = null)
        {
            var s = Runtime.State(); var write = DeviceServiceProtocol.IsWrite(operation); var state = s.Endpoints.Single(e => e.Target == new EndpointReference(device,endpoint));
            return new(2, Hello.Epoch, Hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", operation,
                write ? s.Inventory.TopologyRevision : 0, write ? [new(state.Target,state.Generation,state.PolicyRevision)] : null, Lighting: program, ResultId: resultId);
        }
        public Task<DeviceServiceReply> Apply(LightingProgram p, string device = "a") => Runtime.ExecuteAsync(Request(DeviceOperation.SetLighting, device, program:p));
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }
    private static LightingProgram Blue => new("fixed", [new(10,60,200)], Brightness:47);
    [Theory][InlineData(false)][InlineData(true)]
    public async Task TimedWhiteRestoresTheExactUnscaledProgramAndDoesNotHoldTheBus(bool native)
    {
        var a = new Driver("a"); var b = new Driver("b"); await using var r = new Rig(a,b);
        var original = native ? new LightingProgram("native/demo/wave", [new(200,10,30),new(5,100,220)], 224, true, 47, [new(10,20,30),new(60,70,80)]) : Blue;
        await r.Apply(original); var request = r.Request(DeviceOperation.IdentifyLighting); var pending = r.Runtime.ExecuteAsync(request);
        await r.Clock.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(TimeSpan.FromSeconds(3), r.Clock.Due);
        Assert.Equal(DeviceLightingIdentification.White.Colors, a.Writes[1].Colors);
        var blocked = await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetLighting, endpoint:"light2", program:Blue)); Assert.False(blocked.Success);
        Assert.True((await r.Apply(Blue, "b").WaitAsync(TimeSpan.FromSeconds(1))).Success);
        Assert.True((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetDuty,endpoint:"fan") with { Duty=55 }).WaitAsync(TimeSpan.FromSeconds(1))).Success);
        var duplicate = r.Runtime.ExecuteAsync(request); r.Clock.Finish(); var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(Outcome.WrittenUnverified,result.Result!.Outcome); Assert.True(result.Result.Targets[0].Identification!.Restored);
        Assert.True(DeviceLighting.SameProgram(original,a.Writes[2])); Assert.Equal(3,a.Writes.Count); Assert.Equal(1,a.Duties);
        Assert.Equal(JsonSerializer.Serialize(result,Contract.Json), JsonSerializer.Serialize(await duplicate,Contract.Json)); Assert.Single(b.Writes);
        Assert.True(DeviceLighting.SameProgram(original,r.Runtime.State().Endpoints.Single(e=>e.Target==new EndpointReference("a","light1")).RequestedLighting!));
        _ = DeviceServiceProtocol.ReadReply(JsonSerializer.SerializeToUtf8Bytes(result,Contract.Json), r.Hello);
    }
    [Fact] public async Task CancellationRestoresEarlyAndAStaleCancelCannotAffectAnotherRequest()
    {
        var a=new Driver("a"); await using var r=new Rig(a); await r.Apply(Blue);
        var request=r.Request(DeviceOperation.IdentifyLighting); var pending=r.Runtime.ExecuteAsync(request); await r.Clock.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.CancelLightingIdentification,resultId:new('f',32)))).Success);
        Assert.True((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.CancelLightingIdentification,resultId:request.Id))).Success);
        var reply=await pending.WaitAsync(TimeSpan.FromSeconds(3)); Assert.True(reply.Result!.Targets[0].Identification is { Restored:true,Cancelled:true });
        Assert.True(DeviceLighting.SameProgram(Blue,a.Writes.Last()));
        Assert.False((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.CancelLightingIdentification,resultId:request.Id))).Success);
    }
    [Fact] public async Task CancellationWhileWaitingForTheBusNeverWritesWhiteOrReplaysTheBaseline()
    {
        var a=new Driver("a"); await using var r=new Rig(a); await r.Apply(Blue);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder=r.Scheduler.RunAsync(["shared-bus"],HardwarePriority.Manual,async _=>{ entered.SetResult(); await resume.Task; return true; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            var request=r.Request(DeviceOperation.IdentifyLighting); var pending=r.Runtime.ExecuteAsync(request);
            Assert.True((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.CancelLightingIdentification,resultId:request.Id))).Success);
            var result=await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(Outcome.Confirmed,result.Result!.Outcome); Assert.True(result.Result.Targets[0].Identification is { Restored:true,Cancelled:true });
            Assert.Single(a.Writes); Assert.True(DeviceLighting.SameProgram(Blue,a.Writes[0]));
        }
        finally { resume.TrySetResult(); await holder; }
    }
    [Fact] public async Task CompiledHubDriverRestoresThroughIpcWithoutChangingPreferencesAndAllowsRefreshAndCancel()
    {
        using var directory=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); Assert.True(client.SupportsLightingIdentification);
        var target=new EndpointReference(demo.DeviceIds[0],"led1");
        Assert.Throws<IOException>(()=>DeviceLightingIdentificationEdit.Capture(client.Snapshot!,target));
        var draft=new DeviceLightingDraft(); var s=client.Snapshot!;
        draft.Update(s.State,s.Configuration.Generation,s.Configuration.Configuration.Lighting,0,true); draft.Select([target]); draft.SetProgram(Blue);
        await client.ApplyLightingAsync(draft.Capture());
        var generation=client.Snapshot!.Configuration.Generation;
        var configuration=JsonSerializer.Serialize(client.Snapshot.Configuration.Configuration,Contract.Json);
        var edit=DeviceLightingIdentificationEdit.Capture(client.Snapshot,target);
        var pending=client.IdentifyLightingAsync(edit);
        for(var i=0;i<60 && demo.Hubs[0].LightingWrites<2;i++) await Task.Delay(20);
        Assert.Equal(new RgbColor(255,255,255),Assert.Single(demo.Hubs[0].LightEffects["led1"].Colors));
        await client.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(2)); Assert.True(client.IdentificationRunning);
        Assert.True(DeviceLightingIdentification.Running(client.Snapshot!.State.Endpoints.Single(e=>e.Target==target).Identification));
        await client.CancelLightingIdentificationAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var result=await pending.WaitAsync(TimeSpan.FromSeconds(3)); Assert.True(result.Targets[0].Identification is { Restored:true,Cancelled:true });
        Assert.Equal(Blue.Colors[0].Scale(.47),Assert.Single(demo.Hubs[0].LightEffects["led1"].Colors));
        Assert.Equal(3,demo.Hubs[0].LightingWrites); Assert.Equal(0,demo.Hubs[1].LightingWrites);
        Assert.Equal(generation,client.Snapshot!.Configuration.Generation);
        Assert.Equal(configuration,JsonSerializer.Serialize(client.Snapshot.Configuration.Configuration,Contract.Json));
        await Assert.ThrowsAsync<IOException>(()=>client.IdentifyLightingAsync(edit));
    }
    [Theory][InlineData(false)][InlineData(true)] public async Task AFailedOrThrowingWhiteWriteStillRestoresThePreviousEffect(bool throws)
    {
        var a=new Driver("a") { FailWhite=!throws,ThrowWhite=throws }; await using var r=new Rig(a); await r.Apply(Blue);
        var reply=await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.IdentifyLighting)).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(Outcome.Partial,reply.Result!.Outcome); Assert.True(reply.Result.Targets[0].Identification!.Restored);
        Assert.True(DeviceLighting.SameProgram(Blue,a.Writes.Last())); Assert.Equal(3,a.Writes.Count);
    }
    [Theory][InlineData(false)][InlineData(true)] public async Task AFailedRestoreRemainsUnconfirmedAndBlocksFurtherLightingUntilRelease(bool throws)
    {
        var a=new Driver("a"); await using var r=new Rig(a); await r.Apply(Blue);
        a.FailRestore=!throws; var pending=r.Runtime.ExecuteAsync(r.Request(DeviceOperation.IdentifyLighting)); await r.Clock.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        a.ThrowValidationAfterWhite=throws; r.Clock.Finish(); var reply=await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(Outcome.Unknown,reply.Result!.Outcome); Assert.False(reply.Result.Targets[0].Identification!.Restored);
        Assert.False((await r.Apply(Blue)).Success); a.ThrowValidationAfterWhite=false;
        Assert.True((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.StopLighting))).Success);
        a.FailRestore=false; Assert.True((await r.Apply(Blue)).Success);
    }
    [Theory][InlineData(false)][InlineData(true)] public async Task DeviceRemovalOrReplacementNeverReceivesAnOldRestoration(bool replacement)
    {
        var a=new Driver("a"); await using var r=new Rig(a); await r.Apply(Blue);
        var pending=r.Runtime.ExecuteAsync(r.Request(DeviceOperation.IdentifyLighting)); await r.Clock.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if(replacement) a.Locator="replacement"; else a.Connected=false;
        r.Clock.Finish(); var reply=await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(reply.Result!.Targets[0].Identification!.Restored); Assert.Equal(2,a.Writes.Count);
        Assert.False((await r.Apply(Blue)).Success);
    }
    [Fact] public async Task AnUnrelatedTopologyChangeDoesNotPreventRestoringTheSamePhysicalTarget()
    {
        var a=new Driver("a"); var b=new Driver("b") { Connected=false }; await using var r=new Rig(a,b); await r.Apply(Blue);
        var pending=r.Runtime.ExecuteAsync(r.Request(DeviceOperation.IdentifyLighting)); await r.Clock.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        b.Connected=true; r.Clock.Finish(); var reply=await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(reply.Result!.Targets[0].Identification!.Restored); Assert.True(DeviceLighting.SameProgram(Blue,a.Writes.Last()));
    }
    [Fact] public async Task SessionExitCancelsIdentificationAndUsesTheExitPolicyWithoutReplayingTheOldEffect()
    {
        var a=new Driver("a"); await using var r=new Rig(a); await r.Apply(Blue);
        var pending=r.Runtime.ExecuteAsync(r.Request(DeviceOperation.IdentifyLighting)); await r.Clock.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await r.Runtime.DisconnectAsync(r.Hello.Session).WaitAsync(TimeSpan.FromSeconds(3)); var reply=await pending;
        Assert.False(reply.Result!.Targets[0].Identification!.Restored); Assert.Equal(2,a.Writes.Count); Assert.Equal(1,a.Releases);
        Assert.False(r.Runtime.State().Endpoints.Single(e=>e.Target==new EndpointReference("a","light1")).Owned);
    }
    [Theory][InlineData(false)][InlineData(true)] public async Task ServiceStopOrReconfigurationCancelsTheTimerAndReleasesWithoutOldOutput(bool reconfigure)
    {
        var a=new Driver("a"); await using var r=new Rig(a); await r.Apply(Blue);
        var pending=r.Runtime.ExecuteAsync(r.Request(DeviceOperation.IdentifyLighting)); await r.Clock.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if(reconfigure)
            await r.Runtime.ReconfigureAsync(r.Hello.Session,r.Runtime.State().Inventory.TopologyRevision,(_,_)=>Task.FromResult(new[] { a.BindingCandidate })).WaitAsync(TimeSpan.FromSeconds(3));
        else await r.Runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        var reply=await pending; Assert.True(reply.Result!.Targets[0].Identification is { Restored:false,Cancelled:true });
        Assert.Equal(2,a.Writes.Count); Assert.Equal(1,a.Releases); Assert.False(r.Runtime.State().ConfigurationChanging);
        Assert.All(r.Runtime.State().Endpoints,e=>Assert.False(e.Owned));
        if(reconfigure) Assert.True((await r.Apply(Blue)).Success);
    }
    [Fact] public async Task UnknownOutputAndActiveSynchronizationAreRejectedBeforeWhiteWrites()
    {
        var a=new Driver("a"); await using(var r=new Rig(a)) {
            Assert.False((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.IdentifyLighting))).Success); Assert.Empty(a.Writes);
        }
        var streaming=new DeviceLightingRuntimeTests.Driver("a"); await using var streamRig=new Rig(streaming);
        await streamRig.Apply(new("breathing",[new(0,30,200)]));
        Assert.False((await streamRig.Runtime.ExecuteAsync(streamRig.Request(DeviceOperation.IdentifyLighting))).Success); Assert.Equal(0,streaming.NativeWrites);
    }
    [Fact] public async Task StaleGenerationCannotStartIdentification()
    {
        var a=new Driver("a"); await using var r=new Rig(a); await r.Apply(Blue);
        var request=r.Request(DeviceOperation.IdentifyLighting); a.Locator="replacement";
        Assert.False((await r.Runtime.ExecuteAsync(request)).Success); Assert.Single(a.Writes);
    }
    [Theory][InlineData("two-targets")][InlineData("injected-program")][InlineData("missing-cancel-id")][InlineData("cancel-target")]
    public async Task IdentificationProtocolRejectsAmbiguousOrInjectedParameters(string kind)
    {
        var a=new Driver("a"); await using var r=new Rig(a); var request=r.Request(DeviceOperation.IdentifyLighting);
        request=kind switch {
            "two-targets"=>request with { Targets=[request.Targets![0],request.Targets[0] with { Target=new("a","light2") }] },
            "injected-program"=>request with { Lighting=Blue },
            "cancel-target"=>request with { Operation=DeviceOperation.CancelLightingIdentification,ResultId=new('b',32) },
            _=>r.Request(DeviceOperation.CancelLightingIdentification) };
        Assert.Throws<IOException>(()=>DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(request,Contract.Json)));
    }
}
