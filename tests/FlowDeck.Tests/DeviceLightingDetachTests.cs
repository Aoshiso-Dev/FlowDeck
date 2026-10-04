using FlowDeck.Core;
using FlowDeck.Infrastructure;
using HubRig=FlowDeck.Tests.DeviceLightingAdjustmentTests.HubRig;

namespace FlowDeck.Tests;

public partial class DeviceLightingRuntimeTests
{
    [Fact] public async Task StoppingOneControllerKeepsItsSiblingPhaseAndCanRejoinWithoutReopeningTheSibling()
    {
        var a=new Driver("a"); var b=new Driver("b"); await using var r=new Rig(a,b).Open(); await r.Start(["a","b"]); await r.Tick(.4);
        var group=r.Runtime.State().Endpoints.Single(e=>e.Target.DeviceId=="b").Lighting!.GroupId;
        var request=r.Request(DeviceOperation.StopLighting,["a"]); var stopped=await r.Runtime.ExecuteAsync(request);
        Assert.Equal(Outcome.WrittenUnverified,stopped.Result!.Outcome); Assert.Equal(new RgbColor(100,50,20),a.Color);
        Assert.Equal(1,a.Ends); Assert.Equal(0,b.Ends); Assert.False(r.Runtime.State().Endpoints.Single(e=>e.Target.DeviceId=="a").Owned);
        Assert.Equal(group,r.Runtime.State().Endpoints.Single(e=>e.Target.DeviceId=="b").Lighting!.GroupId);
        await r.Runtime.ExecuteAsync(request); Assert.Equal(1,a.Ends);
        await r.Tick(.3); Assert.Equal(DeviceLighting.Frame(JoinProgram(),.7),b.Color);
        await r.Start(["a","b"]); Assert.Equal(2,a.Opens); Assert.Equal(1,b.Opens); Assert.Equal(a.Color,b.Color);
        await r.Tick(.2); Assert.Equal(DeviceLighting.Frame(JoinProgram(),.2),b.Color);
    }
    [Fact] public async Task OneRequestCanStopAWholeGroupAndOnlyOneMemberOfAnotherGroup()
    {
        var a=new Driver("a"); var b=new Driver("b"); var c=new Driver("c"); await using var r=new Rig(a,b,c).Open();
        await r.Start(["a","b"]); await r.Start(["c"]); await r.Tick(.4);
        var reply=await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.StopLighting,["a","c"])); Assert.Equal(Outcome.WrittenUnverified,reply.Result!.Outcome);
        Assert.Equal(1,a.Ends); Assert.Equal(1,c.Ends); Assert.Equal(0,b.Ends); await r.Tick(.2);
        Assert.Equal(DeviceLighting.Frame(JoinProgram(),.6),b.Color); Assert.True(r.Runtime.State().Endpoints.Single(e=>e.Target.DeviceId=="b").Lighting!.Running);
    }
    [Fact] public async Task ASelectiveEndFailureRestoresTheRemainingAffectedGroupAndLeavesAnotherGroupRunning()
    {
        var a=new Driver("a"); var b=new Driver("b"); var c=new Driver("c"); await using var r=new Rig(a,b,c).Open();
        await r.Start(["a","b"]); await r.Start(["c"]); await r.Tick(.4); a.FailEnd=true;
        var reply=await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.StopLighting,["a"])); Assert.Equal(Outcome.Unknown,reply.Result!.Outcome);
        Assert.Equal(2,a.Ends); Assert.Equal(1,b.Ends); Assert.Equal(0,c.Ends);
        Assert.False((await r.Start(["a"])).Success); Assert.True(r.Runtime.State().Endpoints.Single(e=>e.Target.DeviceId=="a").Owned);
        await r.Tick(.2); Assert.Equal(DeviceLighting.Frame(JoinProgram(),.6),c.Color); a.FailEnd=false;
    }
    [Fact] public async Task SelectiveStopWaitsForAnOldFrameAndCannotLeaveThatFrameWritingTheStoppedMember()
    {
        var a=new Driver("a"); var b=new Driver("b"); await using var r=new Rig(a,b).Open(); await r.Start(["a","b"]);
        a.Entered=new(TaskCreationOptions.RunContinuationsAsynchronously); a.Continue=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var frame=r.Tick(.3); await a.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stop=r.Runtime.ExecuteAsync(r.Request(DeviceOperation.StopLighting,["a"])); Assert.False(stop.IsCompleted);
        Assert.False((await r.Start(["b"])).Success); a.Continue.TrySetResult(); await frame;
        Assert.Equal(Outcome.WrittenUnverified,(await stop.WaitAsync(TimeSpan.FromSeconds(3))).Result!.Outcome); var aWrites=a.Writes;
        await r.Tick(.2); Assert.Equal(aWrites,a.Writes); Assert.Equal(DeviceLighting.Frame(JoinProgram(),.5),b.Color); Assert.Equal(0,b.Ends);
    }
    [Fact] public async Task SessionExitWaitsForTheSelectiveEndThenReleasesRemainingStreamsOnlyOnce()
    {
        var a=new Driver("a"); var b=new Driver("b"); await using var r=new Rig(a,b).Open(); await r.Start(["a","b"]);
        a.EndEntered=new(TaskCreationOptions.RunContinuationsAsynchronously); a.ContinueEnd=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop=r.Runtime.ExecuteAsync(r.Request(DeviceOperation.StopLighting,["a"]));
        await a.EndEntered.Task.WaitAsync(TimeSpan.FromSeconds(3)); var exit=r.Disconnect(); Assert.False(exit.IsCompleted);
        a.ContinueEnd.TrySetResult(); await Task.WhenAll(stop,exit).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1,a.Ends); Assert.Equal(1,b.Ends); Assert.All(r.Runtime.State().Endpoints,e=>Assert.False(e.Owned));
    }
}

