namespace FlowDeck.Core;

public static class DeviceCurrentConnectionControl
{
    public const string Feature = "current-connection-control-v1";
}

// Opt-in for compiled drivers that can resolve operational settings against the
// currently identified connection. This does not establish persistent identity,
// restore an old owner, start output, or authorize automatic policy resumption.
public interface IDeviceCurrentConnectionControlDriver : IPhysicalDeviceDriver
{
    PhysicalDeviceBinding? CurrentControlBinding { get; }

    // Must check the exact endpoint and feature. Supporting lighting never
    // implicitly authorizes cooling or a temperature source on the same device.
    bool AllowsCurrentConnectionControl(string endpointId, DeviceFeature feature);

    // Called without hardware output. Saved values are templates, not permission;
    // drivers must validate their compatibility and retain their recovery guards.
    // Missing real settings remain missing. No confirmed flags are synthesized.
    void ConfigureCurrentConnection(PhysicalDeviceBinding currentBinding, PhysicalDeviceBinding? savedBinding,
        IEnumerable<CoolingEndpointConfiguration> cooling, IEnumerable<LightingEndpointConfiguration> lighting,
        IEnumerable<RegisteredEndpoint> endpoints);
}

// Internal service collaboration; neither callbacks nor bindings are IPC inputs.
public interface IDeviceCurrentConnectionConfiguration
{
    string? CurrentControlError { get; }
    string? CurrentControlCapacityProblem(EndpointReference target, DeviceFeature feature,
        IDeviceCurrentConnectionControlDriver[] drivers);
    void AttachCurrentControlResolver(Func<EndpointReference, DeviceFeature, PhysicalDeviceBinding?> resolve);
    void PrepareCurrentConnection(IDeviceCurrentConnectionControlDriver driver);
}
