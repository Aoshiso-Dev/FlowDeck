using System.Collections.ObjectModel;
using System.Windows.Input;
using FlowDeck.Core;

namespace FlowDeck.App;

public sealed record FanTemperatureChoice(string Id, string Name);

public sealed class BoardFanRow(MotherboardFanPort port) : Observable
{
    public MotherboardFanPort Port { get; } = port;
    public string Name { get; private set; } = port.Name;
    public string Reading { get; private set; } = "— rpm";
    public double? Value { get; private set; }
    public string DisplayValue => Value?.ToString("N0") ?? "—";
    public bool Circular => false;
    public bool Bar => true;
    public bool Inline => true;
    public double ScaleMaximum => RpmScaleConverter.Scale(Value, Role == "ポンプ");
    public string ScaleDescription => $"表示目盛 0–{ScaleMaximum:N0} rpm（定格上限・出力率とは別です）";
    public string Role { get; private set; } = "ファン";
    public string Mode { get; private set; } = "マザー管理";
    public void Update(CpuFanState state, AppSettings settings, BoardSnapshot board)
    {
        var sensor = board.Sensors.FirstOrDefault(s => s.Id == Port.SensorId || s.Id == Port.DemoSensorId);
        var savedName = settings.Motherboard.Sensors?.FirstOrDefault(s => s.Id == Port.SensorId || s.Id == Port.DemoSensorId)?.DisplayName;
        Name = !string.IsNullOrWhiteSpace(savedName) && sensor is not null && savedName != MotherboardDisplay.DefaultName(sensor) ? savedName : Port.Name;
        var display = settings.Dashboard.Items?.FirstOrDefault(i => i.Source == "motherboard" && (i.SensorId == Port.SensorId || i.SensorId == Port.DemoSensorId));
        if (display is not null) Name = display.Name;
        Role = display?.Role == "pump" ? "ポンプ" : "ファン";
        Value = state.Reading is { } r && DashboardDisplay.Fresh(r.Timestamp) ? r.Rpm : null;
        Reading = DisplayValue + " rpm";
        Mode = state.Mode switch { "manual" => "手動", "auto" => "自動", "test" => "テスト中", "fault" => "要確認", _ => "マザー管理" };
        Notify(nameof(Name)); Notify(nameof(Reading)); Notify(nameof(Mode)); Notify(nameof(Value)); Notify(nameof(DisplayValue)); Notify(nameof(Role));
        Notify(nameof(ScaleMaximum)); Notify(nameof(ScaleDescription));
    }
}

