using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowDeck.Core;

[Flags]
public enum DeviceDiagnosticScope { Summary = 0, Measurements = 1, Operations = 2, Configuration = 4, All = 7 }
public sealed record DeviceDiagnosticQuery(DeviceDiagnosticScope Scope = DeviceDiagnosticScope.All, string? TransferId = null, int Offset = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IncludeHardware = false);
public sealed record DeviceDiagnosticChunk(string TransferId, DeviceDiagnosticScope Scope, int TotalBytes, string Sha256, int Offset, byte[] Data);
public sealed record DeviceDiagnosticVersion(string Component, string? Version, int? Patch = null, string? BuildId = null);
public enum DeviceDiagnosticReadState { NotObserved, Available, NotInstalled, Missing, AccessDenied, Invalid, Unsupported, Failed, TimedOut }
public sealed record DeviceDiagnosticKernelDriver(string Component, DeviceDiagnosticReadState RegistrationState,
    DeviceDiagnosticReadState FileState, string? FileVersion, DeviceDiagnosticReadState PackageState, string? PackageVersion,
    DateTimeOffset? ObservedUtc, bool LoadedVersionObserved = false);
// Metadata only: implementations must not start a driver, open hardware, or copy raw paths/registry values into the result.
public interface IDeviceDiagnosticSystemSource
{
    Task<DeviceDiagnosticKernelDriver[]> ReadKernelDriversAsync(CancellationToken ct);
}
public sealed record DeviceDiagnosticEnvironment(string OperatingSystem, string OsVersion, string Architecture,
    string RuntimeVersion, string? HostVersion, DeviceDiagnosticVersion[] Components, string? InstalledKernelDriverVersion = null,
    string? OsArchitecture = null, DeviceDiagnosticKernelDriver[]? KernelDrivers = null);
public sealed record DeviceDiagnosticCapability(DeviceFeature Feature, ImplementationSupport Support, VerificationEvidence Evidence, DeviceAvailability Availability, bool HasReason);
public sealed record DeviceDiagnosticMeasurement(DeviceFeature Feature, double? Value, double? AgeSeconds, bool Stale, bool HasProblem,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] DeviceMeasurementIssue Issue = DeviceMeasurementIssue.None);
public sealed record DeviceDiagnosticExecution(long Generation, long PolicyRevision, Outcome Outcome, bool Owned,
    int? RequestedDuty, int? ReportedDuty, bool HasMessage, bool RecoveryRequired, FanMode? CoolingMode, bool CoolingFaulted,
    bool CanResume, bool Restarting, bool? LightingRunning, long? Frames, long? DroppedFrames, double? MaximumLightingWriteMilliseconds, bool LightingFaulted);
public sealed record DeviceDiagnosticEndpoint(string Id, EndpointKind Kind, DeviceDiagnosticCapability[] Capabilities,
    int NativeEffectCount, DeviceDiagnosticMeasurement[]? Measurements, DeviceDiagnosticExecution? Execution);
public sealed record DeviceDiagnosticDevice(string Id, string Provider, string[] Resources, DeviceKind Kind,
    IdentityConfidence IdentityConfidence, bool HasDefinition, long Generation, bool Connected, string? DriverImplementation,
    DeviceDiagnosticEndpoint[] Endpoints,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DeviceDiagnosticHardware? Hardware = null);
public sealed record DeviceDiagnosticTarget(string Device, string Endpoint, long? Generation, Outcome Outcome, int? ReportedDuty);
public sealed record DeviceDiagnosticEvent(long Sequence, DateTimeOffset Time, DeviceOperation? Operation,
    DeviceConfigurationOperation? ConfigurationOperation, DeviceReleaseReason? ReleaseReason, string? Device, double ServiceElapsedMilliseconds,
    Outcome Outcome, DeviceDiagnosticTarget[] Targets, bool FailedWithoutTarget);
public sealed record DeviceDiagnosticCoolingSetting(EndpointReference Target, bool WiringConfirmed, int? Minimum, int? Fallback,
    int? Exit, CoolingExitAction ExitAction, int Profiles, bool ResumeOnStart);
public sealed record DeviceDiagnosticConfiguration(int RegisteredDevices, int RegisteredEndpoints, int Bindings, int DashboardItems,
    int LightingScenes, int LightingGroups, int CoolingGroups, bool NotificationsEnabled, bool OpenRgbEnabled, bool M5ConnectOnStart,
    DeviceDiagnosticCoolingSetting[] Cooling);
public sealed record DeviceDiagnosticConfigurationCapture(string Generation, DeviceDiagnosticConfiguration Configuration);
// Implementations return an in-memory projection of adopted settings. Never read files or probe hardware here.
public interface IDeviceDiagnosticConfiguration
{
    Task<DeviceDiagnosticConfigurationCapture> ReadDiagnosticsAsync(CancellationToken ct);
}
public sealed record DeviceDiagnosticReport(int FormatVersion, DateTimeOffset CapturedUtc, bool Demo, DeviceDiagnosticScope Scope,
    DeviceDiagnosticEnvironment Environment, bool Releasing, bool ConfigurationChanging, bool LifecycleProblem,
    long TopologyRevision, DeviceDiagnosticDevice[] Devices, DeviceDiagnosticConfiguration? Configuration,
    bool ConfigurationAvailable, bool ConfigurationStableDuringCapture, DeviceDiagnosticEvent[]? Events, long EarlierEventsDiscarded, string[] Omitted);

