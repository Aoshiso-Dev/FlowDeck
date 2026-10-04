using System.Collections.ObjectModel;
using System.Windows.Input;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed class DeviceDashboardRow(DeviceDashboardItem item, Action<DeviceDashboardItem> changed) : Observable
{
    private bool updating;
    public DeviceDashboardItem Item { get; private set; } = item;
    public string Name { get => Item.Name; set => Edit(Item with { Name = value }); }
    public bool Visible { get => Item.Visible; set => Edit(Item with { Visible = value }); }
    public string Role { get => Item.Role == "temperature" ? "温度" : Item.Role == "pump" ? "ポンプ" : "ファン";
        set { if (value is not null) Edit(Item with { Role = value == "温度" ? "temperature" : value == "ポンプ" ? "pump" : "fan" }); } }
    public string[] Roles => Item.Measurement == DeviceFeature.Temperature ? ["温度"] : ["ファン", "ポンプ"];
    public bool CanChooseRole => Item.Measurement == DeviceFeature.Rpm;
    public string[] Indicators { get; } = ["円形", "バー", "数値"];
    public string Indicator { get => (Item.Indicator ?? (Item.Measurement == DeviceFeature.Temperature ? "circle" : "bar")) switch {
        "circle" => "円形", "bar" => "バー", _ => "数値" }; set { if (value is not null) Edit(Item with { Indicator = value switch { "円形" => "circle", "バー" => "bar", _ => "number" } }); } }
    public bool AutomaticRange { get => Item.DisplayMaximum is null; set => Edit(Item with { DisplayMaximum = value ? null : Maximum }); }
    public bool CanSetMaximum => !AutomaticRange;
    public double Maximum { get => Item.DisplayMaximum ?? (Item.Measurement == DeviceFeature.Temperature ? 100 : Item.Role == "pump" ? 6000 : 3000);
        set { if (!AutomaticRange) Edit(Item with { DisplayMaximum = Math.Round(value) }); } }
    public double SliderMaximum => Math.Max(Item.Measurement == DeviceFeature.Temperature ? 150 : 20000, Maximum);
    public string MaximumLabel => Maximum.ToString("N0") + (Item.Measurement == DeviceFeature.Temperature ? " °C" : " rpm");
    public string Source { get; private set; } = "";
    private void Edit(DeviceDashboardItem next)
    {
        if (updating || next == Item) return;
        Item = next; changed(next); Publish();
    }
    private void Publish()
    {
        updating = true;
        try
        {
            Notify(nameof(Name)); Notify(nameof(Visible)); Notify(nameof(Role)); Notify(nameof(Indicator));
            Notify(nameof(AutomaticRange)); Notify(nameof(CanSetMaximum)); Notify(nameof(SliderMaximum)); Notify(nameof(Maximum)); Notify(nameof(MaximumLabel));
        }
        finally { updating = false; }
    }
    public void Update(DeviceDashboardItem next, string source)
    {
        var previous = Item; Item = next;
        if (previous != next) Publish();
        if (Source != source) { Source = source; Notify(nameof(Source)); }
    }
}

public sealed partial class DeviceWorkspaceViewModel
{
    public DeviceDashboardDraft DashboardDraft { get; } = new();
    public ObservableCollection<DeviceDashboardRow> DashboardSettings { get; } = [];
    private bool dashboardBusy;
    public bool DashboardSettingsIdle => !dashboardBusy && !setupBusy && !closing && client.Snapshot is { Connected: true, ExitAttempted: false };
    public bool CanSaveDashboard => DashboardSettingsIdle && DashboardDraft.CanSave;
    public string DashboardEditStatus => dashboardBusy ? "保存中" : DashboardDraft.Conflict ? "別の操作で変更あり" :
        DashboardDraft.Dirty ? "未保存" : "保存済み";
    public ICommand SaveDashboard => new AsyncCommand(_ => SaveDashboardAsync(), _ => CanSaveDashboard, e => Notice = e.Message);
    public ICommand ReloadDashboard => new AsyncCommand(_ => ReloadDashboardAsync(), _ => DashboardSettingsIdle, e => Notice = e.Message);
    public ICommand MoveDashboardUp => new AsyncCommand(p => { MoveDashboard((DeviceDashboardRow)p!, -1); return Task.CompletedTask; },
        p => DashboardSettingsIdle && p is DeviceDashboardRow row && DashboardSettings.IndexOf(row) > 0);
    public ICommand MoveDashboardDown => new AsyncCommand(p => { MoveDashboard((DeviceDashboardRow)p!, 1); return Task.CompletedTask; },
        p => DashboardSettingsIdle && p is DeviceDashboardRow row && DashboardSettings.Contains(row) && DashboardSettings.IndexOf(row) < DashboardSettings.Count - 1);
    public ICommand RemoveDashboard => new AsyncCommand(p =>
    {
        var row = (DeviceDashboardRow)p!; DashboardDraft.Remove(row.Item.Target, row.Item.Measurement); Render(); return Task.CompletedTask;
    }, p => DashboardSettingsIdle && p is DeviceDashboardRow row && DashboardSettings.Contains(row));
    private void RenderDashboardSettings(DeviceWorkspaceSnapshot snapshot)
    {
        var config = snapshot.Configuration.Configuration;
        DashboardDraft.Update(config.Devices, config.Dashboard, snapshot.State, snapshot.Configuration.Generation, snapshot.Connected);
        var rows = DashboardDraft.Items.Select(item =>
        {
            var row = DashboardSettings.SingleOrDefault(r => r.Item.Target == item.Target && r.Item.Measurement == item.Measurement) ??
                new DeviceDashboardRow(item, next => { DashboardDraft.Set(next); PublishDashboardState(); });
            var registered = config.Devices.Devices.SingleOrDefault(d => d.Id == item.Target.DeviceId);
            row.Update(item, (registered?.Name ?? "未接続の機器") + " / " +
                (registered?.Endpoints.SingleOrDefault(e => e.Id == item.Target.EndpointId)?.Name ?? item.Target.EndpointId));
            return row;
        }).ToArray();
        Reconcile(DashboardSettings, rows); PublishDashboardState();
    }
    private void PublishDashboardState()
    {
        Notify(nameof(CanSaveDashboard)); Notify(nameof(DashboardSettingsIdle)); Notify(nameof(DashboardEditStatus));
        CommandManager.InvalidateRequerySuggested();
    }
    public void MoveDashboard(DeviceDashboardRow row, int direction)
    {
        if (!DashboardSettingsIdle) return;
        DashboardDraft.Move(row.Item.Target, row.Item.Measurement, direction); Render();
    }
    public async Task SaveDashboardAsync()
    {
        if (dashboardBusy || closing) return;
        Render(); var edit = DashboardDraft.Capture(); dashboardBusy = true; PublishDashboardState();
        try
        {
            var result = await client.SaveDashboardAsync(edit);
            DashboardDraft.Reset(); Render(); Notice = result.Message;
        }
        finally { dashboardBusy = false; Render(); }
    }
    public async Task ReloadDashboardAsync()
    {
        if (dashboardBusy || closing) return;
        dashboardBusy = true; PublishDashboardState();
        try { await client.RefreshAsync(true); DashboardDraft.Reset(); Render(); }
        finally { dashboardBusy = false; Render(); }
    }
}
