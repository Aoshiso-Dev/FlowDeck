using System.Security.Principal;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Light = FlowDeck.Tests.DeviceLightingRuntimeTests.Driver;

namespace FlowDeck.Tests;

public sealed class DeviceLightingLibraryTests
{
    private static readonly RgbColor Blue = new(0, 0, 200), Red = new(160, 0, 0);
    private static EndpointReference Target(string id) => new(id, "light1");
    private static EndpointGroup Group(params string[] ids) => new("group", "グループ", EndpointKind.Lighting, ids.Select(Target).ToArray());
    private static DeviceLightingScene Scene() => new("scene", "シーン", [new(Target("a"), new("fixed", [Red]))],
        [new([Target("b"), Target("c")], new("breathing", [Blue], Brightness: 50))]);
    internal sealed class Rig : IAsyncDisposable
    {
        public readonly ServiceTestDirectory Directory = new();
        public readonly TestClock Clock = new();
        public readonly DeviceResourceScheduler Scheduler = new();
        public readonly Light A = new("a"), B = new("b"), C = new("c");
        public readonly GenericTestDriver Fan = new("fan", "usb:a");
        public readonly ConfigurationGenerationStore Store;
        public readonly DeviceConfigurationAuthority Authority;
        public readonly DeviceControlRuntime Runtime;
        public DeviceServiceHello Hello = null!;
        private long sequence;
        public Rig(string user = "user", bool open = true, Func<DeviceConfigurationAuthority, IDeviceConfigurationHandler>? decorate = null)
        {
            var install = Guid.NewGuid().ToString("N");
            var fan = new BoundFan(Fan);
            var bindings = new[] { A.BindingCandidate, B.BindingCandidate, C.BindingCandidate, fan.BindingCandidate };
            var package = new DeviceConfigurationPackage(new(2, new[] { A, B, C }.Select(d => new RegisteredDevice(d.DeviceId, d.DeviceId, "demo-rgb", DeviceKind.Memory,
                    [new("light1", "RGB", EndpointKind.Lighting)])).Append(new("fan", "fan", "test", DeviceKind.Controller,
                    [new("fan1", "ファン", EndpointKind.Cooling)])).ToArray()), new(2, install, bindings), new(2, [], []), new(2, [], [], [], [], []),
                new(2, []), new(2, new(), new(), new(), new(Rules: []), new([], [])), new(2, "native-v2", new('a', 64), [], []));
            Store = DeviceConfigurationCodec.Store(Path.Combine(Directory.Path, "config")); Store.Activate(Store.Stage(DeviceConfigurationCodec.Write(package)));
            Authority = new(Store, install, Store.ReadActive()!);
            Runtime = new([A, B, C, fan], [user], new TestLog(), Clock, Scheduler, demo: true, physicalBindings: bindings);
            Runtime.InstallConfigurationHandler(decorate?.Invoke(Authority) ?? Authority);
            if (open) Hello = Runtime.OpenSession(user);
        }
        public LightingLibraryRequest SaveGroup(EndpointGroup group) => new(LightingLibraryOperation.SaveGroup, Authority.Generation.Id, group.Id,
            Authority.Configuration.Lighting.Groups.SingleOrDefault(g => g.Id == group.Id)?.Revision ?? 0, Group: group);
        public LightingLibraryRequest SaveScene(DeviceLightingScene scene) => new(LightingLibraryOperation.SaveScene, Authority.Generation.Id, scene.Id,
            Authority.Configuration.Lighting.Scenes.SingleOrDefault(s => s.Id == scene.Id)?.Revision ?? 0, Scene: scene);
        public DeviceServiceRequest Request(DeviceOperation operation) => new(2, Hello.Epoch, Hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", operation);
        public Task<DeviceServiceReply> Save(LightingLibraryRequest value) => Runtime.ExecuteAsync(Request(DeviceOperation.LightingLibrary) with { LightingLibrary = value });
        public DeviceServiceRequest Preset(bool scene = false, string effect = "fixed")
        {
            var library = Authority.Configuration.Lighting; var state = Runtime.State();
            var selected = scene ? library.Scenes.Single().Individual.Select(i => i.Target).Concat(library.Scenes.Single().Synchronized.SelectMany(g => g.Targets)).ToArray()
                : library.Groups.Single().Targets;
            var targets = selected.Select(t => state.Endpoints.Single(e => e.Target == t)).Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray();
            return Request(DeviceOperation.ApplyLightingPreset) with { TopologyRevision = state.Inventory.TopologyRevision, Targets = targets,
                LightingPreset = new(scene ? LightingPresetKind.Scene : LightingPresetKind.Group, Authority.Generation.Id, scene ? "scene" : "group",
                    scene ? library.Scenes.Single().Revision : library.Groups.Single().Revision, scene ? null : new(effect, [Blue])) };
        }
        public async ValueTask DisposeAsync() { await Runtime.DisposeAsync(); Directory.Dispose(); }
    }
    private sealed class BoundFan(GenericTestDriver source) : IPhysicalDeviceDriver
    {
        public string DeviceId => source.DeviceId;
        public PhysicalDeviceBinding BindingCandidate => new(DeviceId, "demo:" + DeviceId, "test-v1", new('b', 64));
        public DeviceObservation? Observe() => source.Observe();
        public string? Validate(DeviceEndpointAction action) => source.Validate(action);
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => source.ApplyAsync(action, ct);
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => source.ReleaseAsync(reason, ct);
        public ValueTask DisposeAsync() => source.DisposeAsync();
    }
    private sealed class ChangedAfterAcceptance(DeviceConfigurationAuthority source) : IDeviceConfigurationHandler, IDeviceLightingLibrary
    {
        private int checks;
        public Task<DeviceConfigurationReply> ExecuteAsync(DeviceConfigurationRequest request, string requestId, string owner, DeviceControlRuntime runtime, CancellationToken ct) =>
            source.ExecuteAsync(request, requestId, owner, runtime, ct);
        public Task<LightingLibraryReply> SaveLightingLibraryAsync(LightingLibraryRequest request, CancellationToken ct) => source.SaveLightingLibraryAsync(request, ct);
        public LightingPresetPlan ResolveLightingPreset(LightingPresetRequest request) => source.ResolveLightingPreset(request);
        public string? CheckLightingPreset(LightingPresetPlan plan) => ++checks > 1 ? "catalog changed" : source.CheckLightingPreset(plan);
    }
    [Fact]
    public async Task RejectionAfterAcceptanceIsPublishedInsteadOfLeavingEndpointsPending()
    {
        await using var r = new Rig(decorate: source => new ChangedAfterAcceptance(source));
        await r.Save(r.SaveGroup(Group("a", "b"))); var reply = await r.Runtime.ExecuteAsync(r.Preset());
        Assert.Equal(Outcome.Rejected, reply.Result!.Outcome);
        Assert.All(r.Runtime.State().Endpoints.Where(e => e.Target.DeviceId is "a" or "b"), e => Assert.Equal(Outcome.Rejected, e.Outcome));
        Assert.Equal(0, r.A.NativeWrites); Assert.Equal(0, r.B.NativeWrites);
    }
    [Fact]
    public async Task GroupAndSceneSaveAreDurableRevisionedAndNeverWriteHardware()
    {
        await using var r = new Rig(); var request = r.Request(DeviceOperation.LightingLibrary) with { LightingLibrary = r.SaveGroup(Group("a", "b")) };
        var reply = await r.Runtime.ExecuteAsync(request); Assert.True(reply.Success, reply.Error); Assert.Equal(1, reply.LightingLibrary!.Revision);
        var generation = r.Authority.Generation; Assert.True((await r.Runtime.ExecuteAsync(request)).Success); Assert.Equal(generation, r.Authority.Generation);
        Assert.True((await r.Save(r.SaveScene(Scene()))).Success);
        Assert.True((await r.Save(r.SaveGroup(Group("b", "c")))).Success);
        var disk = DeviceConfigurationCodec.Read(r.Store.ReadActive()!.Files).Lighting;
        Assert.Equal(2, Assert.Single(disk.Groups).Revision); Assert.Equal(new[] { Target("b"), Target("c") }, disk.Groups[0].Targets);
        Assert.Equal(3, disk.Revision); Assert.Equal("シーン", Assert.Single(disk.Scenes).Name);
        Assert.All(new[] { r.A, r.B, r.C }, d => { Assert.Equal(0, d.NativeWrites); Assert.Equal(0, d.Opens); });
        Assert.Equal(0, r.Fan.Writes); Assert.Contains("lighting-presets-v2", r.Hello.Features);
    }
    [Fact]
    public async Task StaleEditsAndMissingRegistrationsCannotReplaceTheCatalog()
    {
        await using var r = new Rig(); var stale = r.SaveGroup(Group("a"));
        Assert.True((await r.Save(stale)).Success); var generation = r.Authority.Generation;
        Assert.False((await r.Save(stale with { Group = Group("b") })).Success);
        Assert.False((await r.Save(r.SaveGroup(Group("missing")))).Success);
        Assert.Equal(generation, r.Authority.Generation); Assert.Equal(new[] { Target("a") }, r.Authority.Configuration.Lighting.Groups.Single().Targets);
    }
    [Fact]
    public async Task ASceneUsesOneFixedPartAndOneSynchronizedClockAndDuplicateApplyIsNotResent()
    {
        await using var r = new Rig(); await r.Save(r.SaveScene(Scene())); var request = r.Preset(scene: true);
        var reply = await r.Runtime.ExecuteAsync(request); Assert.Equal(Outcome.WrittenUnverified, reply.Result!.Outcome); Assert.Equal(3, reply.Result.Targets.Length);
        Assert.Equal(Red, r.A.Color); Assert.Equal(new RgbColor(0, 0, 100), r.B.Color); Assert.Equal(r.B.Color, r.C.Color);
        await r.Runtime.ExecuteAsync(request); Assert.Equal(1, r.A.NativeWrites); Assert.Equal(1, r.B.Opens);
        r.Clock.Advance(.5); await r.Runtime.TickLightingAsync(); Assert.Equal(r.B.Frames, r.C.Frames); Assert.Equal(Red, r.A.Color);
        Assert.All(reply.Result.Targets, t => Assert.NotNull(t.ReportedLighting));
    }
    [Fact]
    public async Task UnsupportedSceneMembersRejectAllPartsBeforeAnyOutput()
    {
        await using var r = new Rig(); await r.Save(r.SaveScene(Scene())); r.B.Supported = false;
        var reply = await r.Runtime.ExecuteAsync(r.Preset(scene: true)); Assert.False(reply.Success);
        Assert.Equal(0, r.A.NativeWrites); Assert.Equal(0, r.C.Opens); Assert.Equal(0, r.B.Opens);
    }
    [Fact]
    public async Task ACommunicationFailureRestoresOnlyItsSceneSynchronizationPartAndReportsPartial()
    {
        await using var r = new Rig(); await r.Save(r.SaveScene(Scene())); r.B.FailFrame = true;
        var reply = await r.Runtime.ExecuteAsync(r.Preset(scene: true));
        Assert.Equal(Outcome.Partial, reply.Result!.Outcome); Assert.Equal(3, reply.Result.Targets.Length);
        Assert.Equal(Red, r.A.Color); Assert.Equal(DeviceLightingStopReason.Failure, r.B.EndReason);
        Assert.Equal(DeviceLightingStopReason.Failure, r.C.EndReason); Assert.Equal(new RgbColor(9, 8, 7), r.C.Color);
        Assert.Equal(Outcome.WrittenUnverified, reply.Result.Targets.Single(t => t.Target == Target("a")).Outcome);
        Assert.NotEqual(Outcome.WrittenUnverified, reply.Result.Targets.Single(t => t.Target == Target("b")).Outcome);
    }
    [Fact]
    public async Task SlowCatalogStorageAllowsFanCommandsAndKeepalivesAndExitWaitsForTheCommit()
    {
        await using var r = new Rig(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        r.Store.Checkpoint = phase => { if (phase == "before-switch") { entered.TrySetResult(); if (!resume.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException(); } };
        var save = r.Save(r.SaveGroup(Group("a")));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.Renew))).Success);
            var state = r.Runtime.State(); var fan = state.Endpoints.Single(e => e.Target.DeviceId == "fan");
            var command = r.Request(DeviceOperation.SetDuty) with { TopologyRevision = state.Inventory.TopologyRevision,
                Targets = [new(fan.Target, fan.Generation, fan.PolicyRevision)], Duty = 75 };
            Assert.Equal(Outcome.Confirmed, (await r.Runtime.ExecuteAsync(command).WaitAsync(TimeSpan.FromSeconds(2))).Result!.Outcome);
            Assert.Equal(75, r.Fan.Duty); var exit = r.Runtime.DisconnectAsync(r.Hello.Session);
            Assert.False(exit.IsCompleted); Assert.Throws<IOException>(() => r.Runtime.OpenSession("user"));
            resume.Set(); await save; await exit.WaitAsync(TimeSpan.FromSeconds(3));
            r.Hello = r.Runtime.OpenSession("user"); Assert.Single(r.Authority.Configuration.Lighting.Groups);
        }
        finally { resume.Set(); r.Store.Checkpoint = null; await save; }
    }
    [Fact]
    public async Task AStoredGroupUsesTheCompiledHubDriverAndLeavesOtherLightingAndCoolingUntouched()
    {
        using var directory = new ServiceTestDirectory(); var hub = new DemoHub { DelayMs = 0 };
        var config = await DeviceRuntimeStartupTests.Package(hub, directory.Path, resume: false); var id = config.Devices.Devices[0].Id;
        config = config with { Devices = config.Devices with { Devices = config.Devices.Devices.Select(d => d with {
                Endpoints = d.Endpoints.Select(e => e.Id == "led1" ? e with { Setup = new("DEMO RGB", 24, true) } : e).ToArray() }).ToArray() },
            Lighting = config.Lighting with { Endpoints = [new(new(id, "led1"), true, new(LightingExitAction.Off))] } };
        var store = DeviceConfigurationCodec.Store(Path.Combine(directory.Path, "config")); store.Activate(store.Stage(DeviceConfigurationCodec.Write(config)));
        var driver = new HubDeviceDriver(config.Bindings.InstallationId!, DemoHub.Identity.Id, hub, directory.Path);
        await using var startup = await DeviceRuntimeStartup.CreateAsync(store, config.Bindings.InstallationId!, () => new([driver], []), ["user"], new TestLog(), demo: true);
        var hello = startup.Runtime.OpenSession("user");
        var group = new EndpointGroup("group", "Hub照明", EndpointKind.Lighting, [new(id, "led1")]);
        var save = new DeviceServiceRequest(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "test", DeviceOperation.LightingLibrary,
            LightingLibrary: new(LightingLibraryOperation.SaveGroup, startup.Generation.Id, "group", 0, Group: group));
        Assert.True((await startup.Runtime.ExecuteAsync(save)).Success); Assert.Equal(0, hub.LightingWrites); Assert.Equal(0, hub.Writes);
        var state = startup.Runtime.State(); var target = state.Endpoints.Single(e => e.Target == group.Targets[0]);
        var apply = new DeviceServiceRequest(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 2, "test", DeviceOperation.ApplyLightingPreset,
            state.Inventory.TopologyRevision, [new(target.Target, target.Generation, target.PolicyRevision)],
            LightingPreset: new(LightingPresetKind.Group, startup.Generation.Id, "group", 1, new("fixed", [Blue])));
        var reply = await startup.Runtime.ExecuteAsync(apply);
        Assert.True(reply.Result!.Outcome == Outcome.WrittenUnverified, JsonSerializer.Serialize(reply, Contract.Json));
        Assert.Equal(DevicePolicySaveState.Saved, Assert.Single(reply.Result.Targets).Persistence!.State);
        Assert.Equal(Blue, Assert.Single(startup.Configuration.Lighting.Preferences!).Program.Colors[0]);
        Assert.Equal(1, hub.LightingWrites); Assert.Equal(0, hub.Writes); Assert.Equal("fixed", hub.LightEffects["led1"].Mode);
        Assert.DoesNotContain(hub.LightEffects.Keys, key => key != "led1");
    }
    [Fact]
    public async Task TheClientCannotAddOrOmitTargetsOrUseAReplacedSceneRevision()
    {
        await using var r = new Rig(); await r.Save(r.SaveScene(Scene())); var old = r.Preset(scene: true);
        Assert.False((await r.Runtime.ExecuteAsync(old with { Targets = old.Targets![..1] })).Success);
        var next = Scene() with { Name = "新しいシーン" }; await r.Save(r.SaveScene(next));
        Assert.False((await r.Runtime.ExecuteAsync(r.Preset(scene: true) with { LightingPreset = old.LightingPreset })).Success);
        Assert.Equal(0, r.A.NativeWrites); Assert.Equal(0, r.B.Opens);
    }
    [Fact]
    public async Task EditingMembershipDoesNotRedirectAnAlreadyRunningGroup()
    {
        await using var r = new Rig(); await r.Save(r.SaveGroup(Group("a", "b")));
        Assert.Equal(Outcome.WrittenUnverified, (await r.Runtime.ExecuteAsync(r.Preset(effect: "breathing"))).Result!.Outcome);
        await r.Save(r.SaveGroup(Group("b", "c"))); r.Clock.Advance(.5); await r.Runtime.TickLightingAsync();
        Assert.Equal(0, r.C.Opens); Assert.Equal(r.A.Frames, r.B.Frames); Assert.Equal(2, r.A.Frames.Count);
        Assert.Equal(Outcome.WrittenUnverified, (await r.Runtime.ExecuteAsync(r.Preset(effect: "breathing"))).Result!.Outcome);
        Assert.Equal(0, r.A.Ends); Assert.Equal(1, r.B.Opens); Assert.Equal(1, r.C.Opens);
        Assert.Equal(DeviceLighting.Frame(new("breathing", [Blue]), .5), r.A.Color);
        Assert.Equal(DeviceLighting.Frame(new("breathing", [Blue]), 0), r.C.Color);
    }
    [Theory] [InlineData("fixed")] [InlineData("breathing")]
    public async Task AQueuedApplyCannotUseMembershipEditedBeforeTransmission(string effect)
    {
        await using var r = new Rig(); await r.Save(r.SaveGroup(Group("a")));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = r.Scheduler.RunAsync(["usb:a"], HardwarePriority.Manual, async _ => { entered.SetResult(); await resume.Task; return true; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); var apply = r.Runtime.ExecuteAsync(r.Preset(effect: effect));
        try
        {
            await Task.Run(async () => { while (r.Scheduler.PendingCount == 0) await Task.Delay(5); }).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True((await r.Save(r.SaveGroup(Group("b")))).Success); resume.SetResult(); await block; await apply;
            Assert.Equal(0, r.A.NativeWrites); Assert.Equal(0, r.A.Opens); Assert.Equal(0, r.B.NativeWrites); Assert.Equal(0, r.B.Opens);
        }
        finally { resume.TrySetResult(); await block; await apply; }
    }
    [Fact]
    public async Task FailedStorageLeavesThePreviousCatalogUsableAndWritesNoLights()
    {
        await using var r = new Rig(); await r.Save(r.SaveGroup(Group("a"))); var old = r.Authority.Generation;
        r.Store.Checkpoint = phase => { if (phase == "before-switch") throw new IOException("disk failed"); };
        Assert.False((await r.Save(r.SaveGroup(Group("b")))).Success); Assert.Equal(old, r.Authority.Generation);
        Assert.Equal(new[] { Target("a") }, DeviceConfigurationCodec.Read(r.Store.ReadActive()!.Files).Lighting.Groups.Single().Targets);
        r.Store.Checkpoint = null;
        Assert.Equal(Outcome.WrittenUnverified, (await r.Runtime.ExecuteAsync(r.Preset())).Result!.Outcome); Assert.Equal(Blue, r.A.Color); Assert.Equal(0, r.B.NativeWrites);
    }
    [Fact]
    public async Task DeletionAndRecreationCannotReviveAnOldQueuedPreset()
    {
        await using var r = new Rig(); await r.Save(r.SaveGroup(Group("a"))); var old = r.Preset();
        var deleted = await r.Save(new(LightingLibraryOperation.DeleteGroup, r.Authority.Generation.Id, "group", 1)); Assert.True(deleted.Success);
        Assert.Empty(r.Authority.Configuration.Lighting.Groups); await r.Save(r.SaveGroup(Group("a")));
        Assert.False((await r.Runtime.ExecuteAsync(old)).Success); Assert.Equal(0, r.A.NativeWrites);
    }
    [Fact]
    public async Task RealLocalPipeNegotiatesLibrarySavesAndSceneApplicationThroughTheSharedClient()
    {
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User!.Value;
        await using var r = new Rig(sid, open: false); using var stop = new CancellationTokenSource();
        var name = "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N");
        var serving = new DeviceServicePipeServer(r.Runtime, [sid], new TestLog(), name).RunAsync(stop.Token);
        try
        {
            await using var client = DeviceServicePipeClient.ForDemo(name, Environment.ProcessId); await client.ConnectAsync(renew: false);
            var snapshot = await client.ReadConfigurationAsync();
            var save = await client.SaveLightingSceneAsync(snapshot, Scene()); Assert.Equal(Outcome.Confirmed, save.Outcome);
            snapshot = await client.ReadConfigurationAsync(); var state = (await client.SendAsync(client.Request(DeviceOperation.State))).State!;
            var reply = await client.ApplyLightingSceneAsync(snapshot, state, "scene"); Assert.Equal(Outcome.WrittenUnverified, reply.Result!.Outcome);
            Assert.Equal(Red, r.A.Color); Assert.Equal(r.B.Color, r.C.Color);
        }
        finally { stop.Cancel(); await serving.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    [Fact]
    public void ProtocolRejectsMalformedPresetAndUnexpectedLibraryPayloads()
    {
        using var dir = new ServiceTestDirectory();
        var request = new DeviceServiceRequest(2, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), 1, "test", DeviceOperation.LightingLibrary,
            LightingLibrary: new(LightingLibraryOperation.SaveGroup, Guid.NewGuid().ToString("N"), "group", 0, Group: Group("a")));
        DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(request, Contract.Json));
        foreach (var bad in new[] { request with { Operation = DeviceOperation.Renew }, request with { LightingLibrary = request.LightingLibrary! with { ExpectedRevision = -1 } },
            request with { LightingLibrary = request.LightingLibrary! with { Group = Group("a", "a") } }, request with { Duty = 50 } })
            Assert.Throws<IOException>(() => DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(bad, Contract.Json)));
    }
}
