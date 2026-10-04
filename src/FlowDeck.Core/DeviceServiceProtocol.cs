using System.Text.Json;

namespace FlowDeck.Core;

public enum DeviceOperation { State, Renew, SetDuty, SetAutomatic, ReleaseCooling, SetLighting, StopLighting, Result, ReleaseSession, ResumeCooling, RestoreSavedCooling, Configuration, LightingLibrary, ApplyLightingPreset, AdjustLighting, CalibrateCooling, CancelCoolingCalibration, ReadNotifications, TestNotification, ReportM5Connection, IdentifyLighting, CancelLightingIdentification, ReadDiagnostics, ReadStateSnapshot }
public enum DeviceReleaseReason { NormalExit, ClientExpired, ClientDisconnected, ServiceStopping, Suspended, DeviceChanged, StartupRecovery, ConfigurationChanged }
public sealed record DeviceTargetVersion(EndpointReference Target, long Generation, long ExpectedPolicyRevision);
public sealed record DeviceServiceRequest(int Version, string Epoch, string Session, string Id, long Sequence, string Source,
    DeviceOperation Operation, long TopologyRevision = 0, DeviceTargetVersion[]? Targets = null, int? Duty = null,
    CoolingTemperatureProfile? Profile = null, LightingProgram? Lighting = null, string? ResultId = null, DeviceConfigurationRequest? Configuration = null,
    LightingLibraryRequest? LightingLibrary = null, LightingPresetRequest? LightingPreset = null, LightingAdjustmentRequest? LightingAdjustment = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] CoolingGroupSelection? CoolingGroup = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] CoolingCalibrationRequest? Calibration = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeviceNotificationQuery? Notifications = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeviceM5NotificationReport? M5Connection = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeviceDiagnosticQuery? Diagnostics = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeviceStateQuery? StateQuery = null);
public sealed record DeviceServiceHello(int Version, string Epoch, string Session, int LeaseSeconds, string[] Features, bool Demo = false);
public sealed record DeviceTargetResult(EndpointReference Target, long Generation, long PolicyRevision, Outcome Outcome,
    string Message, int? RequestedDuty = null, int? ReportedDuty = null, LightingProgram? ReportedLighting = null, DevicePolicySaveResult? Persistence = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] CoolingCalibrationResult? Calibration = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] LightingIdentificationStatus? Identification = null);
public sealed record DeviceOperationResult(string RequestId, string Source, Outcome Outcome, DateTimeOffset Time,
    DeviceTargetResult[] Targets, string Message);
public sealed record DeviceExecutionState(EndpointReference Target, long Generation, long PolicyRevision, Outcome Outcome,
    int? RequestedDuty = null, int? ReportedDuty = null, LightingProgram? RequestedLighting = null, LightingProgram? ReportedLighting = null,
    string? Message = null, bool Owned = false, DeviceCoolingStatus? Cooling = null, DevicePolicySaveResult? Persistence = null, DeviceLightingStatus? Lighting = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] CoolingCalibrationResult? Calibration = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool CalibrationReady = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] LightingIdentificationStatus? Identification = null);
public sealed record DeviceTelemetry(EndpointReference Target, DeviceFeature Feature, double? Value, double? AgeSeconds, bool Stale, string? Problem = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] DeviceMeasurementIssue Issue = DeviceMeasurementIssue.None);
public sealed record DeviceServiceState(DeviceInventory Inventory, DeviceExecutionState[] Endpoints, bool Releasing, string? LifecycleError,
    DeviceTelemetry[]? Telemetry = null, bool ConfigurationChanging = false);
public sealed record DeviceServiceReply(int Version, string Epoch, string Session, string Id, bool Success,
    string? Error = null, DeviceOperationResult? Result = null, DeviceServiceState? State = null, DeviceConfigurationReply? Configuration = null,
    LightingLibraryReply? LightingLibrary = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeviceNotificationFeed? Notifications = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeviceDiagnosticChunk? Diagnostics = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeviceStateChunk? StateChunk = null);
public sealed record DeviceEndpointAction(string EndpointId, DeviceOperation Operation, int? Duty = null,
    CoolingTemperatureProfile? Profile = null, LightingProgram? Lighting = null);
public enum DeviceDriverFailure { None, CoolingInputExpired, CoolingInputInvalid, LightingFrameExpired }
public sealed record DeviceDriverResult(Outcome Outcome, string Message, int? ReportedDuty = null, LightingProgram? ReportedLighting = null,
    DeviceDriverFailure Failure = DeviceDriverFailure.None);