public sealed partial class FlowDeckViewModel
{
    public ObservableCollection<BoardFanRow> BoardFanRows { get; } = [];
    public string BoardFanCount => $"検出チャンネル {BoardFanRows.Count}";
    public string[] BoardFanModes { get; } = ["手動", "自動"];
    private string boardFanEditMode = "手動";
    public string BoardFanEditMode { get => boardFanEditMode; set => Set(ref boardFanEditMode, value); }
    public ICommand BoardFanApply { get; private set; } = null!;
    private MotherboardFanPort selectedBoardFan = MotherboardFans.Cpu;
    private readonly Dictionary<string, FanDraft> fanDrafts = [];
    public MotherboardFanPort SelectedBoardFan
    {
        get => selectedBoardFan;
        set
        {
            if (value is null || value == selectedBoardFan || cpuFanBusy) return;
            fanDrafts[selectedBoardFan.Id] = CaptureFanDraft();
            selectedBoardFan = value; Notify(nameof(SelectedBoardFan));
            BoardFanEditMode = SelectedFanController.State.Mode == "auto" ? "自動" : "手動";
            LoadCpuFanSettings();
            if (fanDrafts.TryGetValue(value.Id, out var draft)) ApplyFanDraft(draft);
            Notice = "";
        }
    }
    private IBoardFanController SelectedFanController => host.BoardFans[SelectedBoardFan.Id];
    private CpuFanSettings SelectedFanSettings => MotherboardFans.Settings(core.Settings, SelectedBoardFan.Id);
    public string BoardFanTitle => BoardFanRows.FirstOrDefault(r => r.Port == SelectedBoardFan)?.Name ?? SelectedBoardFan.Name;
    public string BoardFanOutputNote => SelectedBoardFan.Chip == "NCT6799D" && SelectedBoardFan.Channel >= 5
        ? "出力欄は設定値です。回転数で実際の変化を確認してください。" : "";
    public string BoardFanSetupSummary => !SelectedFanSettings.WiringConfirmed || SelectedFanSettings.Minimum is null
        ? "初回は「初期設定」で端子と最低出力を確認してください。"
        : $"最低出力 {SelectedFanSettings.Minimum}% · " + (SelectedFanSettings.Profile is null ? "自動カーブ未設定" : "自動カーブ設定済み");
    public string BoardFanWiringLabel => SelectedBoardFan.Name + "にファンを接続し、端子テストで対象の変化を確認した";
    public bool BoardFanCanSelect => !cpuFanBusy;
    public ObservableCollection<FanTemperatureChoice> BoardFanTemperatures { get; } = [];
    public ObservableCollection<CurvePointRow> CpuFanGpuPoints { get; } = [];
    public ICommand CpuFanGpuAddPoint { get; private set; } = null!;
    public ICommand CpuFanGpuRemovePoint { get; private set; } = null!;
    private string? cpuFanSensorId, cpuFanGpuSensorId;
    public string? CpuFanSensorId { get => cpuFanSensorId; set { if (Set(ref cpuFanSensorId, value)) Notify(nameof(BoardCurveTemperature)); } }
    public string? CpuFanGpuSensorId { get => cpuFanGpuSensorId; set { if (Set(ref cpuFanGpuSensorId, value)) Notify(nameof(BoardCurveGpuTemperature)); } }
    public bool CpuFanGpuEnabled { get; set; }
    public string CpuFanGpuOverheat { get; set; } = "";
    public string CpuFanGpuResume { get; set; } = "";
    private void RefreshBoardFanRows()
    {
        foreach (var port in MotherboardFans.Detected(host.Motherboard.Board))
            if (BoardFanRows.All(r => r.Port.Id != port.Id)) BoardFanRows.Add(new(port));
        foreach (var row in BoardFanRows) row.Update(host.BoardFans[row.Port.Id].State, core.Settings, host.Motherboard.Board);
        Notify(nameof(BoardFanCount)); Notify(nameof(BoardFanTitle)); Notify(nameof(BoardFanSetupSummary)); Notify(nameof(BoardFanOutputNote)); Notify(nameof(BoardFanMinimumDuty));
    }
    private void RefreshFanTemperatures()
    {
        var cpuGpu = core.Snapshot.Sensors.Select(s => new FanTemperatureChoice(s.Id, s.Name));
        var board = host.Motherboard.Board.Sensors.Where(s => s.Kind == "temperature").Select(s => new FanTemperatureChoice(s.Id,
            core.Settings.Motherboard.Sensors?.FirstOrDefault(c => c.Id == s.Id)?.DisplayName ?? MotherboardDisplay.DefaultName(s)));
        foreach (var item in cpuGpu.Concat(board))
            if (BoardFanTemperatures.All(c => c.Id != item.Id)) BoardFanTemperatures.Add(item);
        foreach (var curve in new[] { SelectedFanSettings.Profile?.Cpu, SelectedFanSettings.Profile?.Gpu }.OfType<SensorCurve>())
            if (BoardFanTemperatures.All(c => c.Id != curve.SensorId)) BoardFanTemperatures.Add(new(curve.SensorId, "未取得 / " + curve.SensorId));
    }
    private SensorCurve BoardFanCurve(string? id, IEnumerable<CurvePointRow> points, string overheat, string resume) =>
        new(id ?? throw new InvalidOperationException("温度センサーを選択してください"),
            points.Select(p => new CurvePoint(p.Temperature, p.Duty)).ToArray(), RequiredDouble(overheat), RequiredDouble(resume));
    private void LoadBoardFanExtras()
    {
        RefreshFanTemperatures();
        var p = SelectedFanSettings.Profile;
        CpuFanSensorId = p?.Cpu?.SensorId ?? core.Settings.CpuSensorId;
        CpuFanGpuSensorId = p?.Gpu?.SensorId ?? core.Settings.GpuSensorId;
        CpuFanGpuEnabled = p?.Gpu is not null;
        CpuFanGpuOverheat = p?.Gpu?.Overheat.ToString() ?? ""; CpuFanGpuResume = p?.Gpu?.ResumeBelow.ToString() ?? "";
        Replace(CpuFanGpuPoints, (p?.Gpu?.Points ?? []).Select(v => new CurvePointRow(v.Temperature, v.Duty)));
        NotifyBoardFanExtras();
    }
    private void NotifyBoardFanExtras()
    {
        foreach (var name in new[] { nameof(CpuFanSensorId), nameof(CpuFanGpuSensorId), nameof(CpuFanGpuEnabled), nameof(CpuFanGpuOverheat),
            nameof(CpuFanGpuResume), nameof(BoardFanTitle), nameof(BoardFanWiringLabel), nameof(CpuFanDuty) }) Notify(name);
    }
    private sealed record FanDraft(bool Wiring, string Minimum, string Duty, string Mode, string? Sensor, string? GpuSensor, bool GpuEnabled,
        string Overheat, string Resume, string GpuOverheat, string GpuResume, string Delay, string Rate, CurvePoint[] Points, CurvePoint[] GpuPoints);
    private FanDraft CaptureFanDraft() => new(CpuFanWiringConfirmed, CpuFanMinimum, CpuFanDuty, BoardFanEditMode, CpuFanSensorId, CpuFanGpuSensorId, CpuFanGpuEnabled,
        CpuFanOverheat, CpuFanResume, CpuFanGpuOverheat, CpuFanGpuResume, CpuFanDelay, CpuFanRate,
        CpuFanPoints.Select(p => new CurvePoint(p.Temperature, p.Duty)).ToArray(), CpuFanGpuPoints.Select(p => new CurvePoint(p.Temperature, p.Duty)).ToArray());
    private void ApplyFanDraft(FanDraft d)
    {
        CpuFanWiringConfirmed = d.Wiring; CpuFanMinimum = d.Minimum; CpuFanDuty = d.Duty; BoardFanEditMode = d.Mode; CpuFanSensorId = d.Sensor; CpuFanGpuSensorId = d.GpuSensor;
        CpuFanGpuEnabled = d.GpuEnabled; CpuFanOverheat = d.Overheat; CpuFanResume = d.Resume; CpuFanGpuOverheat = d.GpuOverheat; CpuFanGpuResume = d.GpuResume;
        CpuFanDelay = d.Delay; CpuFanRate = d.Rate;
        Replace(CpuFanPoints, d.Points.Select(p => new CurvePointRow(p.Temperature, p.Duty)));
        Replace(CpuFanGpuPoints, d.GpuPoints.Select(p => new CurvePointRow(p.Temperature, p.Duty)));
        NotifyCpuFanFields(); NotifyBoardFanExtras();
    }
}
