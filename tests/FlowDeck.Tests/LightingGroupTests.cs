using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class LightingGroupTests
{
    private static LightingGroup Mixed() => new(Guid.NewGuid().ToString("N"), "ケース", [new("hub", "led2")], [new("board", 2)]);
    [Fact] public void MixedGroupsRejectDuplicatesMissingIdentityInvalidChannelsAndEmptySelection()
    {
        var g = Mixed(); Assert.Null(LightingGroups.Validate(g));
        Assert.NotNull(LightingGroups.Validate(g with { Hub = [g.Hub[0], g.Hub[0]] }));
        Assert.NotNull(LightingGroups.Validate(g with { Hub = [new("", "led2")] }));
        Assert.NotNull(LightingGroups.Validate(g with { Hub = [new("hub", "all")] }));
        Assert.NotNull(LightingGroups.Validate(g with { Board = [new("board", 8)] }));
        Assert.NotNull(LightingGroups.Validate(g with { Board = [], Hub = [] }));
        Assert.NotNull(LightingGroups.Validate(g with { Id = new string('A', 32) }));
        Assert.NotNull(LightingGroups.Validate(new LightingGroupCollection(Groups: [g, g with { Id = Guid.NewGuid().ToString("N"), Name = " ケース " }])));
        Assert.NotNull(LightingGroups.Validate(new LightingGroupCollection(Groups: [g], SyncTargets: [new("board", 2),new("board", 2)])));
    }
    [Fact] public void NativeSceneSelectionUsesExactPhysicalTargetsAndDoesNotMutateTheSavedScene()
    {
        var g = Mixed(); var scene = new LightingScene(Guid.NewGuid().ToString("N"), "全体",
            [new("hub","led1",HubLighting.Preset("white")),new("hub","led2",HubLighting.Preset("blue"))],
            [new("board",2,new(2,1,2,3,123)),new("board",5,new(1,4,5,6))]);
        var selected = LightingGroups.Select(scene, g);
        Assert.Single(selected.Hub); Assert.Equal("led2", selected.Hub[0].Channel); Assert.Single(selected.Board); Assert.Equal(123, selected.Board[0].Effect.Speed);
        Assert.True(LightingGroups.SameTargets(g, selected)); Assert.Equal(2, scene.Hub.Length); Assert.Equal(2, scene.Board.Length);
        Assert.Throws<InvalidOperationException>(() => LightingGroups.Select(scene, g with { Hub = [new("another-hub", "led2")], Board = [] }));
    }
    [Fact] public void SynchronizationSelectionKeepsTheSharedEffectAndRejectsAnUnpairedDevice()
    {
        var g = Mixed(); var scene = new LightingScene(Guid.NewGuid().ToString("N"), "同期", [], [], new("hub",["led1","led2"],"board",[2,5],"breathing",new(12,40,230),4));
        var selected = LightingGroups.Select(scene, g);
        Assert.Equal(["led2"], selected.Sync!.Channels); Assert.Equal(new byte[]{2}, selected.Sync.Zones); Assert.Equal(4, selected.Sync.Speed);
        Assert.True(LightingGroups.Contains(g, selected.Sync)); Assert.False(LightingGroups.Contains(g, scene.Sync!));
        Assert.Throws<InvalidOperationException>(() => LightingGroups.Select(scene, g with { Board = [] }));
    }
    [Fact] public async Task GroupLibraryPreservesMissingIdentitiesAndExplicitSyncSelectionAcrossReload()
    {
        var folder = Path.Combine(Path.GetTempPath(), "FlowDeck-groups-" + Guid.NewGuid().ToString("N"));
        var store = new LightingGroupStore(folder); var g = Mixed(); var original = new LightingGroupCollection(Groups: [g], SyncTargets: [new("unplugged",5)]);
        await store.SaveAsync(original); var loaded = await new LightingGroupStore(folder).LoadAsync();
        Assert.Equal(g.Hub, loaded.Groups![0].Hub); Assert.Equal("unplugged", loaded.SyncTargets![0].Identity);
        var before = await File.ReadAllTextAsync(store.PathName); await store.SaveAsync(loaded with { SyncTargets = [] });
        Assert.Equal(before, await File.ReadAllTextAsync(store.PathName + ".backup")); Assert.Empty((await store.LoadAsync()).SyncTargets!);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(original with { Groups = [g with { Name = "" }] }));
        Assert.Empty((await store.LoadAsync()).SyncTargets!);
        await File.WriteAllTextAsync(store.PathName, "{broken"); await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => store.LoadAsync());
        Assert.Equal("{broken", await File.ReadAllTextAsync(store.PathName));
    }
    [Fact] public async Task BrightnessRestoreFilesAreIsolatedByGroupAndCannotEscapeTheDirectory()
    {
        var folder = Path.Combine(Path.GetTempPath(), "FlowDeck-group-adjustment-" + Guid.NewGuid().ToString("N"));
        var a = new LightingAdjustmentStore(folder, Guid.NewGuid().ToString("N")); var b = new LightingAdjustmentStore(folder, Guid.NewGuid().ToString("N"));
        await a.SaveAsync(new(Brightness: 30)); await b.SaveAsync(new(Brightness: 80));
        Assert.Equal(30, (await a.LoadAsync()).Brightness); Assert.Equal(80, (await b.LoadAsync()).Brightness);
        Assert.Equal(100, (await new LightingAdjustmentStore(folder).LoadAsync()).Brightness);
        await Assert.ThrowsAsync<ArgumentException>(() => new LightingAdjustmentStore(folder, "../escape").LoadAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => new LightingAdjustmentStore(folder, new string('A',32)).LoadAsync());
    }
}
