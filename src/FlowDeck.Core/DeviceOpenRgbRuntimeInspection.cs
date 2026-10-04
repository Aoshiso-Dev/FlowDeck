namespace FlowDeck.Core;

public enum OpenRgbRuntimeInspectionStatus { NotInstalled, AccessDenied, Invalid, Verified }

// File verification and activation acceptance are independent. Neither is an actuation permit.
public sealed record DeviceOpenRgbRuntimeInspection(OpenRgbRuntimeInspectionStatus Status, bool StartupAccepted, string ExpectedVersion)
{
    public const string Feature = "openrgb-runtime-inspection-v2";
    public static bool Valid(DeviceOpenRgbRuntimeInspection? value) => value is not null && Enum.IsDefined(value.Status) &&
        (!value.StartupAccepted || value.Status == OpenRgbRuntimeInspectionStatus.Verified) &&
        value.ExpectedVersion is { Length: > 0 and <= 64 } &&
        value.ExpectedVersion.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');
}
public interface IDeviceOpenRgbRuntimeInspectionHandler : IDeviceConfigurationHandler
{
    bool SupportsOpenRgbRuntimeInspection { get; }
}
