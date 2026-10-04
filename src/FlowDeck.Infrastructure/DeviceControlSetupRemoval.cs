using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public static class DeviceControlSetupRemoval
{
    public static bool Configured(DeviceConfigurationPackage config, EndpointReference target) =>
        config.Cooling.Endpoints.Any(e => e.Target == target) || (config.Lighting.Endpoints ?? []).Any(e => e.Target == target);

    public static DeviceEndpointSetupEdit Capture(DeviceWorkspaceSnapshot snapshot, EndpointReference target)
    {
        var config = snapshot.Configuration.Configuration;
        if (!Configured(config, target)) throw new InvalidOperationException("この端子には解除する制御設定がありません");
        var device = config.Devices.Devices.Single(d => d.Id == target.DeviceId);
        var endpoint = device.Endpoints.Single(e => e.Id == target.EndpointId);
        var cooling = endpoint.Kind == EndpointKind.Cooling;
        var next = config with {
            Cooling = config.Cooling with { Endpoints = config.Cooling.Endpoints.Where(e => e.Target != target).ToArray() },
            Lighting = config.Lighting with { Endpoints = (config.Lighting.Endpoints ?? []).Where(e => e.Target != target).ToArray() },
            Devices = config.Devices with { Devices = config.Devices.Devices.Select(d => d.Id != target.DeviceId ? d : d with {
                Endpoints = d.Endpoints.Select(e => e.Id == target.EndpointId && e.Kind == EndpointKind.Lighting && e.Setup is { } setup ?
                    e with { Setup = setup with { LedCountConfirmed = false } } : e).ToArray() }).ToArray() }
        };
        DeviceConfigurationCodec.Validate(next);
        var edit = new DeviceEndpointSetupEdit(snapshot.State.Inventory.Epoch, snapshot.Configuration.Generation, snapshot.State.Inventory.TopologyRevision,
            snapshot.State.Inventory.Devices.SingleOrDefault(d => d.Device.Id == target.DeviceId)?.Generation ?? 0, target, Contract.Copy(next),
            [device.Name + " / " + endpoint.Name, "この端子の制御設定を解除",
                cooling ? "最低・異常時・終了時の設定、端子のカーブ、保存した制御方針・自動再開を削除" : "LED数の確認と終了時の設定を解除",
                "名前・監視表示・グループ・照明シーンは保持", "再び制御するには端子の設定と確認が必要",
                cooling ? "この端子だけ保存済みの終了方法で終了。ほかのファンの制御は継続" : "この端子の照明だけ保存済みの終了方法で終了。ファンとほかの照明は継続"], Removing: true);
        if (!edit.Matches(snapshot)) throw new InvalidOperationException("接続・機器・設定を再取得してください");
        return edit;
    }
}
