using System.Collections.ObjectModel;
using System.Windows.Input;
using FlowDeck.Core;

namespace FlowDeck.App;

public sealed class DashboardChoice(DashboardItem item) : Observable
{
    public DashboardItem Identity { get; } = item;
    public string Key => Identity.Key;
    private string name = item.Name, role = item.Role;
    private bool visible = item.Visible;
    public string Name { get => name; set => Set(ref name, value); }
    public bool Visible { get => visible; set => Set(ref visible, value); }
    public bool IsFan => Identity.Kind == "fan";
    public string Kind => IsFan ? "回転数" : "温度";
    public string Role { get => role == "pump" ? "ポンプ" : "ファン"; set { role = value == "ポンプ" ? "pump" : "fan"; Notify(nameof(Role)); } }
    public string[] Roles { get; } = ["ファン", "ポンプ"];
    public string Source => Identity.Source switch { "motherboard" => "マザーボード", "nzxt" => "NZXT", _ => Identity.Source.ToUpperInvariant() };
    public string SensorId => Identity.SensorId;
    public string Current { get; private set; } = "未取得";
    public DashboardItem Save() => Identity with { Name = Name.Trim(), Visible = Visible, Role = role };
    public void Update(DashboardReading reading) { Current = reading.DisplayValue + " " + reading.Unit + " / " + reading.State; Notify(nameof(Current)); }
}
public sealed record DashboardTrendOption(string Key, string Name, string Unit);

public sealed partial class FlowDeckViewModel
{
    public ObservableCollection<DashboardReading> DashboardTemperatures { get; } = [];
    public ObservableCollection<DashboardReading> NzxtFanReadings { get; } = [];
    private bool hubControlsOpen;
    public bool HubControlsOpen { get => hubControlsOpen; set => Set(ref hubControlsOpen, value); }
    public ObservableCollection<DashboardReading> DashboardFans { get; } = [];
    public ObservableCollection<DashboardChoice> DashboardChoices { get; } = [];
    public ObservableCollection<DashboardTrendOption> DashboardTrendOptions { get; } = [];
    private DashboardHistory dashboardHistory = new();
    private DashboardChoice? dashboardSelected;
    private DashboardTrendOption? dashboardTrend;
    private bool cpuFanControlsOpen;
    private bool dashboardLogged;
    public DashboardChoice? DashboardSelected { get => dashboardSelected; set => Set(ref dashboardSelected, value); }
    public DashboardTrendOption? DashboardTrend { get => dashboardTrend; set { Set(ref dashboardTrend, value); Notify(nameof(DashboardTrendSamples)); Notify(nameof(DashboardTrendUnit)); } }
    public DashboardSample[] DashboardTrendSamples => dashboardHistory.Get(DashboardTrend?.Key);
    public string DashboardTrendUnit => DashboardTrend?.Unit ?? "°C";
    public double DashboardTrendNow => dashboardHistory.Now;
    public bool DashboardEmptyTemperatures => DashboardTemperatures.Count == 0;
    public bool DashboardEmptyFans => DashboardFans.Count == 0;
    public bool CpuFanControlsOpen { get => cpuFanControlsOpen; set => Set(ref cpuFanControlsOpen, value); }
    public string DashboardHubStatus => state.HubReady ? DashboardDisplay.Fresh(state.TelemetryTimestamp) ? "NZXT：取得中" : "NZXT：更新停止" : "NZXT：未接続";
    public string DashboardBoardStatus => "マザーボード：" + (host.Motherboard.Board.IsFresh() ? "取得中" : host.Motherboard.Board.Error is not null ? "未取得" : "更新停止");
    public string HubControlReadiness => state.Readiness == "制御可能" ? "制御設定済み" : "制御設定：" + state.Readiness;
    public ICommand SaveDashboard { get; private set; } = null!;
    public ICommand ReloadDashboard { get; private set; } = null!;
    public ICommand ResetDashboard { get; private set; } = null!;
    public ICommand MoveDashboard { get; private set; } = null!;
    public ICommand DashboardDetails { get; private set; } = null!;

