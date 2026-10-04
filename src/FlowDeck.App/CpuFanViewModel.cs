using System.Collections.ObjectModel;
using System.Windows.Input;
using FlowDeck.Core;

namespace FlowDeck.App;

public sealed partial class FlowDeckViewModel
{
    public ICommand CpuFanTest { get; private set; } = null!;
    public ICommand CpuFanSave { get; private set; } = null!;
    public ICommand CpuFanReload { get; private set; } = null!;
    public ICommand CpuFanManual { get; private set; } = null!;
    public ICommand CpuFanAuto { get; private set; } = null!;
    public ICommand CpuFanRestore { get; private set; } = null!;
    public ICommand CpuFanAddPoint { get; private set; } = null!;
    public ICommand CpuFanRemovePoint { get; private set; } = null!;
    public ObservableCollection<CurvePointRow> CpuFanPoints { get; } = [];
    public bool CpuFanWiringConfirmed { get; set; }
    private string cpuFanMinimum = "";
    public string CpuFanMinimum { get => cpuFanMinimum; set { if (Set(ref cpuFanMinimum, value)) Notify(nameof(BoardCurveMinimum)); } }
    private string cpuFanDuty = "100";
    public string CpuFanDuty { get => cpuFanDuty; set => Set(ref cpuFanDuty, value); }
    public int BoardFanMinimumDuty => SelectedFanSettings.Minimum ?? 1;
    public string CpuFanOverheat { get; set; } = "";
    public string CpuFanResume { get; set; } = "";
    public string CpuFanDelay { get; set; } = "3";
    public string CpuFanRate { get; set; } = "5";
    private bool cpuFanBusy;
    public string CpuFanStatus => SelectedFanController.State.Message;
    public string CpuFanOutput => SelectedFanController.State.Reading is { } reading && DashboardDisplay.Fresh(reading.Timestamp)
        ? $"{reading.Rpm:N0} rpm / {reading.Duty:F1}%" : "— rpm / —%";
    public string CpuFanRpm => SelectedFanController.State.Reading is { } reading && DashboardDisplay.Fresh(reading.Timestamp) ? reading.Rpm.ToString("N0") : "—";
    public string CpuFanReportedDuty => SelectedFanController.State.Reading is { } reading && DashboardDisplay.Fresh(reading.Timestamp) ? $"{reading.Duty:F1}%" : "—%";
    public string CpuFanMode => SelectedFanController.State.Mode switch { "manual" => "手動", "auto" => "自動", "test" => "端子テスト", "fault" => "異常・復元操作が必要", _ => "マザーボード制御" };
    private void InitializeCpuFan()
    {
        BoardFanApply = Command(_ => CpuFanAction(async () =>
        {
            if (BoardFanEditMode == "自動") await SelectedFanController.AutoAsync(SelectedFanSettings);
            else await SelectedFanController.ManualAsync(SelectedFanSettings, RequiredInt(CpuFanDuty));
        }), _ => !cpuFanBusy);
        CpuFanTest = Command(_ => CpuFanAction(async () => await SelectedFanController.TestAsync()), _ => !cpuFanBusy && !SelectedFanController.State.Managing);
        CpuFanManual = Command(_ => CpuFanAction(async () => await SelectedFanController.ManualAsync(SelectedFanSettings, RequiredInt(CpuFanDuty))), _ => !cpuFanBusy);
        CpuFanAuto = Command(_ => CpuFanAction(async () => await SelectedFanController.AutoAsync(SelectedFanSettings)), _ => !cpuFanBusy);
        CpuFanRestore = Command(_ => CpuFanAction(async () =>
        {
            await SelectedFanController.RestoreAsync();
            if (SelectedFanController.State.Reading is { } reading && DashboardDisplay.Fresh(reading.Timestamp) && double.IsFinite(reading.Duty))
                CpuFanDuty = Math.Clamp((int)Math.Round(reading.Duty), BoardFanMinimumDuty, 100).ToString();
        }), _ => !cpuFanBusy);
        CpuFanReload = Command(_ => { fanDrafts.Remove(SelectedBoardFan.Id); LoadCpuFanSettings(); return Task.CompletedTask; }, _ => !cpuFanBusy);
        CpuFanSave = Command(_ => CpuFanAction(async () =>
        {
            if (SelectedFanController.State.Managing) throw new InvalidOperationException("先に選択したファンをマザーボード制御へ戻してください");
            AutoProfile? profile = null;
            if (CpuFanPoints.Count > 0 || CpuFanGpuEnabled)
            {
                profile = new(SelectedBoardFan.Id, SelectedBoardFan.Name, (SelectedFanSettings.Profile?.Revision ?? 0) + 1,
                    CpuFanPoints.Count > 0 ? BoardFanCurve(CpuFanSensorId, CpuFanPoints, CpuFanOverheat, CpuFanResume) : null,
                    CpuFanGpuEnabled ? BoardFanCurve(CpuFanGpuSensorId, CpuFanGpuPoints, CpuFanGpuOverheat, CpuFanGpuResume) : null,
                    RequiredDouble(CpuFanDelay), RequiredDouble(CpuFanRate));
            }
            var saved = new CpuFanSettings(CpuFanWiringConfirmed, ParseInt(CpuFanMinimum), profile);
            if (CpuFanController.Validate(saved, SelectedBoardFan.Name) is { } error) throw new InvalidOperationException(error);
            await core.UpdateSettingsAsync(MotherboardFans.WithSettings(core.Settings, SelectedBoardFan.Id, saved), false); await host.FlushSettingsAsync();
            Notice = SelectedBoardFan.Name + "の設定を保存しました。手動または自動を適用するまで出力は変わりません";
        }, saving: true), _ => !cpuFanBusy && !SelectedFanController.State.Managing);
        CpuFanAddPoint = Command(_ => { AddCurvePoint(CpuFanPoints, BoardCurveMinimum, false, row => CpuFanSelectedPoint = row); return Task.CompletedTask; });
        CpuFanGpuAddPoint = Command(_ => { AddCurvePoint(CpuFanGpuPoints, BoardCurveMinimum, false, row => CpuFanGpuSelectedPoint = row); return Task.CompletedTask; });
        CpuFanGpuRemovePoint = Command(_ => { if (CpuFanGpuSelectedPoint is not null) CpuFanGpuPoints.Remove(CpuFanGpuSelectedPoint); return Task.CompletedTask; });
        CpuFanRemovePoint = Command(_ => { if (CpuFanSelectedPoint is not null) CpuFanPoints.Remove(CpuFanSelectedPoint); return Task.CompletedTask; });
    }
    private async Task CpuFanAction(Func<Task> action, bool saving = false)
    {
        if (cpuFanBusy) return;
        cpuFanBusy = true; Notify(nameof(BoardFanCanSelect)); CommandManager.InvalidateRequerySuggested();
        try { await action(); if (!saving) Notice = SelectedFanController.State.Message; }
        finally { cpuFanBusy = false; Notify(nameof(BoardFanCanSelect)); RefreshCpuFan(); CommandManager.InvalidateRequerySuggested(); }
    }
    private void LoadCpuFanSettings()
    {
        var saved = SelectedFanSettings;
        Notify(nameof(BoardFanMinimumDuty));
        CpuFanDuty = Math.Clamp(SelectedFanController.State.Target ?? (int)Math.Round(SelectedFanController.State.Reading?.Duty ?? 100), BoardFanMinimumDuty, 100).ToString();
        LoadBoardFanExtras();
        CpuFanWiringConfirmed = saved.WiringConfirmed; CpuFanMinimum = saved.Minimum?.ToString() ?? "";
        CpuFanOverheat = saved.Profile?.Cpu?.Overheat.ToString() ?? ""; CpuFanResume = saved.Profile?.Cpu?.ResumeBelow.ToString() ?? "";
        CpuFanDelay = saved.Profile?.DescentDelaySeconds?.ToString() ?? "3"; CpuFanRate = saved.Profile?.DescentPerSecond?.ToString() ?? "5";
        Replace(CpuFanPoints, (saved.Profile?.Cpu?.Points ?? []).Select(p => new CurvePointRow(p.Temperature, p.Duty)));
        NotifyCpuFanFields(); RefreshCpuFan();
    }
    private void NotifyCpuFanFields()
    {
        foreach (var name in new[] { nameof(CpuFanWiringConfirmed), nameof(CpuFanMinimum), nameof(CpuFanOverheat), nameof(CpuFanResume), nameof(CpuFanDelay), nameof(CpuFanRate) }) Notify(name);
    }
    private void RefreshCpuFan() { RefreshBoardFanRows(); RefreshFanTemperatures(); Notify(nameof(CpuFanStatus)); Notify(nameof(CpuFanOutput)); Notify(nameof(CpuFanRpm)); Notify(nameof(CpuFanReportedDuty)); Notify(nameof(CpuFanMode)); }
}
