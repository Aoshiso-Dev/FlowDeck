namespace FlowDeck.Core;

// An opt-in, finite operation for fixed/off programs. The complete selection is validated before
// output, but the result remains per endpoint: transport failure does not imply atomic hardware.
// Drivers retain recovery obligations and recheck service authorization before each write.
public interface IDeviceLightingBatchDriver : IDeviceControlDriver
{
    string? ValidateLightingBatch(EndpointLightingProgram[] programs);
    Task<DeviceDriverReleaseResult[]> ApplyLightingBatchAsync(EndpointLightingProgram[] programs, CancellationToken ct);
}

public static class DeviceLightingBatch
{
    public static bool Program(LightingProgram? program) => DeviceLighting.FrameProgram(program) && !DeviceLighting.Animated(program);
    public static bool Valid(EndpointLightingProgram[]? programs) => DeviceLightingComposition.Valid(programs) &&
        programs!.All(p => Program(p.Program));
}