    private void InitializeDashboard()
    {
        SaveDashboard = Command(async _ =>
        {
            var settings = new DashboardSettings(DashboardChoices.Select(c => c.Save()).ToArray());
            if (DashboardDisplay.Validate(settings) is { } error) throw new InvalidOperationException(error);
            await core.UpdateSettingsAsync(core.Settings with { Dashboard = settings }, false);
            await host.FlushSettingsAsync(); RefreshDashboard();
            Notice = "ダッシュボードの表示設定を保存しました";
        });
        ReloadDashboard = Command(_ => { LoadDashboard(); Notice = "保存済みの表示設定を読み込みました"; return Task.CompletedTask; });
        ResetDashboard = Command(_ => { LoadDashboardChoices(new()); Notice = "初期表示を編集欄に戻しました。保存すると反映されます"; return Task.CompletedTask; });
        MoveDashboard = Command(p =>
        {
            if (DashboardSelected is not { } selected) return Task.CompletedTask;
            var index = DashboardChoices.IndexOf(selected); var step = p?.ToString() == "up" ? -1 : 1;
            var next = index + step;
            // Move across another row of the same dashboard section, skipping the other kind.
            while (next >= 0 && next < DashboardChoices.Count && DashboardChoices[next].Identity.Kind != selected.Identity.Kind) next += step;
            if (next >= 0 && next < DashboardChoices.Count) DashboardChoices.Move(index, next);
            return Task.CompletedTask;
        }, _ => DashboardSelected is not null);
        DashboardDetails = Command(p =>
        {
            if (p is DashboardReading r && r.Item.Source == "nzxt") { HubControlsOpen = true; SelectedPage = 7; }
            else if (p is DashboardReading b && MotherboardFans.ForSensor(host.Motherboard.Board, b.Item.SensorId) is { } port)
            { SelectedBoardFan = port; CpuFanControlsOpen = true; SelectedPage = 8; }
            else SelectedPage = 5;
            return Task.CompletedTask;
        });
    }
    private void LoadDashboardChoices(DashboardSettings settings)
    {
        Replace(DashboardChoices, DashboardDisplay.Items(settings, DashboardDisplay.Catalog(core.Settings, state, host.Motherboard.Board)).Select(i => new DashboardChoice(i)));
        DashboardSelected = DashboardChoices.FirstOrDefault();
    }
    private void LoadDashboard()
    {
        LoadDashboardChoices(core.Settings.Dashboard); RefreshDashboard();
    }
    private void RefreshDashboard()
    {
        var board = host.Motherboard.Board;
        var readings = DashboardDisplay.ReadAll(core.Settings, state, board, host.BoardFans.States);
        if (!Demo && !dashboardLogged && board.IsFresh() && host.CpuFan.State.Reading is { } cpu && DashboardDisplay.Fresh(cpu.Timestamp))
        {
            dashboardLogged = true;
            host.Log.Write("dashboardInventory", new { version = "1.9.0", readings, nzxtManaging = state.Managing, cpuFanManaging = host.CpuFan.State.Managing, boardFans = host.BoardFans.States });
        }
        // Preserve item instances when values are unchanged; avoid clearing UI selection at every refresh.
        Reconcile(DashboardTemperatures, readings.Where(r => r.Item.Kind == "temperature").ToArray());
        Reconcile(DashboardFans, readings.Where(r => r.Item.Kind == "fan").ToArray());
        dashboardHistory.Add(readings);
        var options = readings.Select(r => new DashboardTrendOption(r.Key, r.Name + " / " + r.Unit, r.Unit)).ToArray();
        var key = DashboardTrend?.Key; Reconcile(DashboardTrendOptions, options);
        DashboardTrend = DashboardTrendOptions.FirstOrDefault(o => o.Key == key) ?? DashboardTrendOptions.FirstOrDefault();
        var all = DashboardDisplay.Items(core.Settings.Dashboard, DashboardDisplay.Catalog(core.Settings, state, board));
        foreach (var item in all)
            if (DashboardChoices.All(c => c.Key != item.Key)) DashboardChoices.Add(new(item));
        var allReadings = DashboardDisplay.ReadAll(core.Settings with { Dashboard = new(all.Select(i => i with { Visible = true }).ToArray()) }, state, board, host.BoardFans.States);
        Reconcile(NzxtFanReadings, allReadings.Where(r => r.Item.Source == "nzxt").ToArray());
        foreach (var choice in DashboardChoices)
            if (allReadings.FirstOrDefault(r => r.Key == choice.Key) is { } reading) choice.Update(reading);
        Notify(nameof(DashboardTrendSamples)); Notify(nameof(DashboardTrendNow));
        Notify(nameof(DashboardEmptyTemperatures)); Notify(nameof(DashboardEmptyFans));
        Notify(nameof(DashboardHubStatus)); Notify(nameof(DashboardBoardStatus)); Notify(nameof(HubControlReadiness));
    }
    private static void Reconcile<T>(ObservableCollection<T> collection, T[] rows)
    {
        for (var i = 0; i < rows.Length; i++)
            if (i >= collection.Count) collection.Add(rows[i]);
            else if (!Equals(collection[i], rows[i])) collection[i] = rows[i];
        while (collection.Count > rows.Length) collection.RemoveAt(collection.Count - 1);
    }
}
