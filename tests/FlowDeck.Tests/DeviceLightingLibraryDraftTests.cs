using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceLightingLibraryDraftTests
{
    private const string Epoch = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Session = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Generation = "cccccccccccccccccccccccccccccccc";
    private static readonly EndpointReference A = new("a", "led1"), B = new("b", "led1"), C = new("b", "led2");
    private static readonly LightingProgram Red = new("fixed", [new(200, 0, 0)]);
    private static DeviceWorkspaceSnapshot Snapshot(DeviceLightingScene? scene = null)
    {
        var config = new DeviceConfigurationPackage(new(2, [new("a", "同じ機器", "demo", DeviceKind.Controller, [new("led1", "同じ照明", EndpointKind.Lighting)]),
            new("b", "同じ機器", "demo", DeviceKind.Controller, [new("led1", "同じ照明", EndpointKind.Lighting), new("led2", "別の照明", EndpointKind.Lighting)])]),
            new(2, Epoch, []), new(2, [], []), new(2, scene is null ? [] : [scene], [new("group", "照明", EndpointKind.Lighting, [A, B], 3)], [], [], []),
            new(2, []), new(2, new(), new(), new(), new(), new([], [])), new(2, "fresh", new('a', 64), [], []));
        return new(new(new(Epoch, 0, []), [], false, null), new(Generation, config, []), 0, true, null, Session: Session);
    }
    [Fact] public void NamesAreNotIdentityAndPollingPreservesGroupNameAndMembership()
    {
        var s = Snapshot(); var d = new DeviceLightingLibraryDraft(s, LightingPresetKind.Group, "group");
        Assert.False(d.Dirty); d.Name = "入力中の前面照明"; d.SetTargets([B]);
        for (var i = 0; i < 3; i++) d.Update(s);
        Assert.Equal("入力中の前面照明", d.Name); Assert.True(d.Dirty); Assert.False(d.Conflict);
        var edit = d.Capture(); Assert.Equal(Session, edit.Session); Assert.Equal(Epoch, edit.Epoch); Assert.Equal(3, edit.Request.ExpectedRevision);
        Assert.Equal(B, Assert.Single(edit.Request.Group!.Targets)); Assert.Null(edit.Request.Scene);
        Assert.Equal(2, s.Configuration.Configuration.Lighting.Groups[0].Targets.Length);
    }
    [Theory][InlineData("epoch")][InlineData("session")][InlineData("generation")]
    public void VersionConflictsAreStickyAndNeverRebaseTypedEdits(string kind)
    {
        var s = Snapshot(); var d = new DeviceLightingLibraryDraft(s, LightingPresetKind.Group, "group"); d.Name = "保存前"; d.SetTargets([C]);
        var changed = kind switch { "epoch" => s with { State = s.State with { Inventory = s.State.Inventory with { Epoch = new('d', 32) } } },
            "session" => s with { Session = new('d', 32) }, _ => s with { Configuration = s.Configuration with { Generation = new('d', 32) } } };
        d.Update(changed); d.Update(s); Assert.True(d.Conflict); Assert.False(d.CanDelete); Assert.Equal("保存前", d.Name); Assert.Equal(C, Assert.Single(d.Targets));
        Assert.Throws<InvalidOperationException>(() => d.Capture()); Assert.Throws<InvalidOperationException>(() => d.Capture(true));
    }
    [Theory][InlineData("disconnect")][InlineData("expired")][InlineData("nan")][InlineData("exit")][InlineData("release")][InlineData("configuration")]
    public void SaveAndDeleteRequireFreshLiveServiceButDoNotRequirePhysicalLightsOnline(string kind)
    {
        var s = Snapshot(); var d = new DeviceLightingLibraryDraft(s, LightingPresetKind.Group, "group"); d.Name = "変更";
        Assert.Null(d.Problem); Assert.True(d.CanDelete); // All physical devices are offline in this snapshot.
        d.Update(kind switch { "disconnect" => s with { Connected = false }, "expired" => s with { ElapsedSeconds = 3 }, "nan" => s with { ElapsedSeconds = double.NaN },
            "exit" => s with { ExitAttempted = true }, "release" => s with { State = s.State with { Releasing = true } },
            _ => s with { State = s.State with { ConfigurationChanging = true } } });
        Assert.NotNull(d.Problem); Assert.False(d.CanDelete); Assert.Throws<InvalidOperationException>(() => d.Capture());
        d.Update(s); Assert.Null(d.Problem); Assert.Equal("変更", d.Name);
    }
    [Fact] public void InvalidAndDuplicateTargetsCannotBeInsertedAndAnEmptyDraftIsEditableButUnsavable()
    {
        var d = new DeviceLightingLibraryDraft(Snapshot(), LightingPresetKind.Group); d.Name = "新規";
        Assert.NotNull(d.Problem); Assert.False(d.CanDelete);
        Assert.Throws<ArgumentException>(() => d.SetTargets([A, A])); Assert.Throws<ArgumentException>(() => d.SetTargets([new("unknown", "led1")]));
        d.SetTargets([A]); Assert.Equal(0, d.Capture().Request.ExpectedRevision); d.Name = " "; Assert.NotNull(d.Problem);
        var existing = new DeviceLightingLibraryDraft(Snapshot(), LightingPresetKind.Group, "group"); existing.Name = ""; existing.SetTargets([]);
        Assert.True(existing.CanDelete); Assert.Equal(LightingLibraryOperation.DeleteGroup, existing.Capture(true).Request.Operation);
    }
    [Fact] public void DeletedDefinitionsHaveAReadableErrorAndNullNamesRemainEditable()
    {
        var s = Snapshot(); Assert.Contains("削除", Assert.Throws<IOException>(() => new DeviceLightingLibraryDraft(s, LightingPresetKind.Scene, "gone")).Message);
        Assert.Contains("削除", Assert.Throws<IOException>(() => new DeviceLightingLibraryDraft(s, LightingPresetKind.Group, "gone")).Message);
        var d = new DeviceLightingLibraryDraft(s, LightingPresetKind.Group, "group") { Name = null! };
        Assert.True(d.Dirty); Assert.NotNull(d.Problem); Assert.True(d.CanDelete);
    }
    [Fact] public void MultipartSceneEditingKeepsUneditedNativePaletteDirectionAndLayout()
    {
        var native = new LightingProgram("vendor:wave", [new(1, 2, 3), new(4, 5, 6)], 213, true, 53, [new(7, 8, 9)]);
        var s = Snapshot(new("scene", "元のシーン", [new(A, native)], [new([B, C], new("breathing", [new(40, 50, 60)], 4, Brightness: 70))], 7));
        var d = new DeviceLightingLibraryDraft(s, LightingPresetKind.Scene, "scene"); Assert.False(d.Dirty); d.Name = "名前だけ変更";
        var renamed = d.Capture().Request.Scene!; Assert.True(DeviceLighting.SameProgram(native, renamed.Individual[0].Program)); Assert.Equal(7, renamed.Revision);
        var p = d.Parts[1]; d.ChangePart(p.Id, [B], p.Program with { Brightness = 25 });
        var changed = d.Capture().Request.Scene!; Assert.Equal(B, Assert.Single(Assert.Single(changed.Synchronized).Targets)); Assert.Equal(25, changed.Synchronized[0].Program.Brightness);
        Assert.True(DeviceLighting.SameProgram(native, changed.Individual[0].Program));
        Assert.Equal(70, s.Configuration.Configuration.Lighting.Scenes[0].Synchronized[0].Program.Brightness);
    }
    [Fact] public void SinglePartReadsAreIndependentAndRevertingEditsRestoresTheCleanState()
    {
        var s = Snapshot(new("scene", "既定", [new(A, Red)], [new([B, C], Red)]));
        var d = new DeviceLightingLibraryDraft(s, LightingPresetKind.Scene, "scene"); var id = d.PartIds[0]; var original = d.ReadPart(id)!;
        var mutated = d.ReadPart(id)!; mutated.Targets[0] = C; mutated.Program.Colors[0] = new(1, 2, 3);
        Assert.False(d.Dirty); Assert.Equal(A, d.ReadPart(id)!.Targets[0]); Assert.Equal(new RgbColor(200, 0, 0), d.ReadPart(id)!.Program.Colors[0]);
        d.ChangePart(id, [A], Red with { Brightness = 50 }); Assert.True(d.Dirty);
        d.ChangePart(id, original.Targets, original.Program); Assert.False(d.Dirty);
        d.Name = "変更"; Assert.True(d.Dirty); d.Name = "既定"; Assert.False(d.Dirty);
        var group = new DeviceLightingLibraryDraft(s, LightingPresetKind.Group, "group"); group.SetTargets([A]); Assert.True(group.Dirty);
        group.SetTargets([A, B]); Assert.False(group.Dirty);
    }
    [Fact] public void ScenePartsCannotShareATargetAndRemovingOneMakesItsTargetsAvailable()
    {
        var d = new DeviceLightingLibraryDraft(Snapshot(), LightingPresetKind.Scene) { Name = "二色" };
        var first = d.AddPart(); var second = d.AddPart(); Assert.False(d.Available(second, A));
        Assert.Throws<ArgumentException>(() => d.ChangePart(second, [A], Red));
        d.ChangePart(first, [], Red); Assert.NotNull(d.Problem); d.RemovePart(first);
        d.ChangePart(second, [A, B], Red); var scene = d.Capture().Request.Scene!; Assert.Empty(scene.Individual); Assert.Equal(2, Assert.Single(scene.Synchronized).Targets.Length);
        d.RemovePart(second); Assert.NotNull(d.Problem); Assert.Throws<InvalidOperationException>(() => d.Capture());
    }
    [Fact] public void CaptureCurrentUsesSavedProgramNotInstantaneousFramesAndKeepsSynchronization()
    {
        var s = Snapshot(); var program = new LightingProgram("breathing", [new(3, 50, 200)], 4, Brightness: 77);
        s = s with { Configuration = s.Configuration with { Configuration = s.Configuration.Configuration with { Lighting =
            s.Configuration.Configuration.Lighting with { Preferences = [new(A, program), new(B, program)], SavedSynchronization = [new([A, B], program)] } } } };
        var d = new DeviceLightingLibraryDraft(s, LightingPresetKind.Scene) { Name = "現在の色" }; d.CaptureCurrent([A, B]);
        var scene = d.Capture().Request.Scene!; Assert.Empty(scene.Individual); Assert.True(DeviceLighting.SameProgram(program, Assert.Single(scene.Synchronized).Program));
        Assert.Throws<InvalidOperationException>(() => d.CaptureCurrent([C])); Assert.Single(d.Parts); // Failed capture must preserve the edit.
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void LibraryLimitsBlockNewEntriesButAllowEditingAndDeletion(bool scene)
    {
        var s = Snapshot(); var lighting = s.Configuration.Configuration.Lighting;
        lighting = scene ? lighting with { Scenes = Enumerable.Range(0, 64).Select(i => new DeviceLightingScene("s" + i, "シーン", [new(A, Red)], [], ShowOnM5: i < 32)).ToArray() } :
            lighting with { Groups = Enumerable.Range(0, 32).Select(i => new EndpointGroup("g" + i, "グループ", EndpointKind.Lighting, [A])).ToArray() };
        s = s with { Configuration = s.Configuration with { Configuration = s.Configuration.Configuration with { Lighting = lighting } } };
        var kind = scene ? LightingPresetKind.Scene : LightingPresetKind.Group; var fresh = new DeviceLightingLibraryDraft(s, kind) { Name = "超過" };
        if (scene) fresh.AddPart(); else fresh.SetTargets([A]); Assert.NotNull(fresh.Problem);
        var old = new DeviceLightingLibraryDraft(s, kind, scene ? "s0" : "g0") { Name = "更新" }; Assert.Null(old.Problem); Assert.True(old.CanDelete);
    }
    [Theory][InlineData("missing-smoke")][InlineData("other-smoke")][InlineData("external")]
    public void LibraryGuiSmokeCannotAccidentallyConnectToTheInstalledService(string mode)
    {
        string[] args = ["--devices-preview", "--demo", "--data-dir", Path.GetTempPath(), "--lighting-library"];
        if (mode != "missing-smoke") args = [.. args, "--smoke"];
        if (mode == "other-smoke") args = [.. args, "--m5-connection"];
        if (mode == "external") args = [.. args, "--devices-pipe", "FlowDeck.Devices.Demo." + Epoch, "--server-pid", "1"];
        Assert.Throws<ArgumentException>(() => DeviceWorkspaceOptions.Parse(args));
    }
    [Fact] public async Task AuthenticatedSceneAndGroupEditsAreNonActuatingUntilExplicitRecallAndRejectStaleEdits()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var group = new DeviceLightingLibraryDraft(client.Snapshot!, LightingPresetKind.Group) { Name = "両方" };
        group.SetTargets(group.Candidates.Select(c => c.Target)); await client.SaveLightingEditAsync(group.Capture());
        var stale = new DeviceLightingLibraryDraft(client.Snapshot!, LightingPresetKind.Group, group.Id) { Name = "旧編集" };
        var edited = new DeviceLightingLibraryDraft(client.Snapshot!, LightingPresetKind.Group, group.Id) { Name = "二台目" };
        edited.SetTargets([edited.Candidates[1].Target]); await client.SaveLightingEditAsync(edited.Capture());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SaveLightingEditAsync(stale.Capture()));
        var scene = new DeviceLightingLibraryDraft(client.Snapshot!, LightingPresetKind.Scene) { Name = "別々の色" };
        var a = scene.AddPart(); var b = scene.AddPart(); scene.ChangePart(a, [scene.Candidates[0].Target], Red);
        scene.ChangePart(b, [scene.Candidates[1].Target], new("fixed", [new(0, 0, 200)], Brightness: 50));
        var capture = scene.Capture(); await Assert.ThrowsAsync<IOException>(() => client.SaveLightingEditAsync(capture with { Session = Epoch }));
        await client.SaveLightingEditAsync(capture); Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
        var result = await client.ApplyLightingAsync(DeviceWorkspaceClient.Preset(client.Snapshot!, LightingPresetKind.Scene, scene.Id));
        Assert.Equal(Outcome.WrittenUnverified, result.Outcome); Assert.Equal(new RgbColor(200, 0, 0), Assert.Single(demo.Hubs[0].LightEffects["led1"].Colors));
        Assert.Equal(new RgbColor(0, 0, 100), Assert.Single(demo.Hubs[1].LightEffects["led1"].Colors));
        var writes = demo.Hubs.Select(h => h.LightingWrites).ToArray();
        var renamed = new DeviceLightingLibraryDraft(client.Snapshot!, LightingPresetKind.Scene, scene.Id) { Name = "改名" }; await client.SaveLightingEditAsync(renamed.Capture());
        var delete = new DeviceLightingLibraryDraft(client.Snapshot!, LightingPresetKind.Scene, scene.Id); await client.SaveLightingEditAsync(delete.Capture(true));
        var deleteGroup = new DeviceLightingLibraryDraft(client.Snapshot!, LightingPresetKind.Group, group.Id); await client.SaveLightingEditAsync(deleteGroup.Capture(true));
        Assert.Empty(client.Snapshot!.Configuration.Configuration.Lighting.Scenes); Assert.Empty(client.Snapshot.Configuration.Configuration.Lighting.Groups);
        Assert.Equal(writes, demo.Hubs.Select(h => h.LightingWrites)); Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
    }
}
