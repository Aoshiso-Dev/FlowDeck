using System.Collections.ObjectModel;
using System.Windows.Input;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed class DeviceMonitoringRow(DeviceMonitoringCandidate candidate, Action<DeviceMonitoringRow, bool> select) : Observable
{
    private bool updating, selected, canSelectMore = true;
    public DeviceMonitoringCandidate Candidate { get; private set; } = candidate;
    public string Key => DeviceMonitoringDraft.Key(Candidate);
    public string Name => Candidate.Selection.Item.Name;
    public string Source => Candidate.DeviceName;
    public bool CanSelect => selected || Candidate.Available && canSelectMore;
    public bool Selected { get => selected; set { if (!updating && value != selected) select(this, value); } }
    public string Value => Candidate.Value is { } value ? value.ToString(Candidate.Selection.Item.Measurement == DeviceFeature.Temperature ? "F1" : "N0") +
        (Candidate.Selection.Item.Measurement == DeviceFeature.Temperature ? " °C" : " RPM") : Candidate.Status;
    public void Update(DeviceMonitoringCandidate next, bool isSelected, bool canAdd = true)
    { updating = true; try { Candidate = next; selected = isSelected; canSelectMore = canAdd; Refresh(); } finally { updating = false; } }
}

public sealed partial class DeviceWorkspaceViewModel
{
    public DeviceMonitoringDraft MonitoringDraft { get; } = new();
    public ObservableCollection<DeviceMonitoringRow> MonitoringCandidates { get; } = [];
    private bool monitoringBusy;
    public bool HasMonitoringCandidates => MonitoringDraft.CandidateCount != 0;
    public bool HasMonitoringPages => MonitoringDraft.PageCount > 1;
    public string MonitoringPageLabel => $"{MonitoringDraft.Page + 1} / {MonitoringDraft.PageCount}ページ · {MonitoringDraft.CandidateCount}項目";
    public ICommand PreviousMonitoringPage => new AsyncCommand(_ => { MonitoringDraft.MovePage(-1); Render(); return Task.CompletedTask; },
        _ => MonitoringIdle && MonitoringDraft.Page > 0);
    public ICommand NextMonitoringPage => new AsyncCommand(_ => { MonitoringDraft.MovePage(1); Render(); return Task.CompletedTask; },
        _ => MonitoringIdle && MonitoringDraft.Page + 1 < MonitoringDraft.PageCount);
    public bool MonitoringIdle => !monitoringBusy && !dashboardBusy && !setupBusy && !closing && client.Snapshot is { Connected: true, ExitAttempted: false };
    public bool CanRegisterMonitoring => MonitoringIdle && client.SupportsMonitoringRegistration && MonitoringDraft.CanRegister;
    public string MonitoringStatus => monitoringBusy ? "追加中" : !client.SupportsMonitoringRegistration ? "サービスの更新が必要" :
        MonitoringDraft.Conflict ? "構成が変わりました。再取得してください" :
        $"登録 {MonitoringDraft.RegisteredCount}/{DeviceDashboard.MaximumSavedItems} · 選択 {MonitoringDraft.SelectedCount}" +
        (MonitoringDraft.RemainingSlots == 0 ? " · 登録上限" : "");
    public ICommand RegisterMonitoring => new AsyncCommand(_ => RegisterMonitoringAsync(), _ => CanRegisterMonitoring, e => Notice = e.Message);
    public ICommand ReloadMonitoring => new AsyncCommand(_ => ReloadMonitoringAsync(), _ => MonitoringIdle, e => Notice = e.Message);
    private void RenderMonitoring(DeviceWorkspaceSnapshot snapshot)
    {
        var config = snapshot.Configuration.Configuration;
        MonitoringDraft.Update(config.Devices, config.Dashboard, snapshot.State, snapshot.Configuration.Generation, snapshot.ElapsedSeconds, snapshot.Connected);
        var existing = MonitoringCandidates.ToDictionary(r => r.Key);
        var rows = MonitoringDraft.VisibleCandidates.Select(candidate =>
        {
            var row = existing.GetValueOrDefault(DeviceMonitoringDraft.Key(candidate)) ?? new DeviceMonitoringRow(candidate, SelectMonitoring);
            row.Update(candidate, MonitoringDraft.IsSelected(candidate), MonitoringDraft.CanSelectMore); return row;
        }).ToArray();
        Reconcile(MonitoringCandidates, rows);
        Notify(nameof(HasMonitoringCandidates)); Notify(nameof(MonitoringIdle)); Notify(nameof(CanRegisterMonitoring)); Notify(nameof(MonitoringStatus));
        Notify(nameof(HasMonitoringPages)); Notify(nameof(MonitoringPageLabel));
    }
    private void SelectMonitoring(DeviceMonitoringRow row, bool value)
    {
        try { MonitoringDraft.Select(row.Candidate, value); }
        catch (Exception e) { Notice = e.Message; }
        Render();
    }
    public async Task RegisterMonitoringAsync()
    {
        if (!CanRegisterMonitoring) return;
        Render(); var edit = MonitoringDraft.Capture(); monitoringBusy = true; Render();
        try { var result = await client.RegisterMonitoringAsync(edit); MonitoringDraft.Reset(); Notice = result.Message; }
        finally { monitoringBusy = false; Render(); }
    }
    public async Task ReloadMonitoringAsync()
    {
        if (!MonitoringIdle) return; monitoringBusy = true; Render();
        try { await client.RefreshAsync(true); MonitoringDraft.Reset(); }
        finally { monitoringBusy = false; Render(); }
    }
}
