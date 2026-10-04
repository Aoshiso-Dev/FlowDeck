namespace FlowDeck.Core;

// Normal is an explicit effect stop that keeps the base color. SessionEnded applies the configured exit policy.
public enum DeviceLightingStopReason { Normal, Failure, DeviceChanged, OwnerLost, ServiceStopping, ConfigurationChanged, Suspended, SessionEnded }
public sealed record DeviceLightingLimits(int MinimumIntervalMilliseconds, int MaximumFrameAgeMilliseconds, int MaximumLeds,
    bool EndpointColors = false, bool ConcurrentEndpointStreams = false);
public sealed record DeviceLightingStatus(string GroupId, bool Running, long Frames, long DroppedFrames, RgbColor? LastColor,
    double MaximumWriteMilliseconds, string? Fault = null, M5BodyLedFrame? Mirror = null);
public sealed record LightingFrameAuthorization(string? Error = null, bool Expired = false, TimeSpan? ValidFor = null);
public sealed record EndpointLightingFrame(string EndpointId, RgbColor Color, RgbColor NormalStopColor);

// Captured by the service, not deserialized from client-provided register/transport commands.
public sealed class DeviceLightingFrame
{
    private readonly Func<LightingFrameAuthorization> authorize;
    private readonly Dictionary<string, EndpointLightingFrame>? endpoints;
    public long Sequence { get; }
    public RgbColor Color { get; }
    public bool HasEndpointColors => endpoints is not null;
    internal DeviceLightingFrame(long sequence, RgbColor color, Func<LightingFrameAuthorization> authorize, EndpointLightingFrame[]? endpoints = null)
    {
        if (endpoints is not null && (endpoints.Length is < 1 or > 256 || endpoints.Any(e => e is null ||
            !DeviceConfigurationRules.Key(e.EndpointId) || e.Color?.Valid != true || e.NormalStopColor?.Valid != true) ||
            endpoints.DistinctBy(e => e.EndpointId).Count() != endpoints.Length)) throw new ArgumentException("端子別の照明フレームが不正です");
        Sequence = sequence; Color = color; this.authorize = authorize;
        this.endpoints = endpoints is null ? null : Contract.Copy(endpoints).ToDictionary(e => e.EndpointId);
    }
    public string? TargetError(IEnumerable<string> targets) => endpoints is null || endpoints.Keys.ToHashSet().SetEquals(targets) ? null :
        "照明フレームの端子と所有している対象が一致しません";
    public RgbColor ColorFor(string endpoint) => endpoints is null ? Color : endpoints[endpoint].Color;
    public RgbColor? NormalStopColorFor(string endpoint) => endpoints is null ? null : endpoints[endpoint].NormalStopColor;
    public LightingFrameAuthorization Authorize() => authorize();
}
public interface IDeviceLightingStream
{
    // Verify the captured physical device. Call authorization outside locks used by Observe, immediately before transport I/O.
    // Unconfirmed End results retain the driver's recovery obligation; Apply(StopLighting)/ReleaseAsync must be able to retry it.
    Task<DeviceDriverReleaseResult[]> WriteAsync(DeviceLightingFrame frame, CancellationToken ct);
    Task<DeviceDriverReleaseResult[]> EndAsync(DeviceLightingStopReason reason, CancellationToken ct);
}
// Opt-in: successful endpoints leave the active target set. Controller-wide frames/recovery may
// retain their accepted fixed color as background, but must never resume their old effect or exit policy.
// Unconfirmed endpoints remain owned and must still be returned by EndAsync. No sibling output changes.
public interface ISelectiveDeviceLightingStream : IDeviceLightingStream
{
    Task<DeviceDriverReleaseResult[]> EndSelectedAsync(string[] endpoints, CancellationToken ct);
}
// Persist intent before reserving shared hardware. The callback may be cancelled or reject the frame
// without calling WriteAsync, so the wrapper must also roll back a provably unsent preparation.
public interface IPreparedDeviceLightingStream : IDeviceLightingStream
{
    Task<DeviceDriverReleaseResult[]> PrepareFrameAsync(DeviceLightingFrame frame,
        Func<CancellationToken, Task<DeviceDriverReleaseResult[]>> transmit, CancellationToken ct);
}
public interface IDeviceLightingDriver : IDeviceControlDriver
{
    DeviceLightingLimits? LightingLimits { get; }
    string? ValidateLightingStream(string[] endpoints, LightingProgram program);
    // Opening may read and prepare recovery records; hardware mode changes belong in the first authorized Write.
    Task<IDeviceLightingStream> OpenLightingStreamAsync(string[] endpoints, LightingProgram program, CancellationToken ct);
}
public static class DeviceLighting
{
    public static bool SameProgram(LightingProgram a, LightingProgram b) => a.EffectId == b.EffectId && a.Speed == b.Speed && a.Reverse == b.Reverse &&
        a.Brightness == b.Brightness && a.Colors.SequenceEqual(b.Colors) &&
        (a.LedColors is null ? b.LedColors is null : b.LedColors is not null && a.LedColors.SequenceEqual(b.LedColors));
    public static bool Animated(LightingProgram? program) => program?.EffectId is "breathing" or "color-cycle";
    public static bool ValidAnimation(LightingProgram? program) => DeviceConfigurationRules.ValidProgram(program) && Animated(program) &&
        program!.Colors.Length == 1 && program.Speed <= 4 && !program.Reverse && program.LedColors is null;
    public static bool FrameProgram(LightingProgram? program) => ValidAnimation(program) ||
        DeviceConfigurationRules.ValidProgram(program) && program!.LedColors is null && !program.Reverse &&
        (program.EffectId == "fixed" && program.Colors.Length == 1 || program.EffectId == "off" && program.Colors.Length == 0);
    public static RgbColor StopColor(LightingProgram program) => program.EffectId == "off" ? new(0, 0, 0) : program.Colors[0].Scale(program.Brightness / 100d);
    public static bool IntensityCompatible(LightingProgram before, LightingProgram after) => FrameProgram(before) && FrameProgram(after) &&
        (!Animated(before) && !Animated(after) || SameProgram(before with { Brightness = after.Brightness }, after));
    public static bool ValidLimits(DeviceLightingLimits? limits) => limits is { MinimumIntervalMilliseconds: >= 10 and <= 10000,
        MaximumFrameAgeMilliseconds: >= 10 and <= 10000, MaximumLeds: >= 1 and <= 4096 } && (!limits.ConcurrentEndpointStreams || limits.EndpointColors);
    public static RgbColor Frame(LightingProgram program, double elapsed) =>
        program.EffectId is "fixed" or "off" ? StopColor(program) :
        HubLighting.Frame(program.EffectId, program.Colors[0], elapsed, HubLighting.Period(program.Speed)).Scale(program.Brightness / 100d);
}
