using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceNativeSceneTests
{
    private static readonly EndpointReference A = new("a", "rgb"), B = new("b", "rgb");
    private static readonly LightingProgram Palette = new("native/nzxt-control-hub/covering-marquee", [new(200, 20, 0), new(0, 30, 220)], 4, true, 50);
    private static DeviceWorkspaceSnapshot Snapshot(bool sameProvider = false)
    {
        var config = new DeviceConfigurationPackage(new(2, new[] { A, B }.Select(t => new RegisteredDevice(t.DeviceId, "照明", "demo", DeviceKind.Controller, [new(t.EndpointId, "RGB", EndpointKind.Lighting)])).ToArray()),
            new(2, new('a', 32), []), new(2, [], []), new(2, [], [], [], [], []), new(2, []), new(2, new(), new(), new(), new([], [])), new(2, "fresh", new('a', 64), [], []));
        var devices = new[] { A, B }.Select(t => new DeviceInstance(new(t.DeviceId, "demo", "照明", DeviceKind.Controller, IdentityConfidence.SerialBound, t.DeviceId, null, [t.DeviceId],
            [new(t.EndpointId, "RGB", EndpointKind.Lighting, new[] { DeviceFeature.FixedColor, DeviceFeature.NativeEffect, DeviceFeature.ColorFrames }.Select(f => new DeviceCapability(f,
                ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)).ToArray(), NativeLightingEffects: t == A || sameProvider ? BuiltInLightingEffects.Hub() : BuiltInLightingEffects.Memory())]), 1, true)).ToArray();
        return new(new(new(new('b', 32), 1, devices, 1), [], false, null), new(new('c', 32), config, []), 0, true, null, Session: new('d', 32));
    }
    [Fact] public void EachPartCanChooseItsOwnNativeEffectAndSavingCapturesItsRequirementsWithoutOutput()
    {
        var s = Snapshot(); var d = new DeviceLightingLibraryDraft(s, LightingPresetKind.Scene) { Name = "内蔵効果" }; var a = d.AddPart(); var b = d.AddPart();
        Assert.All(d.NativeEffects(a), e => Assert.StartsWith("native/nzxt-control-hub/", e.Id)); Assert.All(d.NativeEffects(b), e => Assert.StartsWith("native/ene-memory/", e.Id));
        d.ChangeKnownProgram(a, Palette); var effect = d.NativeEffects(b).Single(e => e.Id.EndsWith("/2"));
        var memory = DeviceNativeLighting.Create(effect, new("fixed", [new(10, 20, 30)])) with { Speed = 211, LedColors = Enumerable.Range(0, 8).Select(i => new RgbColor(i * 10, 20, 50)).ToArray() };
        d.ChangeKnownProgram(b, memory); d.Update(s); var captured = d.Capture().Request.Scene!;
        Assert.True(DeviceLighting.SameProgram(Palette, captured.Individual[0].Program)); Assert.Equal(8, captured.Individual[1].Program.LedColors!.Length);
        var requirements = captured.Requirements!; Assert.Equal(2, requirements.Length); Assert.All(requirements, r => Assert.Equal(DeviceFeature.NativeEffect, r.Feature));
        Assert.True(requirements.Single(r => r.Target == A).Reverse); Assert.True(requirements.Single(r => r.Target == A).RequiresBrightness);
        Assert.Equal(211, requirements.Single(r => r.Target == B).Speed); Assert.Equal(8, requirements.Single(r => r.Target == B).LedCount);
    }
    [Fact] public void ChangingPartMembershipNeverConvertsAnUnsupportedEffectToANameMatch()
    {
        var d = new DeviceLightingLibraryDraft(Snapshot(), LightingPresetKind.Scene) { Name = "保持" }; var part = d.AddPart(); d.ChangeKnownProgram(part, Palette);
        d.ChangePart(part, [B], Palette); Assert.NotNull(d.CapabilityIssue(part)); Assert.True(DeviceLighting.SameProgram(Palette, d.ReadPart(part)!.Program));
        Assert.Throws<ArgumentException>(() => d.ChangeKnownProgram(part, Palette with { Brightness = 70 }));
        Assert.Null(d.Problem); Assert.Equal(Palette.EffectId, d.Capture().Request.Scene!.Individual[0].Program.EffectId);
        d.ChangeKnownProgram(part, new("fixed", [new(0, 10, 100)])); Assert.Null(d.CapabilityIssue(part));
    }
    [Fact] public void CapabilityChangesPreserveSavedProgramsAndTheUserCanStillRenameOfflineScenes()
    {
        var s = Snapshot(); var d = new DeviceLightingLibraryDraft(s, LightingPresetKind.Scene) { Name = "保存途中" }; var part = d.AddPart(); d.ChangeKnownProgram(part, Palette);
        var changed = s with { State = s.State with { Inventory = s.State.Inventory with { Devices = s.State.Inventory.Devices.Select(i => i with { Connected = false,
            Device = i.Device with { Endpoints = i.Device.Endpoints.Select(e => e with { NativeLightingEffects = [] }).ToArray() } }).ToArray() } } };
        d.Update(changed); Assert.Empty(d.NativeEffects(part)); Assert.NotNull(d.CapabilityIssue(part)); Assert.Equal("保存途中", d.Name);
        Assert.Equal(2, d.Capture().Request.Scene!.Individual[0].Program.Colors.Length); Assert.True(d.ReadPart(part)!.Program.Reverse);
    }
    [Fact] public void SharedNativePartStoresOneRequirementPerTargetAndKeepsPaletteOrder()
    {
        var d = new DeviceLightingLibraryDraft(Snapshot(true), LightingPresetKind.Scene) { Name = "二台" }; var part = d.AddPart(); d.ChangePart(part, [A, B], Palette);
        d.ChangeKnownProgram(part, Palette with { Speed = 3 }); var scene = d.Capture().Request.Scene!;
        Assert.Empty(scene.Individual); Assert.Equal(2, Assert.Single(scene.Synchronized).Targets.Length); Assert.Equal(2, scene.Requirements!.Length);
        Assert.Equal(Palette.Colors, scene.Synchronized[0].Program.Colors); Assert.All(scene.Requirements, r => Assert.Equal(3, r.Speed));
    }
    [Theory][InlineData("target")][InlineData("feature")][InlineData("effect")][InlineData("colors")][InlineData("speed")][InlineData("direction")][InlineData("brightness")][InlineData("leds")][InlineData("missing")][InlineData("duplicate")]
    public void SuppliedRequirementsCannotWeakenOrContradictTheSavedPrograms(string kind)
    {
        var scene = DeviceLightingSceneRequirements.Stamp(new("scene", "照明", [new(A, Palette)], [])); var original = scene.Requirements![0];
        var altered = kind switch { "target" => original with { Target = B }, "feature" => original with { Feature = DeviceFeature.FixedColor },
            "effect" => original with { EffectId = "fixed" }, "colors" => original with { ColorCount = 1 }, "speed" => original with { Speed = 0 },
            "direction" => original with { Reverse = false }, "brightness" => original with { RequiresBrightness = false }, "leds" => original with { LedCount = 8 }, _ => original };
        var invalid = scene with { Requirements = kind == "missing" ? [] : kind == "duplicate" ? [original, original] : [altered] };
        Assert.False(DeviceLightingLibrary.ValidScene(invalid)); var s = Snapshot(); var config = s.Configuration.Configuration;
        var error = Assert.Throws<IOException>(() => DeviceConfigurationCodec.Validate(config with { Lighting = config.Lighting with { Scenes = [invalid] } }));
        Assert.IsType<ArgumentException>(error.InnerException); Assert.Contains("照明のシーン", error.InnerException!.Message);
    }
    [Fact] public async Task ServiceStampsRequirementsAndRejectsAnUnsupportedSceneBeforeAnyPartWrites()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var targets = demo.DeviceIds.Select(id => new EndpointReference(id, "led1")).ToArray();
        var scene = new DeviceLightingScene("native-scene", "内蔵シーン", [new(targets[0], Palette), new(targets[1], new("native/ene-memory/2", [new(20, 30, 40)], 224))], []);
        await client.SaveLightingAsync(new(LightingLibraryOperation.SaveScene, client.Snapshot!.Configuration.Generation, scene.Id, 0, Scene: scene));
        var saved = client.Snapshot!.Configuration.Configuration.Lighting.Scenes.Single(); Assert.Equal(2, saved.Requirements!.Length);
        var reply = await client.ApplyLightingAsync(DeviceWorkspaceClient.Preset(client.Snapshot, LightingPresetKind.Scene, scene.Id));
        Assert.Equal(Outcome.Rejected, reply.Outcome); Assert.All(demo.Hubs, h => Assert.Equal(0, h.LightingWrites));
        var draft = new DeviceLightingLibraryDraft(client.Snapshot!, LightingPresetKind.Scene, scene.Id); var second = draft.PartIds[1];
        draft.ChangeKnownProgram(second, Palette with { Reverse = false }); await client.SaveLightingEditAsync(draft.Capture());
        reply = await client.ApplyLightingAsync(DeviceWorkspaceClient.Preset(client.Snapshot!, LightingPresetKind.Scene, scene.Id)); Assert.Equal(Outcome.WrittenUnverified, reply.Outcome);
        Assert.True(demo.Hubs[0].LightEffects["led1"].Reverse); Assert.False(demo.Hubs[1].LightEffects["led1"].Reverse);
        Assert.All(demo.Hubs, h => { Assert.Equal(new RgbColor(100, 10, 0), h.LightEffects["led1"].Colors[0]); Assert.Equal(0, h.Writes); });
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task BackupAndPortableRemappingKeepRequirementsAlignedWithScenesAndRestoreBaselines(bool portable)
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var s = client.Snapshot!; var config = s.Configuration.Configuration;
        var targets = demo.DeviceIds.Select(id => new EndpointReference(id, "led1")).ToArray();
        var scene = DeviceLightingSceneRequirements.Stamp(new("scene", "移動する照明", [new(targets[0], Palette)], []));
        config = config with { Lighting = config.Lighting with { Scenes = [scene], Adjustments = [new("all", 100, true, scene)] } };
        var archive = DeviceSettingsArchives.Read(DeviceSettingsArchives.Write(DeviceSettingsArchives.Create(config, portable)));
        Assert.Equal(scene.Requirements, archive.Configuration.Lighting.Scenes[0].Requirements);
        var draft = new DeviceSettingsImportDraft(s, archive); foreach (var row in draft.Endpoints) draft.Map(row.Source,
            portable && row.Source == targets[0] ? targets[1] : portable && row.Source == targets[1] ? targets[0] : row.Source);
        draft.Confirmed = true; var next = draft.Capture().Configuration;
        foreach (var restored in new[] { next.Lighting.Scenes[0], next.Lighting.Adjustments[0].Baseline! }) {
            Assert.True(DeviceLightingSceneRequirements.Matches(restored)); Assert.Equal(portable ? targets[1] : targets[0], restored.Requirements![0].Target);
            Assert.True(DeviceLighting.SameProgram(Palette, restored.Individual[0].Program)); }
        Assert.All(demo.Hubs, h => Assert.Equal(0, h.LightingWrites));
    }
}