public sealed partial class DeviceLightingCompositionTests
{
    [Fact] public async Task StoppingOneHubPortRetainsSiblingRecoveryOwnersAndNeverOverwritesItsLaterNativeEffect()
    {
        await using var r=await HubRig.Create(); var cycle=new LightingProgram("color-cycle",[Blue],4);
        await r.Apply(r.Request(DeviceOperation.SetLighting,"led1","led2","led3") with {Lighting=cycle}); r.Clock.Advance(.4); await r.Runtime.TickLightingAsync();
        var journal=r.Journal.Pending.ToDictionary(p=>p.Channel); var request=r.Request(DeviceOperation.StopLighting,"led2");
        var reply=await r.Runtime.ExecuteAsync(request); Assert.Equal(Outcome.WrittenUnverified,reply.Result!.Outcome);
        Assert.Equal(Blue,r.Hub.Colors["led2"].Colors[0]); Assert.Equal(2,r.Journal.Pending.Length);
        Assert.Equal(journal["led1"],r.Journal.Pending.Single(p=>p.Channel=="led1")); Assert.Equal(journal["led3"],r.Journal.Pending.Single(p=>p.Channel=="led3"));
        Assert.False(r.Runtime.State().Endpoints.Single(e=>e.Target.EndpointId=="led2").Owned);
        var stoppedWrites=r.Hub.Writes.Count(w=>w.Channel=="led2"); r.Clock.Advance(.2); await r.Runtime.TickLightingAsync();
        Assert.Equal(stoppedWrites,r.Hub.Writes.Count(w=>w.Channel=="led2")); Assert.Equal(DeviceLighting.Frame(cycle,.6),r.Hub.Colors["led1"].Colors[0]);
        await r.Apply(r.Request(DeviceOperation.SetLighting,"led2") with {Lighting=new("native/nzxt-control-hub/breathing",[Red],3)});
        stoppedWrites=r.Hub.Writes.Count(w=>w.Channel=="led2"); r.Clock.Advance(.3); await r.Runtime.TickLightingAsync();
        Assert.Equal(stoppedWrites,r.Hub.Writes.Count(w=>w.Channel=="led2")); Assert.Equal("breathing",r.Hub.Colors["led2"].Mode);
        Assert.Equal(DeviceLighting.Frame(cycle,.9),r.Hub.Colors["led3"].Colors[0]); Assert.Equal(0,r.Hub.Fans.Writes);
        Assert.Equal(2,Assert.Single(r.Authority.Configuration.Lighting.SavedFrameGroups!).Length);
    }
    [Fact] public async Task MixedHubStopResultsDoNotRestoreAnAlreadySuccessfullyDetachedPortAgain()
    {
        await using var r=await HubRig.Create(); var pulse=new LightingProgram("breathing",[Blue],4);
        await r.Apply(r.Request(DeviceOperation.SetLighting,"led1","led2","led3") with {Lighting=pulse}); r.Clock.Advance(.4); await r.Runtime.TickLightingAsync();
        r.Hub.FailChannel="led2"; var reply=await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.StopLighting,"led1","led2"));
        Assert.Equal(Outcome.Partial,reply.Result!.Outcome); Assert.Equal(Blue,r.Hub.Colors["led1"].Colors[0]);
        Assert.DoesNotContain(r.Journal.Pending,p=>p.Channel=="led1"); Assert.Contains(r.Journal.Pending,p=>p.Channel=="led2");
        Assert.False(r.Runtime.State().Endpoints.Single(e=>e.Target.EndpointId=="led1").Owned);
        var count=r.Hub.Writes.Count(w=>w.Channel=="led1"); r.Hub.FailChannel=null;
        Assert.Equal(Outcome.WrittenUnverified,(await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.StopLighting,"led2"))).Result!.Outcome);
        Assert.Empty(r.Journal.Pending); Assert.Equal(count,r.Hub.Writes.Count(w=>w.Channel=="led1"));
    }
    private sealed class WithoutSelectiveStream(HubDeviceDriver driver) : IDeviceLightingCompositionDriver,IPhysicalDeviceDriver
    {
        private sealed class Nonselective(IDeviceLightingStream stream):IDeviceLightingStream
        { public Task<DeviceDriverReleaseResult[]> WriteAsync(DeviceLightingFrame f,CancellationToken ct)=>stream.WriteAsync(f,ct);
          public Task<DeviceDriverReleaseResult[]> EndAsync(DeviceLightingStopReason r,CancellationToken ct)=>stream.EndAsync(r,ct); }
        public string DeviceId=>driver.DeviceId;
        public PhysicalDeviceBinding? BindingCandidate=>driver.BindingCandidate;
        public DeviceLightingLimits? LightingLimits=>driver.LightingLimits;
        public DeviceObservation? Observe()=>driver.Observe();
        public string? Validate(DeviceEndpointAction a)=>driver.Validate(a);
        public string? ValidateLightingStream(string[] e,LightingProgram p)=>driver.ValidateLightingStream(e,p);
        public async Task<IDeviceLightingStream> OpenLightingStreamAsync(string[] e,LightingProgram p,CancellationToken ct)=>new Nonselective(await driver.OpenLightingStreamAsync(e,p,ct));
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction a,CancellationToken ct)=>driver.ApplyAsync(a,ct);
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason r,CancellationToken ct)=>driver.ReleaseAsync(r,ct);
        public ValueTask DisposeAsync()=>driver.DisposeAsync();
    }
    [Fact] public async Task AnUnimplementedPartialStreamStopRejectsBeforeAnyOutputButAllowsExplicitWholeStreamStop()
    {
        await using var r=await HubRig.Create(d=>new WithoutSelectiveStream(d)); var pulse=new LightingProgram("breathing",[Blue],4);
        await r.Apply(r.Request(DeviceOperation.SetLighting,"led1","led2","led3") with {Lighting=pulse}); var writes=r.Hub.Writes.Count;
        var denied=await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.StopLighting,"led1")); Assert.False(denied.Success); Assert.Contains("個別",denied.Error);
        Assert.Equal(writes,r.Hub.Writes.Count); Assert.Equal(3,r.Journal.Pending.Length);
        Assert.Equal(Outcome.WrittenUnverified,(await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.StopLighting,"led1","led2","led3"))).Result!.Outcome);
        Assert.Empty(r.Journal.Pending);
    }
}
