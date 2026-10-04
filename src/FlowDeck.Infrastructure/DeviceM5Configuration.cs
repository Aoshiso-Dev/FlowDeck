using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public static class DeviceM5Configuration
{
    public static DeviceM5Settings Read(DevicePreferences preferences) => new(preferences.M5, preferences.M5Selection ?? new(), preferences.M5ConnectOnStart);
    public static DevicePreferences Apply(DevicePreferences preferences, DeviceM5Settings settings) => preferences with {
        M5 = Contract.Copy(settings.Connection), M5Selection = Contract.Copy(settings.Selection), M5ConnectOnStart = settings.ConnectOnStart };
    public static void ValidateStored(DeviceConfigurationPackage config)
    {
        var preferences = config.Preferences;
        // Earlier v2 generations without an M5 selection have no panel write authority.
        if (preferences.M5Selection is null && !preferences.M5ConnectOnStart) return;
        if (DeviceM5Settings.Problem(Read(preferences)) is { } error) throw new IOException(error);
        var selection = preferences.M5Selection ?? new();
        RegisteredDevice? Device(EndpointReference t) => config.Devices.Devices.SingleOrDefault(d => d.Id == t.DeviceId);
        bool Sensor(EndpointReference? t, DeviceKind kind, DeviceFeature feature) => t is null || Device(t) is { } d && d.Kind == kind &&
            d.Endpoints.Any(e => e.Id == t.EndpointId && (feature == DeviceFeature.Temperature ? e.Kind == EndpointKind.Sensor : e.Kind is EndpointKind.Cooling or EndpointKind.Sensor));
        if (!Sensor(selection.Cpu, DeviceKind.Processor, DeviceFeature.Temperature) || !Sensor(selection.Gpu, DeviceKind.Graphics, DeviceFeature.Temperature) ||
            selection.Motherboard.Any(i => !Sensor(i.Target, DeviceKind.Motherboard, i.Measurement)))
            throw new IOException("M5GOの表示先に対応する登録済み温度・回転数を選択してください");
        if (selection.Hub is not { } h) return;
        var hub = config.Devices.Devices.SingleOrDefault(d => d.Id == h.DeviceId);
        if (hub is not { Provider: DeviceM5Selection.HubProvider, Kind: DeviceKind.Controller } ||
            selection.Fans.Any(id => !hub.Endpoints.Any(e => e.Id == id && e.Kind == EndpointKind.Cooling)) ||
            selection.Leds.Any(id => !hub.Endpoints.Any(e => e.Id == id && e.Kind == EndpointKind.Lighting)))
            throw new IOException("M5GOのHub操作先に登録済みの対応端子を選択してください");
        // A saved binding or profile may later become stale. Keep the selection visible; the bridge
        // disables it until explicitly rebound, rather than preventing all other settings from saving.
    }
    public static string? LiveProblem(DeviceM5Settings settings, DeviceConfigurationPackage config, DeviceServiceState state, PhysicalDeviceBinding[] candidates)
    {
        if (DeviceM5Settings.Problem(settings) is { } error) return error;
        try { ValidateStored(config with { Preferences = Apply(config.Preferences, settings) }); }
        catch (IOException e) { return e.Message; }
        var selection = settings.Selection;
        bool Measurement(EndpointReference? t, DeviceFeature feature) => t is null || state.Inventory.Devices.SingleOrDefault(d => d.Device.Id == t.DeviceId && d.Connected)?
            .Device.Endpoints.SingleOrDefault(e => e.Id == t.EndpointId)?.Capabilities.Any(c => c.Feature == feature && c.Support == ImplementationSupport.Supported) == true;
        if (!Measurement(selection.Cpu, DeviceFeature.Temperature) || !Measurement(selection.Gpu, DeviceFeature.Temperature) ||
            selection.Motherboard.Any(i => !Measurement(i.Target, i.Measurement))) return "M5GOの表示対象を現在の機器から選び直してください";
        if (selection.Hub is not { } hub) return null;
        var instance = state.Inventory.Devices.SingleOrDefault(d => d.Device.Id == hub.DeviceId && d.Connected);
        if (instance?.Device is not { Provider: DeviceM5Selection.HubProvider, DefinitionId: DeviceM5Selection.HubDefinition } ||
            selection.HasHubControl() && config.Bindings.Devices.SingleOrDefault(b => b.DeviceId == hub.DeviceId) != hub ||
            !candidates.Contains(hub)) return "Hubの個体・配線を確認して選び直してください";
        foreach (var id in selection.Fans)
        {
            var target = new EndpointReference(hub.DeviceId, id);
            var setup = config.Cooling.Endpoints.SingleOrDefault(e => e.Target == target);
            if (setup?.WiringConfirmed != true || DeviceConfigurationRules.SafetyReadiness(setup.Safety) is not null ||
                instance.Device.Endpoints.SingleOrDefault(e => e.Id == id)?.Capabilities.Any(c => c.Feature == DeviceFeature.FixedDuty && c.Usable) != true)
                return "Hubのファン端子を設定してから選択してください";
        }
        foreach (var id in selection.Leds)
            if (instance.Device.Endpoints.SingleOrDefault(e => e.Id == id)?.Capabilities.Any(c => c.Feature == DeviceFeature.FixedColor && c.Usable) != true)
                return "Hubの照明端子を設定してから選択してください";
        foreach (var slot in selection.Profiles)
        {
            var profiles = selection.Fans.Select(id => config.Cooling.Endpoints.Single(e => e.Target == new EndpointReference(hub.DeviceId, id)).Profiles.SingleOrDefault(p => p.Id == slot.ProfileId)).ToArray();
            if (profiles.Length == 0 || profiles.Any(p => p is null) || profiles.Select(p => SettingsTransfer.Hash(p)).Distinct().Count() != 1)
                return "選択した各ファンに同じプリセットを設定してください";
        }
        return null;
    }
}
