namespace FlowDeck.Core;

// A service-produced review of a compiled routing policy, never permission supplied by a client.
public sealed record DeviceProviderRouteReview(string PolicyId, int PolicyRevision, string PolicyDigest,
    string[] RemoveProviderIds, string[] AddProviderIds, string[] RetainedProviderIds,
    long TopologyRevision, string[] RemovingDeviceIds, bool StopsAllControls = true,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string[]? OpenRgbDeviceIds = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string[]? ContinuingDeviceIds = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? IsolationDigest = null)
{
    public const string Feature = "provider-routing-preview-v2";
    public const string SelectionFeature = "provider-device-selection-v2";
    public static bool IsOpenRgbDeviceId(string? id)
    {
        const string prefix = "openrgb-sdk:";
        return id is not null && id.Length == prefix.Length + 64 && id.StartsWith(prefix, StringComparison.Ordinal) &&
            !id.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef");
    }
    internal static bool Ids(string[]? values, int maximum) => values is not null && values.Length <= maximum &&
        values.All(DeviceConfigurationRules.Key) && values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    public static bool Valid(DeviceProviderRouteReview? value) => value is { PolicyRevision: > 0, TopologyRevision: >= 0 } &&
        (value.StopsAllControls ? value.ContinuingDeviceIds is null && value.IsolationDigest is null :
            DeviceConfigurationProtocol.Digest(value.IsolationDigest) && Ids(value.ContinuingDeviceIds, DeviceRegistry.MaximumDevices) &&
            Ids(value.RemovingDeviceIds, DeviceRegistry.MaximumDevices) && !value.ContinuingDeviceIds!.Intersect(value.RemovingDeviceIds).Any()) &&
        DeviceConfigurationRules.Key(value.PolicyId) && DeviceConfigurationProtocol.Digest(value.PolicyDigest) &&
        Ids(value.RemoveProviderIds, 8) && Ids(value.AddProviderIds, 8) && Ids(value.RetainedProviderIds, 8) &&
        Ids(value.RemovingDeviceIds, DeviceRegistry.MaximumDevices) && (value.OpenRgbDeviceIds is null ||
            Ids(value.OpenRgbDeviceIds, DeviceRegistry.MaximumDevices) && value.OpenRgbDeviceIds.All(IsOpenRgbDeviceId)) &&
        value.RemoveProviderIds.Length + value.AddProviderIds.Length > 0 &&
        value.RemoveProviderIds.Length + value.RetainedProviderIds.Length <= 8 && value.AddProviderIds.Length + value.RetainedProviderIds.Length <= 8 &&
        !value.RemoveProviderIds.Intersect(value.RetainedProviderIds).Any() && !value.AddProviderIds.Intersect(value.RetainedProviderIds).Any();
}

public interface IDeviceProviderRoutingHandler : IDeviceConfigurationHandler
{
    bool SupportsProviderRouting { get; }
    bool SupportsProviderDeviceSelection => false;
}

public sealed record DeviceProviderReplacementScope(long TopologyRevision, string[] RemovingDeviceIds, string[] RetainedProviderIds);
