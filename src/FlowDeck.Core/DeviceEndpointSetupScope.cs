namespace FlowDeck.Core;

// Produced by the service after comparing the complete upload with its current generation.
public sealed record DeviceEndpointSetupScope(EndpointReference Target, string[] ReboundDevices, string[]? LightingEndpoints = null)
{
    public const string Feature = "individual-cooling-setup-v2";
    public const string LightingFeature = "individual-lighting-setup-v2";
    public bool IsLighting => LightingEndpoints is not null;
    public bool Contains(EndpointReference target) => target == Target || ReboundDevices.Contains(target.DeviceId) ||
        IsLighting && target.DeviceId == Target.DeviceId && LightingEndpoints!.Contains(target.EndpointId);
    public bool BlocksDevice(string id) => id == Target.DeviceId || ReboundDevices.Contains(id);
}
