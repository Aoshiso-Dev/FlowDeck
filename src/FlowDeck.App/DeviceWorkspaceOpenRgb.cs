using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class DeviceWorkspaceViewModel
{
    private string openRgbPort = "6742";
    private bool openRgbInitialized, openRgbExpanded, openRgbBusy;
    private string? openRgbGeneration, openRgbSession, openRgbMessage, openRgbCheckedAt;
    public ObservableCollection<OpenRgbInspectionItem> OpenRgbItems { get; } = [];
    public bool OpenRgbExpanded { get => openRgbExpanded; set => Set(ref openRgbExpanded, value); }
    public bool OpenRgbIdle => !openRgbBusy && !openRgbRuntimeBusy && !setupBusy && !closing;
    public string OpenRgbPort
    {
        get => openRgbPort;
        set { if (openRgbPort == value) return; Set(ref openRgbPort, value); ClearOpenRgb(); providerRoutePreview = null; providerSelectedDevices.Clear(); PublishOpenRgb(); PublishProviderRoutes(); }
    }
    private bool ReadOpenRgbPort(out int port) => int.TryParse(openRgbPort, NumberStyles.None, CultureInfo.InvariantCulture, out port) && DeviceOpenRgbInspection.PortValid(port);
    public bool CanInspectOpenRgb => OpenRgbIdle && !setupBusy && client.SupportsOpenRgbInspection &&
        client.Snapshot is { Connected: true, ExitAttempted: false, State: { Releasing: false, ConfigurationChanging: false } } && ReadOpenRgbPort(out _);
    public string OpenRgbStatus => openRgbBusy ? "確認中…" : !client.SupportsOpenRgbInspection ? "サービス未対応" :
        client.Snapshot?.Connected != true ? "サービス未接続" : !ReadOpenRgbPort(out _) ? "ポート：1024〜65535" : openRgbMessage ?? "未確認";
    public string OpenRgbCheckedAt => openRgbCheckedAt is null ? "" : "前回確認 " + openRgbCheckedAt;
    public ICommand InspectOpenRgb => new AsyncCommand(_ => InspectOpenRgbAsync(), _ => CanInspectOpenRgb, e => { openRgbMessage = e.Message; PublishOpenRgb(); });
    private void ClearOpenRgb()
    {
        OpenRgbItems.Clear(); openRgbMessage = null; openRgbCheckedAt = null; openRgbGeneration = null; openRgbSession = null;
    }
    private void PublishOpenRgb()
    {
        Notify(nameof(OpenRgbStatus)); Notify(nameof(OpenRgbCheckedAt)); Notify(nameof(OpenRgbIdle)); Notify(nameof(CanInspectOpenRgb));
        PublishOpenRgbRuntime();
        CommandManager.InvalidateRequerySuggested();
    }
    private void RenderOpenRgb(DeviceWorkspaceSnapshot snapshot)
    {
        RenderOpenRgbRuntime(snapshot);
        if (!openRgbInitialized)
        {
            openRgbInitialized = true; openRgbPort = (snapshot.Configuration.Configuration.Preferences.Providers?.OpenRgbPort ?? 6742).ToString(CultureInfo.InvariantCulture);
            Notify(nameof(OpenRgbPort));
        }
        if (openRgbGeneration is not null && (!snapshot.Connected || snapshot.Configuration.Generation != openRgbGeneration || snapshot.Session != openRgbSession)) ClearOpenRgb();
        PublishOpenRgb();
        RenderProviderRoutes(snapshot);
    }
    public async Task InspectOpenRgbAsync()
    {
        if (!CanInspectOpenRgb || !ReadOpenRgbPort(out var port)) return;
        var before = client.Snapshot!; var draft = openRgbPort;
        openRgbBusy = true; ClearOpenRgb(); PublishOpenRgb();
        try
        {
            var result = await client.InspectOpenRgbAsync(port);
            var after = client.Snapshot;
            if (closing || draft != openRgbPort || after?.Connected != true || before.Session != after.Session ||
                before.Configuration.Generation != after.Configuration.Generation) return;
            Reconcile(OpenRgbItems, result.Items); openRgbMessage = result.Message;
            openRgbGeneration = after.Configuration.Generation; openRgbSession = after.Session;
            openRgbCheckedAt = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (Exception e)
        {
            if (!closing && draft == openRgbPort) openRgbMessage = e.Message;
        }
        finally { openRgbBusy = false; PublishOpenRgb(); }
    }
}
