namespace FlowDeck.Core;

public sealed record DeviceDialDisplaySettings(EndpointReference? Cpu = null, EndpointReference? Gpu = null)
{
    public const string Feature = "dial-display-settings-v2";
    public static bool Valid(DeviceDialDisplaySettings? value) => value is not null &&
        (value.Cpu is null || DeviceConfigurationRules.ValidReference(value.Cpu)) &&
        (value.Gpu is null || DeviceConfigurationRules.ValidReference(value.Gpu));
}
public interface IDeviceDialDisplayConfigurationHandler : IDeviceConfigurationHandler { }
