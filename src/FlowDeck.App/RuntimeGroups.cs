using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class RuntimeHost
{
    public LightingGroupCollection GroupLibrary { get; private set; } = new(Groups: []);
    public string? GroupLibraryError { get; private set; }
    private LightingGroupStore GroupStore => new(Store.DirectoryPath);
    private async Task LoadLightingGroupsCoreAsync()
    {
        try { GroupLibrary = await GroupStore.LoadAsync(); GroupLibraryError = null; }
        catch (Exception e) { GroupLibrary = new(Groups: []); GroupLibraryError = "グループを読み込めません: " + e.Message; }
        groupAdjustments.Clear(); groupAdjustmentErrors.Clear();
        foreach (var group in GroupLibrary.Groups ?? []) await LoadGroupAdjustmentAsync(group.Id);
    }
    public async Task LoadLightingGroupsAsync()
    {
        await lightingSceneGate.WaitAsync();
        try { await LoadLightingGroupsCoreAsync(); }
        finally { lightingSceneGate.Release(); }
    }
    public async Task SaveLightingGroupAsync(LightingGroup value)
    {
        value = Contract.Copy(value); await lightingSceneGate.WaitAsync();
        try
        {
            if (GroupLibraryError is not null) throw new IOException(GroupLibraryError);
            var groups = (GroupLibrary.Groups ?? []).ToList(); var index = groups.FindIndex(g => g.Id == value.Id);
            if (index < 0) groups.Add(value); else groups[index] = value;
            var next = GroupLibrary with { Groups = groups.ToArray() };
            await GroupStore.SaveAsync(next); GroupLibrary = next;
            if (!groupAdjustments.ContainsKey(value.Id)) await LoadGroupAdjustmentAsync(value.Id);
        }
        finally { lightingSceneGate.Release(); }
    }
    private async Task SaveSyncTargetsCoreAsync(BoardLightAddress[] targets)
    {
        if (GroupLibraryError is not null) throw new IOException(GroupLibraryError);
        var next = GroupLibrary with { SyncTargets = targets.ToArray() };
        await GroupStore.SaveAsync(next); GroupLibrary = next;
    }
    public async Task DeleteLightingGroupAsync(string id)
    {
        await lightingSceneGate.WaitAsync();
        try
        {
            if (GroupLibraryError is not null) throw new IOException(GroupLibraryError);
            var next = GroupLibrary with { Groups = (GroupLibrary.Groups ?? []).Where(g => g.Id != id).ToArray() };
            await GroupStore.SaveAsync(next); GroupLibrary = next;
        }
        finally { lightingSceneGate.Release(); }
    }
    private LightingGroup? ResolveLightingGroup(LightingGroup? group)
    {
        if (GroupLibraryError is not null) throw new IOException(GroupLibraryError);
        if (group is null) return null;
        if (LightingGroups.Validate(group) is { } error) throw new ArgumentException(error);
        var saved = GroupLibrary.Groups?.SingleOrDefault(g => g.Id == group.Id);
        if (saved is null || !saved.Hub.ToHashSet().SetEquals(group.Hub) || !saved.Board.ToHashSet().SetEquals(group.Board))
            throw new IOException("グループが変更されています。選び直してください");
        return Contract.Copy(saved);
    }
    private void CheckGroupTargets(LightingGroup group) => CheckSceneTargets(new(group.Id, group.Name,
        group.Hub.Select(h => new SceneHubLight(h.HubId, h.Channel, new("fixed", [new(0, 0, 0)]))).ToArray(),
        group.Board.Select(b => new SceneBoardLight(b.Identity, b.Zone, new(1, 0, 0, 0))).ToArray()));
    public void CheckGroupSyncScope(LightingGroup? group)
    {
        group = ResolveLightingGroup(group);
        if (group is not null)
        {
            CheckGroupTargets(group);
            if (LightingSynchronized && ActiveLightingSync is { } active && !LightingGroups.Contains(group, active))
                throw new InvalidOperationException("グループ外の照明が同期中です。「同期停止」後に操作してください");
        }
    }
    private void CheckSceneSyncScope(LightingScene scene)
    {
        if (!LightingSynchronized || ActiveLightingSync is not { } active) return;
        var targets = new LightingGroup(scene.Id, scene.Name, LightingGroups.HubTargets(scene), LightingGroups.BoardTargets(scene));
        if (!LightingGroups.Contains(targets, active)) throw new InvalidOperationException("対象外の照明が同期中です。「同期停止」後に操作してください");
    }
}
