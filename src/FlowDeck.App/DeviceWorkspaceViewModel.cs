using System.Collections.ObjectModel;
using System.Windows.Input;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed record DeviceProfileChoice(string Id, string Name);
public sealed partial class DeviceCoolingRow(EndpointReference target) : Observable
{
    private bool updating;
    private string managementMode = "機器管理";
    public DeviceCoolingDraft Draft { get; } = new(target);
    public string Name { get; private set; } = "";
    public string Connection { get; private set; } = "";
    public DashboardReading? Reading { get; private set; }
    public bool Busy { get; private set; }
    public bool CanApply => !Busy && Draft.CanApply;
    public bool HasPendingChanges => Draft.Dirty;
    public bool ModeChanged => Draft.ModeChanged;
    public bool DutyChanged => Draft.DutyChanged;
    public bool ProfileChanged => Draft.ProfileChanged;
    public bool CanRevert => !Busy && (Draft.Dirty || Draft.Conflict);
    public bool CanRelease => !Busy && Draft.CanRelease;
    public bool SupportsRelease => Draft.SupportsRelease;
    public bool CanResume => !Busy && Draft.CanResume;
    public bool CanSelectMode => !Busy && (Draft.Available || Draft.CanRelease);
    public bool CanEdit => !Busy && Draft.Available && !Draft.DeviceManaged;
    public bool NeedsSetup => Draft.NeedsSetup;
    public int Minimum => Draft.Minimum;
    public int Duty { get => Draft.Duty; set { if (updating) return; Draft.SetDuty(value); Refresh(); } }
    public string Mode { get => Draft.DeviceManaged ? managementMode : Draft.Automatic ? "自動" : "固定"; set { if (updating || value is null) return; if (value == managementMode) Draft.SelectDeviceManagement(); else Draft.SetAutomatic(value == "自動"); Refresh(); } }
    public ObservableCollection<string> Modes { get; } = ["機器管理", "固定", "自動"];
    public bool Automatic => Draft.Automatic;
    public bool Fixed => !Draft.Automatic && !Draft.DeviceManaged;
    public bool UnknownManagedDuty => Draft.DeviceManaged && Reading?.Duty is null;
    public string ReleaseLabel => managementMode == "制御終了" ? "制御終了" : managementMode + "へ戻す";
    public string? ProfileId { get => Draft.ProfileId; set { if (updating) return; Draft.SelectProfile(value); Refresh(); } }
    public ObservableCollection<DeviceProfileChoice> Profiles { get; } = [];
    public string Status => Busy ? "適用中" : Draft.Conflict ? "別の操作で変更あり" : Draft.Problem ??
        (!Draft.DeviceManaged && !Draft.Automatic && Draft.Duty < Draft.Minimum ? $"最低 {Draft.Minimum}%" : Draft.Dirty ? "未適用" : Draft.DeviceManaged ? managementMode : Reading?.Control ?? "監視のみ");
    public string CurrentDuty => Reading?.DisplayDuty ?? "—";
    public string Message { get; private set; } = "";
    public void Update(DeviceWorkspaceSnapshot snapshot)
    {
        updating = true;
        try
        {
        var config = snapshot.Configuration.Configuration;
        var device = snapshot.State.Inventory.Devices.SingleOrDefault(d => d.Device.Id == Draft.Target.DeviceId);
        var registered = config.Devices.Devices.SingleOrDefault(d => d.Id == Draft.Target.DeviceId);
        var endpoint = device?.Device.Endpoints.SingleOrDefault(e => e.Id == Draft.Target.EndpointId);
        var saved = registered?.Endpoints.SingleOrDefault(e => e.Id == Draft.Target.EndpointId);
        Name = saved?.Name ?? endpoint?.Name ?? Draft.Target.EndpointId;
        Connection = registered?.Name ?? device?.Device.Name ?? "未接続の機器";
        var display = config.Dashboard.Items.SingleOrDefault(i => i.Target == Draft.Target && i.Measurement == DeviceFeature.Rpm) ??
            new(Draft.Target, DeviceFeature.Rpm, Name, saved?.Role == "pump" || endpoint?.Role == "pump" ? "pump" : "fan");
        Reading = DeviceDashboard.Read(display, config.Devices, snapshot.State, snapshot.ElapsedSeconds, snapshot.Connected);
        var setup = config.Cooling.Endpoints.SingleOrDefault(e => e.Target == Draft.Target);
        managementMode = setup?.Safety.ExitAction == CoolingExitAction.FixedDuty ? "制御終了" : device?.Device.Kind == DeviceKind.Motherboard ? "マザー管理" : "機器管理";
        Draft.Update(snapshot.State, snapshot.Configuration.Generation, setup, snapshot.ElapsedSeconds, snapshot.Connected, config.Bindings);
        if (!Draft.SupportsRelease) managementMode = "監視のみ";
        var modes = new List<string>();
        if (Draft.SupportsRelease || Draft.DeviceManaged) modes.Add(managementMode);
        if (endpoint?.Capabilities.Any(c => c.Feature == DeviceFeature.FixedDuty && c.Support == ImplementationSupport.Supported) == true) modes.Add("固定");
        if (endpoint?.Capabilities.Any(c => c.Feature == DeviceFeature.AutomaticDuty && c.Support == ImplementationSupport.Supported) == true) modes.Add("自動");
        // Keep a disconnected or now-unsupported draft visible without making it executable.
        if (Draft.Dirty && !modes.Contains(Mode)) modes.Add(Mode);
        DeviceWorkspaceViewModel.Reconcile(Modes, modes.ToArray());
        DeviceWorkspaceViewModel.Reconcile(Profiles, Draft.Profiles.Select(p => new DeviceProfileChoice(p.Id, p.Name)).ToArray());
        Refresh();
        }
        finally { updating = false; }
    }
    public void SetBusy(bool value) { Busy = value; Refresh(); }
    public void SetResult(DeviceCoolingEdit edit, long editRevision, DeviceOperationResult result)
    {
        Message = string.Join(" / ", new[] { result.Message }.Concat(result.Targets.Where(t => t.Persistence is { State: not DevicePolicySaveState.Saved }).Select(t => t.Persistence!.Message)));
        Draft.Complete(edit, editRevision, result);
        Refresh();
    }
    public void Reset() { Draft.Reset(); Message = ""; Refresh(); }
}

