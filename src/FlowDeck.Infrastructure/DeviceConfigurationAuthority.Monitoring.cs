using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceConfigurationAuthority
{
    private DeviceConfigurationReply RegisterMonitoring(DeviceConfigurationRequest request, DeviceControlRuntime runtime)
    {
        if (request.ExpectedGeneration != generation.Id) throw new IOException("選択元の設定が変更されました。再取得してください");
        var state = runtime.State();
        if (request.TopologyRevision != state.Inventory.TopologyRevision || state.Releasing || state.ConfigurationChanging)
            throw new IOException("選択後に機器構成が変わりました。再取得してください");
        var devices = configuration.Devices.Devices.ToDictionary(d => d.Id);
        var items = configuration.Dashboard.Items.ToList();
        if (items.Count + request.Monitoring!.Length > DeviceDashboard.MaximumSavedItems)
            throw new IOException($"監視項目は{DeviceDashboard.MaximumSavedItems}件までです（登録済み{items.Count}件・追加{request.Monitoring.Length}件）。モニターの表示から不要な項目を削除して保存してください");
        foreach (var selection in request.Monitoring!)
        {
            var item = selection.Item;
            var current = state.Inventory.Devices.SingleOrDefault(d => d.Device.Id == item.Target.DeviceId);
            var endpoint = current?.Device.Endpoints.SingleOrDefault(e => e.Id == item.Target.EndpointId);
            if (current?.Connected != true || current.Generation != selection.Generation || endpoint is null ||
                !endpoint.Capabilities.Any(c => c.Feature == item.Measurement && c.Usable) ||
                (item.Measurement == DeviceFeature.Temperature ? endpoint.Kind != EndpointKind.Sensor : endpoint.Kind is not (EndpointKind.Sensor or EndpointKind.Cooling)) ||
                DeviceDashboard.Sample(state, item.Target, item.Measurement) is not { } sample || !DeviceDashboard.Fresh(sample, 0))
                throw new IOException("選択した温度・回転数を現在の機器から取得できません");
            if (items.Any(i => i.Target == item.Target && i.Measurement == item.Measurement)) throw new IOException("すでに登録された監視項目です。再取得してください");
            var actual = current.Device;
            var registered = devices.GetValueOrDefault(actual.Id) ?? new RegisteredDevice(actual.Id, DeviceMonitoringDraft.DisplayDeviceName(actual, state.Inventory), actual.Provider, actual.Kind, []);
            if (registered.Provider != actual.Provider || registered.Kind != actual.Kind) throw new IOException("登録した機器の取得元・種類が一致しません");
            var saved = registered.Endpoints.SingleOrDefault(e => e.Id == endpoint.Id);
            if (saved is not null && saved.Kind != endpoint.Kind) throw new IOException("登録した端子の種類が一致しません");
            if (saved is null) registered = registered with { Endpoints = [.. registered.Endpoints,
                new(endpoint.Id, DeviceMonitoringDraft.DisplayName(endpoint.Name), endpoint.Kind, item.Role)] };
            devices[actual.Id] = registered; items.Add(item);
        }
        // Registration stores display references only. Bindings, wiring confirmations, output policies,
        // driver configuration and running hardware owners are deliberately unaffected.
        var registrations = new DeviceRegistrations(2, devices.Values.ToArray());
        var registrationChanged = !Same(configuration.Devices, registrations);
        PersistConfiguration(configuration with { Devices = registrations, Dashboard = new(2, items.ToArray()) }, "監視項目");
        return Reply("監視項目を追加しました", changed: registrationChanged ? ["devices.json", "dashboard.json"] : ["dashboard.json"]);
    }
}
