using System.Collections.ObjectModel;
using FlowDeck.Core;

namespace FlowDeck.App;

public sealed partial class FlowDeckViewModel
{
    public int? HubCurveMinimum => core.Settings.Limits.Minimum;
    public bool HubCurveAllowStop => core.Settings.Limits.AllowStop && FanPolicyEngine.ValidateLimits(core.Settings.Limits) is null;
    public int? BoardCurveMinimum => int.TryParse(CpuFanMinimum, out var minimum) && minimum is >= 1 and <= 100 ? minimum : null;
    public double? HubCurveCpuTemperature => CurveTemperature(core.Settings.CpuSensorId);
    public double? HubCurveGpuTemperature => CurveTemperature(core.Settings.GpuSensorId);
    public double? HubCurveDuty => state.ConfirmedDuty;
    public double? BoardCurveTemperature => CurveTemperature(CpuFanSensorId);
    public double? BoardCurveGpuTemperature => CurveTemperature(CpuFanGpuSensorId);
    public double? BoardCurveDuty => SelectedFanController.State.Reading is { } r && DashboardDisplay.Fresh(r.Timestamp) && double.IsFinite(r.Duty) ? r.Duty : null;
    private double? CurveTemperature(string? id)
    {
        if (id is null) return null;
        var reading = state.Sensors.FirstOrDefault(s => s.Id == id);
        if (reading is not null) return DashboardDisplay.Fresh(reading.Timestamp) && reading.Celsius is { } value && double.IsFinite(value) ? value : null;
        var board = host.Motherboard.Board;
        return board.IsFresh() && board.Sensors.FirstOrDefault(s => s.Id == id && s.Kind == "temperature") is { Value: { } t, Problem: null } && double.IsFinite(t) ? t : null;
    }
    private CurvePointRow? selectedCpuPoint, selectedGpuPoint, cpuFanSelectedPoint, cpuFanGpuSelectedPoint;
    public CurvePointRow? SelectedCpuPoint { get => selectedCpuPoint; set => Set(ref selectedCpuPoint, value); }
    public CurvePointRow? SelectedGpuPoint { get => selectedGpuPoint; set => Set(ref selectedGpuPoint, value); }
    public CurvePointRow? CpuFanSelectedPoint { get => cpuFanSelectedPoint; set => Set(ref cpuFanSelectedPoint, value); }
    public CurvePointRow? CpuFanGpuSelectedPoint { get => cpuFanGpuSelectedPoint; set => Set(ref cpuFanGpuSelectedPoint, value); }
    private void AddCurvePoint(ObservableCollection<CurvePointRow> collection, int? minimum, bool allowStop, Action<CurvePointRow> select)
    {
        var values = collection.Select(p => new CurvePoint(p.Temperature, p.Duty)).ToArray();
        if (!CurveEditing.ValidShape(values, minimum, allowStop)) throw new InvalidOperationException("最低出力とカーブの数値・順序を確認してください");
        double t; double duty;
        if (values.Length == 0) { t = 40; duty = minimum!.Value; }
        else if (values.Length == 1) { t = values[0].Temperature + 20; duty = values[0].Duty; }
        else
        {
            var gap = Enumerable.Range(1, values.Length - 1).OrderByDescending(i => values[i].Temperature - values[i - 1].Temperature).First();
            t = (values[gap - 1].Temperature + values[gap].Temperature) / 2; duty = FanPolicyEngine.Interpolate(values, t);
        }
        var point = CurveEditing.Insert(values, t, duty, minimum, allowStop, Math.Min(0, t - 1), Math.Max(100, t + 1));
        if (point is not { } next) throw new InvalidOperationException("この区間には点を追加できません");
        var row = new CurvePointRow(next.Point.Temperature, next.Point.Duty); collection.Insert(next.Index, row); select(row);
    }
}
