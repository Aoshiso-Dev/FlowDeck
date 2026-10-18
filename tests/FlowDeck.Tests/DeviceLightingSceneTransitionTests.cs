using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Light = FlowDeck.Tests.DeviceLightingRuntimeTests.Driver;

namespace FlowDeck.Tests;

public sealed class DeviceLightingSceneTransitionTests
{
    private sealed class Composed(Light source) : IDeviceLightingCompositionDriver, IPhysicalDeviceDriver
    {
        public bool FailOpen;
        public TaskCompletionSource? Entered, Continue;
        public string DeviceId => source.DeviceId;
        public PhysicalDeviceBinding BindingCandidate => source.BindingCandidate;
        public DeviceLightingLimits? LightingLimits => source.LightingLimits;
        public DeviceObservation? Observe() => source.Observe();
        public string? Validate(DeviceEndpointAction action) => source.Validate(action);
        public string? ValidateLightingStream(string[] endpoints, LightingProgram program) => source.ValidateLightingStream(endpoints,program);
        public async Task<IDeviceLightingStream> OpenLightingStreamAsync(string[] endpoints, LightingProgram program, CancellationToken ct)
        {
            Entered?.TrySetResult(); if(Continue is not null) await Continue.Task;
            if(FailOpen) throw new IOException("open failed");
            return await source.OpenLightingStreamAsync(endpoints,program,ct);
        }
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action,CancellationToken ct)=>source.ApplyAsync(action,ct);
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason,CancellationToken ct)=>source.ReleaseAsync(reason,ct);
        public ValueTask DisposeAsync()=>source.DisposeAsync();
    }
    private sealed class Rig : IAsyncDisposable
    {
        public readonly ServiceTestDirectory Directory=new();
        public readonly TestClock Clock=new();
        public readonly Dictionary<string,Light> Lights="abcdef".ToDictionary(c=>c.ToString(),c=>new Light(c.ToString()));
        public readonly Dictionary<string,Composed> Drivers;
        public readonly DeviceConfigurationAuthority Authority;
        public readonly DeviceControlRuntime Runtime;
        public readonly DeviceServiceHello Hello;
        private long sequence;
        public Rig()
        {
            Drivers=Lights.ToDictionary(p=>p.Key,p=>new Composed(p.Value)); var install=Guid.NewGuid().ToString("N");
            var bindings=Drivers.Values.Select(d=>d.BindingCandidate).ToArray();
            var package=new DeviceConfigurationPackage(new(2,Drivers.Values.Select(d=>new RegisteredDevice(d.DeviceId,d.DeviceId,"demo-rgb",DeviceKind.Memory,
                [new("light1","RGB",EndpointKind.Lighting)])).ToArray()),new(2,install,bindings),new(2,[],[]),new(2,[],[],[],[],[]),new(2,[]),
                new(2,new(),new(),new(Rules:[]),new([],[])),new(2,"native-v2",new('a',64),[],[]));
            var store=DeviceConfigurationCodec.Store(Path.Combine(Directory.Path,"config")); store.Activate(store.Stage(DeviceConfigurationCodec.Write(package)));
            Authority=new(store,install,store.ReadActive()!);
            Runtime=new(Drivers.Values,["user"],new TestLog(),Clock,physicalBindings:bindings); Runtime.InstallConfigurationHandler(Authority); Hello=Runtime.OpenSession("user");
        }
        public DeviceServiceRequest Command(DeviceOperation operation,params string[] ids)
        {
            var s=Runtime.State(); var write=DeviceServiceProtocol.IsWrite(operation);
            return new(2,Hello.Epoch,Hello.Session,Guid.NewGuid().ToString("N"),++sequence,"test",operation,write?s.Inventory.TopologyRevision:0,
                write?ids.Select(id=>s.Endpoints.Single(e=>e.Target==Target(id))).Select(e=>new DeviceTargetVersion(e.Target,e.Generation,e.PolicyRevision)).ToArray():null);
        }
        public async Task Set(LightingProgram p,params string[] ids)
        { var reply=await Runtime.ExecuteAsync(Command(DeviceOperation.SetLighting,ids) with { Lighting=p }); Assert.Equal(Outcome.WrittenUnverified,reply.Result!.Outcome); }
        public Task Tick(double time) { Clock.Advance(time); return Runtime.TickLightingAsync(); }
        public async Task<DeviceServiceRequest> Scene(params SynchronizedLighting[] parts)
        {
            var scene=new DeviceLightingScene("scene","切り替え",[],parts);
            var saved=await Runtime.ExecuteAsync(Command(DeviceOperation.LightingLibrary) with { LightingLibrary=new(LightingLibraryOperation.SaveScene,Authority.Generation.Id,
                scene.Id,Authority.Configuration.Lighting.Scenes.SingleOrDefault()?.Revision??0,Scene:scene) }); Assert.True(saved.Success,saved.Error);
            return Command(DeviceOperation.ApplyLightingPreset,parts.SelectMany(p=>p.Targets).Select(t=>t.DeviceId).ToArray()) with {
                LightingPreset=new(LightingPresetKind.Scene,Authority.Generation.Id,scene.Id,saved.LightingLibrary!.Revision) };
        }
        public async ValueTask DisposeAsync() { foreach(var d in Drivers.Values) d.Continue?.TrySetResult(); await Runtime.DisposeAsync(); Directory.Dispose(); }
    }
    private static EndpointReference Target(string id)=>new(id,"light1");
    private static readonly LightingProgram Cycle=new("color-cycle",[new(30,90,210)],4), Pulse=new("breathing",[new(220,40,10)],3);
    private static SynchronizedLighting Part(LightingProgram program,params string[] ids)=>new(ids.Select(Target).ToArray(),program);
    private static DeviceExecutionState State(Rig r,string id)=>r.Runtime.State().Endpoints.Single(e=>e.Target==Target(id));

    [Fact] public async Task MultipartSceneJoinsExistingGroupsButRetainsUnselectedClocksAndIndependentParts()
    {
        await using var r=new Rig(); await r.Set(Cycle,"a","b"); await r.Tick(.4); await r.Set(Pulse,"c","d"); await r.Tick(.3);
        var request=await r.Scene(Part(Pulse,"a","c"),Part(Cycle,"e")); var reply=await r.Runtime.ExecuteAsync(request);
        Assert.Equal(Outcome.WrittenUnverified,reply.Result!.Outcome); Assert.Equal(3,reply.Result.Targets.Length);
        Assert.Equal(DeviceLighting.Frame(Pulse,0),r.Lights["a"].Color); Assert.Equal(r.Lights["a"].Color,r.Lights["c"].Color);
        Assert.Equal(DeviceLighting.Frame(Cycle,.7),r.Lights["b"].Color); Assert.Equal(DeviceLighting.Frame(Pulse,.3),r.Lights["d"].Color);
        Assert.Equal(State(r,"a").Lighting!.GroupId,State(r,"d").Lighting!.GroupId); Assert.NotEqual(State(r,"a").Lighting!.GroupId,State(r,"e").Lighting!.GroupId);
        Assert.All(r.Lights.Values.Take(5),d=>{ Assert.Equal(1,d.Opens); Assert.Equal(0,d.Ends); });
        Assert.Equal(new[] {1,4},r.Authority.Configuration.Lighting.SavedFrameGroups!.Select(g=>g.Length).Order());
        Assert.All(reply.Result.Targets,t=>Assert.Equal(DevicePolicySaveState.Saved,t.Persistence!.State));
        var writes=r.Lights.Values.Sum(d=>d.Writes); await r.Runtime.ExecuteAsync(request); Assert.Equal(writes,r.Lights.Values.Sum(d=>d.Writes));
        await r.Tick(.2); Assert.Equal(DeviceLighting.Frame(Pulse,.2),r.Lights["a"].Color); Assert.Equal(DeviceLighting.Frame(Cycle,.9),r.Lights["b"].Color);
        Assert.Equal(DeviceLighting.Frame(Pulse,.5),r.Lights["d"].Color); Assert.Equal(DeviceLighting.Frame(Cycle,.2),r.Lights["e"].Color);
    }
    [Fact] public async Task SeparateScenePartsUpdateSeparateLiveGroupsWithoutCreatingOneFailureDomain()
    {
        await using var r=new Rig(); await r.Set(Cycle,"a","b"); await r.Set(Cycle,"c","d"); await r.Tick(.6);
        var aGroup=State(r,"a").Lighting!.GroupId; var cGroup=State(r,"c").Lighting!.GroupId;
        var reply=await r.Runtime.ExecuteAsync(await r.Scene(Part(Pulse,"a"),Part(Pulse with {Brightness=45},"c")));
        Assert.Equal(Outcome.WrittenUnverified,reply.Result!.Outcome); Assert.Equal(aGroup,State(r,"a").Lighting!.GroupId); Assert.Equal(cGroup,State(r,"c").Lighting!.GroupId);
        Assert.NotEqual(aGroup,cGroup); Assert.Equal(DeviceLighting.Frame(Cycle,.6),r.Lights["b"].Color); Assert.Equal(r.Lights["b"].Color,r.Lights["d"].Color);
        r.Lights["a"].FailFrame=true; await r.Tick(.2);
        Assert.False(State(r,"a").Lighting!.Running); Assert.True(State(r,"c").Lighting!.Running); Assert.Equal(0,r.Lights["c"].Ends);
        Assert.Equal(DeviceLighting.Frame(Pulse with {Brightness=45},.2),r.Lights["c"].Color);
    }
    [Fact] public async Task TwoDistinctJoinsInOneSceneUseDifferentGroupIdsAndRetainBothOutsideMembers()
    {
        await using var r=new Rig(); await r.Set(Cycle,"a","b"); await r.Set(Cycle,"c","d"); await r.Tick(.5);
        var reply=await r.Runtime.ExecuteAsync(await r.Scene(Part(Pulse,"a","e"),Part(Pulse,"c","f")));
        Assert.Equal(Outcome.WrittenUnverified,reply.Result!.Outcome); Assert.NotEqual(State(r,"a").Lighting!.GroupId,State(r,"c").Lighting!.GroupId);
        Assert.Equal(State(r,"a").Lighting!.GroupId,State(r,"e").Lighting!.GroupId); Assert.Equal(State(r,"c").Lighting!.GroupId,State(r,"f").Lighting!.GroupId);
        Assert.All(new[] {"b","d"},id=>Assert.Equal(DeviceLighting.Frame(Cycle,.5),r.Lights[id].Color));
        Assert.All(r.Lights.Values,d=>{Assert.Equal(1,d.Opens);Assert.Equal(0,d.Ends);});
        Assert.Equal(new[] {3,3},r.Authority.Configuration.Lighting.SavedFrameGroups!.Select(g=>g.Length).Order());
    }
    [Fact] public async Task AllPartsAreValidatedBeforeChangingAnyOldStreamOrOpeningANewOne()
    {
        await using var r=new Rig(); await r.Set(Cycle,"a","b"); await r.Tick(.4); var writes=r.Lights.Values.Sum(d=>d.Writes);
        r.Lights["f"].Supported=false; var request=await r.Scene(Part(Pulse,"a","c"),Part(Pulse,"f"));
        Assert.False((await r.Runtime.ExecuteAsync(request)).Success); Assert.Equal(writes,r.Lights.Values.Sum(d=>d.Writes));
        Assert.Equal(0,r.Lights["c"].Opens); Assert.Equal(0,r.Lights["a"].Ends); Assert.True(State(r,"b").Lighting!.Running);
    }
    [Fact] public async Task AFailedJoinRestoresItsSourcesButDoesNotStopAnIndependentScenePart()
    {
        await using var r=new Rig(); await r.Set(Cycle,"a","b"); await r.Set(Cycle,"c","d"); await r.Tick(.4); r.Lights["c"].FailFrame=true;
        var reply=await r.Runtime.ExecuteAsync(await r.Scene(Part(Pulse,"a","c"),Part(Pulse,"e")));
        Assert.Equal(Outcome.Partial,reply.Result!.Outcome); Assert.True(State(r,"e").Lighting!.Running); Assert.Equal(0,r.Lights["e"].Ends);
        Assert.All(new[] {"a","b","c","d"},id=>{Assert.False(State(r,id).Lighting!.Running);Assert.Equal(1,r.Lights[id].Ends);});
        Assert.True(DeviceLighting.SameProgram(Cycle,r.Authority.Configuration.Lighting.Preferences!.Single(p=>p.Target==Target("a")).Program));
        Assert.Equal(DevicePolicySaveState.Saved,reply.Result.Targets.Single(t=>t.Target==Target("e")).Persistence!.State);
    }
    [Fact] public async Task AFailedNewStreamLeavesTheOriginalGroupRunningAndOtherScenePartsCanComplete()
    {
        await using var r=new Rig(); await r.Set(Cycle,"a","b"); await r.Tick(.4); r.Drivers["c"].FailOpen=true;
        var reply=await r.Runtime.ExecuteAsync(await r.Scene(Part(Pulse,"a","c"),Part(Pulse,"e")));
        Assert.Equal(Outcome.Partial,reply.Result!.Outcome); Assert.True(State(r,"a").Lighting!.Running); Assert.True(State(r,"e").Lighting!.Running);
        Assert.True(DeviceLighting.SameProgram(Cycle,State(r,"a").RequestedLighting!)); Assert.Equal(0,r.Lights["a"].Ends);
        await r.Tick(.2); Assert.Equal(DeviceLighting.Frame(Cycle,.6),r.Lights["b"].Color); Assert.Equal(r.Lights["a"].Color,r.Lights["b"].Color);
    }
    [Fact] public async Task EditingTheSceneDuringNewStreamOpeningCannotApplyItsOldPartsLater()
    {
        await using var r=new Rig(); await r.Set(Cycle,"a","b"); await r.Tick(.4);
        var added=r.Drivers["c"]; added.Entered=new(TaskCreationOptions.RunContinuationsAsynchronously); added.Continue=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending=r.Runtime.ExecuteAsync(await r.Scene(Part(Pulse,"a","c"),Part(Pulse,"e")));
        try { await added.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await r.Scene(Part(Cycle,"a","c"),Part(Cycle,"e")); }
        finally { added.Continue.TrySetResult(); }
        var reply=await pending.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Contains(reply.Result!.Targets,t=>t.Outcome==Outcome.Rejected);
        Assert.True(DeviceLighting.SameProgram(Cycle,State(r,"a").RequestedLighting!)); Assert.Equal(0,r.Lights["a"].Ends); Assert.Equal(0,r.Lights["c"].Writes);
        await r.Tick(.2); Assert.Equal(DeviceLighting.Frame(Cycle,.6),r.Lights["a"].Color);
    }
}

