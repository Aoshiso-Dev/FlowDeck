using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Rig = FlowDeck.Tests.DeviceLightingLibraryTests.Rig;

namespace FlowDeck.Tests;

public sealed class DeviceLightingPersistenceTests
{
    private static readonly RgbColor Blue = new(0, 0, 200), Red = new(160, 40, 20);
    private static EndpointReference Target(string id) => new(id, "light1");
    private static DeviceServiceRequest Apply(Rig r, LightingProgram program, params string[] ids)
    {
        var state = r.Runtime.State();
        return r.Request(DeviceOperation.SetLighting) with { TopologyRevision = state.Inventory.TopologyRevision, Lighting = program,
            Targets = ids.Select(id => state.Endpoints.Single(e => e.Target == Target(id))).Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray() };
    }
    private static LightingConfiguration Disk(Rig r) => DeviceConfigurationCodec.Read(r.Store.ReadActive()!.Files).Lighting;
    private static void Saved(DeviceServiceReply reply) => Assert.All(reply.Result!.Targets, t => Assert.Equal(DevicePolicySaveState.Saved, t.Persistence!.State));
    [Fact]
    public async Task AnInPlaceIntensityEditSavesItsUnscaledBaseAndKeepsOtherTargetsSelectedPrograms()
    {
        await using var r = new Rig(); var original = new LightingProgram("breathing", [Blue], 4);
        await r.Runtime.ExecuteAsync(Apply(r, original, "a", "b")); r.Clock.Advance(.5); await r.Runtime.TickLightingAsync();
        Saved(await r.Runtime.ExecuteAsync(Apply(r, original with { Brightness = 50 }, "a")));
        var disk = Disk(r); Assert.Equal(50, disk.Preferences!.Single(p => p.Target == Target("a")).Program.Brightness);
        Assert.Equal(100, disk.Preferences!.Single(p => p.Target == Target("b")).Program.Brightness);
        Assert.All(disk.Preferences!, p => Assert.Equal(Blue, p.Program.Colors[0]));
        Assert.Equal(2, disk.SavedSynchronization!.Length); Assert.Equal(1, r.A.Opens); Assert.Equal(1, r.B.Opens); Assert.Equal(0, r.A.Ends);
        var generation = r.Authority.Generation;
        Saved(await r.Runtime.ExecuteAsync(Apply(r, original with { Brightness = 50 }, "a")));
        Assert.Equal(generation, r.Authority.Generation); Assert.Equal(DeviceLighting.Frame(original with { Brightness = 50 }, .5), r.A.Color);
        Assert.Equal(DeviceLighting.Frame(original, .5), r.B.Color);
    }
    [Fact]
    public async Task NativeSelectionKeepsUnscaledColorsAndBrightnessWithoutClaimingReadback()
    {
        await using var r = new Rig(); var program = new LightingProgram("fixed", [Red], Brightness: 50);
        var request = Apply(r, program, "a"); var reply = await r.Runtime.ExecuteAsync(request); Saved(reply);
        Assert.Contains("lighting-policy-persistence-v2", r.Hello.Features);
        Assert.Equal(Outcome.WrittenUnverified, reply.Result!.Outcome); Assert.Equal(Red.Scale(.5), r.A.Color);
        var preference = Assert.Single(Disk(r).Preferences!); Assert.True(DeviceLighting.SameProgram(program, preference.Program));
        Assert.Equal(0, Disk(r).Revision); Assert.Equal(0, r.Fan.Writes);
        var generation = r.Authority.Generation; await r.Runtime.ExecuteAsync(request);
        Assert.Equal(generation, r.Authority.Generation); Assert.Equal(1, r.A.NativeWrites);
        Saved(await r.Runtime.ExecuteAsync(Apply(r, program, "a")));
        Assert.Equal(Red.Scale(.5), r.A.Color); Assert.Equal(generation, r.Authority.Generation);
        Assert.Equal(DevicePolicySaveState.Saved, r.Runtime.State().Endpoints.Single(e => e.Target == Target("a")).Persistence!.State);
    }
    [Theory] [InlineData("breathing")] [InlineData("color-cycle")]
    public async Task AnimationFramesDoNotReplaceTheSelectedProgramOrItsSynchronization(string effect)
    {
        await using var r = new Rig(); var program = new LightingProgram(effect, [Blue], 4, Brightness: 50);
        Saved(await r.Runtime.ExecuteAsync(Apply(r, program, "a", "b")));
        var generation = r.Authority.Generation; r.Clock.Advance(.5); await r.Runtime.TickLightingAsync();
        Assert.Equal(2, r.A.Frames.Count); Assert.Equal(r.A.Frames, r.B.Frames); Assert.Equal(generation, r.Authority.Generation);
        var disk = Disk(r); Assert.Equal(2, disk.Preferences!.Length);
        Assert.All(disk.Preferences, p => Assert.True(DeviceLighting.SameProgram(program, p.Program)));
        var sync = Assert.Single(disk.SavedSynchronization!); Assert.Equal(new[] { Target("a"), Target("b") }, sync.Targets);
        Assert.True(DeviceLighting.SameProgram(program, sync.Program));
        var portable = DeviceConfigurationCodec.Portable(r.Authority.Configuration);
        Assert.Empty(portable.Bindings.Devices); Assert.Null(portable.Bindings.InstallationId);
        Assert.True(DeviceLighting.SameProgram(program, Assert.Single(portable.Lighting.SavedSynchronization!).Program));
    }
    [Fact]
    public async Task AFailedSynchronizedPartIsRestoredWithoutSavingItsSuccessfulMembers()
    {
        await using var r = new Rig(); r.B.FailFrame = true;
        var scene = new DeviceLightingScene("scene", "test", [new(Target("a"), new("fixed", [Red]))],
            [new([Target("b"), Target("c")], new("breathing", [Blue]))]);
        Assert.True((await r.Save(r.SaveScene(scene))).Success); var reply = await r.Runtime.ExecuteAsync(r.Preset(scene: true));
        Assert.Equal(Outcome.Partial, reply.Result!.Outcome); Assert.Equal(Target("a"), Assert.Single(Disk(r).Preferences!).Target);
        Assert.Empty(Disk(r).SavedSynchronization!); Assert.Equal(DeviceLightingStopReason.Failure, r.C.EndReason);
        Assert.Equal(DevicePolicySaveState.Saved, reply.Result.Targets.Single(t => t.Target == Target("a")).Persistence!.State);
        Assert.Equal(DevicePolicySaveState.Superseded, reply.Result.Targets.Single(t => t.Target == Target("c")).Persistence!.State);
    }
    [Fact]
    public async Task ANewNativeSelectionUpdatesOnlyItsStoredTargetsAndClearsTheirOldSynchronization()
    {
        await using var r = new Rig(); await r.Runtime.ExecuteAsync(Apply(r, new("breathing", [Blue]), "a", "b"));
        Saved(await r.Runtime.ExecuteAsync(Apply(r, new("fixed", [Red]), "a")));
        var disk = Disk(r); Assert.Equal(Red, disk.Preferences!.Single(p => p.Target == Target("a")).Program.Colors[0]);
        Assert.Equal(Blue, disk.Preferences!.Single(p => p.Target == Target("b")).Program.Colors[0]);
        Assert.Equal(Target("b"), Assert.Single(Assert.Single(disk.SavedSynchronization!).Targets));
    }
    [Fact]
    public async Task ACommitFailureReportsStorageSeparatelyAndRetainsThePreviousGeneration()
    {
        await using var r = new Rig(); await r.Runtime.ExecuteAsync(Apply(r, new("fixed", [Red]), "a")); var before = r.Authority.Generation;
        r.Store.Checkpoint = phase => { if (phase == "before-switch") throw new IOException("disk failure"); };
        try
        {
            var reply = await r.Runtime.ExecuteAsync(Apply(r, new("fixed", [Blue]), "a"));
            Assert.Equal(Blue, r.A.Color); Assert.Equal(Outcome.Partial, reply.Result!.Outcome);
            var result = Assert.Single(reply.Result.Targets); Assert.Equal(Outcome.WrittenUnverified, result.Outcome);
            Assert.Equal(DevicePolicySaveState.Failed, result.Persistence!.State);
            Assert.Equal(DevicePolicySaveState.Failed, r.Runtime.State().Endpoints.Single(e => e.Target == Target("a")).Persistence!.State);
            Assert.Equal(before, r.Authority.Generation); Assert.Equal(Red, Assert.Single(Disk(r).Preferences!).Program.Colors[0]);
        }
        finally { r.Store.Checkpoint = null; }
    }
    private sealed class PersistenceHandler(DeviceConfigurationAuthority source) : IDeviceConfigurationHandler, IDeviceLightingPersistence
    {
        public Func<LightingPolicyChange, Task<LightingPolicySaveResult[]>> Save = source.SaveLightingPoliciesAsync;
        public Task<LightingPolicySaveResult[]> SaveLightingPoliciesAsync(LightingPolicyChange change) => Save(change);
        public Task<DeviceConfigurationReply> ExecuteAsync(DeviceConfigurationRequest request, string requestId, string owner, DeviceControlRuntime runtime, CancellationToken ct) =>
            source.ExecuteAsync(request, requestId, owner, runtime, ct);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ALateOlderCommitCannotOverwriteANewerSelectionOrSaveHalfAnOldSynchronization(bool synchronized)
    {
        PersistenceHandler handler = null!;
        await using var r = new Rig(decorate: source => handler = new(source));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var first = true;
        handler.Save = async change => { if (first) { first = false; entered.TrySetResult(); await proceed.Task; } return await r.Authority.SaveLightingPoliciesAsync(change); };
        var old = r.Runtime.ExecuteAsync(Apply(r, new(synchronized ? "breathing" : "fixed", [Red]), synchronized ? ["a", "b"] : ["a"]));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Saved(await r.Runtime.ExecuteAsync(Apply(r, new("fixed", [Blue]), "a")).WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { proceed.TrySetResult(); }
        Assert.All((await old).Result!.Targets, t => Assert.Equal(DevicePolicySaveState.Superseded, t.Persistence!.State));
        var preference = Assert.Single(Disk(r).Preferences!); Assert.Equal(Target("a"), preference.Target); Assert.Equal(Blue, preference.Program.Colors[0]);
        Assert.Empty(Disk(r).SavedSynchronization!);
        Assert.Equal(DevicePolicySaveState.Saved, r.Runtime.State().Endpoints.Single(e => e.Target == Target("a")).Persistence!.State);
    }
    [Fact]
    public async Task AFailedNewerCommitStillPreventsAnOlderDelayedSaveFromReplacingItsSelection()
    {
        PersistenceHandler handler = null!;
        await using var r = new Rig(decorate: source => handler = new(source));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var first = true;
        handler.Save = async change => { if (first) { first = false; entered.TrySetResult(); await proceed.Task; } return await r.Authority.SaveLightingPoliciesAsync(change); };
        var old = r.Runtime.ExecuteAsync(Apply(r, new("fixed", [Red]), "a"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            r.Store.Checkpoint = phase => { if (phase == "before-switch") throw new IOException("disk failure"); };
            var newer = await r.Runtime.ExecuteAsync(Apply(r, new("fixed", [Blue]), "a"));
            Assert.Equal(Outcome.Partial, newer.Result!.Outcome); Assert.Equal(DevicePolicySaveState.Failed, newer.Result.Targets[0].Persistence!.State);
        }
        finally { r.Store.Checkpoint = null; proceed.TrySetResult(); }
        Assert.Equal(DevicePolicySaveState.Superseded, (await old).Result!.Targets[0].Persistence!.State);
        Assert.Null(Disk(r).Preferences); Assert.Equal(Blue, r.A.Color);
        Assert.Equal(DevicePolicySaveState.Failed, r.Runtime.State().Endpoints.Single(e => e.Target == Target("a")).Persistence!.State);
    }
    [Fact]
    public async Task AMalformedStorageResultCannotEraseTheHardwareOutcome()
    {
        await using var r = new Rig(decorate: source => new PersistenceHandler(source) { Save = _ => Task.FromResult<LightingPolicySaveResult[]>([]) });
        var reply = await r.Runtime.ExecuteAsync(Apply(r, new("fixed", [Blue]), "a"));
        Assert.Equal(Outcome.Partial, reply.Result!.Outcome); Assert.Equal(Outcome.WrittenUnverified, reply.Result.Targets[0].Outcome);
        Assert.Equal(DevicePolicySaveState.Failed, reply.Result.Targets[0].Persistence!.State); Assert.Null(Disk(r).Preferences);
    }
    [Fact]
    public async Task SlowStorageDoesNotBlockHardwareReleaseButTheNextOwnerWaitsForItsCommit()
    {
        await using var r = new Rig(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var proceed = new ManualResetEventSlim();
        r.Store.Checkpoint = phase => { if (phase == "before-switch") { entered.TrySetResult(); if (!proceed.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); } };
        var command = r.Runtime.ExecuteAsync(Apply(r, new("fixed", [Blue]), "a")); Task? exit = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.Renew)).WaitAsync(TimeSpan.FromSeconds(1))).Success);
            var state = r.Runtime.State(); var fan = state.Endpoints.Single(e => e.Target.DeviceId == "fan");
            var duty = r.Request(DeviceOperation.SetDuty) with { TopologyRevision = state.Inventory.TopologyRevision,
                Targets = [new(fan.Target, fan.Generation, fan.PolicyRevision)], Duty = 75 };
            Assert.Equal(Outcome.Confirmed, (await r.Runtime.ExecuteAsync(duty).WaitAsync(TimeSpan.FromSeconds(1))).Result!.Outcome);
            exit = r.Runtime.DisconnectAsync(r.Hello.Session);
            await Task.Run(async () => { while (Volatile.Read(ref r.Fan.Releases) == 0) await Task.Delay(5); }).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(exit.IsCompleted); Assert.Throws<IOException>(() => r.Runtime.OpenSession("user"));
        }
        finally { proceed.Set(); r.Store.Checkpoint = null; await command; if (exit is not null) await exit; }
        r.Hello = r.Runtime.OpenSession("user"); Assert.Equal(Blue, Assert.Single(Disk(r).Preferences!).Program.Colors[0]);
    }
    [Fact]
    public async Task AStoredSelectionDoesNotAutomaticallyStartOutputInANewRuntime()
    {
        await using var r = new Rig(); await r.Runtime.ExecuteAsync(Apply(r, new("breathing", [Blue]), "a")); await r.Runtime.DisposeAsync();
        var driver = new DeviceLightingRuntimeTests.Driver("a"); var config = r.Authority.Configuration;
        await using var restarted = new DeviceControlRuntime([driver], ["user"], new TestLog(), demo: true, physicalBindings: config.Bindings.Devices);
        restarted.InstallConfigurationHandler(new DeviceConfigurationAuthority(r.Store, config.Bindings.InstallationId!, r.Store.ReadActive()!));
        restarted.OpenSession("user"); Assert.False(restarted.State().Endpoints.Single().Owned); Assert.Equal(0, driver.NativeWrites); Assert.Equal(0, driver.Opens);
    }
    [Fact]
    public async Task SavedSynchronizationRequiresTheMatchingBaseProgramAndUniqueLightingTargets()
    {
        await using var r = new Rig(); await r.Runtime.ExecuteAsync(Apply(r, new("breathing", [Blue]), "a", "b"));
        var config = r.Authority.Configuration; var sync = config.Lighting.SavedSynchronization![0];
        foreach (var invalid in new[] { sync with { Program = new("breathing", [Red]) }, sync with { Targets = [Target("a"), Target("a")] },
            sync with { Targets = [new("fan", "fan1")] } })
            Assert.Throws<IOException>(() => DeviceConfigurationCodec.Write(config with { Lighting = config.Lighting with { SavedSynchronization = [invalid] } }));
        var oldShape = config with { Lighting = config.Lighting with { SavedSynchronization = null } };
        Assert.Null(DeviceConfigurationCodec.Read(DeviceConfigurationCodec.Write(oldShape)).Lighting.SavedSynchronization);
    }
    [Fact]
    public void ExpandedPartsAllowIndividualTargetsUpToTheSceneLimitAndRejectDuplicates()
    {
        var parts = Enumerable.Range(0, 256).Select(i => new SynchronizedLighting([new("device", "led" + i)], new("fixed", [Blue]))).ToArray();
        Assert.True(DeviceLightingLibrary.ValidParts(parts)); Assert.False(DeviceLightingLibrary.ValidParts([.. parts, parts[0]]));
        Assert.False(DeviceLightingLibrary.ValidParts([parts[0], parts[0]]));
    }
}
