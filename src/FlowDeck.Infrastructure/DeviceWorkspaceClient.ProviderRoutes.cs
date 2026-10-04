using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceProviderRouteEdit(string Epoch, string Session, string Generation, long Topology, DeviceProviderPreferences Settings)
{
    public bool Matches(DeviceWorkspaceSnapshot? snapshot) => snapshot is { Connected: true, ExitAttempted: false } &&
        snapshot.Session == Session && snapshot.State.Inventory.Epoch == Epoch && snapshot.Configuration.Generation == Generation &&
        snapshot.State.Inventory.TopologyRevision == Topology;
    public static DeviceProviderRouteEdit Capture(DeviceWorkspaceSnapshot snapshot, bool enabled, int port, IEnumerable<string>? selectedDevices = null)
    {
        var ids = enabled ? (selectedDevices ?? snapshot.Configuration.Configuration.Preferences.Providers?.OpenRgbDeviceIds ?? [])
            .Take(DeviceRegistry.MaximumDevices + 1).ToArray() : [];
        return new(snapshot.State.Inventory.Epoch, snapshot.Session!, snapshot.Configuration.Generation, snapshot.State.Inventory.TopologyRevision,
            new DeviceProviderPreferences(enabled, port, ids).Normalize());
    }
}
public sealed record DeviceProviderRoutePreview(DeviceProviderRouteEdit Edit, ServiceConfigurationPreview Transfer);

public static class DeviceProviderRoutePresentation
{
    public static string Describe(DeviceProviderRouteReview? review, DeviceWorkspaceSnapshot? snapshot)
    {
        if (review is null) return "";
        string Name(string id) => snapshot?.Configuration.Configuration.Devices.Devices.FirstOrDefault(d => d.Id == id)?.Name ??
            snapshot?.State.Inventory.Devices.FirstOrDefault(d => d.Device.Id == id)?.Device.Name ?? id;
        static string List(string[] ids) => ids.Length == 0 ? "なし" : string.Join("、", ids);
        return string.Join(Environment.NewLine, ["切替対象：" + List(review.RemovingDeviceIds.Select(Name).ToArray()),
            .. review.OpenRgbDeviceIds is { } selected ? new[] { "OpenRGBの制御対象：" + List(selected.Select(Name).ToArray()) } : Array.Empty<string>(),
            "終了する接続：" + List(review.RemoveProviderIds), "開始する接続：" + List(review.AddProviderIds),
            "保持する接続：" + List(review.RetainedProviderIds),
            .. review.StopsAllControls ? new[] { "切替時：すべての冷却・照明制御を終了", "切替後：監視のみ" } :
                new[] { "制御を継続：" + List((review.ContinuingDeviceIds ?? []).Select(Name).ToArray()),
                    "切替時：対象の制御を終了", "切替後：切替対象は監視のみ" }]);
    }
}

public sealed partial class DeviceWorkspaceClient
{
    public bool SupportsProviderRouting => client.Hello?.Features.Contains(DeviceProviderRouteReview.Feature) == true;
    public bool SupportsProviderDeviceSelection => client.Hello?.Features.Contains(DeviceProviderRouteReview.SelectionFeature) == true;
    private void CheckProviderRouteEdit(DeviceProviderRouteEdit edit)
    {
        if (!SupportsProviderRouting || !edit.Matches(Snapshot)) throw new IOException("接続・設定・機器構成が変わりました。切替内容を再確認してください");
        if (!edit.Settings.Valid()) throw new IOException("ポート・選択機器が不正です");
        if (edit.Settings.OpenRgbDeviceIds is { Length: > 0 } && !SupportsProviderDeviceSelection)
            throw new IOException("このサービスでは機器別の選択に対応していません");
    }
    public async Task<DeviceProviderRoutePreview> PreviewProviderRouteAsync(DeviceProviderRouteEdit edit, CancellationToken ct = default)
    {
        edit = Contract.Copy(edit);
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen(); await ReadStateAsync(ct).ConfigureAwait(false); CheckProviderRouteEdit(edit);
            var current = configuration!.Configuration;
            if ((current.Preferences.Providers ?? new()).EquivalentTo(edit.Settings)) throw new IOException("取得経路に変更はありません");
            var next = current with { Preferences = current.Preferences with { Providers = edit.Settings } };
            var preview = await client.PreviewConfigurationAsync(edit.Generation, next, ct).ConfigureAwait(false);
            await ReadStateAsync(ct).ConfigureAwait(false); CheckProviderRouteEdit(edit);
            if (preview.ProviderRoute is not { } route || route.TopologyRevision != edit.Topology)
                throw new IOException("確認した切替対象と現在の機器構成が一致しません");
            return new(Contract.Copy(edit), preview);
        }
        catch (Exception e) { if (!client.Connected) RecordError(e); throw; }
        finally { operations.Release(); }
    }
    public async Task<DeviceConfigurationReply> ApplyProviderRouteAsync(DeviceProviderRoutePreview preview, CancellationToken ct = default)
    {
        preview = Contract.Copy(preview);
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen(); await ReadStateAsync(ct).ConfigureAwait(false); CheckProviderRouteEdit(preview.Edit);
            if (preview.Transfer.Generation != preview.Edit.Generation || preview.Transfer.ProviderRoute?.TopologyRevision != preview.Edit.Topology ||
                SupportsProviderDeviceSelection && !(preview.Transfer.ProviderRoute.OpenRgbDeviceIds ?? [])
                    .SequenceEqual((preview.Edit.Settings.OpenRgbDeviceIds ?? []).Order(StringComparer.Ordinal)))
                throw new IOException("確認した切替内容が一致しません");
            ct.ThrowIfCancellationRequested();
            // Do not cancel the shared connection once the service begins ending control and replacing providers.
            return await client.ApplyConfigurationAsync(preview.Transfer, preview.Edit.Topology).ConfigureAwait(false);
        }
        catch (Exception e) { if (!client.Connected) RecordError(e); throw; }
        finally
        {
            try { if (client.Connected) { var next = await client.ReadConfigurationAsync().ConfigureAwait(false); lock (gate) configuration = next; await ReadStateAsync(default).ConfigureAwait(false); } }
            catch (Exception e) { RecordError(e); }
            operations.Release();
        }
    }
}
