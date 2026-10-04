using System.Text.Json.Serialization;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceProviderPreferences(bool OpenRgbEnabled = false, int OpenRgbPort = 6742,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? OpenRgbDeviceIds = null)
{
    public static bool IsOpenRgbDeviceId(string? id) => DeviceProviderRouteReview.IsOpenRgbDeviceId(id);
    public static bool ValidSelection(string[]? ids) => ids is null || ids.Length <= DeviceRegistry.MaximumDevices &&
        ids.All(IsOpenRgbDeviceId) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length;
    public bool Valid() => DeviceOpenRgbInspection.PortValid(OpenRgbPort) && ValidSelection(OpenRgbDeviceIds) &&
        (OpenRgbEnabled || OpenRgbDeviceIds is null or { Length: 0 });
    public bool EquivalentTo(DeviceProviderPreferences? other) => other is not null && OpenRgbEnabled == other.OpenRgbEnabled &&
        OpenRgbPort == other.OpenRgbPort && (OpenRgbDeviceIds ?? []).Order(StringComparer.Ordinal)
            .SequenceEqual((other.OpenRgbDeviceIds ?? []).Order(StringComparer.Ordinal));
    public DeviceProviderPreferences Normalize()
    {
        if (!Valid()) throw new IOException("OpenRGBのポート・選択機器が不正です");
        return this with { OpenRgbDeviceIds = OpenRgbDeviceIds is { Length: > 0 } ids ? ids.Order(StringComparer.Ordinal).ToArray() : null };
    }
}
