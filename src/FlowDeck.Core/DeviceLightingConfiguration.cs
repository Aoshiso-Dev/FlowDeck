namespace FlowDeck.Core;

public enum LightingExitAction { FixedColor, Off, RestoreOriginal }
public sealed record LightingExitPolicy(LightingExitAction Action, RgbColor? Color = null);
public sealed record LightingEndpointConfiguration(EndpointReference Target, bool WiringConfirmed, LightingExitPolicy? Exit = null);

public interface IConfiguredLightingDriver : IDeviceLightingDriver, IPhysicalDeviceDriver
{
    void ConfigureLighting(PhysicalDeviceBinding? binding, IEnumerable<LightingEndpointConfiguration> configurations,
        IEnumerable<RegisteredEndpoint> endpoints);
}

public interface IIndividualLightingConfiguration : IConfiguredLightingDriver
{
    void ConfigureLightingEndpoint(PhysicalDeviceBinding binding, string endpointId,
        LightingEndpointConfiguration? configuration, RegisteredEndpoint endpoint);
}

public static class DeviceLightingConfiguration
{
    public static void CheckIndividual(string deviceId, string endpointId, LightingEndpointConfiguration? configuration, RegisteredEndpoint endpoint)
    {
        if (!DeviceConfigurationRules.ValidReference(new(deviceId, endpointId)) || endpoint is null ||
            endpoint.Id != endpointId || endpoint.Kind != EndpointKind.Lighting || configuration is not null &&
            (!ValidEndpoint(configuration) || configuration.Target != new EndpointReference(deviceId, endpointId)))
            throw new ArgumentException("照明端子の設定が一致しません");
    }
    public static bool ValidExit(LightingExitPolicy? value) => value is not null && Enum.IsDefined(value.Action) &&
        (value.Action == LightingExitAction.FixedColor ? value.Color?.Valid == true : value.Color is null);
    public static bool ValidEndpoint(LightingEndpointConfiguration? value) => value is not null &&
        DeviceConfigurationRules.ValidReference(value.Target) && (value.Exit is null || ValidExit(value.Exit));
    public static LightingProgram ExitProgram(LightingExitPolicy policy) => ValidExit(policy) ? policy.Action switch
    {
        LightingExitAction.FixedColor => new("fixed", [policy.Color!]),
        LightingExitAction.Off => new("off", []),
        _ => throw new ArgumentException("元設定の復元には機器から読み取った情報が必要です")
    } : throw new ArgumentException("照明の終了設定が不正です");
}
