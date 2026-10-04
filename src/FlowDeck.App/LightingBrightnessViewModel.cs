using System.Windows.Input;

namespace FlowDeck.App;

public sealed partial class FlowDeckViewModel
{
    private bool brightnessBusy;
    private int masterBrightness = 100;
    private string brightnessResult = "";
    private long lastM5LightingChanges;
    private void RefreshRemoteBrightness()
    {
        var changes = host.M5LightingChanges;
        if (changes == lastM5LightingChanges) return;
        lastM5LightingChanges = changes;
        if (brightnessBusy) return;
        if (brightnessResult.EndsWith("未適用", StringComparison.Ordinal)) brightnessResult = "M5GOで変更あり・未適用";
        else
        {
            var saved = host.AdjustmentFor(CurrentLightingGroup);
            Set(ref masterBrightness, saved.Applied ? saved.Brightness : 100, nameof(MasterBrightness));
            brightnessResult = saved.Applied ? saved.IsOff ? "消灯済み" : $"明るさ {saved.Brightness}% · 適用済み" : "M5GOから照明を変更";
        }
        Notify(nameof(BrightnessResult)); Notify(nameof(HasBrightnessResult));
    }
    public int MasterBrightness
    {
        get => masterBrightness;
        set { if (Set(ref masterBrightness, Math.Clamp(value, 1, 100))) { brightnessResult = "未適用"; Notify(nameof(BrightnessResult)); Notify(nameof(HasBrightnessResult)); } }
    }
    public string BrightnessResult => host.AdjustmentErrorFor(CurrentLightingGroup) ?? brightnessResult;
    public bool HasBrightnessResult => !string.IsNullOrWhiteSpace(BrightnessResult);
    public ICommand ApplyBrightness { get; private set; } = null!;
    public ICommand TurnAllLightsOff { get; private set; } = null!;
    public ICommand RestoreLighting { get; private set; } = null!;
    private void ResetBrightnessDisplay() { var saved = host.AdjustmentFor(CurrentLightingGroup); MasterBrightness = saved.Applied ? saved.Brightness : 100; brightnessResult = ""; Notify(nameof(BrightnessResult)); }
    private void InitializeLightingBrightness()
    {
        async Task Adjust(int? level = null, bool off = false, bool restore = false)
        {
            brightnessBusy = true; brightnessResult = "適用中…"; Refresh();
            try
            {
                var group = CurrentLightingGroup;
                var result = await host.AdjustLightingAsync(level, off, restore, group);
                var adjustment = host.AdjustmentFor(group);
                LightingSceneResults.Clear(); foreach (var row in result.Rows) LightingSceneResults.Add(row);
                if (result.Succeeded) { Notice = ""; MasterBrightness = adjustment.Brightness; LoadHubLighting(); RefreshMotherboard(); LoadBoardLightDraft(); }
                brightnessResult = result.Succeeded ? off ? "消灯済み" : $"明るさ {adjustment.Brightness}% · 適用済み" : result.Error ?? "適用を確認できません";
            }
            catch (Exception e) { brightnessResult = e.Message; throw; }
            finally { brightnessBusy = false; Refresh(); }
        }
        ApplyBrightness = Command(_ => Adjust(MasterBrightness), _ => LightingScenesIdle && CanUseLightingScope);
        TurnAllLightsOff = Command(_ => Adjust(off: true), _ => LightingScenesIdle && CanUseLightingScope);
        RestoreLighting = Command(_ => Adjust(restore: true), _ => LightingScenesIdle && CanUseLightingScope && host.CanRestoreAdjustment(CurrentLightingGroup));
    }
}
