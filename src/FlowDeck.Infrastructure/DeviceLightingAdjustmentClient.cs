using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public static class DeviceLightingAdjustmentClient
{
    public static Task<DeviceServiceReply> AdjustLightingAsync(this DeviceServicePipeClient client, ServiceConfigurationSnapshot snapshot,
        DeviceServiceState state, LightingAdjustmentKind kind, LightingAdjustmentScope scope, int? brightness = null,
        string? groupId = null, EndpointReference[]? targets = null, CancellationToken ct = default)
    {
        if (client.Hello?.Features.Contains("lighting-adjustments-v2") != true) throw new IOException("明るさ操作に対応するサービス更新が必要です");
        if (state.Inventory.Epoch != client.Hello.Epoch) throw new IOException("機器一覧を再取得してください");
        var group = scope == LightingAdjustmentScope.Group ? snapshot.Configuration.Lighting.Groups.SingleOrDefault(g => g.Id == groupId)
            ?? throw new IOException("照明グループがありません") : null;
        var references = scope switch {
            LightingAdjustmentScope.All => snapshot.Configuration.Devices.Devices.SelectMany(d => d.Endpoints.Where(e => e.Kind == EndpointKind.Lighting)
                .Select(e => new EndpointReference(d.Id, e.Id))).ToArray(),
            LightingAdjustmentScope.Group => group!.Targets,
            _ => targets ?? throw new IOException("照明対象を指定してください")
        };
        var request = new LightingAdjustmentRequest(kind, scope, snapshot.Generation, brightness, group?.Id, group?.Revision ?? 0);
        if (!DeviceLightingAdjustments.Valid(request) || scope != LightingAdjustmentScope.Group && groupId is not null ||
            scope != LightingAdjustmentScope.Targets && targets is not null) throw new IOException("明るさ操作の指定が不正です");
        var versions = references.Select(r => state.Endpoints.SingleOrDefault(e => e.Target == r) ?? throw new IOException("照明対象を再取得してください"))
            .Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray();
        return client.SendAsync(client.Request(DeviceOperation.AdjustLighting, topology: state.Inventory.TopologyRevision,
            targets: versions, lightingAdjustment: request), ct);
    }
}
