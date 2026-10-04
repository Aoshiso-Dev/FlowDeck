using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceNativeLightingTests
{
    private static readonly RgbColor Blue = new(20, 60, 200);
    private static DeviceNativeLightingEffect Effect => new("native/test/wave", "ウェーブ", true, 1, 3, 0, 255, 128, Direction: true, LedCount: 8);
    private static DeviceEndpoint Endpoint(DeviceNativeLightingEffect[]? effects) => new("rgb", "照明", EndpointKind.Lighting,
        [new(DeviceFeature.NativeEffect, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)], NativeLightingEffects: effects);
    private static DeviceObservation Observation(DeviceNativeLightingEffect[]? effects) => new("test", "test", "機器", DeviceKind.Memory, IdentityConfidence.SerialBound, "serial", "definition", ["demo:rgb"], [Endpoint(effects)]);
    [Fact] public void NativeDefinitionsHaveExactBoundsAndDoNotUseDisplayNamesAsIdentity()
    {
        Assert.All(BuiltInLightingEffects.Hub().Concat(BuiltInLightingEffects.Asrock()).Concat(BuiltInLightingEffects.Memory()), e => Assert.True(DeviceNativeLighting.Valid(e)));
        var p = new LightingProgram(Effect.Id, [Blue], 213, true, 50, Enumerable.Repeat(Blue, 8).ToArray()); Assert.True(DeviceNativeLighting.Accepts(Effect, p));
        Assert.False(DeviceNativeLighting.Accepts(Effect, p with { EffectId = "native/other/wave" }));
        var common = DeviceNativeLighting.Common([Endpoint([Effect]), Endpoint([Effect with { Name = "別名" }])]); Assert.Single(common);
        Assert.Equal(2, Assert.Single(DeviceNativeLighting.Common([Endpoint([Effect]), Endpoint([Effect with { MaximumColors = 2 }])])).MaximumColors);
        Assert.Empty(DeviceNativeLighting.Common([Endpoint([Effect]), Endpoint(null)]));
    }
    [Fact] public void MultiTargetIntersectionUsesOnlyRangesAndOptionsAcceptedByEveryEndpoint()
    {
        var other = Effect with { MinimumColors = 2, MinimumSpeed = 180, DefaultSpeed = 200, MaximumSpeed = 220, Direction = false, Brightness = false, LedCount = 16 };
        var common = Assert.Single(DeviceNativeLighting.Common([Endpoint([Effect]), Endpoint([other])]));
        Assert.Equal(2, common.MinimumColors); Assert.Equal(180, common.MinimumSpeed); Assert.Equal(220, common.MaximumSpeed); Assert.Equal(180, common.DefaultSpeed);
        Assert.False(common.Direction); Assert.False(common.Brightness); Assert.Equal(0, common.LedCount);
        var p = DeviceNativeLighting.Create(common, new("fixed", [Blue], 2, true, 40));
        Assert.True(DeviceNativeLighting.Accepts(Effect, p)); Assert.True(DeviceNativeLighting.Accepts(other, p));
        Assert.Empty(DeviceNativeLighting.Common([Endpoint([Effect with { MaximumColors = 1 }]), Endpoint([other])]));
        Assert.Empty(DeviceNativeLighting.Common([Endpoint([Effect with { MaximumSpeed = 150 }]), Endpoint([other])]));
    }
    [Theory][InlineData("id")][InlineData("name")][InlineData("colors")][InlineData("speed")][InlineData("default")][InlineData("leds")]
    public void InvalidCapabilityMetadataIsRejectedBeforeInventoryMutation(string kind)
    {
        var effect = kind switch { "id" => Effect with { Id = "native//wave" }, "name" => Effect with { Name = "" },
            "colors" => Effect with { MaximumColors = 33 }, "speed" => Effect with { MaximumSpeed = 256 }, "default" => Effect with { DefaultSpeed = -1 }, _ => Effect with { LedCount = 4097 } };
        var registry = new DeviceRegistry(); var first = registry.Reconcile([Observation([Effect])]);
        Assert.False(DeviceNativeLighting.Valid(effect)); Assert.Throws<ArgumentException>(() => registry.Reconcile([Observation([effect])])); Assert.Equal(first.Revision, registry.Snapshot.Revision);
    }
    [Fact] public void CapabilityChangesInvalidateOldGenerationsButNamesAndOrderDoNot()
    {
        var registry = new DeviceRegistry(); var extra = Effect with { Id = "native/test/fade" };
        var first = registry.Reconcile([Observation([Effect, extra])]).Devices[0];
        var renamed = registry.Reconcile([Observation([extra, Effect with { Name = "変更" }])]).Devices[0]; Assert.Equal(first.Generation, renamed.Generation);
        var changed = registry.Reconcile([Observation([Effect with { MaximumColors = 2 }, extra])]).Devices[0]; Assert.True(changed.Generation > first.Generation);
        var copy = registry.Snapshot; copy.Devices[0].Device.Endpoints[0].NativeLightingEffects![0] = Effect;
        Assert.Equal(2, registry.Snapshot.Devices[0].Device.Endpoints[0].NativeLightingEffects![0].MaximumColors);
        Assert.Throws<ArgumentException>(() => registry.Reconcile([Observation([Effect, Effect])]));
    }
    [Fact] public void ChoosingAnEffectNormalizesOnlyByItsAdvertisedCapabilities()
    {
        var prior = new LightingProgram("fixed", [Blue, new(255, 0, 0)], 200, true, 42, Enumerable.Repeat(Blue, 8).ToArray());
        var spectrum = BuiltInLightingEffects.Hub().Single(e => e.Id.EndsWith("/spectrum-wave"));
        var created = DeviceNativeLighting.Create(spectrum, prior); Assert.Empty(created.Colors); Assert.Null(created.LedColors); Assert.Equal(2, created.Speed); Assert.True(created.Reverse); Assert.Equal(100, created.Brightness);
        var memory = BuiltInLightingEffects.Memory().Single(e => e.Id.EndsWith("/2")); created = DeviceNativeLighting.Create(memory, prior);
        Assert.Single(created.Colors); Assert.Equal(8, created.LedColors!.Length); Assert.Equal(224, created.Speed); Assert.False(created.Reverse); Assert.Equal(42, created.Brightness);
        Assert.Equal(8, prior.LedColors!.Length); Assert.Equal(2, prior.Colors.Length); Assert.True(prior.Reverse);
    }
    [Fact] public async Task PackagedHubMetadataMatchesTheExistingDriverValidatorForEveryEffect()
    {
        using var directory = new ServiceTestDirectory(); var hub = new HubLightingDriverTests.Hub(); await using var driver = await HubLightingDriverTests.Ready(hub, directory.Path);
        var effects = driver.Observe()!.Endpoints.Single(e => e.Id == "led1").NativeLightingEffects!;
        Assert.Equal(BuiltInLightingEffects.Hub(), effects);
        foreach (var e in effects)
        {
            var p = DeviceNativeLighting.Create(e, new("fixed", [Blue]));
            Assert.True(DeviceNativeLighting.Accepts(e, p)); Assert.Null(driver.Validate(new("led1", DeviceOperation.SetLighting, Lighting: p)));
            var max = p with { Colors = Enumerable.Repeat(Blue, e.MaximumColors).ToArray(), Speed = e.MaximumSpeed, Reverse = e.Direction, Brightness = e.Brightness ? 50 : 100 };
            Assert.True(DeviceNativeLighting.Accepts(e, max)); Assert.Null(driver.Validate(new("led1", DeviceOperation.SetLighting, Lighting: max)));
        }
        Assert.Empty(hub.Writes);
    }
    [Fact] public void PackagedMemoryMetadataMatchesNativeColorAndLedTranslation()
    {
        foreach (var e in BuiltInLightingEffects.Memory())
        {
            var p = DeviceNativeLighting.Create(e, new("fixed", [Blue], 255, Brightness: 50));
            if (e.LedCount > 0) p = p with { LedColors = Enumerable.Range(0, 8).Select(i => new RgbColor(i * 10, 50, 100)).ToArray() };
            Assert.True(DeviceNativeLighting.Accepts(e, p)); var native = EneMemoryDeviceDriver.NativeSetting(p);
            Assert.True(MemoryLighting.Valid(native));
            if (e.LedCount > 0) { Assert.Equal(48, native.MemoryPalette!.Length); Assert.Equal(p.LedColors![0].Scale(.5).R, native.Red); }
        }
    }
    [Fact] public void NativeDraftPreservesPaletteAndDetectsUnsupportedTargetChanges()
    {
        var state = new DeviceServiceState(new(new('a', 32), 1, [new(Observation([Effect]), 1, true)], 1), [new(new("test", "rgb"), 1, 1, Outcome.Confirmed)], false, null);
        var original = new LightingProgram(Effect.Id, [Blue, new(200, 0, 0)], 213, true, 60);
        var library = new LightingConfiguration(2, [], [], [], [], [], Preferences: [new(new("test", "rgb"), original)]);
        var draft = new DeviceLightingDraft(); draft.Update(state, new('b', 32), library, 0, true); draft.Select([new("test", "rgb")]);
        Assert.False(draft.Mixed); Assert.False(draft.Dirty); Assert.True(draft.CanApply); Assert.True(DeviceLighting.SameProgram(original, draft.Program));
        draft.SetProgram(original with { Speed = 201 }); draft.Update(state, new('b', 32), library, 0, true); Assert.Equal(201, draft.Program.Speed); Assert.True(draft.Program.Reverse);
        var without = state with { Inventory = state.Inventory with { Devices = [new(Observation([]), 2, true)], TopologyRevision = 2 }, Endpoints = [state.Endpoints[0] with { Generation = 2 }] };
        draft.Update(without, new('b', 32), library, 0, true); Assert.True(draft.Conflict); Assert.False(draft.CanApply); Assert.Equal(2, draft.Program.Colors.Length);
        draft.Reset(); Assert.False(draft.CanApply); Assert.False(draft.Mixed); Assert.Equal(original.EffectId, draft.Program.EffectId);
    }
    private sealed class NativeDriver : IDeviceControlDriver
    {
        public int Writes;
        public string DeviceId => "test";
        public DeviceObservation Observe() => Observation([Effect with { Direction = false, MaximumSpeed = 200, LedCount = 0, Brightness = false }]);
        public string? Validate(DeviceEndpointAction action) => null; // Service metadata must still prevent invalid writes.
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) { Writes++; return Task.FromResult(new DeviceDriverResult(Outcome.WrittenUnverified, "sent")); }
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => Task.FromResult<DeviceDriverReleaseResult[]>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Theory][InlineData("color-count")][InlineData("speed")][InlineData("reverse")][InlineData("brightness")][InlineData("led-colors")][InlineData("foreign-id")]
    public async Task ServiceEnforcesNativeBoundsEvenWhenADriverWouldAcceptEverything(string kind)
    {
        var driver = new NativeDriver(); await using var runtime = new DeviceControlRuntime([driver], ["user"], new TestLog(), demo: true);
        var hello = runtime.OpenSession("user"); var state = runtime.State(); var target = state.Endpoints.Single();
        var p = new LightingProgram(Effect.Id, [Blue], 100);
        p = kind switch { "color-count" => p with { Colors = [] }, "speed" => p with { Speed = 201 }, "reverse" => p with { Reverse = true },
            "brightness" => p with { Brightness = 50 }, "led-colors" => p with { LedColors = [Blue] }, _ => p with { EffectId = "native/foreign/wave" } };
        var request = new DeviceServiceRequest(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "test", DeviceOperation.SetLighting,
            state.Inventory.TopologyRevision, [new(target.Target, target.Generation, target.PolicyRevision)], Lighting: p);
        var reply = await runtime.ExecuteAsync(request); Assert.False(reply.Success); Assert.Contains("能力", reply.Error); Assert.Equal(0, driver.Writes);
    }
}
