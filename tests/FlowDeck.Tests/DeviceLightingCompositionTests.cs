using FlowDeck.Core;
using FlowDeck.Infrastructure;
using HubRig = FlowDeck.Tests.DeviceLightingAdjustmentTests.HubRig;
using Rig = FlowDeck.Tests.DeviceLightingLibraryTests.Rig;

namespace FlowDeck.Tests;

public sealed partial class DeviceLightingCompositionTests
{
    private static readonly RgbColor Red = new(200, 10, 0), Blue = new(10, 40, 240);
    private static EndpointReference Target(HubRig r, string port) => new(r.Driver.DeviceId, port);
    private static async Task<DeviceServiceRequest> Scene(HubRig r, params LightingProgram[] programs)
    {
        var scene = new DeviceLightingScene("mix", "複数の効果", programs.Select((p, i) => new EndpointLighting(Target(r, "led" + (i + 1)), p)).ToArray(), []);
        var save = await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.LightingLibrary) with { Targets = null, TopologyRevision = 0,
            LightingLibrary = new(LightingLibraryOperation.SaveScene, r.Authority.Generation.Id, "mix", 0, Scene: scene) });
        Assert.True(save.Success, save.Error);
        return r.Request(DeviceOperation.ApplyLightingPreset, scene.Individual.Select(p => p.Target.EndpointId).ToArray()) with {
            LightingPreset = new(LightingPresetKind.Scene, r.Authority.Generation.Id, "mix", 1) };
    }
    [Fact]
    public async Task FixedBreathingAndOffComposeWithoutNativeWritesAndIntensityDoesNotRestartTheClock()
    {
        await using var r = await HubRig.Create(); var pulse = new LightingProgram("breathing", [Blue], 4);
        await r.Apply(await Scene(r, new("fixed", [Red]), pulse, new("off", [])));
        r.Clock.Advance(.4); await r.Runtime.TickLightingAsync(); var owned = r.Journal.Pending;
        var group = Assert.Single(r.Runtime.State().Endpoints.Where(e => e.Lighting?.Running == true).Select(e => e.Lighting!.GroupId).Distinct());
        await r.Apply(r.Adjustment(LightingAdjustmentKind.Brightness, LightingAdjustmentScope.All, 50, "led1", "led2", "led3"));
        Assert.Equal(Red.Scale(.5), r.Hub.Colors["led1"].Colors[0]); Assert.Equal(DeviceLighting.Frame(pulse with { Brightness = 50 }, .4), r.Hub.Colors["led2"].Colors[0]);
        await r.Apply(r.Adjustment(LightingAdjustmentKind.Off, LightingAdjustmentScope.Targets, null, "led1")); Assert.Equal(new RgbColor(0, 0, 0), r.Hub.Colors["led1"].Colors[0]);
        r.Clock.Advance(.3); await r.Runtime.TickLightingAsync();
        await r.Apply(r.Adjustment(LightingAdjustmentKind.Restore, LightingAdjustmentScope.Targets, null, "led1"));
        Assert.Equal(Red.Scale(.5), r.Hub.Colors["led1"].Colors[0]); Assert.Equal(DeviceLighting.Frame(pulse with { Brightness = 50 }, .7), r.Hub.Colors["led2"].Colors[0]);
        Assert.Equal(new RgbColor(0, 0, 0), r.Hub.Colors["led3"].Colors[0]); Assert.Equal(owned, r.Journal.Pending);
        Assert.All(r.Hub.Writes, w => Assert.NotNull(w.Deadline)); Assert.Equal(0, r.Hub.Fans.Writes);
        Assert.All(r.Runtime.State().Endpoints.Where(e => e.Lighting?.Running == true), e => Assert.Equal(group, e.Lighting!.GroupId));
        var stop = await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.StopLighting, "led1", "led2", "led3")); Assert.Equal(Outcome.WrittenUnverified, stop.Result!.Outcome);
        Assert.Equal(Red.Scale(.5), r.Hub.Colors["led1"].Colors[0]); Assert.Equal(Blue.Scale(.5), r.Hub.Colors["led2"].Colors[0]);
        Assert.Equal(new RgbColor(0, 0, 0), r.Hub.Colors["led3"].Colors[0]); Assert.Empty(r.Journal.Pending);
    }
    [Fact]
    public async Task DifferentSoftwareEffectsShareOneControllerWhileAnUnselectedPortKeepsItsColor()
    {
        await using var r = await HubRig.Create(); var pulse = new LightingProgram("breathing", [Blue], 2); var cycle = new LightingProgram("color-cycle", [Red], 4);
        await r.Apply(r.Request(DeviceOperation.SetLighting, "led3") with { Lighting = new("fixed", [Red]) });
        var outsideWrites = r.Hub.Writes.Count(w => w.Channel == "led3");
        await r.Apply(await Scene(r, pulse, cycle)); r.Clock.Advance(.7); await r.Runtime.TickLightingAsync();
        Assert.Equal(DeviceLighting.Frame(pulse, .7), r.Hub.Colors["led1"].Colors[0]); Assert.Equal(DeviceLighting.Frame(cycle, .7), r.Hub.Colors["led2"].Colors[0]);
        Assert.Equal(Red, r.Hub.Colors["led3"].Colors[0]); Assert.Equal(outsideWrites, r.Hub.Writes.Count(w => w.Channel == "led3"));
        Assert.Equal(2, Assert.Single(r.Authority.Configuration.Lighting.SavedFrameGroups!).Length);
        Assert.Equal(2, r.Authority.Configuration.Lighting.SavedSynchronization!.Length);
    }
    [Fact]
    public async Task UnknownNativeEffectIsNotEmulatedOrAppliedBeforeTheWholeCompositionIsValidated()
    {
        await using var r = await HubRig.Create();
        var request = await Scene(r, new("native/nzxt-control-hub/rainbow", []), new("breathing", [Blue]));
        var reply = await r.Runtime.ExecuteAsync(request); Assert.False(reply.Success); Assert.Contains("合成", reply.Error);
        Assert.Empty(r.Hub.Writes); Assert.Empty(r.Journal.Pending); Assert.Empty(r.Authority.Configuration.Lighting.Preferences ?? []);
    }
    private sealed class Uncomposed(HubDeviceDriver source) : IDeviceLightingDriver, IPhysicalDeviceDriver
    {
        public string DeviceId => source.DeviceId;
        public PhysicalDeviceBinding? BindingCandidate => source.BindingCandidate;
        public DeviceLightingLimits? LightingLimits => source.LightingLimits;
        public DeviceObservation? Observe() => source.Observe();
        public string? Validate(DeviceEndpointAction action) => source.Validate(action);
        public string? ValidateLightingStream(string[] endpoints, LightingProgram program) => source.ValidateLightingStream(endpoints, program);
        public Task<IDeviceLightingStream> OpenLightingStreamAsync(string[] endpoints, LightingProgram program, CancellationToken ct) => source.OpenLightingStreamAsync(endpoints, program, ct);
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => source.ApplyAsync(action, ct);
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => source.ReleaseAsync(reason, ct);
        public ValueTask DisposeAsync() => source.DisposeAsync();
    }
    [Fact]
    public async Task EndpointColorsAloneDoNotOptADriverIntoDifferentEffects()
    {
        await using var r = await HubRig.Create(d => new Uncomposed(d));
        var reply = await r.Runtime.ExecuteAsync(await Scene(r, new("breathing", [Red]), new("color-cycle", [Blue])));
        Assert.False(reply.Success); Assert.Contains("合成", reply.Error); Assert.Empty(r.Hub.Writes);
    }
    [Fact]
    public async Task AFailedComposedFirstFrameDoesNotSaveSuccessfullyWrittenSiblingsAsActiveSelections()
    {
        await using var r = await HubRig.Create(); r.Hub.FailChannel = "led2";
        var reply = await r.Runtime.ExecuteAsync(await Scene(r, new("fixed", [Red]), new("breathing", [Blue])));
        Assert.True(reply.Success); Assert.Equal(Outcome.Partial, reply.Result!.Outcome);
        Assert.Empty(r.Authority.Configuration.Lighting.Preferences ?? []); Assert.Empty(r.Authority.Configuration.Lighting.SavedFrameGroups ?? []);
        Assert.DoesNotContain(reply.Result.Targets, t => t.Persistence?.State == DevicePolicySaveState.Saved); Assert.Equal(0, r.Hub.Fans.Writes);
        r.Hub.FailChannel = null;
    }
    [Fact]
    public async Task DifferentBrightnessDoesNotSplitSavedPhaseMembershipAcrossIndependentControllers()
    {
        await using var r = new Rig();
        DeviceServiceRequest Command(DeviceOperation operation, string[] ids)
        {
            var state = r.Runtime.State();
            return r.Request(operation) with { TopologyRevision = state.Inventory.TopologyRevision,
                Targets = ids.Select(id => state.Endpoints.Single(e => e.Target == new EndpointReference(id, "light1")))
                    .Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray() };
        }
        async Task Apply(DeviceServiceRequest request)
        {
            var reply = await r.Runtime.ExecuteAsync(request); Assert.True(reply.Success, reply.Error);
            Assert.All(reply.Result!.Targets, t => Assert.Equal(DevicePolicySaveState.Saved, t.Persistence!.State));
        }
        var program = new LightingProgram("color-cycle", [Blue], 4);
        await Apply(Command(DeviceOperation.SetLighting, ["a", "b", "c"]) with { Lighting = program });
        await Apply(Command(DeviceOperation.AdjustLighting, ["a"]) with {
            LightingAdjustment = new(LightingAdjustmentKind.Brightness, LightingAdjustmentScope.Targets, r.Authority.Generation.Id, 30) });
        Assert.Equal(3, Assert.Single(r.Authority.Configuration.Lighting.SavedFrameGroups!).Length);
        var portable = DeviceConfigurationCodec.Portable(r.Authority.Configuration); Assert.Empty(portable.Bindings.Devices);
        Assert.Equal(3, Assert.Single(DeviceConfigurationCodec.Read(DeviceConfigurationCodec.Write(portable)).Lighting.SavedFrameGroups!).Length);
        await r.Runtime.ExecuteAsync(Command(DeviceOperation.StopLighting, ["a", "b", "c"]));
        await Apply(Command(DeviceOperation.AdjustLighting, ["a", "b", "c"]) with {
            LightingAdjustment = new(LightingAdjustmentKind.Restore, LightingAdjustmentScope.All, r.Authority.Generation.Id) });
        Assert.Single(r.Runtime.State().Endpoints.Where(e => e.Lighting?.Running == true).Select(e => e.Lighting!.GroupId).Distinct());
        r.Clock.Advance(.5); await r.Runtime.TickLightingAsync();
        Assert.Equal(DeviceLighting.Frame(program with { Brightness = 30 }, .5), r.A.Color); Assert.Equal(DeviceLighting.Frame(program, .5), r.B.Color);
        Assert.Equal(2, r.A.Opens); Assert.Equal(2, r.B.Opens); Assert.Equal(2, r.C.Opens); Assert.Equal(0, r.Fan.Writes);
    }
    [Fact]
    public async Task ADelayedPhaseSnapshotCannotRecaptureASiblingOwnedByANewerSelection()
    {
        await using var r = new Rig(); var a = new EndpointReference("a", "light1"); var b = new EndpointReference("b", "light1");
        var program = new LightingProgram("breathing", [Blue]); var dimmed = program with { Brightness = 50 };
        var bindings = r.Authority.Configuration.Bindings.Devices.ToDictionary(d => d.DeviceId);
        await r.Authority.SaveLightingPoliciesAsync(new(100, [new([b], program)], [bindings["b"]], FrameGroups: [[new(b, program)]]));
        await r.Authority.SaveLightingPoliciesAsync(new(50, [new([a], dimmed)], [bindings["a"]], FrameGroups: [[new(a, dimmed), new(b, program)]]));
        var groups = r.Authority.Configuration.Lighting.SavedFrameGroups!;
        Assert.Equal(2, groups.Length); Assert.All(groups, g => Assert.Single(g));
        Assert.Equal(new[] { a, b }, groups.SelectMany(g => g).OrderBy(t => t.DeviceId));
        Assert.Equal(0, r.A.Writes); Assert.Equal(0, r.A.NativeWrites); Assert.Equal(0, r.B.Writes);
    }
    [Fact]
    public async Task StoredPhaseMembershipRejectsDuplicatesMissingSelectionsAndNativeEffects()
    {
        await using var r = new Rig(); var a = new EndpointReference("a", "light1"); var b = new EndpointReference("b", "light1");
        var program = new LightingProgram("breathing", [Blue]); var binding = r.Authority.Configuration.Bindings.Devices.Single(d => d.DeviceId == "a");
        await r.Authority.SaveLightingPoliciesAsync(new(1, [new([a], program)], [binding], FrameGroups: [[new(a, program)]]));
        var saved = r.Authority.Configuration;
        foreach (var invalid in new EndpointReference[][][] { [[a, a]], [[a], [a]], [[a, b]], [[new("fan", "fan1")]] })
            Assert.Throws<IOException>(() => DeviceConfigurationCodec.Write(saved with { Lighting = saved.Lighting with { SavedFrameGroups = invalid } }));
        Assert.Throws<IOException>(() => DeviceConfigurationCodec.Write(saved with { Lighting = saved.Lighting with {
            Preferences = [new(a, new("native/test/rainbow", []))], SavedSynchronization = [] } }));
        await Assert.ThrowsAsync<IOException>(() => r.Authority.SaveLightingPoliciesAsync(new(2, [new([a], program)], [binding], FrameGroups: [[new(a, program), new(a, program)]])));
    }
}