public sealed partial class DeviceLightingCompositionTests
{
    [Fact] public async Task MultipartSceneCanAddAHubPortWithoutStoppingAnUnselectedPortOrReplacingItsRecoveryRecord()
    {
        await using var r=await DeviceLightingAdjustmentTests.HubRig.Create(); var cycle=new LightingProgram("color-cycle",[Blue],4);
        await r.Apply(r.Request(DeviceOperation.SetLighting,"led1","led3") with { Lighting=cycle }); r.Clock.Advance(.4); await r.Runtime.TickLightingAsync();
        var saved=r.Journal.Pending.ToDictionary(p=>p.Channel); var pulse=new LightingProgram("breathing",[Red],3);
        await r.Apply(await Scene(r,pulse,new("fixed",[Blue],Brightness:40)));
        Assert.Equal(saved["led1"],r.Journal.Pending.Single(p=>p.Channel=="led1")); Assert.Equal(saved["led3"],r.Journal.Pending.Single(p=>p.Channel=="led3"));
        Assert.Equal(3,r.Journal.Pending.Length); Assert.Equal(DeviceLighting.Frame(cycle,.4),r.Hub.Colors["led3"].Colors[0]);
        Assert.Equal(Blue.Scale(.4),r.Hub.Colors["led2"].Colors[0]); r.Clock.Advance(.3); await r.Runtime.TickLightingAsync();
        Assert.Equal(DeviceLighting.Frame(pulse,.3),r.Hub.Colors["led1"].Colors[0]); Assert.Equal(DeviceLighting.Frame(cycle,.7),r.Hub.Colors["led3"].Colors[0]);
        Assert.Equal(0,r.Hub.Fans.Writes); Assert.Equal(3,Assert.Single(r.Authority.Configuration.Lighting.SavedFrameGroups!).Length);
    }
}