public sealed record DeviceDriverReleaseResult(string EndpointId, DeviceDriverResult Result);

// Only compiled drivers implement this boundary. Validate performs no writes; each Apply checks actual identity again before I/O.
public interface IDeviceControlDriver : IAsyncDisposable
{
    string DeviceId { get; }
    DeviceObservation? Observe();
    string? Validate(DeviceEndpointAction action);
    Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct);
    Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct);
}
public interface IPhysicalDeviceDriver : IDeviceControlDriver
{
    PhysicalDeviceBinding? BindingCandidate { get; }
}
// Drops cached permission even when discovery is absent. This never removes recovery obligations or performs hardware I/O.
public interface IDeviceConfigurationRevocation
{
    void RevokeConfiguration();
}
public interface IConfiguredCoolingDriver : IDeviceCoolingDriver, IPhysicalDeviceDriver
{
    void Configure(PhysicalDeviceBinding? binding, IEnumerable<CoolingEndpointConfiguration> configurations);
}
// Retains the binding and every other endpoint, including their recovery obligations.
public interface IIndividualCoolingConfiguration : IConfiguredCoolingDriver
{
    void ConfigureCoolingEndpoint(PhysicalDeviceBinding binding, string endpointId, CoolingEndpointConfiguration? configuration);
}
public interface IDevicePollingDriver : IDeviceControlDriver
{
    Task PollAsync(CancellationToken ct);
}
// One scheduled read may discover/update several physical-device drivers without opening the hardware more than once.
public interface IDeviceDriverProvider : IAsyncDisposable
{
    string ProviderId { get; }
    string[] ResourceDomains { get; }
    IDeviceControlDriver[] CachedDrivers { get; }
    Task PollAsync(CancellationToken ct);
}
// Compiled providers with an independent recovery path can close failed child drivers as part
// of their own disposal. The runtime must first drain operations, check exact driver ownership,
// and recheck every child's disposal afterwards. Successful parent disposal is not child proof.
public interface IDeviceProviderShutdownCoordinator : IDeviceDriverProvider
{
    bool CanCompleteDriverShutdown(IDeviceControlDriver[] pending);
    DeviceShutdownRecovery[] ReadShutdownRecoveries() => [];
}
// Historical restoration evidence from a closed owning provider, never current telemetry.
// Locator/definition identify the connection that was actually restored. Results carry no values.
public sealed record DeviceShutdownRecovery(string Id, string DeviceId, string Locator, string? DefinitionId,
    DeviceDriverReleaseResult[] Results);
public enum DevicePreparationKind { Polling, Command, LightingStart, LightingFrame, Release }
// Optional compiled-provider boundary for OS metadata and other non-hardware work. The callback submits
// the actual hardware work after preparation; preparation must never open a controller or reserve its bus.
public interface IDeviceOperationPreparation
{
    Task<T> PrepareOperationAsync<T>(DevicePreparationKind kind, Func<CancellationToken, Task<T>> operation, CancellationToken ct);
}
// Compiled drivers may separate durable storage from physical I/O. Every hardware phase must run
// through this scoped executor, await completion, and close its physical handles before returning.
// Drivers serialize their own logical operations; shared resources are held only during each phase.
public interface IDeviceHardwareExecution
{
    // Recheck the enclosing request's ownership/version immediately before physical data transfers.
    void CheckAuthorization();
    Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> hardware, CancellationToken ct);
}
public interface IDevicePhasedOperation
{
    Task<T> RunPhasedOperationAsync<T>(DevicePreparationKind kind, IDeviceHardwareExecution hardware,
        Func<CancellationToken, Task<T>> operation, CancellationToken ct);
}
// Timestamp is from the service's monotonic TimeProvider. Reading this cache must not perform I/O.
public sealed record DeviceSample(string EndpointId, DeviceFeature Feature, double? Value, long Timestamp, string? Problem = null,
    DeviceMeasurementIssue Issue = DeviceMeasurementIssue.None);
public interface IDeviceTelemetrySource
{
    DeviceSample[] ReadCachedSamples();
}

