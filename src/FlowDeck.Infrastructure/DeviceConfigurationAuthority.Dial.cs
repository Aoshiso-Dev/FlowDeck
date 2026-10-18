using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceConfigurationAuthority
{
    private DeviceConfigurationReply SaveDialDisplay(DeviceConfigurationRequest request, DeviceControlRuntime runtime)
    {
        if (request.ExpectedGeneration != generation.Id) throw new IOException("ダイヤルの編集元設定が変更されました。再取得してください");
        var state = runtime.State();
        if (state.Releasing || state.ConfigurationChanging || state.Inventory.TopologyRevision != request.TopologyRevision)
            throw new IOException("ダイヤルの表示対象が変更されました。再取得してください");
        var settings = Contract.Copy(request.DialDisplay!);
        var next = configuration with { Preferences = configuration.Preferences with { DialDisplay = settings } };
        DeviceConfigurationCodec.Validate(next);
        if (Same(configuration.Preferences.DialDisplay, settings)) return Reply("ダイヤルの温度表示に変更はありません", changed: []);
        PersistConfiguration(next, "ダイヤル温度表示");
        return Reply("ダイヤルの温度表示を保存しました", changed: ["preferences.json"]);
    }
}
