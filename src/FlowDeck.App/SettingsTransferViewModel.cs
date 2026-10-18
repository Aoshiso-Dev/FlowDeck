using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Win32;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class FlowDeckViewModel
{
    private SettingsRestorePreview? settingsRestorePreview;
    private bool settingsTransferBusy;
    private string settingsTransferResult = "", settingsTransferFile = "";
    public ObservableCollection<SettingsDifference> SettingsDifferences { get; } = [];
    public ObservableCollection<string> SettingsDeviceChecks { get; } = [];
    public string SettingsTransferResult => settingsTransferResult.Length > 0 ? settingsTransferResult : host.SettingsRestoreMessage;
    public string SettingsTransferFile => settingsTransferFile;
    public bool SettingsTransferIdle => !settingsTransferBusy;
    public bool SettingsHasPreview => settingsRestorePreview is not null;
    public bool SettingsHasPending => host.SettingsRestorePending;
    public string SettingsDifferenceSummary => settingsRestorePreview is null ? "ファイル未選択" : SettingsDifferences.Count == 0 ? "変更なし" : $"{SettingsDifferences.Count} 項目を変更";
    public ICommand ExportSettingsFile { get; private set; } = null!;
    public ICommand ChooseSettingsFile { get; private set; } = null!;
    public ICommand ScheduleSettingsRestore { get; private set; } = null!;
    public ICommand CancelSettingsRestore { get; private set; } = null!;
    private void InitializeSettingsTransfer()
    {
        ExportSettingsFile = Command(async _ =>
        {
            var dialog = new SaveFileDialog { Filter = "FlowDeck 設定 (*.flowdeck.json)|*.flowdeck.json", FileName = $"FlowDeck-{DateTime.Now:yyyyMMdd-HHmmss}.flowdeck.json" };
            if (dialog.ShowDialog() == true) await ExportSettingsFileAsync(dialog.FileName);
        }, _ => SettingsTransferIdle);
        ChooseSettingsFile = Command(async _ =>
        {
            var dialog = new OpenFileDialog { Filter = "FlowDeck 設定 (*.flowdeck.json)|*.flowdeck.json|JSON (*.json)|*.json", CheckFileExists = true };
            if (dialog.ShowDialog() == true) await PreviewSettingsFileAsync(dialog.FileName);
        }, _ => SettingsTransferIdle && !SettingsHasPending);
        ScheduleSettingsRestore = Command(async _ => await TransferOperation(async () =>
        {
            if (settingsRestorePreview is null) throw new InvalidOperationException("差分を確認してください");
            await host.ScheduleSettingsAsync(settingsRestorePreview); settingsRestorePreview = null;
            settingsTransferResult = "復元予約済み（次回起動）";
        }), _ => SettingsTransferIdle && SettingsHasPreview && !SettingsHasPending);
        CancelSettingsRestore = Command(async _ => await TransferOperation(async () =>
        {
            await host.CancelSettingsRestoreAsync(); settingsRestorePreview = null; SettingsDifferences.Clear(); SettingsDeviceChecks.Clear();
            settingsTransferResult = "復元予約取消済み";
        }), _ => SettingsTransferIdle && SettingsHasPending);
    }
    internal Task ExportSettingsFileAsync(string path) => TransferOperation(async () =>
    { await host.ExportSettingsAsync(path); settingsTransferResult = "書き出し済み · " + path; });
    internal Task PreviewSettingsFileAsync(string path) => TransferOperation(async () =>
    {
        settingsRestorePreview = null; settingsTransferFile = Path.GetFileName(path); SettingsDifferences.Clear(); SettingsDeviceChecks.Clear();
        var preview = await host.PreviewSettingsAsync(path);
        settingsRestorePreview = preview; settingsTransferFile = Path.GetFileName(path);
        Replace(SettingsDifferences, preview.Differences); Replace(SettingsDeviceChecks, preview.Devices);
        settingsTransferResult = "";
    });
    private async Task TransferOperation(Func<Task> action)
    {
        if (settingsTransferBusy) return;
        settingsTransferBusy = true; Refresh();
        try { await action(); }
        catch (Exception e) { settingsTransferResult = e.Message; }
        finally { settingsTransferBusy = false; Refresh(); CommandManager.InvalidateRequerySuggested(); }
    }
}