public static class DeviceServiceProtocol
{
    public const int Version = 2, MaximumFrameBytes = 512 * 1024, LeaseSeconds = 12, MaximumTargets = 256;
    public const string PipeName = "FlowDeck.Devices.v2";
    public static bool Id(string? value) => Guid.TryParseExact(value, "N", out var id) && id.ToString("N") == value;
    public static bool IsWrite(DeviceOperation operation) => operation is DeviceOperation.SetDuty or DeviceOperation.SetAutomatic or
        DeviceOperation.ReleaseCooling or DeviceOperation.SetLighting or DeviceOperation.StopLighting or DeviceOperation.ResumeCooling or DeviceOperation.RestoreSavedCooling or DeviceOperation.ApplyLightingPreset or DeviceOperation.AdjustLighting or DeviceOperation.CalibrateCooling or DeviceOperation.IdentifyLighting;
    public static DeviceFeature Feature(DeviceServiceRequest request) => request.Operation switch
    {
        DeviceOperation.SetDuty or DeviceOperation.ResumeCooling or DeviceOperation.RestoreSavedCooling => DeviceFeature.FixedDuty, DeviceOperation.SetAutomatic => DeviceFeature.AutomaticDuty,
        DeviceOperation.ReleaseCooling => DeviceFeature.ReleaseCooling, DeviceOperation.StopLighting => DeviceFeature.RestoreLighting,
        DeviceOperation.SetLighting => request.Lighting!.EffectId switch { "fixed" or "off" => DeviceFeature.FixedColor,
            "breathing" or "color-cycle" => DeviceFeature.ColorFrames, _ => DeviceFeature.NativeEffect },
        _ => throw new ArgumentException("機器への出力操作ではありません")
    };
    public static DeviceServiceRequest Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 2 or > MaximumFrameBytes) throw new IOException("サービス要求の長さが不正です");
        try
        {
            using var json = JsonDocument.Parse(bytes.ToArray()); Unique(json.RootElement);
            var r = json.RootElement.Deserialize<DeviceServiceRequest>(Contract.Json);
            if (r is null || r.Version != Version || !Id(r.Epoch) || !Id(r.Session) || !Id(r.Id) || r.Sequence < 1 ||
                !DeviceConfigurationRules.Text(r.Source, 64) || !Enum.IsDefined(r.Operation) || r.TopologyRevision < 0)
                throw new IOException("サービス要求の形式が不正です");
            if (IsWrite(r.Operation))
            {
                if (r.Targets is not { Length: > 0 and <= MaximumTargets } || r.Targets.Any(t => t is null ||
                    !DeviceConfigurationRules.ValidReference(t.Target) || t.Generation < 1 || t.ExpectedPolicyRevision < 0) ||
                    r.Targets.DistinctBy(t => t.Target).Count() != r.Targets.Length) throw new IOException("操作対象が不正または重複しています");
            }
            else if (r.Targets is not null || r.TopologyRevision != 0) throw new IOException("この要求には機器の指定は不要です");
            var valid = r.Operation switch
            {
                DeviceOperation.SetDuty => r.Duty is >= 0 and <= 100 && r.Profile is null && r.Lighting is null && r.ResultId is null,
                DeviceOperation.SetAutomatic => r.Duty is null && DeviceConfigurationRules.ValidProfileDraft(r.Profile) &&
                    r.Profile!.Curves.Length > 0 && r.Lighting is null && r.ResultId is null,
                DeviceOperation.SetLighting => r.Duty is null && r.Profile is null && DeviceConfigurationRules.ValidProgram(r.Lighting) && r.ResultId is null,
                DeviceOperation.Result or DeviceOperation.CancelCoolingCalibration or DeviceOperation.CancelLightingIdentification => r.Duty is null && r.Profile is null && r.Lighting is null && Id(r.ResultId),
                _ => r.Duty is null && r.Profile is null && r.Lighting is null && r.ResultId is null
            };
            if (!valid || r.Operation == DeviceOperation.IdentifyLighting && r.Targets?.Length != 1 || (r.Operation==DeviceOperation.CalibrateCooling ? !CoolingCalibrationRules.Valid(r.Calibration)||r.Targets?.Length!=1||r.Targets[0].Target.DeviceId!=r.Calibration!.Binding.DeviceId : r.Calibration is not null) ||
                (r.Operation == DeviceOperation.ReadDiagnostics ? !DeviceDiagnostics.Valid(r.Diagnostics) : r.Diagnostics is not null) ||
                (r.Operation == DeviceOperation.ReadStateSnapshot ? !DeviceStateTransfer.Valid(r.StateQuery) : r.StateQuery is not null) ||
                (r.Operation == DeviceOperation.ReadNotifications ? !DeviceNotificationProtocol.Valid(r.Notifications) : r.Notifications is not null) ||
                (r.Operation == DeviceOperation.ReportM5Connection ? !DeviceM5NotificationReport.Valid(r.M5Connection) : r.M5Connection is not null) ||
                r.CoolingGroup is not null && (!DeviceCoolingGroups.Operation(r.Operation)||!DeviceCoolingGroups.Valid(r.CoolingGroup)) ||
                (r.Operation == DeviceOperation.Configuration ? !DeviceConfigurationProtocol.Valid(r.Configuration) : r.Configuration is not null) ||
                (r.Operation == DeviceOperation.LightingLibrary ? !DeviceLightingLibrary.Valid(r.LightingLibrary) : r.LightingLibrary is not null) ||
                (r.Operation == DeviceOperation.ApplyLightingPreset ? !DeviceLightingLibrary.Valid(r.LightingPreset) : r.LightingPreset is not null) ||
                (r.Operation == DeviceOperation.AdjustLighting ? !DeviceLightingAdjustments.Valid(r.LightingAdjustment) : r.LightingAdjustment is not null))
                throw new IOException("操作のパラメーターが不正です");
            return r;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NullReferenceException or NotSupportedException)
        { throw new IOException("サービス要求を解釈できません", e); }
    }
    private static T Decode<T>(byte[] bytes, int maximum = MaximumFrameBytes)
    {
        if (bytes.Length < 2 || bytes.Length > maximum) throw new IOException("サービス応答の長さが不正です");
        try
        {
            using var document = JsonDocument.Parse(bytes); Unique(document.RootElement);
            return document.RootElement.Deserialize<T>(Contract.Json) ?? throw new IOException("サービス応答が空です");
        }
        catch (JsonException e) { throw new IOException("サービス応答の形式が不正です", e); }
    }
    public static DeviceServiceHello ReadHello(byte[] bytes)
    {
        var hello = Decode<DeviceServiceHello>(bytes);
        if (hello.Version != Version || !Id(hello.Epoch) || !Id(hello.Session) || hello.LeaseSeconds != LeaseSeconds ||
            hello.Features is not { Length: <= 64 } || hello.Features.Any(f => !DeviceConfigurationRules.Text(f, 64)) ||
            hello.Features.Distinct().Count() != hello.Features.Length) throw new IOException("サービスの通信バージョン・接続情報が不正です");
        return hello;
    }
    public static DeviceServiceReply ReadReply(byte[] bytes, DeviceServiceHello hello)
        => ReadReply(bytes, hello, MaximumFrameBytes);
    public static DeviceServiceReply ReadStateSnapshot(byte[] bytes, DeviceServiceHello hello)
    {
        var reply = ReadReply(bytes, hello, DeviceStateTransfer.MaximumBytes);
        if (!reply.Success || reply.State is null || reply.Error is not null || reply.Result is not null || reply.Configuration is not null ||
            reply.LightingLibrary is not null || reply.Notifications is not null || reply.Diagnostics is not null || reply.StateChunk is not null)
            throw new IOException("機器一覧のスナップショット形式が不正です");
        return reply;
    }
    private static DeviceServiceReply ReadReply(byte[] bytes, DeviceServiceHello hello, int maximum)
    {
        var reply = Decode<DeviceServiceReply>(bytes, maximum);
        if (reply.Version != Version || reply.Epoch != hello.Epoch || reply.Session != hello.Session || !Id(reply.Id) ||
            reply.StateChunk is { } chunk && (!DeviceStateTransfer.Valid(chunk) || !reply.Success || reply.Error is not null ||
                reply.State is not null || reply.Result is not null || reply.Configuration is not null || reply.LightingLibrary is not null ||
                reply.Notifications is not null || reply.Diagnostics is not null) ||
            reply.Diagnostics is { } diagnostics && !DeviceDiagnostics.Valid(diagnostics) ||
            reply.Notifications is { } notifications && !DeviceNotificationProtocol.Valid(notifications) ||
            reply.Error is { Length: > 16384 } || reply.Configuration is { } config && !DeviceConfigurationProtocol.Valid(config) ||
            reply.LightingLibrary is { } library && !DeviceLightingLibrary.Valid(library) ||
            reply.Result is { } result && (!Id(result.RequestId) || !Enum.IsDefined(result.Outcome) ||
                !DeviceConfigurationRules.Text(result.Source, 64) || result.Targets is not { Length: <= MaximumTargets } ||
                result.Targets.Any(r => r is null || !DeviceConfigurationRules.ValidReference(r.Target) || r.Generation < 1 || r.PolicyRevision < 0 || !Enum.IsDefined(r.Outcome) ||
                    r.ReportedDuty is < 0 or > 100 || r.RequestedDuty is < 0 or > 100 || !ValidSave(r.Persistence) || !CoolingCalibrationRules.Valid(r.Calibration) || !DeviceLightingIdentification.Valid(r.Identification) || r.ReportedLighting is not null && !DeviceConfigurationRules.ValidProgram(r.ReportedLighting)) ||
                result.Targets.DistinctBy(t => t.Target).Count() != result.Targets.Length) ||
            reply.State is { } state && (state.Inventory is null || state.Inventory.Epoch != hello.Epoch || state.Inventory.Revision < 0 || state.Inventory.TopologyRevision < 0 ||
                state.Inventory.Devices is not { Length: <= DeviceRegistry.MaximumDevices } || state.Endpoints is not { Length: <= DeviceRegistry.MaximumDevices * DeviceRegistry.MaximumEndpoints } ||
                state.Inventory.Devices.Any(d => d is null || d.Generation < 1 || !DeviceRegistry.Valid(d.Device)) ||
                state.Inventory.Devices.DistinctBy(d => d.Device.Id).Count() != state.Inventory.Devices.Length ||
                state.Endpoints.Any(e => e is null || !DeviceConfigurationRules.ValidReference(e.Target) || e.Generation < 1 || e.PolicyRevision < 0 || !Enum.IsDefined(e.Outcome) ||
                    e.RequestedDuty is < 0 or > 100 || e.ReportedDuty is < 0 or > 100 || !ValidSave(e.Persistence) || !ValidLighting(e.Lighting) || !CoolingCalibrationRules.Valid(e.Calibration) || !DeviceLightingIdentification.Valid(e.Identification) ||
                    e.RequestedLighting is not null && !DeviceConfigurationRules.ValidProgram(e.RequestedLighting) ||
                    e.ReportedLighting is not null && !DeviceConfigurationRules.ValidProgram(e.ReportedLighting) ||
                    e.Cooling is { } cooling && (!Enum.IsDefined(cooling.Mode) || cooling.TargetDuty is < 0 or > 100 || cooling.CanResume && !cooling.Faulted ||
                        cooling.Fault is { Length: > 16384 } || cooling.ProfileId is not null && !DeviceConfigurationRules.ProfileId(cooling.ProfileId) || cooling.ProfileRevision < 0)) ||
                state.Endpoints.DistinctBy(e => e.Target).Count() != state.Endpoints.Length ||
                state.Telemetry is { } telemetry && (telemetry.Length > DeviceRegistry.MaximumDevices * DeviceRegistry.MaximumEndpoints * 3 ||
                    telemetry.Any(t => t is null || !DeviceConfigurationRules.ValidReference(t.Target) || t.Feature is not (DeviceFeature.Temperature or DeviceFeature.Rpm or DeviceFeature.DutyRead) ||
                        t.Value is { } v && !double.IsFinite(v) || t.AgeSeconds is { } age && (!double.IsFinite(age) || age < 0) ||
                        !Enum.IsDefined(t.Issue) || t.Issue != DeviceMeasurementIssue.None && (t.Value is not null || !t.Stale) ||
                        !t.Stale && (t.Value is null || t.AgeSeconds is null)) ||
                    telemetry.DistinctBy(t => (t.Target, t.Feature)).Count() != telemetry.Length)))
            throw new IOException("古い接続または不正なサービス応答を破棄しました");
        return reply;
    }
    private static bool ValidSave(DevicePolicySaveResult? value) => value is null || Enum.IsDefined(value.State) && value.Message is { Length: <= 16384 };
    private static bool ValidLighting(DeviceLightingStatus? value) => value is null || Id(value.GroupId) && value.Frames >= 0 && value.DroppedFrames >= 0 &&
        value.LastColor is null or { Valid: true } && double.IsFinite(value.MaximumWriteMilliseconds) && value.MaximumWriteMilliseconds >= 0 && value.Fault is null or { Length: <= 16384 } &&
        value.Mirror is null or { Valid: true };
    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in element.EnumerateObject()) { if (!keys.Add(p.Name)) throw new IOException("要求項目が重複しています"); Unique(p.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
    }
}