public static class DeviceDiagnostics
{
    public const string Feature = "diagnostics-export-v2";
    public const int MaximumReportBytes = 16 * 1024 * 1024, ChunkBytes = 48 * 1024, HistoryLimit = 64, TransferSeconds = 60;
    public static bool ValidScope(DeviceDiagnosticScope value) => (value & ~DeviceDiagnosticScope.All) == 0;
    public static bool Valid(DeviceDiagnosticQuery? value) => value is not null && ValidScope(value.Scope) && value.Offset >= 0 &&
        value.Offset < MaximumReportBytes && (value.TransferId is null ? value.Offset == 0 : DeviceServiceProtocol.Id(value.TransferId));
    public static bool Valid(DeviceDiagnosticChunk? value) => value is not null && DeviceServiceProtocol.Id(value.TransferId) &&
        ValidScope(value.Scope) && value.TotalBytes is > 0 and <= MaximumReportBytes && DeviceConfigurationProtocol.Digest(value.Sha256) &&
        value.Offset >= 0 && value.Offset < value.TotalBytes && value.Data is { Length: > 0 and <= ChunkBytes } &&
        value.Data.Length == Math.Min(ChunkBytes, value.TotalBytes - value.Offset);
    public static string? NumericVersion(string? value) => value is { Length: > 0 and <= 48 } &&
        value.All(c => c is >= '0' and <= '9' or '.') && Version.TryParse(value, out var version) ? version.ToString() : null;
    public static DeviceDiagnosticVersion? ApplicationInfo()
    {
        var assembly = Assembly.GetEntryAssembly();
        return assembly?.GetName().Name == "FlowDeck" ? new("FlowDeck", assembly.GetName().Version?.ToString(),
            BuildId: assembly.ManifestModule.ModuleVersionId.ToString("N")) : null;
    }
    public static DeviceDiagnosticEnvironment EnvironmentInfo(DeviceDiagnosticKernelDriver[]? kernelDrivers = null)
    {
        // Assembly versions are numeric; informational versions, paths and OS descriptions can include build-machine data.
        var names = new[] { "FlowDeck.Core", "FlowDeck.Infrastructure", "FlowDeck.Service", "FlowDeck", "LibreHardwareMonitorLib" };
        var components = AppDomain.CurrentDomain.GetAssemblies().Where(a => names.Contains(a.GetName().Name))
            .DistinctBy(a => a.GetName().Name).OrderBy(a => a.GetName().Name).Select(a => new DeviceDiagnosticVersion(a.GetName().Name!,
                a.GetName().Version?.ToString(), int.TryParse(a.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(m => m.Key == "FlowDeck.LhmPatch")?.Value, out var patch) ? patch : null,
                a.ManifestModule.ModuleVersionId.ToString("N"))).ToArray();
        // Only compiled, known components and numeric versions enter the export. Metadata errors carry no exception text.
        var kernel = (kernelDrivers ?? []).Where(d => d is not null && d.Component == "PawnIO").Take(1).Select(d => d with {
            RegistrationState = Enum.IsDefined(d.RegistrationState) ? d.RegistrationState : DeviceDiagnosticReadState.Invalid,
            FileState = Enum.IsDefined(d.FileState) && (d.FileState != DeviceDiagnosticReadState.Available || NumericVersion(d.FileVersion) is not null) ? d.FileState : DeviceDiagnosticReadState.Invalid,
            PackageState = Enum.IsDefined(d.PackageState) && (d.PackageState != DeviceDiagnosticReadState.Available || NumericVersion(d.PackageVersion) is not null) ? d.PackageState : DeviceDiagnosticReadState.Invalid,
            FileVersion = d.FileState == DeviceDiagnosticReadState.Available ? NumericVersion(d.FileVersion) : null,
            PackageVersion = d.PackageState == DeviceDiagnosticReadState.Available ? NumericVersion(d.PackageVersion) : null,
            LoadedVersionObserved = false }).ToArray();
        return new(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "macOS" : "Other",
            Environment.OSVersion.Version.ToString(), RuntimeInformation.ProcessArchitecture.ToString(), Environment.Version.ToString(),
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString(), components,
            kernel.FirstOrDefault(d => d.Component == "PawnIO")?.FileVersion, RuntimeInformation.OSArchitecture.ToString(), kernel);
    }
    public static byte[] Encode(DeviceDiagnosticReport report)
    {
        using var output = new DiagnosticStream();
        JsonSerializer.Serialize(output, report, Contract.Json);
        return output.ToArray();
    }
    private sealed class DiagnosticStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        private void Check(int count) { if (Position + count > MaximumReportBytes) throw new IOException("診断情報がサイズ上限を超えました。対象範囲を減らしてください"); }
    }
}
