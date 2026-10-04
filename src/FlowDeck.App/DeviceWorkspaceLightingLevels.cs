using System.Windows.Input;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed record DeviceLightScopeChoice(LightingAdjustmentScope Id, string Name);
public sealed partial class DeviceWorkspaceViewModel
{
    public DeviceLightingLevelDraft LightLevelDraft { get; } = new();
    public DeviceLightScopeChoice[] LightLevelScopes { get; } = [new(LightingAdjustmentScope.All, "すべての照明"), new(LightingAdjustmentScope.Group, "グループ"), new(LightingAdjustmentScope.Targets, "選択した照明")];
    public LightingAdjustmentScope LightLevelScope
    {
        get => LightLevelDraft.Scope;
        set { if (!CanSelectLightLevel || !Enum.IsDefined(value)) return; LightLevelDraft.Select(value, value == LightingAdjustmentScope.Group ? LightLevelGroupId : null); PublishLightLevels(); }
    }
    public string? LightLevelGroupId
    {
        get => LightLevelDraft.GroupId;
        set { if (!CanSelectLightLevel || !LightLevelIsGroup) return; LightLevelDraft.Select(LightingAdjustmentScope.Group, value); PublishLightLevels(); }
    }
    public bool LightLevelIsGroup => LightLevelScope == LightingAdjustmentScope.Group;
    public bool CanSelectLightLevel => LightingIdle && !LightLevelDraft.Dirty;
    public bool CanEditLightLevel => LightingIdle && !LightingDraft.Dirty && LightLevelDraft.Problem is null;
    public bool CanApplyLightLevel => CanEditLightLevel && LightLevelDraft.Dirty;
    public bool CanToggleLightLevel => CanEditLightLevel && !LightLevelDraft.Dirty;
    public int LightLevelBrightness { get => LightLevelDraft.Brightness; set { if (!LightingIdle || value == LightLevelDraft.Brightness) return; LightLevelDraft.SetBrightness(Math.Clamp(value, 0, 100)); PublishLightLevels(); } }
    public string LightLevelCurrent => LightLevelDraft.Current;
    public string LightLevelValue => !LightLevelDraft.Dirty && LightLevelDraft.Mixed ? "混在" : $"{LightLevelBrightness}%";
    public string LightLevelStatus => LightLevelDraft.Problem ?? (LightingDraft.Dirty ? "色・効果の編集を適用または取り消してください" : LightLevelDraft.Dirty ? "未適用" : $"{LightLevelDraft.Targets.Length} 個の照明");
    public ICommand ApplyLightLevel => new AsyncCommand(_ => ApplyLightLevelAsync(LightingAdjustmentKind.Brightness), _ => CanApplyLightLevel, e => Notice = e.Message);
    public ICommand ToggleLightLevel => new AsyncCommand(p => ApplyLightLevelAsync(p?.ToString() == "restore" ? LightingAdjustmentKind.Restore : LightingAdjustmentKind.Off), _ => CanToggleLightLevel, e => Notice = e.Message);
    public ICommand ReloadLightLevel => new AsyncCommand(async _ => { await client.RefreshAsync(true); LightLevelDraft.Reset(); Render(); }, _ => LightingIdle, e => Notice = e.Message);
    public async Task ApplyLightLevelAsync(LightingAdjustmentKind kind)
    {
        Render();
        if (!CanEditLightLevel) throw new InvalidOperationException(LightLevelStatus);
        var edit = LightLevelDraft.Capture(kind); lightingBusy = true; PublishLightingEditor();
        try
        {
            var result = await client.ApplyLightingLevelAsync(edit);
            foreach (var target in result.Targets) Lights.SingleOrDefault(l => l.Target == target.Target)?.Report(target);
            if (result.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified) LightLevelDraft.Reset();
            Notice = result.Message;
        }
        finally { lightingBusy = false; Render(); }
    }
    private void PublishLightLevels()
    {
        foreach (var name in new[] { nameof(LightLevelScope), nameof(LightLevelGroupId), nameof(LightLevelIsGroup), nameof(CanSelectLightLevel), nameof(CanEditLightLevel), nameof(CanApplyLightLevel), nameof(CanToggleLightLevel), nameof(LightLevelBrightness), nameof(LightLevelValue), nameof(LightLevelCurrent), nameof(LightLevelStatus) }) Notify(name);
        CommandManager.InvalidateRequerySuggested();
    }
}
