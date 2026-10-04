using System.Text.Json;
using System.Text.Json.Nodes;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceSceneCapacityTests
{
    private static readonly EndpointReference Target = new("a", "light1");
    private static readonly LightingProgram Blue = new("fixed", [new(0, 0, 180)]);
    private static DeviceLightingScene Scene(int index, bool visible = true) => new("scene-" + index, "シーン " + index, [new(Target, Blue)], [], ShowOnM5: visible);
    private static DeviceConfigurationPackage Package(int count, int visible)
    {
        var source = ServiceRuntimeBootstrap.MonitoringConfiguration(new('a', 32));
        return source with {
            Devices = new(2, [new("a", "照明", "demo", DeviceKind.Memory, [new("light1", "照明", EndpointKind.Lighting)])]),
            Lighting = source.Lighting with { Scenes = Enumerable.Range(0, count).Select(i => Scene(i, i < visible)).ToArray() }
        };
    }
    private static DeviceWorkspaceSnapshot Snapshot(DeviceConfigurationPackage configuration)
    {
        var device = new DeviceObservation("a", "demo", "照明", DeviceKind.Memory, IdentityConfidence.SerialBound, "a", "demo", ["a"],
            [new("light1", "照明", EndpointKind.Lighting, [new(DeviceFeature.FixedColor, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)])]);
        return new(new(new(new('b', 32), 1, [new(device, 1, true)], 1), [new(Target, 1, 1, Outcome.WrittenUnverified)], false, null),
            new(new('c', 32), configuration, []), 0, true, null, Session: new('d', 32));
    }
    [Theory]
    [InlineData(64, 32, true)] [InlineData(64, 0, true)] [InlineData(65, 32, false)] [InlineData(64, 33, false)]
    public void StoredCatalogEnforcesPcAndFirmwareLimitsSeparately(int count, int visible, bool accepted)
    {
        var package = Package(count, visible);
        if (!accepted)
        {
            var error = Assert.Throws<IOException>(() => DeviceConfigurationCodec.Validate(package));
            Assert.IsType<ArgumentException>(error.InnerException); return;
        }
        DeviceConfigurationCodec.Validate(package);
        var copy = DeviceConfigurationCodec.Read(DeviceConfigurationCodec.Write(package));
        Assert.Equal(count, copy.Lighting.Scenes.Length); Assert.Equal(visible, copy.Lighting.Scenes.Count(s => s.ShowOnM5));
        foreach (var portable in new[] { false, true })
        {
            var restored = DeviceSettingsArchives.Read(DeviceSettingsArchives.Write(DeviceSettingsArchives.Create(package, portable)));
            Assert.Equal(package.Lighting.Scenes.Select(s => s.ShowOnM5), restored.Configuration.Lighting.Scenes.Select(s => s.ShowOnM5));
        }
    }
    [Theory] [InlineData(31)] [InlineData(32)]
    public void DraftUsesAvailableFirmwareSlotsWithoutDiscardingAnEdit(int visible)
    {
        var snapshot = Snapshot(Package(visible, visible));
        var draft = new DeviceLightingLibraryDraft(snapshot, LightingPresetKind.Scene) { Name = "追加" }; draft.AddPart();
        Assert.Equal(visible < 32, draft.ShowOnM5); Assert.Equal(visible < 32, draft.CanShowOnM5);
        Assert.Equal(visible < 32, draft.Capture().Request.Scene!.ShowOnM5);
        draft.ShowOnM5 = true; draft.Update(snapshot);
        Assert.True(draft.ShowOnM5); Assert.Equal(visible + 1, draft.M5SceneCount);
        if (visible == 32) { Assert.Contains("32", draft.Problem); Assert.Throws<InvalidOperationException>(() => draft.Capture()); }
        draft.ShowOnM5 = false; Assert.Null(draft.Problem); Assert.False(draft.Capture().Request.Scene!.ShowOnM5);
        var saved = new DeviceLightingLibraryDraft(snapshot, LightingPresetKind.Scene, "scene-0");
        Assert.False(saved.Dirty); saved.ShowOnM5 = false; saved.Update(snapshot); Assert.True(saved.Dirty);
        Assert.False(saved.Capture().Request.Scene!.ShowOnM5); saved.ShowOnM5 = true; Assert.False(saved.Dirty);
        Assert.True(saved.CanDelete);
    }
    [Fact]
    public void FirmwareOnlyExposesChosenScenesAndRejectsHiddenOrOutdatedRequests()
    {
        var package = Package(64, 0);
        package = package with { Lighting = package.Lighting with { Scenes = package.Lighting.Scenes.Select((s, i) => s with { ShowOnM5 = i >= 32 }).ToArray() } };
        var snapshot = Snapshot(package); var view = DeviceM5Lighting.Read(snapshot, "all");
        Assert.True(view.Ready, view.Error); Assert.Equal(32, view.Scenes.Length);
        Assert.Equal(DeviceM5Lighting.WireId(LightingPresetKind.Scene, "scene-32"), view.Scenes[0].Id);
        var command = new M5LightingCommand("request", "ApplyLightingScene", "all", view.Stamp, view.Scenes[^1].Id);
        Assert.Equal("scene-63", DeviceM5Lighting.Capture(snapshot, command).Preset!.Id);
        Assert.Throws<InvalidOperationException>(() => DeviceM5Lighting.Capture(snapshot,
            command with { SceneId = DeviceM5Lighting.WireId(LightingPresetKind.Scene, "scene-0") }));
        var reversed = Snapshot(package with { Lighting = package.Lighting with { Scenes = package.Lighting.Scenes.Reverse().ToArray() } });
        var after = DeviceM5Lighting.Read(reversed, "all"); Assert.Equal(view.Scenes[^1].Id, after.Scenes[0].Id);
        Assert.Equal("scene-63", DeviceM5Lighting.Capture(reversed, command with { ExpectedStamp = after.Stamp }).Preset!.Id);
        var hidden = Snapshot(package with { Lighting = package.Lighting with { Scenes = package.Lighting.Scenes.Select(s => s with { ShowOnM5 = false }).ToArray() } });
        Assert.True(DeviceM5Lighting.Read(hidden, "all").Ready); Assert.Empty(DeviceM5Lighting.Read(hidden, "all").Scenes);
        Assert.Throws<InvalidOperationException>(() => DeviceM5Lighting.Capture(hidden, command));
    }
    [Fact]
    public void AbsentV2VisibilityKeepsPreviouslyPublishedScenesAndExplicitFalseRoundTrips()
    {
        var node = JsonSerializer.SerializeToNode(Scene(1), Contract.Json)!.AsObject(); node.Remove("showOnM5");
        Assert.True(node.Deserialize<DeviceLightingScene>(Contract.Json)!.ShowOnM5);
        Assert.False(Contract.Copy(Scene(1, false)).ShowOnM5);
    }
    [Fact]
    public async Task ServicePersistsAll64RejectsOverflowAtomicallyAndAllowsExplicitFirmwareReplacement()
    {
        await using var rig = new DeviceLightingLibraryTests.Rig();
        for (var i = 0; i < 64; i++)
        {
            var reply = await rig.Save(rig.SaveScene(Scene(i, i < 32))); Assert.True(reply.Success, reply.Error);
        }
        var generation = rig.Authority.Generation;
        var full = await rig.Save(rig.SaveScene(Scene(64, false)));
        Assert.False(full.Success); Assert.Contains("64", full.Error); Assert.Equal(generation, rig.Authority.Generation);
        var overflow = await rig.Save(rig.SaveScene(Scene(63)));
        Assert.False(overflow.Success); Assert.Contains("M5GO", overflow.Error); Assert.Equal(generation, rig.Authority.Generation);
        Assert.True((await rig.Save(rig.SaveScene(Scene(0, false)))).Success);
        Assert.True((await rig.Save(rig.SaveScene(Scene(63)))).Success);
        var restored = DeviceConfigurationCodec.Read(rig.Store.ReadActive()!.Files);
        Assert.Equal(64, restored.Lighting.Scenes.Length); Assert.Equal(32, restored.Lighting.Scenes.Count(s => s.ShowOnM5));
        Assert.False(restored.Lighting.Scenes.Single(s => s.Id == "scene-0").ShowOnM5);
        Assert.True(restored.Lighting.Scenes.Single(s => s.Id == "scene-63").ShowOnM5);
        Assert.All(new[] { rig.A, rig.B, rig.C }, d => { Assert.Equal(0, d.NativeWrites); Assert.Equal(0, d.Opens); });
        Assert.Equal(0, rig.Fan.Writes);
    }
}
