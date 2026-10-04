using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceConfigurationAuthority
{
    private DeviceConfigurationReply SaveM5(DeviceConfigurationRequest request, DeviceControlRuntime runtime)
    {
        if (request.ExpectedGeneration != generation.Id) throw new IOException("M5GOの編集元設定が変更されました。再取得してください");
        var state = runtime.State();
        if (state.Inventory.TopologyRevision != request.TopologyRevision || state.Releasing || state.ConfigurationChanging)
            throw new IOException("M5GOの対象機器が変更されました。再取得してください");
        if (DeviceM5Configuration.LiveProblem(request.M5!, configuration, state, runtime.PhysicalCandidates()) is { } error) throw new IOException(error);
        var next = configuration with { Preferences = DeviceM5Configuration.Apply(configuration.Preferences, request.M5!) };
        if (!Same(configuration.Preferences, next.Preferences)) PersistConfiguration(next, "M5GO設定");
        upload = null;
        return Reply("M5GOの設定を保存しました", changed: ["preferences.json"]);
    }
}
