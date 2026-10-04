namespace FlowDeck.Core;

public enum OpenRgbInspectionStatus { ConnectionFailed, SettingsUnavailable, Observed }
public enum OpenRgbInspectionCondition { Observed, Unavailable, Unverified }
public sealed record OpenRgbInspectionItem(string Key, string Name, string Value, OpenRgbInspectionCondition Condition);

// A point-in-time observation for settings UI. It contains no write permit or provider activation flag.
public sealed record DeviceOpenRgbInspection(int Port, OpenRgbInspectionStatus Status, string Message, OpenRgbInspectionItem[] Items)
{
    public const string Feature = "openrgb-inspection-v2";
    public static bool PortValid(int port) => port is >= 1024 and <= 65535;
    public static bool Valid(DeviceOpenRgbInspection? value) => value is not null && PortValid(value.Port) &&
        Enum.IsDefined(value.Status) && Text(value.Message) && value.Items is { Length: > 0 and <= 16 } &&
        value.Items.All(i => i is not null && DeviceConfigurationRules.Key(i.Key) && Text(i.Name) && Text(i.Value) && Enum.IsDefined(i.Condition)) &&
        value.Items.DistinctBy(i => i.Key).Count() == value.Items.Length;
    private static bool Text(string? value) => value is { Length: > 0 and <= 160 } && !value.Any(char.IsControl);
}
public interface IDeviceOpenRgbInspectionHandler : IDeviceConfigurationHandler
{
    bool SupportsOpenRgbInspection { get; }
}
