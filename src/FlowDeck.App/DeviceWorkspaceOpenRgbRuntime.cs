using System.Globalization;
using System.Windows.Input;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class DeviceWorkspaceViewModel
{
    private bool openRgbRuntimeBusy;
    private DeviceOpenRgbRuntimeInspection? openRgbRuntime;
    private string? openRgbRuntimeGeneration, openRgbRuntimeSession, openRgbRuntimeError, openRgbRuntimeCheckedAt;
    public bool CanInspectOpenRgbRuntime => OpenRgbIdle && client.SupportsOpenRgbRuntimeInspection &&
        client.Snapshot is { Connected: true, ExitAttempted: false, State: { Releasing: false, ConfigurationChanging: false } };
    public string OpenRgbRuntimeStatus => client.Snapshot?.Connected != true ? "サービス未接続" :
        !client.SupportsOpenRgbRuntimeInspection ? "サービス未対応" : openRgbRuntimeBusy ? "確認中…" : openRgbRuntimeError ??
        (openRgbRuntime?.Status switch
        {
            OpenRgbRuntimeInspectionStatus.NotInstalled => "未導入",
            OpenRgbRuntimeInspectionStatus.AccessDenied => "読み取り権限なし",
            OpenRgbRuntimeInspectionStatus.Invalid => "導入状態の確認失敗",
            OpenRgbRuntimeInspectionStatus.Verified => openRgbRuntime.StartupAccepted ? "導入済み" : "導入済み・連携は検証待ち",
            _ => "未確認"
        });
    public string OpenRgbRuntimeVersion => openRgbRuntime is null ? "" : "必要な専用版 " + openRgbRuntime.ExpectedVersion;
    public string OpenRgbRuntimeCheckedAt => openRgbRuntimeCheckedAt is null ? "" : "前回確認 " + openRgbRuntimeCheckedAt;
    public ICommand InspectOpenRgbRuntime => new AsyncCommand(_ => InspectOpenRgbRuntimeAsync(), _ => CanInspectOpenRgbRuntime,
        _ => { openRgbRuntimeError = "導入状態を確認できません"; PublishOpenRgb(); });
    private void ClearOpenRgbRuntime()
    {
        openRgbRuntime = null; openRgbRuntimeGeneration = null; openRgbRuntimeSession = null;
        openRgbRuntimeError = null; openRgbRuntimeCheckedAt = null;
    }
    private void PublishOpenRgbRuntime()
    {
        foreach (var property in new[] { nameof(CanInspectOpenRgbRuntime), nameof(OpenRgbRuntimeStatus),
            nameof(OpenRgbRuntimeVersion), nameof(OpenRgbRuntimeCheckedAt) }) Notify(property);
    }
    private void RenderOpenRgbRuntime(DeviceWorkspaceSnapshot snapshot)
    {
        if (openRgbRuntimeGeneration is not null && (!snapshot.Connected || snapshot.Configuration.Generation != openRgbRuntimeGeneration ||
            snapshot.Session != openRgbRuntimeSession)) ClearOpenRgbRuntime();
        PublishOpenRgbRuntime();
    }
    public async Task InspectOpenRgbRuntimeAsync()
    {
        if (!CanInspectOpenRgbRuntime) return;
        var before = client.Snapshot!; openRgbRuntimeBusy = true; ClearOpenRgbRuntime(); PublishOpenRgb(); PublishProviderRoutes();
        try
        {
            var result = await client.InspectOpenRgbRuntimeAsync();
            var after = client.Snapshot;
            if (closing || after?.Connected != true || after.Session != before.Session ||
                after.Configuration.Generation != before.Configuration.Generation) return;
            openRgbRuntime = result; openRgbRuntimeGeneration = after.Configuration.Generation; openRgbRuntimeSession = after.Session;
            openRgbRuntimeCheckedAt = DateTime.Now.ToString("HH:mm:ss",CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            if (!closing && client.Snapshot is { Connected: true } after && after.Session == before.Session &&
                after.Configuration.Generation == before.Configuration.Generation)
            {
                openRgbRuntimeError = "導入状態を確認できません。再確認してください";
                openRgbRuntimeGeneration = after.Configuration.Generation; openRgbRuntimeSession = after.Session;
            }
        }
        finally { openRgbRuntimeBusy = false; PublishOpenRgb(); PublishProviderRoutes(); }
    }
}