/// <summary>Service-only view model; all collections retain row instances through telemetry polling.</summary>
public sealed partial class DeviceWorkspaceViewModel(DeviceWorkspaceClient client, DeviceUiPreferencesStore? uiStore = null, DeviceLoginStartup? loginStartup = null) : Observable
{
    public ObservableCollection<DashboardReading> DashboardTemperatures { get; } = [];
    public ObservableCollection<DashboardReading> DashboardFans { get; } = [];
    public ObservableCollection<DeviceCoolingRow> Cooling { get; } = [];
    public ObservableCollection<DeviceCoolingRow> VisibleCooling { get; } = [];
    public ObservableCollection<DeviceCatalogRow> Devices { get; } = [];
    public ObservableCollection<DashboardTrendOption> DashboardTrendOptions { get; } = [];
    private readonly DashboardHistory history = new();
    private DashboardTrendOption? trend;
    private string notice = "", theme = "system";
    private int selectedPage;
    private bool refreshing, closing;
    private bool reconnecting, reconnectAttempted, showReconnect;
    private string connectionState = "接続待ち", deviceCountLabel = "接続機器 —";
    private long reconnectAfter;
    public string EnvironmentLabel => client.IsDemo ? "DEMO" : "";
    public string ConnectionLabel => (client.IsDemo ? "共通サービス · デモ機器" : "Windowsサービス") + "：" + connectionState;
    public string DeviceCountLabel => deviceCountLabel;
    public bool ShowReconnect => showReconnect;
    public ICommand Reconnect => new AsyncCommand(_ => ReconnectAsync(), _ => client.NeedsReconnect && !refreshing && !closing, e => ReportConnectionFailure(e.Message));
    public int SelectedPage { get => selectedPage; set { if (value is >= 0 and <= 3 && Set(ref selectedPage, value)) RememberWorkspacePage(); } }
    public string Notice { get => notice; set => SetNotice(value); }
    public string Status { get; private set; } = "接続待ち";
    public string[] Themes { get; } = ["system", "light", "dark"];
    public DeviceProfileChoice[] ThemeChoices { get; } = [new("system", "システム"), new("light", "ライト"), new("dark", "ダーク")];
    public string Theme { get => theme; set {
        if (!Themes.Contains(value) || !Set(ref theme, value)) return;
        ThemeManager.Apply(value, AccentColor); ui = ui with { Theme = value }; QueueUiSave(); } }
    public DashboardTrendOption? DashboardTrend { get => trend; set { Set(ref trend, value); Notify(nameof(DashboardTrendSamples)); Notify(nameof(DashboardTrendUnit)); } }
    public DashboardSample[] DashboardTrendSamples => history.Get(DashboardTrend?.Key);
    public string DashboardTrendUnit => DashboardTrend?.Unit ?? "°C";
    public double DashboardTrendNow => history.Now;
    public ICommand Navigate => new AsyncCommand(p => { SelectedPage = int.Parse(p!.ToString()!); return Task.CompletedTask; });
    public ICommand Apply => new AsyncCommand(p => OperateAsync((DeviceCoolingRow)p!), p => p is DeviceCoolingRow { CanApply: true } && !closing && !setupBusy, e => Notice = e.Message);
    public ICommand Release => new AsyncCommand(p => OperateAsync((DeviceCoolingRow)p!, DeviceOperation.ReleaseCooling), p => p is DeviceCoolingRow { CanRelease: true } && !closing && !setupBusy, e => Notice = e.Message);
    public ICommand Resume => new AsyncCommand(p => OperateAsync((DeviceCoolingRow)p!, DeviceOperation.ResumeCooling), p => p is DeviceCoolingRow { CanResume: true } && !closing && !setupBusy, e => Notice = e.Message);
    public ICommand Revert => new AsyncCommand(p => { ((DeviceCoolingRow)p!).Reset(); return Task.CompletedTask; }, p => p is DeviceCoolingRow { CanRevert: true });
    public async Task InitializeAsync()
    {
        if (client.Snapshot?.Connected != true) await client.ConnectAsync();
        LightSavedColors = new(SaveLightColorsAsync); Notify(nameof(LightSavedColors));
        ui = uiStore?.Load(client.Snapshot!.Configuration.Configuration.Preferences.Ui) ?? client.Snapshot!.Configuration.Configuration.Preferences.Ui;
        LoadWorkspaceUi();
        theme = ui.Theme; accentColor = uiStore?.AccentColor ?? DeviceAccentColor.Default;
        Notify(nameof(Theme)); Notify(nameof(AccentChoices)); Notify(nameof(AccentColor)); ThemeManager.Apply(theme, AccentColor);
        if (uiStore?.LoadWarning is { } warning) Notice = warning;
        Render();
        await ReloadLoginAsync();
        await PollNotificationsAsync();
    }
    public async Task PollAsync()
    {
        Render(); // Client-side aging continues even while another state request is awaiting its response.
        if (refreshing || closing || client.Snapshot?.ExitAttempted == true) return;
        refreshing = true;
        try
        {
            if (client.NeedsReconnect)
            {
                if (Environment.TickCount64 < reconnectAfter) return;
                reconnectAfter = Environment.TickCount64 + 5000;
                await ReconnectClientAsync(automatic: true);
            }
            else await client.RefreshAsync();
            await PollNotificationsAsync();
        }
        catch (Exception e) { if (client.NeedsReconnect) ReportConnectionFailure(e.Message); else Notice = e.Message; }
        finally { refreshing = false; Render(); }
    }
    public async Task ReconnectAsync()
    {
        if (refreshing || closing) return; refreshing = true;
        try { await ReconnectClientAsync(automatic: false); await PollNotificationsAsync(); }
        finally { reconnectAfter = Environment.TickCount64 + 5000; refreshing = false; Render(); }
    }
    private async Task ReconnectClientAsync(bool automatic)
    {
        // A normal telemetry poll is also refreshing; only this operation means reconnecting.
        reconnecting = true; reconnectAttempted = true; PublishConnectionHeader(client.Snapshot);
        using var status = BeginStatus("サービス再接続中");
        try { await client.ReconnectAsync(); ReportConnectionRestored(automatic); }
        finally { reconnecting = false; PublishConnectionHeader(client.Snapshot); }
    }
    private void PublishConnectionHeader(DeviceWorkspaceSnapshot? snapshot)
    {
        var awaitingFirstConnection = snapshot is null && !reconnectAttempted;
        var nextState = closing ? "終了処理中" : snapshot?.ExitAttempted == true ? "終了処理待ち" :
            reconnecting ? "再接続中" : snapshot?.Connected == true ? "接続中" :
            awaitingFirstConnection ? "接続待ち" : client.NeedsReconnect ? "未接続" : "状態取得失敗";
        Set(ref connectionState, nextState, nameof(ConnectionLabel));
        Set(ref deviceCountLabel, snapshot?.Connected == true ?
            $"接続機器 {snapshot.State.Inventory.Devices.Count(d => d.Connected)}台" : "接続機器 —", nameof(DeviceCountLabel));
        Set(ref showReconnect, !closing && snapshot?.ExitAttempted != true &&
            (reconnecting || !awaitingFirstConnection && client.NeedsReconnect), nameof(ShowReconnect));
        CommandManager.InvalidateRequerySuggested();
    }
    public void Render()
    {
        var snapshot = client.Snapshot;
        PublishConnectionHeader(snapshot);
        if (snapshot is null) return;
        var config = snapshot.Configuration.Configuration;
        Status = snapshot.ExitAttempted ? "終了処理の再試行が必要" : snapshot.Connected ? $"接続中 {snapshot.State.Inventory.Devices.Count(d => d.Connected)}" : "サービス未接続";
        var readings = DeviceDashboard.VisibleItems(config.Devices, config.Dashboard, snapshot.State, snapshot.ElapsedSeconds, snapshot.Connected)
            .Select(i => DeviceDashboard.Read(i, config.Devices, snapshot.State, snapshot.ElapsedSeconds, snapshot.Connected)).ToArray();
        Reconcile(DashboardTemperatures, readings.Where(r => r.Item.Kind == "temperature").ToArray());
        Reconcile(DashboardFans, readings.Where(r => r.Item.Kind == "fan").ToArray());
        history.Add(readings);
        var key = DashboardTrend?.Key;
        Reconcile(DashboardTrendOptions, readings.Select(r => new DashboardTrendOption(r.Key, r.Name + " / " + r.Connection, r.Unit)).ToArray());
        DashboardTrend = DashboardTrendOptions.FirstOrDefault(r => r.Key == key) ?? DashboardTrendOptions.FirstOrDefault();
        var targets = config.Devices.Devices.SelectMany(d => d.Endpoints.Where(e => e.Kind == EndpointKind.Cooling).Select(e => new EndpointReference(d.Id, e.Id)))
            .Concat(snapshot.State.Inventory.Devices.SelectMany(d => d.Device.Endpoints.Where(e => e.Kind == EndpointKind.Cooling).Select(e => new EndpointReference(d.Device.Id, e.Id))))
            .Distinct().ToArray();
        foreach (var old in Cooling.Where(r => !targets.Contains(r.Draft.Target) && !r.Draft.Dirty && !r.Busy && !r.HasSettingsEditor).ToArray()) Cooling.Remove(old);
        foreach (var target in targets) if (Cooling.All(r => r.Draft.Target != target)) Cooling.Add(new(target));
        foreach (var row in Cooling) row.Update(snapshot);
        // Keep pending edits and in-flight operations in Cooling while hiding absent hardware.
        Reconcile(VisibleCooling, Cooling.Where(r => snapshot.Connected && snapshot.State.Inventory.Devices.Any(d =>
            d.Connected && d.Device.Id == r.Draft.Target.DeviceId &&
            d.Device.Endpoints.Any(e => e.Id == r.Draft.Target.EndpointId && e.Kind == EndpointKind.Cooling))).ToArray());
        var catalog = DeviceCatalogPresentation.Build(snapshot);
        foreach (var old in Devices.Where(d => !catalog.Any(c => c.Id == d.Id)).ToArray()) Devices.Remove(old);
        foreach (var device in catalog)
        {
            var row = Devices.FirstOrDefault(r => r.Id == device.Id);
            if (row is null) { row = new(device.Id); Devices.Add(row); }
            row.Update(device);
        }
        RenderLighting(snapshot);
        RestoreWorkspaceUi(snapshot);
        RenderDashboardSettings(snapshot);
        RenderDeviceNames(snapshot);
        RenderCacheHistory(snapshot);
        RenderMonitoring(snapshot);
        RenderSetup(snapshot);
        RenderCalibration(snapshot);
        RenderTransfer(snapshot);
        RenderStartup(snapshot);
        RenderCoolingGroups(snapshot);
        RenderNotifications(snapshot);
        RenderDialDisplay(snapshot);
        RenderOpenRgb(snapshot);
        RenderPersistentWarnings(snapshot);
        PublishDiagnostics();
        PublishLogin();
        foreach (var row in Cooling) row.SettingsEditor?.Update(snapshot,!SetupIdle);
        Notify(nameof(Status)); Notify(nameof(DashboardTrendSamples)); Notify(nameof(DashboardTrendNow)); Notify(nameof(AutomaticMonitoring));
        CommandManager.InvalidateRequerySuggested();
    }
    public async Task OperateAsync(DeviceCoolingRow row, DeviceOperation? kind = null)
    {
        if (row.Busy || closing || setupBusy) return;
        Render(); var edit = row.Draft.Capture(kind); var editRevision = row.Draft.EditRevision; row.SetBusy(true);
        using var status = BeginStatus("冷却制御の結果を確認中");
        try
        {
            var result = await client.ApplyCoolingAsync(edit); Render(); row.SetResult(edit, editRevision, result); ReportResult(result);
        }
        finally { row.SetBusy(false); Render(); }
    }
    public async Task<bool> CloseAsync()
    {
        if (closing) return false; closing = true;
        PublishConnectionHeader(client.Snapshot);
        try
        {
            await DisconnectDialAsync();
            var reply = await client.ReleaseAsync();
            if (!reply.Success) { Notice = reply.Error ?? "終了処理未確認"; closing = false; Render(); return false; }
            return true;
        }
        catch (Exception e) { Notice = e.Message; closing = false; Render(); return false; }
    }
    internal static void Reconcile<T>(ObservableCollection<T> collection, T[] values)
    {
        for (var i = 0; i < values.Length; i++)
            if (i >= collection.Count) collection.Add(values[i]); else if (!Equals(collection[i], values[i])) collection[i] = values[i];
        while (collection.Count > values.Length) collection.RemoveAt(collection.Count - 1);
    }
}
