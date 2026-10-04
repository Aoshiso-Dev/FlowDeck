using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceConfigurationAuthority
{
    private DeviceConfigurationReply SaveDeviceName(DeviceConfigurationRequest request)
    {
        if (request.ExpectedGeneration != generation.Id)
            throw new IOException("編集元の設定が変更されました。現在の名前を読み込んでください");
        var edit = request.DeviceName!;
        var registered = configuration.Devices.Devices.SingleOrDefault(d => d.Id == edit.DeviceId)
            ?? throw new IOException("名前を変更する機器が登録されていません");
        if (registered.Name == edit.Name) return Reply("機器名に変更はありません", changed: []);
        var devices = configuration.Devices with {
            Devices = configuration.Devices.Devices.Select(d => d.Id == edit.DeviceId ? d with { Name = edit.Name } : d).ToArray() };
        PersistConfiguration(configuration with { Devices = devices }, "機器名");
        return Reply("機器名を保存しました", changed: ["devices.json"]);
    }
}
