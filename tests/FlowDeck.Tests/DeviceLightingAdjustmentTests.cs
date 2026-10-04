using System.Security.Principal;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Rig = FlowDeck.Tests.DeviceLightingLibraryTests.Rig;

namespace FlowDeck.Tests;

public sealed class DeviceLightingAdjustmentTests
{
    private static readonly RgbColor Color = new(200, 100, 40);
    internal sealed class HubRig : IAsyncDisposable
    {
        private const string Installation = "0123456789abcdef0123456789abcdef";
        private readonly ServiceTestDirectory directory = new();
        public readonly HubLightingDriverTests.Hub Hub = new();
        public readonly TestClock Clock = new();
        public HubDeviceDriver Driver = null!;
        public DeviceConfigurationAuthority Authority = null!;
        public DeviceControlRuntime Runtime = null!;
        public HubLightingRecoveryJournal Journal => new(directory.Path, Driver.DeviceId);
        private DeviceServiceHello hello = null!;
        private long sequence;
        public static async Task<HubRig> Create(Func<HubDeviceDriver, IDeviceControlDriver>? decorate = null)
        {
            var r = new HubRig();
            r.Driver = await HubLightingDriverTests.Ready(r.Hub, r.directory.Path, r.Clock);
            var observed = r.Driver.Observe()!;
            var ports = observed.Endpoints.Select(e => new RegisteredEndpoint(e.Id, e.Name, e.Kind,
                Setup: e.Kind == EndpointKind.Lighting ? new("RGB", 24, true) : null)).ToArray();
            var package = new DeviceConfigurationPackage(new(2, [new(observed.Id, observed.Name, observed.Provider, observed.Kind, ports)]),
                new(2, Installation, [r.Driver.BindingCandidate!]), new(2, [], []), new(2, [], [], [], [], []),
                new(2, []), new(2, new(), new(), new(), new(Rules: []), new([], [])), new(2, "native-v2", new('a', 64), [], []));
            var store = DeviceConfigurationCodec.Store(Path.Combine(r.directory.Path, "config"));
            store.Activate(store.Stage(DeviceConfigurationCodec.Write(package)));
            r.Authority = new(store, Installation, store.ReadActive()!);
            r.Runtime = new([decorate?.Invoke(r.Driver) ?? r.Driver], ["user"], new TestLog(), r.Clock, demo: true, physicalBindings: [r.Driver.BindingCandidate!]);
            r.Runtime.InstallConfigurationHandler(r.Authority); r.hello = r.Runtime.OpenSession("user"); return r;
        }
        public DeviceServiceRequest Request(DeviceOperation operation, params string[] ports)
        {
            var state = Runtime.State();
            return new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", operation, state.Inventory.TopologyRevision,
                ports.Select(port => state.Endpoints.Single(e => e.Target.EndpointId == port)).Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray());
        }
        public DeviceServiceRequest Adjustment(LightingAdjustmentKind kind, LightingAdjustmentScope scope, int? brightness, params string[] ports) =>
            Request(DeviceOperation.AdjustLighting, ports) with { LightingAdjustment = new(kind, scope, Authority.Generation.Id, brightness) };
        public async Task Apply(DeviceServiceRequest request)
        {
            var reply = await Runtime.ExecuteAsync(request).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(reply.Success, JsonSerializer.Serialize(reply, Contract.Json)); Assert.Equal(Outcome.WrittenUnverified, reply.Result!.Outcome);
            Assert.All(reply.Result.Targets, t => Assert.Equal(DevicePolicySaveState.Saved, t.Persistence!.State));
        }
        public async ValueTask DisposeAsync() { await Runtime.DisposeAsync(); directory.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompiledHubRestartsOneStreamWithTheSavedPerPortLevelsAfterAnExplicitRestore(bool offBeforeStop)
    {
        await using var r = await HubRig.Create(); var program = new LightingProgram("color-cycle", [Color], 4);
        await r.Apply(r.Request(DeviceOperation.SetLighting, "led1", "led2", "led3") with { Lighting = program });
        await r.Apply(r.Adjustment(LightingAdjustmentKind.Brightness, LightingAdjustmentScope.Targets, 50, "led1"));
        if (offBeforeStop) await r.Apply(r.Adjustment(LightingAdjustmentKind.Off, LightingAdjustmentScope.All, null, "led1", "led2", "led3"));
        r.Clock.Advance(.4); await r.Runtime.TickLightingAsync();
        var stopped = await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.StopLighting, "led1", "led2", "led3"));
        Assert.Equal(Outcome.WrittenUnverified, stopped.Result!.Outcome);
        Assert.Empty(r.Journal.Pending);
        await r.Apply(r.Adjustment(LightingAdjustmentKind.Restore, LightingAdjustmentScope.All, null, "led1", "led2", "led3"));
        var live = r.Runtime.State().Endpoints.Where(e => e.Lighting is not null).Select(e => e.Lighting!.GroupId).Distinct().ToArray();
        Assert.Single(live);
        r.Clock.Advance(.3); await r.Runtime.TickLightingAsync();
        Assert.Equal(DeviceLighting.Frame(program with { Brightness = 50 }, .3), r.Hub.Colors["led1"].Colors[0]);
        Assert.Equal(DeviceLighting.Frame(program, .3), r.Hub.Colors["led2"].Colors[0]); Assert.Equal(r.Hub.Colors["led2"].Colors[0], r.Hub.Colors["led3"].Colors[0]);
        Assert.Equal(0, r.Hub.Fans.Writes);
        Assert.All(r.Authority.Configuration.Lighting.TargetAdjustments!, a => { Assert.False(a.All.Off); Assert.Equal(Color, a.Baseline.Colors[0]); });
    }

    [Fact]
    public async Task ColdFixedAndStreamCompositionUsesOneStreamAndPreservesEachSelectedProgram()
    {
        await using var r = await HubRig.Create();
        await r.Apply(r.Request(DeviceOperation.SetLighting, "led1", "led2") with { Lighting = new("breathing", [Color]) });
        await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.StopLighting, "led1", "led2"));
        await r.Apply(r.Request(DeviceOperation.SetLighting, "led3") with { Lighting = new("fixed", [Color]) });
        await r.Apply(r.Adjustment(LightingAdjustmentKind.Brightness, LightingAdjustmentScope.All, 50, "led1", "led2", "led3"));
        r.Clock.Advance(.5); await r.Runtime.TickLightingAsync();
        Assert.Equal(Color.Scale(.5), r.Hub.Colors["led3"].Colors[0]);
        Assert.Equal(DeviceLighting.Frame(new("breathing", [Color], Brightness: 50), .5), r.Hub.Colors["led1"].Colors[0]);
        Assert.Single(r.Runtime.State().Endpoints.Where(e => e.Lighting?.Running == true).Select(e => e.Lighting!.GroupId).Distinct());
        Assert.Equal(3, Assert.Single(r.Authority.Configuration.Lighting.SavedFrameGroups!).Length);
        Assert.Equal(0, r.Hub.Fans.Writes);
    }

    private static EndpointReference Target(string id) => new(id, "light1");
    private static DeviceServiceRequest Set(Rig r, LightingProgram program, params string[] ids) => Command(r, DeviceOperation.SetLighting, ids) with { Lighting = program };
    private static DeviceServiceRequest Command(Rig r, DeviceOperation op, string[] ids)
    {
        var state = r.Runtime.State(); return r.Request(op) with { TopologyRevision = state.Inventory.TopologyRevision,
            Targets = ids.Select(id => state.Endpoints.Single(e => e.Target == Target(id))).Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray() };
    }
    private static DeviceServiceRequest Adjust(Rig r, LightingAdjustmentKind kind, LightingAdjustmentScope scope = LightingAdjustmentScope.Targets,
        int? brightness = null, string? group = null, params string[] ids) => Command(r, DeviceOperation.AdjustLighting, ids) with {
            LightingAdjustment = new(kind, scope, r.Authority.Generation.Id, brightness, group,
                group is null ? 0 : r.Authority.Configuration.Lighting.Groups.Single(g => g.Id == group).Revision) };
    private static async Task Apply(Rig r, DeviceServiceRequest request)
    {
        var reply = await r.Runtime.ExecuteAsync(request).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(reply.Success, reply.Error); Assert.Equal(Outcome.WrittenUnverified, reply.Result!.Outcome);
        Assert.All(reply.Result.Targets, t => Assert.Equal(DevicePolicySaveState.Saved, t.Persistence!.State));
    }
    private static Task Group(Rig r, string id, params string[] ids) => r.Save(r.SaveGroup(new(id, id, EndpointKind.Lighting, ids.Select(Target).ToArray())));
    [Fact]
    public async Task GlobalGroupAndIndividualLevelsMultiplyOnceAndAnEndpointHasOnlyOneGroupLayer()
    {
        await using var r = new Rig(); await Group(r, "first", "a", "b"); await Group(r, "second", "b", "c");
        await Apply(r, Set(r, new("fixed", [Color], Brightness: 80), "a", "b", "c"));
        await Apply(r, Adjust(r, LightingAdjustmentKind.Brightness, LightingAdjustmentScope.All, 50, ids: ["a", "b", "c"]));
        var generation = r.Authority.Generation;
        await Apply(r, Adjust(r, LightingAdjustmentKind.Brightness, LightingAdjustmentScope.All, 50, ids: ["a", "b", "c"]));
        Assert.Equal(generation, r.Authority.Generation); Assert.Equal(Color.Scale(.4), r.A.Color);
        await Apply(r, Adjust(r, LightingAdjustmentKind.Brightness, LightingAdjustmentScope.Group, 50, "first", "a", "b"));
        await Apply(r, Adjust(r, LightingAdjustmentKind.Brightness, brightness: 50, ids: ["a"]));
        Assert.Equal(Color.Scale(.1), r.A.Color); Assert.Equal(Color.Scale(.2), r.B.Color); Assert.Equal(Color.Scale(.4), r.C.Color);
        await Apply(r, Adjust(r, LightingAdjustmentKind.Brightness, LightingAdjustmentScope.Group, 25, "second", "b", "c"));
        Assert.Equal(Color.Scale(.1), r.A.Color); Assert.Equal(Color.Scale(.1), r.B.Color); Assert.Equal(Color.Scale(.1), r.C.Color);
        var levels = r.Authority.Configuration.Lighting.TargetAdjustments!;
        Assert.All(levels, l => { Assert.Equal(Color, l.Baseline.Colors[0]); Assert.Equal(80, l.Baseline.Brightness); Assert.Equal(50, l.All.Brightness); });
        Assert.Equal("second", levels.Single(l => l.Target == Target("b")).GroupId); Assert.Equal(0, r.Fan.Writes);
    }
    [Fact]
    public async Task OffKeepsLevelsAndRestoringOneLayerDoesNotOverrideAnotherLayersOffFlag()
    {
        await using var r = new Rig(); await Group(r, "g", "a", "b"); await Apply(r, Set(r, new("fixed", [Color]), "a", "b", "c"));
        await Apply(r, Adjust(r, LightingAdjustmentKind.Brightness, brightness: 50, ids: ["a"]));
        await Apply(r, Adjust(r, LightingAdjustmentKind.Off, ids: ["a"]));
        await Apply(r, Adjust(r, LightingAdjustmentKind.Brightness, brightness: 75, ids: ["a"])); Assert.Equal(new RgbColor(0, 0, 0), r.A.Color);
        await Apply(r, Adjust(r, LightingAdjustmentKind.Off, LightingAdjustmentScope.All, ids: ["a", "b", "c"]));
        await Apply(r, Adjust(r, LightingAdjustmentKind.Restore, ids: ["a"])); Assert.Equal(new RgbColor(0, 0, 0), r.A.Color);
        await Apply(r, Adjust(r, LightingAdjustmentKind.Restore, LightingAdjustmentScope.All, ids: ["a", "b", "c"]));
        Assert.Equal(Color.Scale(.75), r.A.Color); Assert.Equal(Color, r.B.Color); Assert.Equal(Color, r.C.Color);
        var saved = DeviceConfigurationCodec.Read(r.Store.ReadActive()!.Files); var value = saved.Lighting.TargetAdjustments!.Single(t => t.Target == Target("a"));
        Assert.Equal(75, value.Individual.Brightness); Assert.False(value.Individual.Off); Assert.False(value.All.Off); Assert.Equal(Color, value.Baseline.Colors[0]);
    }
    [Fact]
    public async Task SynchronizedOffRestoreKeepsItsPhaseAndRestoresDifferentPerTargetLevels()
    {
        await using var r = new Rig(); await Group(r, "g", "a", "b");
        var program = new LightingProgram("color-cycle", [Color], 4); await Apply(r, Set(r, program, "a", "b", "c"));
        r.Clock.Advance(.3); await r.Runtime.TickLightingAsync(); var group = r.Runtime.State().Endpoints.First(e => e.Lighting is not null).Lighting!.GroupId;
        await Apply(r, Adjust(r, LightingAdjustmentKind.Brightness, LightingAdjustmentScope.Group, 50, "g", "a", "b"));
        await Apply(r, Adjust(r, LightingAdjustmentKind.Off, LightingAdjustmentScope.All, ids: ["a", "b", "c"]));
        Assert.Equal(new RgbColor(0, 0, 0), r.A.Color); r.Clock.Advance(.4); await r.Runtime.TickLightingAsync();
        await Apply(r, Adjust(r, LightingAdjustmentKind.Restore, LightingAdjustmentScope.All, ids: ["a", "b", "c"]));
        Assert.Equal(DeviceLighting.Frame(program with { Brightness = 50 }, .7), r.A.Color); Assert.Equal(r.A.Color, r.B.Color);
        Assert.Equal(DeviceLighting.Frame(program, .7), r.C.Color);
        Assert.All(new[] { r.A, r.B, r.C }, d => { Assert.Equal(1, d.Opens); Assert.Equal(0, d.Ends); });
        Assert.All(r.Runtime.State().Endpoints.Where(e => e.Lighting is not null), e => Assert.Equal(group, e.Lighting!.GroupId));
    }
    [Fact]
    public async Task MissingBaselineStaleGenerationAndIncompleteGroupsNeverChangeOutput()
    {
        await using var r = new Rig(); await Group(r, "g", "a", "b"); await Apply(r, Set(r, new("fixed", [Color]), "a"));
        var missing = Adjust(r, LightingAdjustmentKind.Brightness, LightingAdjustmentScope.Group, 50, "g", "a", "b");
        Assert.False((await r.Runtime.ExecuteAsync(missing)).Success); Assert.Equal(1, r.A.NativeWrites); Assert.Equal(0, r.B.NativeWrites);
        await Apply(r, Set(r, new("fixed", [Color]), "b"));
        var incomplete = Adjust(r, LightingAdjustmentKind.Brightness, LightingAdjustmentScope.Group, 50, "g", "a");
        Assert.False((await r.Runtime.ExecuteAsync(incomplete)).Success);
        var stale = Adjust(r, LightingAdjustmentKind.Brightness, brightness: 50, ids: ["a"]);
        await Apply(r, Set(r, new("fixed", [new(1, 2, 3)]), "b"));
        Assert.False((await r.Runtime.ExecuteAsync(stale)).Success); Assert.Equal(Color, r.A.Color);
    }
    [Fact]
    public async Task EditingGroupMembershipDoesNotChangeAppliedLevelsAndAReappliedGroupOwnsOnlyItsNewTargets()
    {
        await using var r = new Rig(); await Group(r, "g", "a", "b"); await Apply(r, Set(r, new("fixed", [Color]), "a", "b", "c"));
        await Apply(r, Adjust(r, LightingAdjustmentKind.Brightness, LightingAdjustmentScope.Group, 50, "g", "a", "b"));
        await Group(r, "g", "b", "c"); Assert.Equal(Color.Scale(.5), r.A.Color); Assert.Equal(Color, r.C.Color);
        _ = DeviceConfigurationCodec.Read(r.Store.ReadActive()!.Files);
        await Apply(r, Adjust(r, LightingAdjustmentKind.Brightness, LightingAdjustmentScope.Group, 25, "g", "b", "c"));
        Assert.Equal(Color.Scale(.5), r.A.Color); Assert.Equal(Color.Scale(.25), r.B.Color); Assert.Equal(Color.Scale(.25), r.C.Color);
        var entries = r.Authority.Configuration.Lighting.TargetAdjustments!;
        Assert.Equal(1, entries.Single(e => e.Target == Target("a")).GroupRevision); Assert.Equal(2, entries.Single(e => e.Target == Target("b")).GroupRevision);
    }
    [Fact]
    public async Task ExplicitNewColorReplacesTheOldOffBaselineInsteadOfRevivingIt()
    {
        await using var r = new Rig(); await Apply(r, Set(r, new("fixed", [Color]), "a")); await Apply(r, Adjust(r, LightingAdjustmentKind.Off, ids: ["a"]));
        var blue = new RgbColor(0, 0, 200); await Apply(r, Set(r, new("fixed", [blue]), "a"));
        Assert.Empty(r.Authority.Configuration.Lighting.TargetAdjustments!);
        await Apply(r, Adjust(r, LightingAdjustmentKind.Restore, ids: ["a"])); Assert.Equal(blue, r.A.Color);
    }
    [Fact]
    public async Task FailedStorageKeepsThePriorLayersAndDoesNotPretendThatTheNewOutputWasSaved()
    {
        await using var r = new Rig(); await Apply(r, Set(r, new("fixed", [Color]), "a"));
        await Apply(r, Adjust(r, LightingAdjustmentKind.Brightness, brightness: 50, ids: ["a"])); var before = r.Authority.Generation;
        r.Store.Checkpoint = phase => { if (phase == "before-switch") throw new IOException("disk failure"); };
        try
        {
            var reply = await r.Runtime.ExecuteAsync(Adjust(r, LightingAdjustmentKind.Off, ids: ["a"]));
            Assert.Equal(Outcome.Partial, reply.Result!.Outcome); Assert.Equal(Outcome.WrittenUnverified, reply.Result.Targets[0].Outcome);
            Assert.Equal(DevicePolicySaveState.Failed, reply.Result.Targets[0].Persistence!.State); Assert.Equal(new RgbColor(0, 0, 0), r.A.Color);
        }
        finally { r.Store.Checkpoint = null; }
        Assert.Equal(before, r.Authority.Generation); Assert.False(r.Authority.Configuration.Lighting.TargetAdjustments![0].Individual.Off);
        Assert.False((await r.Runtime.ExecuteAsync(Adjust(r, LightingAdjustmentKind.Restore, ids: ["a"]))).Success);
    }
    [Fact]
    public async Task ANewAdjustmentCannotReadAnOldBaselineWhileItsPredecessorIsBeingSaved()
    {
        await using var r = new Rig(); await Apply(r, Set(r, new("fixed", [Color]), "a"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var proceed = new ManualResetEventSlim();
        r.Store.Checkpoint = phase => { if (phase == "before-switch") { entered.TrySetResult(); if (!proceed.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); } };
        var applying = r.Runtime.ExecuteAsync(Adjust(r, LightingAdjustmentKind.Brightness, brightness: 50, ids: ["a"]));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False((await r.Runtime.ExecuteAsync(Adjust(r, LightingAdjustmentKind.Off, ids: ["a"]))).Success);
            Assert.True((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.Renew))).Success); Assert.Equal(Color.Scale(.5), r.A.Color);
        }
        finally { proceed.Set(); r.Store.Checkpoint = null; await applying; }
        await Apply(r, Adjust(r, LightingAdjustmentKind.Off, ids: ["a"]));
        await Apply(r, Adjust(r, LightingAdjustmentKind.Restore, ids: ["a"])); Assert.Equal(Color.Scale(.5), r.A.Color);
    }
    [Fact]
    public async Task SavedLevelsSurvivePortableExportButCannotStartHardwareByThemselves()
    {
        await using var r = new Rig(); await Apply(r, Set(r, new("fixed", [Color]), "a"));
        await Apply(r, Adjust(r, LightingAdjustmentKind.Brightness, brightness: 50, ids: ["a"])); await Apply(r, Adjust(r, LightingAdjustmentKind.Off, ids: ["a"]));
        var portable = DeviceConfigurationCodec.Portable(r.Authority.Configuration); Assert.Empty(portable.Bindings.Devices);
        var layer = Assert.Single(portable.Lighting.TargetAdjustments!); Assert.True(layer.Individual.Off); Assert.Equal(50, layer.Individual.Brightness);
        Assert.Equal(Color, layer.Baseline.Colors[0]); Assert.Equal("off", DeviceLightingAdjustments.Effective(layer).EffectId);
        Assert.Throws<IOException>(() => DeviceConfigurationCodec.Write(portable with { Lighting = portable.Lighting with {
            TargetAdjustments = [layer with { Individual = new(50, false) }] } }));
    }
    [Fact]
    public async Task AuthenticatedDemoPipeUsesTheSameBrightnessOffAndRestoreContract()
    {
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User!.Value;
        await using var r = new Rig(sid, open: false); using var stop = new CancellationTokenSource();
        var name = "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N");
        var serving = new DeviceServicePipeServer(r.Runtime, [sid], new TestLog(), name).RunAsync(stop.Token);
        try
        {
            await using var client = DeviceServicePipeClient.ForDemo(name, Environment.ProcessId); await client.ConnectAsync(renew: false);
            var state = (await client.SendAsync(client.Request(DeviceOperation.State))).State!; var target = state.Endpoints.Single(e => e.Target == Target("a"));
            await client.SendAsync(client.Request(DeviceOperation.SetLighting, topology: state.Inventory.TopologyRevision,
                targets: [new(target.Target, target.Generation, target.PolicyRevision)], lighting: new("fixed", [Color])));
            foreach (var kind in new[] { LightingAdjustmentKind.Brightness, LightingAdjustmentKind.Off, LightingAdjustmentKind.Restore })
            {
                var snapshot = await client.ReadConfigurationAsync(); state = (await client.SendAsync(client.Request(DeviceOperation.State))).State!;
                var reply = await client.AdjustLightingAsync(snapshot, state, kind, LightingAdjustmentScope.Targets,
                    kind == LightingAdjustmentKind.Brightness ? 50 : null, targets: [Target("a")]);
                Assert.Equal(Outcome.WrittenUnverified, reply.Result!.Outcome); Assert.Equal(DevicePolicySaveState.Saved, reply.Result.Targets[0].Persistence!.State);
            }
            Assert.Equal(Color.Scale(.5), r.A.Color); Assert.Equal(0, r.Fan.Writes);
        }
        finally { stop.Cancel(); await serving.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    [Fact]
    public void ProtocolRejectsUnknownScopesExtraneousParametersAndInvalidLevelRequests()
    {
        var id = Guid.NewGuid().ToString("N"); var request = new DeviceServiceRequest(2, id, id, id, 1, "test", DeviceOperation.AdjustLighting, 1,
            [new(Target("a"), 1, 0)], LightingAdjustment: new(LightingAdjustmentKind.Brightness, LightingAdjustmentScope.Targets, id, 50));
        DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(request, Contract.Json));
        foreach (var bad in new[] { request with { LightingAdjustment = null }, request with { Lighting = new("fixed", [Color]) },
            request with { LightingAdjustment = request.LightingAdjustment! with { Brightness = 101 } },
            request with { LightingAdjustment = request.LightingAdjustment! with { Kind = LightingAdjustmentKind.Off } },
            request with { LightingAdjustment = request.LightingAdjustment! with { Scope = (LightingAdjustmentScope)99 } },
            request with { LightingAdjustment = request.LightingAdjustment! with { Scope = LightingAdjustmentScope.Group } },
            request with { Operation = DeviceOperation.SetLighting, Lighting = new("fixed", [Color]) } })
            Assert.Throws<IOException>(() => DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(bad, Contract.Json)));
    }
}
