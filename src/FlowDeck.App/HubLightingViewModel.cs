using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using FlowDeck.Core;

namespace FlowDeck.App;

public sealed class HubLightTarget(string id, string name)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
}
public sealed partial class FlowDeckViewModel
{
    public ObservableCollection<HubLightTarget> HubLightTargets { get; } = [];
    private static readonly HubLightEffect[] SyncEffects = [new("breathing", "呼吸", true, false, true, false),
        new("color-cycle", "カラーサイクル", false, false, true, false), new("fixed", "固定", true, false, false, false), new("off", "消灯", false, false, false, false)];
    public IEnumerable<HubLightEffect> HubLightEffects => HubLightSync ? SyncEffects : HubLighting.Effects;
    public IEnumerable<HubLightEffect> HubNativeEffects => HubLighting.Effects;
    public IEnumerable<HubLightEffect> HubSyncEffects => SyncEffects;
    public bool HubLightNative => !HubLightSync;
    public string[] HubColorSlots => ["カラー1", "カラー2"];
    public ColorPalette LightPalette { get; private set; } = null!;
    public bool HubColorFirst { get => HubColorSlot == 0; set { if (value) HubColorSlot = 0; } }
    public bool HubColorSecond { get => HubColorSlot == 1; set { if (value) HubColorSlot = 1; } }
    public Brush HubFirstBrush => new SolidColorBrush(ToMediaColor(hubColor1));
    public Brush HubSecondBrush => new SolidColorBrush(ToMediaColor(hubColor2));
    private static Color ToMediaColor(RgbColor color) => Color.FromRgb((byte)color.R, (byte)color.G, (byte)color.B);
    public Color HubSelectedColor { get => ToMediaColor(HubEditedColor); set => SetHubColor(new(value.R, value.G, value.B)); }
    private string hubLightTarget = "all", hubLightMode = "fixed", hubLightResult = "", hubLightSignature = "";
    private bool hubLightSync, hubLightReverse, hubLightBusy, hubLightDirty;
    private bool rebuildingHubTargets;
    private int hubLightSpeed = 2, hubColorSlot;
    private long hubLightDraftRevision;
    private RgbColor hubColor1 = new(52, 137, 255), hubColor2 = new(160, 121, 236);
    public ICommand ApplyHubLight { get; private set; } = null!;
    public ICommand RevertHubLight { get; private set; } = null!;
    public ICommand SelectHubLightColor { get; private set; } = null!;
    public ICommand StopLightingSync { get; private set; } = null!;
    public bool HubLightIdle => !hubLightBusy && !host.SceneApplying && !sceneBusy && !brightnessBusy && !groupBusy && !host.LightingAdjustmentBusy && !IdentificationBusy;
    public bool LightingSyncRunning => host.LightingSynchronized;
    public string LightingEditorTitle => HubLightSync ? "照明の同期" : "NZXT Control Hub";
    public string HubLightTarget { get => hubLightTarget; set { if (value is not null && Set(ref hubLightTarget, value)) { if (value == "none") HubLightSync = true; Notify(nameof(SelectedHubLightTarget)); MarkHubLightDraft(); } } }
    public HubLightTarget? SelectedHubLightTarget
    {
        get => HubLightTargets.FirstOrDefault(t => t.Id == hubLightTarget);
        set { if (!rebuildingHubTargets && value is not null) HubLightTarget = value.Id; }
    }
    public string HubLightMode { get => hubLightMode; set { if (value is not null && Set(ref hubLightMode, value)) { hubColorSlot = 0; MarkHubLightDraft(); Refresh(); } } }
    public bool HubLightSync
    {
        get => hubLightSync;
        set
        {
            if (!Set(ref hubLightSync, value)) return;
            if (!HubLightEffects.Any(e => e.Id == hubLightMode)) hubLightMode = "breathing";
            hubColorSlot = 0; MarkHubLightDraft(); Refresh();
        }
    }
    public bool HubLightReverse { get => hubLightReverse; set { if (Set(ref hubLightReverse, value)) MarkHubLightDraft(); } }
    public int HubLightSpeed { get => hubLightSpeed; set { if (Set(ref hubLightSpeed, Math.Clamp(value, 0, 4))) { MarkHubLightDraft(); Notify(nameof(HubLightSpeedLabel)); } } }
    public string HubLightSpeedLabel => HubLightSync ? $"{HubLighting.Period(HubLightSpeed):0} 秒 / 周期" : new[] { "最も遅い", "遅い", "標準", "速い", "最も速い" }[HubLightSpeed];
    public int HubColorSlot { get => hubColorSlot; set { if (Set(ref hubColorSlot, Math.Clamp(value, 0, 1))) Refresh(); } }
    private RgbColor HubEditedColor => HubColorSlot == 0 ? hubColor1 : hubColor2;
    private void SetHubColor(RgbColor value)
    {
        if (HubEditedColor == value) return;
        if (HubColorSlot == 0) hubColor1 = value; else hubColor2 = value;
        MarkHubLightDraft(); Refresh();
    }
    public int HubRed { get => HubEditedColor.R; set => SetHubColor(HubEditedColor with { R = Math.Clamp(value, 0, 255) }); }
    public int HubGreen { get => HubEditedColor.G; set => SetHubColor(HubEditedColor with { G = Math.Clamp(value, 0, 255) }); }
    public int HubBlue { get => HubEditedColor.B; set => SetHubColor(HubEditedColor with { B = Math.Clamp(value, 0, 255) }); }
    public string HubLightHex => $"#{HubRed:X2}{HubGreen:X2}{HubBlue:X2}";
    public Brush HubLightBrush => new SolidColorBrush(Color.FromRgb((byte)HubRed, (byte)HubGreen, (byte)HubBlue));
    public bool HubLightUsesColor => HubLightEffects.FirstOrDefault(e => e.Id == HubLightMode)?.Color == true;
    public bool HubLightUsesPalette => HubLightEffects.FirstOrDefault(e => e.Id == HubLightMode)?.Palette == true;
    public bool HubLightUsesSpeed => HubLightEffects.FirstOrDefault(e => e.Id == HubLightMode)?.Speed == true;
    public bool HubLightUsesDirection => HubLightEffects.FirstOrDefault(e => e.Id == HubLightMode)?.Direction == true;
    public string HubLightResult => host.LightingSynchronized ? "同期中 · " + HubLighting.Name(host.LightingSyncMode) :
        host.LightingSyncError ?? (hubLightDirty && hubLightDraftRevision != core.Snapshot.LightingRevision ? "別の照明操作で変更されています" : hubLightResult);
    public bool CanApplyHubLight => HubLightIdle && CanUseLightingScope && !state.LightingBusy &&
        (HubLightSync ? SelectedSyncBoardTargets.Length > 0 && (SelectedHubLightChannels.Length > 0 ? state.HubReady :
            HubLightTarget == "none" || CurrentLightingGroup is { Hub.Length: 0 } || core.Settings.Hardware.Hub is null) : state.HubReady && SelectedHubLightChannels.Length > 0);
    private string[] ScopedHubChannels => CurrentLightingGroup is { } group ? group.Hub.Where(h => h.HubId == core.Settings.Hardware.Hub?.Id).Select(h => h.Channel).ToArray() : core.Settings.Hardware.Leds ?? [];
    private string[] SelectedHubLightChannels => HubLightTarget == "all" ? ScopedHubChannels :
        HubLightTargets.Any(t => t.Id == HubLightTarget) && ScopedHubChannels.Contains(HubLightTarget) ? [HubLightTarget] : [];
    private void MarkHubLightDraft()
    {
        if (!hubLightDirty) hubLightDraftRevision = core.Snapshot.LightingRevision;
        hubLightDirty = true; hubLightResult = "未適用"; Notify(nameof(HubLightResult)); Notify(nameof(SyncTargetsSummary));
    }
    private void InitializeHubLighting()
    {
        ApplyHubLight = Command(async _ =>
        {
            var channels = SelectedHubLightChannels; var mode = HubLightMode; var syncLights = HubLightSync;
            var color = hubColor1; var second = hubColor2; var speed = HubLightSpeed; var reverse = HubLightReverse;
            var expected = hubLightDirty ? hubLightDraftRevision : core.Snapshot.LightingRevision;
            var owned = host.LightingSynchronized;
            if (!owned && expected != core.Snapshot.LightingRevision) throw new InvalidOperationException("別の照明操作で変更されました。「変更を戻す」で最新設定を読み込んでください");
            hubLightBusy = true; Refresh();
            try
            {
                if (syncLights)
                    await host.StartLightingSyncAsync(channels, mode, color, speed, SelectedSyncBoardTargets, CurrentLightingGroup);
                else
                {
                    host.CheckGroupSyncScope(CurrentLightingGroup);
                    await host.StopLightingSyncAsync();
                    if (owned) expected = core.Snapshot.LightingRevision;
                    var colors = mode switch { "fixed" or "breathing" => new[] { color }, "fading" or "covering-marquee" => new[] { color, second }, _ => [] };
                    var result = await host.ApplyHubLightingAsync(Request("SetLighting", expected) with { LightingEffect = new(mode, colors, speed, reverse), LightingChannels = channels }, CurrentLightingGroup);
                    Show(result);
                    if (result.Outcome != Outcome.WrittenUnverified) throw new IOException(result.Message);
                }
                if (!syncLights) host.DismissLightingSyncError();
                ResetBrightnessDisplay();
                if (mode is "fixed" or "breathing" or "fading" or "covering-marquee")
                    LightPalette.Remember(mode is "fading" or "covering-marquee" ? [ToMediaColor(color), ToMediaColor(second)] : [ToMediaColor(color)]);
                hubLightDirty = false; hubLightDraftRevision = core.Snapshot.LightingRevision;
                hubLightResult = syncLights ? "同期設定を送信済み" : "送信済み";
            }
            catch (Exception e) { hubLightResult = e.Message; throw; }
            finally { hubLightBusy = false; RefreshMotherboard(); Refresh(); }
        }, _ => CanApplyHubLight);
        RevertHubLight = Command(_ => { LoadHubLighting(); return Task.CompletedTask; }, _ => !hubLightBusy);
        SelectHubLightColor = Command(p =>
        {
            SetHubColor(p?.ToString() switch { "white" => new(233, 242, 255), "purple" => new(160, 121, 236),
                "red" => new(255, 48, 64), "green" => new(32, 220, 120), "warm" => new(255, 184, 100), _ => new(52, 137, 255) });
            return Task.CompletedTask;
        });
        StopLightingSync = Command(async _ =>
        {
            hubLightBusy = true; Refresh();
            try { await host.StopLightingSyncAsync(); ResetBrightnessDisplay(); hubLightDirty = false; hubLightDraftRevision = core.Snapshot.LightingRevision; hubLightResult = "同期停止 · 固定色"; }
            finally { hubLightBusy = false; RefreshMotherboard(); Refresh(); }
        }, _ => !hubLightBusy && host.LightingSynchronized);
    }
    private void LoadHubLighting()
    {
        var perPort = core.Settings.LightingPorts.GetValueOrDefault(HubLightTarget);
        var value = perPort is { Outcome: Outcome.WrittenUnverified, Effect: { } portValue } ? portValue : core.Snapshot.Lighting.Effect ?? HubLighting.Preset(core.Snapshot.Lighting.LastSent ?? "blue");
        hubLightMode = HubLighting.Valid(value) ? value.Mode : "fixed"; hubLightSync = false;
        if (core.Settings.Hardware.Hub is null) { hubLightSync = true; hubLightTarget = "none"; if (!SyncEffects.Any(e => e.Id == hubLightMode)) hubLightMode = "fixed"; }
        hubColor1 = value.Colors.FirstOrDefault() ?? new(52, 137, 255); hubColor2 = value.Colors.Skip(1).FirstOrDefault() ?? new(160, 121, 236);
        hubLightSpeed = value.Speed; hubLightReverse = value.Reverse; hubColorSlot = 0;
        if (host.LightingSynchronized && host.ActiveLightingSync is { } active)
        {
            hubLightSync = true; hubLightMode = active.Mode; hubColor1 = active.Color; hubLightSpeed = active.Speed;
            hubLightTarget = active.Channels.Length == 0 ? "none" : active.Channels.Length == 1 ? active.Channels[0] : "all";
            foreach (var choice in SyncBoardChoices) choice.Selected = choice.Board is { } b && LightingGroups.SyncTargets(active).Contains(b);
        }
        hubLightDirty = false; hubLightDraftRevision = core.Snapshot.LightingRevision; hubLightResult = "";
        RefreshHubLighting(); Refresh();
    }
    private void RefreshHubLighting()
    {
        var leds = ScopedHubChannels; var hubId = core.Settings.Hardware.Hub?.Id ?? "";
        string Name(string led) => host.LightName(new(Hub: new(hubId, led)), led.ToUpperInvariant());
        var signature = hubId + string.Join('|', leds.Select(led => led + Name(led))) + CurrentLightingGroup?.Id;
        if (signature == hubLightSignature && HubLightTargets.Count > 0) return;
        hubLightSignature = signature;
        rebuildingHubTargets = true;
        try
        {
            HubLightTargets.Clear(); HubLightTargets.Add(new("all", CurrentLightingGroup is null ? "NZXT 全体" : "グループ内のNZXT"));
            foreach (var led in leds) HubLightTargets.Add(new(led, Name(led)));
            HubLightTargets.Add(new("none", "NZXTを含めない"));
        }
        finally { rebuildingHubTargets = false; }
        if (!HubLightTargets.Any(t => t.Id == hubLightTarget)) hubLightTarget = "all";
        Notify(nameof(HubLightTarget)); Notify(nameof(SelectedHubLightTarget)); Notify(nameof(CanApplyHubLight));
    }
}

