using System.Collections.ObjectModel;
using System.Windows.Input;
using FlowDeck.Core;

namespace FlowDeck.App;

public record LightingGroupChoice(string Name, LightingGroup? Group)
{
    public string Label => Group is null ? Name : $"{Name} · NZXT {Group.Hub.Length} / マザー {Group.Board.Length}";
}
public sealed class LightingTargetChoice(string key, string name, HubLightAddress? hub, BoardLightAddress? board, bool available, bool selected, Action? changed = null) : Observable
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public HubLightAddress? Hub { get; } = hub;
    public BoardLightAddress? Board { get; } = board;
    public LightAddress Address => new(Hub, Board);
    public bool Available { get; } = available;
    public string DisplayName => Available || Name.StartsWith("未検出") ? Name : "未検出 · " + Name;
    private bool selected = selected;
    public bool Selected { get => selected; set { if (Set(ref selected, value)) changed?.Invoke(); } }
}

public sealed partial class FlowDeckViewModel
{
    public ObservableCollection<LightingGroupChoice> LightingGroupChoices { get; } = [];
    public ObservableCollection<LightingTargetChoice> GroupTargetChoices { get; } = [];
    public ObservableCollection<LightingTargetChoice> SyncBoardChoices { get; } = [];
    private LightingGroupChoice? selectedLightingGroup;
    private LightingGroup? deletedLightingGroup;
    private bool groupBusy, rebuildingGroups, groupManagementOpen;
    private string lightingGroupName = "", lightingGroupResult = "", groupInventorySignature = "", syncInventorySignature = "";
    public LightingGroup? CurrentLightingGroup => selectedLightingGroup?.Group;
    public LightingGroupChoice? SelectedLightingGroup
    {
        get => selectedLightingGroup;
        set
        {
            if (rebuildingGroups || value is null || !Set(ref selectedLightingGroup, value)) return;
            LoadLightingGroupSelection(); Refresh();
        }
    }
    public string LightingGroupName { get => lightingGroupName; set => Set(ref lightingGroupName, value); }
    public string LightingGroupResult => host.GroupLibraryError ?? lightingGroupResult;
    public bool CanUseLightingScope => host.GroupLibraryError is null;
    public bool GroupManagementOpen { get => groupManagementOpen; set => Set(ref groupManagementOpen, value); }
    public string? LightingGroupError => host.GroupLibraryError;
    public bool HasLightingGroupError => host.GroupLibraryError is not null;
    public bool HasMissingGroupTargets => LightingGroupSummary.Contains("未検出");
    public string SyncTargetsSummary => "同期対象 · " + SyncBoardChoices.Count(t => t.Selected);
    public string LightingScopeName => CurrentLightingGroup?.Name ?? "すべての照明";
    public string LightsOffLabel => CurrentLightingGroup is null ? "一括消灯" : "グループ消灯";
    public string LightingGroupSummary => CurrentLightingGroup is { } g ? $"NZXT {g.Hub.Length} · マザー {g.Board.Length}" +
        (GroupTargetChoices.Count(t => !t.Available && (t.Hub is not null && g.Hub.Contains(t.Hub) || t.Board is not null && g.Board.Contains(t.Board))) is > 0 and var missing ? $" · 未検出 {missing}" : "") : "";
    public ICommand NewLightingGroup { get; private set; } = null!;
    public ICommand SaveLightingGroup { get; private set; } = null!;
    public ICommand DeleteLightingGroup { get; private set; } = null!;
    public ICommand UndoDeleteLightingGroup { get; private set; } = null!;
    public ICommand ReloadLightingGroups { get; private set; } = null!;
    public ICommand ToggleGroupManagement { get; private set; } = null!;
    private void InitializeLightingGroups()
    {
        ToggleGroupManagement = Command(_ => { GroupManagementOpen = !GroupManagementOpen; return Task.CompletedTask; }, _ => LightingScenesIdle);
        async Task Edit(Func<Task> action)
        {
            groupBusy = true; Refresh();
            try { await action(); }
            catch (Exception e) { lightingGroupResult = e.Message; throw; }
            finally { groupBusy = false; Refresh(); }
        }
        NewLightingGroup = Command(_ =>
        {
            SelectedLightingGroup = LightingGroupChoices[0]; LightingGroupName = "";
            foreach (var row in GroupTargetChoices) row.Selected = false;
            lightingGroupResult = "新規グループ"; Refresh(); return Task.CompletedTask;
        }, _ => LightingScenesIdle);
        SaveLightingGroup = Command(_ => Edit(async () =>
        {
            var group = new LightingGroup(CurrentLightingGroup?.Id ?? Guid.NewGuid().ToString("N"), LightingGroupName.Trim(),
                GroupTargetChoices.Where(t => t.Selected && t.Hub is not null).Select(t => t.Hub!).ToArray(),
                GroupTargetChoices.Where(t => t.Selected && t.Board is not null).Select(t => t.Board!).ToArray());
            await host.SaveLightingGroupAsync(group); RefreshLightingGroups(group.Id); lightingGroupResult = "保存済み · " + group.Name;
        }), _ => LightingScenesIdle && !string.IsNullOrWhiteSpace(LightingGroupName) && GroupTargetChoices.Any(t => t.Selected));
        DeleteLightingGroup = Command(_ => Edit(async () =>
        {
            var group = CurrentLightingGroup!; await host.DeleteLightingGroupAsync(group.Id); deletedLightingGroup = group;
            RefreshLightingGroups(); lightingGroupResult = "削除済み";
        }), _ => LightingScenesIdle && CurrentLightingGroup is not null);
        UndoDeleteLightingGroup = Command(_ => Edit(async () =>
        {
            var group = deletedLightingGroup!; await host.SaveLightingGroupAsync(group); deletedLightingGroup = null;
            RefreshLightingGroups(group.Id); lightingGroupResult = "削除取消済み";
        }), _ => LightingScenesIdle && deletedLightingGroup is not null);
        ReloadLightingGroups = Command(_ => Edit(async () => { var id = CurrentLightingGroup?.Id; await host.LoadLightingGroupsAsync(); RefreshLightingGroups(id); lightingGroupResult = "再読込済み"; }), _ => LightingScenesIdle);
    }
    private void RefreshLightingGroups(string? select = null)
    {
        rebuildingGroups = true;
        try
        {
            LightingGroupChoices.Clear(); LightingGroupChoices.Add(new("すべての照明", null));
            foreach (var group in host.GroupLibrary.Groups ?? []) LightingGroupChoices.Add(new(group.Name, group));
            selectedLightingGroup = LightingGroupChoices.FirstOrDefault(g => g.Group?.Id == select) ?? LightingGroupChoices[0];
        }
        finally { rebuildingGroups = false; }
        LoadLightingGroupSelection(); Notify(nameof(SelectedLightingGroup)); Refresh();
    }
    private void LoadLightingGroupSelection()
    {
        LightingGroupName = CurrentLightingGroup?.Name ?? ""; groupInventorySignature = ""; syncInventorySignature = "";
        GroupTargetChoices.Clear(); SyncBoardChoices.Clear(); RefreshLightingGroupTargets();
        hubLightTarget = "all"; hubLightSignature = ""; boardLightTarget = null; lastLightInventory = null;
        RefreshHubLighting(); RefreshBoardLighting(); RefreshMemoryLighting(); RefreshIdentificationTargets(); MarkHubLightDraft();
        MasterBrightness = host.AdjustmentFor(CurrentLightingGroup).Brightness; brightnessResult = "";
        LightingSceneResults.Clear(); lightingSceneResult = "";
    }
    private List<LightingTargetChoice> AvailableLightingTargets()
    {
        var targets = new List<LightingTargetChoice>(); var settings = core.Settings;
        if (settings.Hardware.Hub is { } identity)
            foreach (var channel in settings.Hardware.Leds ?? [])
                targets.Add(new("hub:" + identity.Id + ":" + channel, "NZXT · " + host.LightName(new(Hub: new(identity.Id, channel)), channel.ToUpperInvariant()), new(identity.Id, channel), null,
                    state.HubReady && core.LastStatus.Lighting.Any(l => l.Id == channel), false));
        foreach (var device in host.Rgb.Devices.Where(d => d.ControlIdentity is not null))
        foreach (var zone in device.Zones.Where(z => z.HardwareZone is not null && z.LedCount > 0))
        {
            var registration = device.ZoneRegistrationId(zone.Name, Array.IndexOf(device.Zones, zone));
            var label = settings.Motherboard.RgbZones?.FirstOrDefault(z => z.Id == registration)?.Accessory;
            targets.Add(new("board:" + device.ControlIdentity + ":" + zone.HardwareZone, (device.IsMemory ? "メモリ · " : "マザー · ") + host.LightName(new(Board: new(device.ControlIdentity!, zone.HardwareZone!.Value)), string.IsNullOrWhiteSpace(label) ? zone.Name : label + " · " + zone.Name),
                null, new(device.ControlIdentity!, zone.HardwareZone!.Value), host.Rgb.Error is null && zone.Configuration is not null, false));
        }
        return targets;
    }
    private void RefreshLightingGroupTargets()
    {
        var available = AvailableLightingTargets(); var group = CurrentLightingGroup;
        foreach (var h in group?.Hub ?? []) if (available.All(t => t.Hub != h)) available.Add(new("hub:" + h.HubId + ":" + h.Channel, "未検出 · NZXT " + h.Channel.ToUpperInvariant(), h, null, false, true));
        foreach (var b in group?.Board ?? []) if (available.All(t => t.Board != b)) available.Add(new("board:" + b.Identity + ":" + b.Zone, "未検出 · マザー ゾーン " + b.Zone, null, b, false, true));
        // Keep unavailable draft choices visible when a device disappears during editing.
        foreach (var row in GroupTargetChoices.Where(t => t.Selected && available.All(a => a.Key != t.Key)).ToArray())
            available.Add(new(row.Key, "未検出 · " + row.Name.Replace("未検出 · ", ""), row.Hub, row.Board, false, true));
        var signature = string.Join('|', available.Select(t => t.Key + t.Name + t.Available));
        if (signature != groupInventorySignature)
        {
            var old = GroupTargetChoices.ToDictionary(t => t.Key, t => t.Selected); groupInventorySignature = signature;
            GroupTargetChoices.Clear();
            foreach (var t in available)
            {
                var selected = old.GetValueOrDefault(t.Key, group is not null && (t.Hub is not null && group.Hub.Contains(t.Hub) || t.Board is not null && group.Board.Contains(t.Board)));
                GroupTargetChoices.Add(new(t.Key, t.Name, t.Hub, t.Board, t.Available, selected, () => { lightingGroupResult = "未保存"; Notify(nameof(LightingGroupResult)); CommandManager.InvalidateRequerySuggested(); }));
            }
            Notify(nameof(LightingGroupSummary));
        }
        var syncTargets = available.Where(t => t.Board is not null && (group is null || group.Board.Contains(t.Board))).ToList();
        if (group is null)
            foreach (var b in host.GroupLibrary.SyncTargets ?? []) if (syncTargets.All(t => t.Board != b))
                syncTargets.Add(new("board:" + b.Identity + ":" + b.Zone, "未検出 · マザー ゾーン " + b.Zone, null, b, false, true));
        var syncSignature = string.Join('|', syncTargets.Select(t => t.Key + t.Name + t.Available));
        if (syncSignature == syncInventorySignature) return;
        var oldSync = SyncBoardChoices.ToDictionary(t => t.Key, t => t.Selected); syncInventorySignature = syncSignature; SyncBoardChoices.Clear();
        foreach (var t in syncTargets) SyncBoardChoices.Add(new(t.Key, t.Name, null, t.Board, t.Available,
            oldSync.GetValueOrDefault(t.Key, group is not null || host.GroupLibrary.SyncTargets is null || host.GroupLibrary.SyncTargets.Contains(t.Board!)), MarkHubLightDraft));
    }
    private BoardLightAddress[] SelectedSyncBoardTargets => SyncBoardChoices.Where(t => t.Selected).Select(t => t.Board!).ToArray();
}

