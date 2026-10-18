using System.Text.Json;

namespace FlowDeck.Core;

/// <summary>Presentation of service-owned identities; no vendor names, channel guesses or local hardware reads.</summary>
public static class DeviceDashboard
{
    public const int MaximumSavedItems = 256;

    // An untouched dashboard is useful immediately. This is a view only: it does not create
    // bindings, wiring confirmations, policies or saved selections. Explicit hidden rows stay hidden.
    public static DeviceDashboardItem[] VisibleItems(DeviceRegistrations registrations, DeviceDashboardConfiguration saved,
        DeviceServiceState state, double elapsedSeconds, bool connected)
    {
        if (!connected) return [];
        var items = Items(registrations, saved, state.Inventory).Where(i => state.Inventory.Devices.Any(d =>
            d.Connected && d.Device.Id == i.Target.DeviceId && d.Device.Endpoints.Any(e => e.Id == i.Target.EndpointId)));
        if (saved.Items.Length != 0) return items.Where(i => i.Visible).ToArray();
        if (elapsedSeconds is not (>= 0 and < 3)) return [];
        return items.Where(i => state.Inventory.Devices.Any(d => d.Device.Id == i.Target.DeviceId && d.Connected &&
                d.Device.Endpoints.Any(e => e.Id == i.Target.EndpointId && e.Capabilities.Any(c => c.Feature == i.Measurement && c.Usable))) &&
                Sample(state, i.Target, i.Measurement) is { } sample && Fresh(sample, elapsedSeconds))
            .Take(MaximumSavedItems).Select(i => i with { Visible = true }).ToArray();
    }
    public static string Key(EndpointReference target, DeviceFeature feature) =>
        JsonSerializer.Serialize(new[] { target.DeviceId, target.EndpointId, feature.ToString() });

    public static DeviceDashboardItem[] Items(DeviceRegistrations registrations, DeviceDashboardConfiguration saved, DeviceInventory inventory)
    {
        var result = saved.Items.ToList();
        var known = result.Select(i => (i.Target, i.Measurement)).ToHashSet();
        foreach (var instance in inventory.Devices)
        foreach (var endpoint in instance.Device.Endpoints)
        foreach (var feature in endpoint.Capabilities.Where(c => c.Feature is DeviceFeature.Temperature or DeviceFeature.Rpm))
        {
            var target = new EndpointReference(instance.Device.Id, endpoint.Id);
            if (!known.Add((target, feature.Feature))) continue;
            var registered = registrations.Devices.SingleOrDefault(d => d.Id == target.DeviceId)?.Endpoints.SingleOrDefault(e => e.Id == target.EndpointId);
            result.Add(new(target, feature.Feature, registered?.Name ?? endpoint.Name,
                feature.Feature == DeviceFeature.Temperature ? "temperature" : (registered?.Role ?? endpoint.Role) == "pump" ? "pump" : "fan", false));
        }
        // The saved dashboard has its own limit. Discovery must not hide later devices.
        return result.ToArray();
    }

    // Age starts at the local request send time, conservatively including the response transit time.
    public static DashboardReading Read(DeviceDashboardItem item, DeviceRegistrations registrations, DeviceServiceState state,
        double elapsedSeconds, bool connected)
    {
        var instance = state.Inventory.Devices.SingleOrDefault(d => d.Device.Id == item.Target.DeviceId);
        var endpoint = instance?.Device.Endpoints.SingleOrDefault(e => e.Id == item.Target.EndpointId);
        var name = registrations.Devices.SingleOrDefault(d => d.Id == item.Target.DeviceId)?.Name ?? instance?.Device.Name ?? "未接続の機器";
        var status = !connected ? "サービス未接続" : instance?.Connected != true ? "未接続" : endpoint is null ? "未取得" : "取得中";
        var sample = Sample(state, item.Target, item.Measurement);
        if (status == "取得中")
        {
            var measurement = DeviceMeasurement.State(sample, elapsedSeconds);
            if (measurement == DeviceMeasurementState.Valid && endpoint?.Capabilities.Any(c => c.Feature == item.Measurement && c.Usable) != true)
                measurement = DeviceMeasurementState.Unavailable;
            status = DeviceMeasurement.Label(measurement);
        }
        var value = status == "取得中" ? sample!.Value : null;
        if (value == 0 && item.Measurement == DeviceFeature.Rpm) status = "0 RPM";
        var execution = state.Endpoints.SingleOrDefault(e => e.Target == item.Target && e.Generation == instance?.Generation);
        var dutySample = Sample(state, item.Target, DeviceFeature.DutyRead);
        var duty = connected && instance?.Connected == true && dutySample is not null && Fresh(dutySample, elapsedSeconds) ? dutySample.Value : null;
        var control = execution?.Owned == true ? execution.Cooling?.Mode switch
        { FanMode.Auto => "自動", FanMode.Manual => "固定", FanMode.Fallback => "異常時制御", _ => "制御中" } :
            endpoint?.Capabilities.Any(c => c.Feature == DeviceFeature.ReleaseCooling && c.Support == ImplementationSupport.Supported) == true ? "機器管理" : "監視のみ";
        var key = Key(item.Target, item.Measurement);
        return new(new("device", key, item.Measurement == DeviceFeature.Temperature ? "temperature" : "fan", item.Name, item.Visible, item.Role),
            value, duty, status, control, name,
            JsonSerializer.Serialize(new[] { key, state.Inventory.Epoch, instance?.Generation.ToString() ?? "missing" })) {
                Indicator = item.Indicator ?? (item.Measurement == DeviceFeature.Temperature ? "circle" : "bar"), DisplayMaximum = item.DisplayMaximum };
    }
    public static DeviceTelemetry? Sample(DeviceServiceState state, EndpointReference target, DeviceFeature feature)
    {
        var samples = (state.Telemetry ?? []).Where(s => s.Target == target && s.Feature == feature).Take(2).ToArray();
        return samples.Length == 1 ? samples[0] : null;
    }
    public static bool Fresh(DeviceTelemetry sample, double elapsedSeconds) => DeviceMeasurement.State(sample, elapsedSeconds) == DeviceMeasurementState.Valid;
}
