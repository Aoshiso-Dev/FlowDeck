using System.Windows;
using FlowDeck.Core;

namespace FlowDeck.App;

public sealed partial class DeviceWorkspaceViewModel
{
    private UiSettings ui = new();
    private Task uiSave = Task.CompletedTask;
    public UiSettings WindowPreferences => ui;
    private void QueueUiSave()
    {
        if (uiStore is null) return;
        // Queue a snapshot in UI input order; a slow older write cannot overwrite a newer choice.
        uiSave = SaveUiAsync(uiSave, ui);
    }
    private async Task SaveUiAsync(Task preceding, UiSettings value)
    {
        await preceding;
        try { await uiStore!.SaveAsync(value); }
        catch (Exception e) { Notice = "表示設定を保存できません: " + e.Message; }
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
    public void ApplySystemTheme() { if (Theme == "system") ThemeManager.Apply(Theme); }
}
