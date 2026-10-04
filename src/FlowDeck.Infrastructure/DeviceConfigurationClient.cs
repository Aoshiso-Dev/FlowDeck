using System.Security.Cryptography;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record ServiceConfigurationSnapshot(string Generation, DeviceConfigurationPackage Configuration, PhysicalDeviceBinding[] Candidates,
    DeviceSetupCandidate[]? Setup = null, long? TopologyRevision = null);
public sealed record ServiceConfigurationPreview(string TransferId, string Generation, string[] ChangedComponents, DeviceBindingStatus[] Bindings,
    DeviceProviderRouteReview? ProviderRoute = null);
public static class DeviceConfigurationClient
{
    private static async Task<DeviceConfigurationReply> Send(DeviceServicePipeClient client, DeviceConfigurationRequest request, CancellationToken ct)
    {
        if (client.Hello?.Features.Contains("configuration-transfer-v2") != true) throw new IOException("サービスの設定反映機能に対応する更新が必要です");
        var reply = await client.SendAsync(client.Request(DeviceOperation.Configuration, configuration: request), ct).ConfigureAwait(false);
        if (!reply.Success || reply.Configuration is null) throw new IOException(reply.Error ?? "設定の応答を取得できません");
        return reply.Configuration;
    }
    public static async Task<ServiceConfigurationSnapshot> ReadConfigurationAsync(this DeviceServicePipeClient client, CancellationToken ct = default)
    {
        var combined = client.Hello?.Features.Contains(DeviceConfigurationProtocol.SnapshotFeature) == true;
        async Task<DeviceConfigurationReply> Read(DeviceConfigurationRequest request)
        {
            ct.ThrowIfCancellationRequested();
            // Read-only cancellation is observed between chunks without tearing down the shared control connection.
            var reply = await Send(client, request, CancellationToken.None).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); return reply;
        }
        var head = await Read(new(combined ? DeviceConfigurationOperation.SnapshotWithCandidates : DeviceConfigurationOperation.Snapshot)).ConfigureAwait(false);
        var maximum = combined ? DeviceConfigurationProtocol.MaximumSnapshotBytes : DeviceConfigurationProtocol.MaximumBytes;
        if (!DeviceServiceProtocol.Id(head.TransferId) || head.TotalBytes is not >= 2 || head.TotalBytes > maximum ||
            !DeviceConfigurationProtocol.Digest(head.Sha256) || (combined ? head.Candidates is not null || head.Setup is not null : head.Candidates is null))
            throw new IOException("設定取得の開始情報が不正です");
        var bytes = new byte[head.TotalBytes.Value]; var offset = 0;
        while (offset < bytes.Length)
        {
            var part = await Read(new(DeviceConfigurationOperation.ReadSnapshot, head.TransferId, Offset: offset)).ConfigureAwait(false);
            if (part.TransferId != head.TransferId || part.Generation != head.Generation || part.TotalBytes != bytes.Length || part.Offset != offset ||
                part.Data is null || part.Data.Length != Math.Min(DeviceConfigurationProtocol.ChunkBytes, bytes.Length - offset)) throw new IOException("設定取得の順序・長さが不正です");
            part.Data.CopyTo(bytes, offset); offset += part.Data.Length;
        }
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != head.Sha256) throw new IOException("取得した設定の整合性が不正です");
        return combined ? DeviceConfigurationSnapshotCodec.Deserialize(bytes, head.Generation) :
            new(head.Generation, DeviceConfigurationCodec.DeserializePackage(bytes), head.Candidates!, head.Setup);
    }
    public static async Task<ServiceConfigurationPreview> PreviewConfigurationAsync(this DeviceServicePipeClient client, string expectedGeneration,
        DeviceConfigurationPackage configuration, CancellationToken ct = default, EndpointReference? coolingSetupTarget = null, EndpointReference? lightingSetupTarget = null)
    {
        if (coolingSetupTarget is not null && lightingSetupTarget is not null) throw new ArgumentException("設定対象は1端子です");
        if (lightingSetupTarget is not null && client.Hello?.Features.Contains(DeviceEndpointSetupScope.LightingFeature) != true)
            throw new IOException("ほかの端子の制御を維持して照明設定を保存するには、FlowDeckサービスの更新が必要です");
        if (coolingSetupTarget is not null && client.Hello?.Features.Contains(DeviceEndpointSetupScope.Feature) != true)
            throw new IOException("ほかのファンの制御を維持して保存するには、FlowDeckサービスの更新が必要です");
        var bytes = DeviceConfigurationCodec.SerializePackage(configuration);
        var selected = (configuration.Preferences.Providers?.OpenRgbDeviceIds ?? []).Order(StringComparer.Ordinal).ToArray();
        var selectionSupported = client.Hello?.Features.Contains(DeviceProviderRouteReview.SelectionFeature) == true;
        if (selected.Length > 0 && !selectionSupported) throw new IOException("このサービスでは機器別の選択に対応していません");
        var head = await Send(client, new(DeviceConfigurationOperation.BeginUpload, ExpectedGeneration: expectedGeneration,
            TotalBytes: bytes.Length, Sha256: Convert.ToHexStringLower(SHA256.HashData(bytes))), ct).ConfigureAwait(false);
        if (!DeviceServiceProtocol.Id(head.TransferId) || head.Generation != expectedGeneration || head.TotalBytes != bytes.Length || head.Offset != 0)
            throw new IOException("設定転送の開始情報が不正です");
        for (var offset = 0; offset < bytes.Length;)
        {
            var part = bytes.AsSpan(offset, Math.Min(DeviceConfigurationProtocol.ChunkBytes, bytes.Length - offset)).ToArray();
            var reply = await Send(client, new(DeviceConfigurationOperation.Append, head.TransferId, Offset: offset, Data: part), ct).ConfigureAwait(false);
            offset += part.Length;
            if (reply.TransferId != head.TransferId || reply.Offset != offset || reply.Generation != expectedGeneration || reply.TotalBytes != bytes.Length)
                throw new IOException("設定転送の受付位置が不正です");
        }
        var reviewRoutes = client.Hello?.Features.Contains(DeviceProviderRouteReview.Feature) == true;
        var preview = await Send(client, new(coolingSetupTarget is not null ? DeviceConfigurationOperation.ValidateCoolingSetup :
            lightingSetupTarget is not null ? DeviceConfigurationOperation.ValidateLightingSetup :
            selectionSupported ? DeviceConfigurationOperation.ValidateProviderSelection :
            reviewRoutes ? DeviceConfigurationOperation.ValidateProviderRoute : DeviceConfigurationOperation.Validate, head.TransferId,
            CoolingSetupTarget: coolingSetupTarget, LightingSetupTarget: lightingSetupTarget), ct).ConfigureAwait(false);
        if (preview.TransferId != head.TransferId || preview.Generation != expectedGeneration || preview.ChangedComponents is null || preview.Bindings is null)
            throw new IOException("設定差分の応答が不正です");
        if (preview.ProviderRoute is not null && (!reviewRoutes || !DeviceProviderRouteReview.Valid(preview.ProviderRoute)))
            throw new IOException("取得経路の切替確認が不正です");
        if (preview.ProviderRoute is { } route && selectionSupported &&
            (route.OpenRgbDeviceIds is null || !route.OpenRgbDeviceIds.SequenceEqual(selected)))
            throw new IOException("確認したOpenRGBの選択機器が一致しません");
        return new(head.TransferId!, preview.Generation, preview.ChangedComponents, preview.Bindings, preview.ProviderRoute);
    }
    public static Task<DeviceConfigurationReply> ApplyConfigurationAsync(this DeviceServicePipeClient client, ServiceConfigurationPreview preview,
        long topologyRevision, CancellationToken ct = default) => Send(client, new(DeviceConfigurationOperation.Apply,
            preview.TransferId, TopologyRevision: topologyRevision), ct);
    public static Task<DeviceConfigurationReply> SaveDeviceNameAsync(this DeviceServicePipeClient client, string expectedGeneration,
        DeviceNameChange change, CancellationToken ct = default)
    {
        if (client.Hello?.Features.Contains(DeviceNameChange.Feature) != true) throw new IOException("サービスの機器名設定に対応する更新が必要です");
        return Send(client, new(DeviceConfigurationOperation.SaveDeviceName, ExpectedGeneration: expectedGeneration, DeviceName: change), ct);
    }
    public static Task<DeviceConfigurationReply> SaveDashboardAsync(this DeviceServicePipeClient client, string expectedGeneration,
        DeviceDashboardConfiguration dashboard, CancellationToken ct = default)
    {
        if (client.Hello?.Features.Contains("dashboard-settings-v2") != true) throw new IOException("サービスの表示設定機能に対応する更新が必要です");
        return Send(client, new(DeviceConfigurationOperation.SaveDashboard, ExpectedGeneration: expectedGeneration, Dashboard: dashboard), ct);
    }
    public static Task<DeviceConfigurationReply> SaveNotificationsAsync(this DeviceServicePipeClient client, string expectedGeneration,
        DeviceNotificationSettings settings, CancellationToken ct = default)
    {
        if (client.Hello?.Features.Contains("notifications-v2") != true) throw new IOException("サービスの通知機能に対応する更新が必要です");
        return Send(client, new(DeviceConfigurationOperation.SaveNotifications, ExpectedGeneration: expectedGeneration, Notifications: settings), ct);
    }
    public static Task<DeviceConfigurationReply> SaveCoolingStartupAsync(this DeviceServicePipeClient client,CoolingStartupEdit edit,CancellationToken ct=default)
    {
        if(client.Hello?.Features.Contains("cooling-startup-settings-v2")!=true) throw new IOException("サービスの起動時冷却設定に対応する更新が必要です");
        if(client.Hello.Epoch!=edit.Epoch) throw new IOException("サービスの接続世代が変わりました");
        return Send(client,new(DeviceConfigurationOperation.SaveCoolingStartup,ExpectedGeneration:edit.Generation,TopologyRevision:edit.Topology,CoolingStartup:edit.Selection),ct);
    }
    public static Task<DeviceConfigurationReply> RegisterMonitoringAsync(this DeviceServicePipeClient client, DeviceMonitoringEdit edit, CancellationToken ct = default)
    {
        if (client.Hello?.Features.Contains("monitoring-registration-v2") != true) throw new IOException("サービスの監視登録機能に対応する更新が必要です");
        if (client.Hello.Epoch != edit.Epoch) throw new IOException("サービスの接続世代が変わりました。再取得してください");
        return Send(client, new(DeviceConfigurationOperation.RegisterMonitoring, ExpectedGeneration: edit.ConfigurationGeneration,
            TopologyRevision: edit.TopologyRevision, Monitoring: edit.Items), ct);
    }
}
