using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowDeck.Core;

public static class Contract
{
    public const int Version = 1;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
}
public enum Outcome { Accepted, Pending, Confirmed, WrittenUnverified, Partial, Failed, Unknown, Rejected }
public enum FanMode { Monitor, Manual, Auto, Fallback, Unknown }
public enum Presence { Unknown, Present, Absent }
public record HubIdentity(string Id, string Description, string? Serial, string Path);
public record FanChannel(string Id, Presence Presence, int? Rpm, int? Duty, string? ControlMode);
public record LedChannel(string Id, string Accessory, int? LedCount = null);
public record HubStatus(FanChannel[] Channels, LedChannel[] Lighting, string? Firmware = null, int SampleAgeMilliseconds = 0);
public record ChannelResult(string Id, string Write, int? ReportedDuty, string Confirmation, string? Error = null);
public record HubResult(Outcome Outcome, HubStatus? Status = null, ChannelResult[]? Channels = null, string? Error = null);
public record SensorReading(string Id, string Name, string Kind, double? Celsius, long Timestamp,
    double? RawCelsius = null, string? InvalidReason = null);
public record CurvePoint(double Temperature, int Duty);
public record SensorCurve(string SensorId, CurvePoint[] Points, double Overheat, double ResumeBelow);
public record AutoProfile(string Id, string Name, long Revision = 0, SensorCurve? Cpu = null, SensorCurve? Gpu = null,
    double? DescentDelaySeconds = null, double? DescentPerSecond = null);
public record StopConditions(double CpuBelow, double GpuBelow, int RestartDuty, double RestartSeconds);
public record FanLimits(int? Minimum = null, int? Fallback = null, int? Exit = null, bool AllowStop = false,
    StopConditions? Stop = null, bool ProtectManualCpu = false, bool ProtectManualGpu = false,
    double? CpuOverheat = null, double? CpuResumeBelow = null, double? GpuOverheat = null, double? GpuResumeBelow = null);
public record HardwareSettings(HubIdentity? Hub = null, string[]? Fans = null, string[]? Leds = null,
    string? WiringSignature = null, bool WiringConfirmed = false, LedChannel[]? LedDetails = null);
public record StartupSettings(bool Login = false, bool ResumeControl = false);
public record UiSettings(string Theme = "system", double? Left = null, double? Top = null, double Width = 1100, double Height = 800);
public record M5Settings(string? Port = null, string? DeviceId = null, string Layout = "A",
    string Transport = "usb", string? WifiAddress = null, string? WifiInterfaceId = null, int WifiPort = 47631);
public record FanPolicy(FanMode Mode, int? ManualDuty = null, AutoProfile? Profile = null, FanLimits? Limits = null,
    string? CpuSensorId = null, string? GpuSensorId = null);
public record LightingState(string? Requested = null, string? LastSent = null, Outcome Outcome = Outcome.Unknown,
    DateTimeOffset? SentAt = null, long Generation = 0, HubLightSetting? Effect = null, string[]? Channels = null);
public record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public HardwareSettings Hardware { get; init; } = new();
    public FanLimits Limits { get; init; } = new();
    public string? CpuSensorId { get; init; }
    public string? GpuSensorId { get; init; }
    public AutoProfile[] Profiles { get; init; } = [new("quiet", "静音"), new("balanced", "標準"), new("cool", "冷却優先")];
    public FanPolicy? LastConfirmedPolicy { get; init; }
    public LightingState Lighting { get; init; } = new();
    public Dictionary<string, HubPortLighting> LightingPorts { get; init; } = [];
    public M5Settings M5 { get; init; } = new();
    public MotherboardSettings Motherboard { get; init; } = new();
    public CpuFanSettings CpuFan { get; init; } = new();
    public CpuFanSettings ChassisFan { get; init; } = new();
    public Dictionary<string, CpuFanSettings> BoardFans { get; init; } = [];
    public DashboardSettings Dashboard { get; init; } = new();
    public StartupSettings Startup { get; init; } = new();
    public UiSettings Ui { get; init; } = new();
    public string? PythonPath { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ServiceConfigurationPending { get; init; }
}
public record ControlRequest(string Id, string Source, string ControllerEpoch, long ExpectedRevision, string Command,
    int? Duty = null, string? ProfileId = null, long? ProfileRevision = null, string? Lighting = null,
    HubLightSetting? LightingEffect = null, string[]? LightingChannels = null);
public record OperationResult(string Id, string Source, string Command, Outcome Outcome, string Message,
    DateTimeOffset Time, long ControlRevision, long LightingRevision, long DeviceGeneration,
    ChannelResult[]? Channels = null, string? ErrorCode = null);
public record ControlSnapshot(string ControllerEpoch, long Sequence, long ControlRevision, long LightingRevision,
    long DeviceGeneration, bool HubReady, bool Managing, FanMode Mode, FanPolicy? Desired, FanPolicy? LastConfirmed,
    int? TargetDuty, int? ConfirmedDuty, Outcome Confirmation, FanChannel[] Channels, SensorReading[] Sensors,
    long? TelemetryTimestamp, LightingState Lighting, bool FanBusy, bool LightingBusy, bool Faulted,
    string? Fault, bool CanResume, string Readiness, OperationResult? Operation, bool Demo);

public interface IHubAdapter : IAsyncDisposable
{
    bool IsDemo { get; }
    string Version { get; }
    Task<HubIdentity[]> ProbeAsync(CancellationToken ct = default);
    Task<HubStatus> OpenAsync(HubIdentity identity, long generation, CancellationToken ct = default);
    Task<HubStatus> ReadAsync(CancellationToken ct = default);
    Task<HubResult> SetFanGroupAsync(string[] channels, int duty, CancellationToken ct = default);
    Task<HubResult> SetLightingAsync(string[] channels, string preset, CancellationToken ct = default);
    Task<HubResult> SetLightingEffectAsync(string[] channels, HubLightSetting effect, CancellationToken ct = default)
        => Task.FromResult(new HubResult(Outcome.Rejected, Error: "未対応の照明アダプターです"));
    Task CloseAsync(CancellationToken ct = default);
}
public interface ITemperatureSource : IAsyncDisposable { Task<SensorReading[]> ReadAsync(CancellationToken ct = default); }
// Windows GetTickCount64 milliseconds; the worker checks this absolute deadline immediately before the write.
public sealed record HubEndpointExpectation(string HubId, string WiringSignature, int CurrentDuty, long? ValidUntilTick64 = null);
public interface IHubEndpointAdapter : IHubAdapter
{
    bool SupportsFanEndpoints { get; }
    bool SupportsFanDeadlines => false;
    Task<HubResult> SetFanEndpointAsync(string channel, int duty, HubEndpointExpectation expected, CancellationToken ct = default);
}
public interface ISettingsStore { Task<AppSettings> LoadAsync(); Task SaveAsync(AppSettings settings); }
public interface IEventLog { void Write(string category, object data); }
