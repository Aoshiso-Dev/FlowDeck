using System.Windows;
using System.Windows.Media;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class DeviceWorkspaceViewModel
{
    private UiSettings ui = new();
    private Task uiSave = Task.CompletedTask;
    private string accentColor = DeviceAccentColor.Default;
    private static readonly ColorSwatch[] accentPresets =
    [
        new(Color.FromRgb(163, 217, 54), "ライム"), new(Color.FromRgb(160, 121, 236), "パープル"),
        new(Color.FromRgb(70, 140, 240), "ブルー"), new(Color.FromRgb(36, 195, 209), "シアン"),
        new(Color.FromRgb(53, 187, 122), "グリーン"), new(Color.FromRgb(244, 185, 66), "アンバー"),
        new(Color.FromRgb(242, 139, 66), "オレンジ"), new(Color.FromRgb(232, 112, 163), "ピンク")
    ];
    public IEnumerable<ColorSwatch> AccentChoices => accentPresets.Any(c => c.Hex == AccentColor)
        ? accentPresets : accentPresets.Append(new((Color)ColorConverter.ConvertFromString(AccentColor), "カスタム"));
    public string AccentColor
    {
        get => accentColor;
        set
        {
            if (!DeviceAccentColor.Valid(value) || accentColor == DeviceAccentColor.Normalize(value)) return;
            accentColor = DeviceAccentColor.Normalize(value);
            Notify(nameof(AccentChoices)); Notify(nameof(AccentColor));
            ThemeManager.Apply(Theme, accentColor);
            QueueUiSave();
        }
    }
    public UiSettings WindowPreferences => ui;
    private void QueueUiSave()
    {
        if (uiStore is null || !workspaceUiLoaded || restoringWorkspaceUi) return;
        // Queue a snapshot in UI input order; a slow older write cannot overwrite a newer choice.
        uiSave = SaveUiAsync(uiSave, ui, DeviceWorkspaceUiState.Normalize(workspaceUi), AccentColor);
    }
    private async Task SaveUiAsync(Task preceding, UiSettings value, DeviceWorkspaceUiState state, string accent)
    {
        await preceding;
        try { await uiStore!.SaveAsync(value, state, accent); }
        catch (Exception e) { Notice = "表示設定の保存失敗: " + e.Message; }
    }
    public Task FlushUiAsync() => uiSave;
    public Task SaveWindowAsync(Rect bounds)
    {
        if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
        {
            ui = ui with { Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height };
            QueueUiSave();
        }
        return FlushUiAsync();
    }
    public void ApplySystemTheme() { if (Theme == "system") ThemeManager.Apply(Theme, AccentColor); }
}
