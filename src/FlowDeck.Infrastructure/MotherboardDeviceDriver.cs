using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record MotherboardCoolingOptions(string RecoveryDirectory, string BootIdentity, TimeProvider? Clock = null);
public sealed record MotherboardCoolingIdentity(string NativeId, string ConnectionId, string Fingerprint, string DefinitionId)
{
    public static MotherboardCoolingIdentity From(HardwareSensorDevice d) => new(d.NativeId, d.ConnectionId, d.Fingerprint,
        d.DefinitionId ?? throw new InvalidOperationException("マザー定義がありません"));
}
public enum MotherboardFanOperation { Read, Write, Release }
public sealed record MotherboardFanCommand(MotherboardCoolingIdentity Identity, string EndpointId, MotherboardFanOperation Operation,
    int? Duty = null, int? ExpectedDuty = null, long? Deadline = null);
public interface IMotherboardCoolingBackend
{
    Task<CpuFanReading?> ExecuteFanAsync(MotherboardFanCommand command, IFanRecoveryJournal journal, CancellationToken ct);
}
internal sealed class MotherboardCommandRejectedException(string message, DeviceDriverFailure failure = DeviceDriverFailure.None) : IOException(message)
{ public DeviceDriverFailure Failure { get; } = failure; }

/// <summary>One board, shared telemetry and individually configured cooling endpoints. The backend owns the sole LHM handle.</summary>
public sealed partial class MotherboardDeviceDriver : IConfiguredCoolingDriver, IDeviceTelemetrySource, ILegacyMigrationSource, IDeviceSetupDriver, IDeviceCoolingCalibrationDriver
{
    private readonly HardwareSensorProvider owner;
    private readonly HardwareSensorDeviceDriver sensors;
    private readonly IMotherboardCoolingBackend backend;
    private readonly MotherboardCoolingOptions options;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly SemaphoreSlim io = new(1);
    private readonly Dictionary<string, (CpuFanReading Reading, long Revision)> measured = [];
    private readonly HashSet<string> attempted = [];
    private Dictionary<string, CoolingEndpointConfiguration> cooling = [];
    private FanRecoveryJournal? journal;
    private PhysicalDeviceBinding? binding;
    private MotherboardCoolingIdentity? identity;
    private string? recoveryError;
    private bool disposed;
    public string DeviceId => sensors.DeviceId;
    public PhysicalDeviceBinding? BindingCandidate => sensors.BindingCandidate;
    public MigratedReference[] LegacyReferences() => sensors.LegacyReferences();
    public EndpointSetupOptions[] DescribeSetup()
    {
        lock (gate) return Definition?.Ports.Select(p => new EndpointSetupOptions(p.Id, [CoolingExitAction.ReleaseToDevice], [], CoolingCalibrationSupported: true)).ToArray() ?? [];
    }
    internal MotherboardDeviceDriver(HardwareSensorProvider owner, string id, IMotherboardCoolingBackend backend, MotherboardCoolingOptions options, HardwareSensorDevice discovered)
    {
        this.owner = owner; sensors = new(owner, id); this.backend = backend; this.options = options;
        clock = options.Clock ?? TimeProvider.System;
        identity = MotherboardCoolingIdentity.From(discovered);
        try { journal = LoadJournal(discovered); }
        catch (Exception e) { recoveryError = "マザー復旧記録: " + e.Message; }
    }
    private FanRecoveryJournal LoadJournal(HardwareSensorDevice current)
    {
        var next = new FanRecoveryJournal(Path.Combine(options.RecoveryDirectory, "board-" + SettingsTransfer.Hash(DeviceId)),
            options.BootIdentity, HardwareSensorDeviceDriver.BindingFor(DeviceId, current));
        if (next.PreviousBoot) next.ArchivePreviousBoot();
        return next;
    }
    private MotherboardDefinition? Definition => owner.Cached(DeviceId) is { DefinitionId: { } id }
        ? MotherboardDefinitionCatalog.BuiltIn.Boards.SingleOrDefault(b => b.Id == id) : null;
    public void Configure(PhysicalDeviceBinding? binding, IEnumerable<CoolingEndpointConfiguration> configurations)
    {
        var entries = configurations.Take(257).Select(Contract.Copy).ToArray();
        if (!io.Wait(0)) throw new IOException("端子の処理中は設定を変更できません");
        try
        {
            var current = owner.Cached(DeviceId) ?? throw new IOException("マザーを検出できません");
            var definition = Definition ?? throw new IOException("マザー定義がありません");
            if (entries.Length > definition.Ports.Count || entries.Any(c => c is null || c.Target is null || c.Target.DeviceId != DeviceId ||
                !definition.Ports.Any(p => p.Id == c.Target.EndpointId) || !DeviceConfigurationRules.ValidSafety(c.Safety)) ||
                entries.DistinctBy(c => c.Target).Count() != entries.Length || binding is not null && binding != BindingCandidate)
                throw new ArgumentException("マザーの関連付け・端子設定が不正です");
            bool reload;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (calibrating.Count>0 || attempted.Count != 0 || journal?.Pending.Length > 0) throw new IOException("端子の制御を終了してから設定を変更してください");
                reload = identity != MotherboardCoolingIdentity.From(current) || journal is null || recoveryError is not null;
            }
            var nextJournal = reload ? LoadJournal(current) : journal;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (HardwareSensorDeviceDriver.BindingFor(DeviceId, current) != BindingCandidate) throw new IOException("設定中にマザー構成が変更されました");
                journal = nextJournal; identity = MotherboardCoolingIdentity.From(current); recoveryError = null;
                this.binding = binding; cooling = entries.ToDictionary(c => c.Target.EndpointId);
            }
        }
        finally { io.Release(); }
    }
    public CoolingEndpointConfiguration? CoolingConfiguration(string endpointId)
    { lock (gate) return cooling.TryGetValue(endpointId, out var c) ? Contract.Copy(c) : null; }
    private string? Ready(string endpointId, bool requireCachedTelemetry = true)
    {
        if(calibrating.Contains(endpointId)) return "この端子は試験中です";
        if (disposed) return "マザードライバーは終了しています";
        if (recoveryError is not null) return recoveryError;
        if (owner.Cached(DeviceId) is not { DefinitionId: not null } current || identity != MotherboardCoolingIdentity.From(current)) return "マザーの個体・接続・構成を確認してください";
        if (binding is null || binding != BindingCandidate) return "マザーの物理関連付けを確認してください";
        if (Definition?.Ports.SingleOrDefault(p => p.Id == endpointId) is not { } port) return "未対応のマザー端子です";
        var rpm = current.Sensors.SingleOrDefault(s => s.Id == port.SensorId && s.Feature == DeviceFeature.Rpm);
        var duty = current.Sensors.SingleOrDefault(s => s.Feature == DeviceFeature.DutyRead && s.HardwareId == port.HardwareId && s.Id == port.HardwareId + "/control/" + port.Channel);
        if (requireCachedTelemetry && new[] { rpm, duty }.Any(s => s?.Value is null || s.Problem is not null || clock.GetElapsedTime(s.Timestamp).TotalSeconds is < 0 or >= 3))
            return "端子の新しい実測値を取得できません";
        if (!cooling.TryGetValue(endpointId, out var config) || !config.WiringConfirmed) return "端子ごとの配線と最低出力を確認してください";
        if (DeviceConfigurationRules.SafetyReadiness(config.Safety) is { } error) return error;
        if (config.Safety.ExitAction != CoolingExitAction.ReleaseToDevice) return "マザー管理へ戻す終了方法を指定してください";
        if (config.Safety.Stop is not null || config.Safety.Minimum < 1) return "この端子ドライバーは停止出力に対応していません";
        if (journal is null) return "端子の復旧記録を利用できません";
        if (!attempted.Contains(endpointId) && journal.Pending.Any(p => p.Chip == port.Chip && p.Channel == port.Channel))
            return "前回の端子制御を復旧してください";
        return null;
    }
    public string? ValidateCoolingSetup(string endpointId) { lock (gate) return Ready(endpointId); }
    public DeviceObservation? Observe()
    {
        lock (gate)
        {
            var observation = sensors.Observe(); if (observation is null) return null;
            return observation with { Endpoints = observation.Endpoints.Select(e =>
            {
                if (e.Kind != EndpointKind.Cooling || Definition?.Ports.Any(p => p.Id == e.Id) != true) return e;
                var problem = Ready(e.Id);
                DeviceCapability Cap(DeviceFeature f, bool ready) => new(f, ImplementationSupport.Supported, VerificationEvidence.Automated,
                    ready ? DeviceAvailability.Ready : DeviceAvailability.NeedsSetup, ready ? null : problem);
                return e with { Capabilities = [.. e.Capabilities, Cap(DeviceFeature.FixedDuty, problem is null),
                    Cap(DeviceFeature.AutomaticDuty, problem is null), Cap(DeviceFeature.ReleaseCooling, true)] };
            }).ToArray() };
        }
    }
    public DeviceSample[] ReadCachedSamples()
    {
        lock (gate)
        {
        var current = owner.Cached(DeviceId); var revision = owner.Revision;
        var sameIdentity = current?.DefinitionId is not null && identity == MotherboardCoolingIdentity.From(current);
        return sensors.ReadCachedSamples().Select(s => s.Issue == DeviceMeasurementIssue.None && s.Problem is null && sameIdentity && measured.TryGetValue(s.EndpointId, out var measuredValue) &&
            (measuredValue.Reading.Timestamp > s.Timestamp || measuredValue.Reading.Timestamp == s.Timestamp && measuredValue.Revision == revision) &&
            s.Feature is DeviceFeature.Rpm or DeviceFeature.DutyRead ? s with { Value = s.Feature == DeviceFeature.Rpm ? measuredValue.Reading.Rpm : measuredValue.Reading.Duty,
                Timestamp = measuredValue.Reading.Timestamp, Problem = null } : s).ToArray();
        }
    }
    public string? Validate(DeviceEndpointAction action)
    {
        lock (gate)
        {
            if (action is null || Definition?.Ports.Any(p => p.Id == action.EndpointId) != true) return "マザー端子が不正です";
            if (action.Operation == DeviceOperation.ReleaseCooling) return null;
            return "冷却方針を共通サービスから操作してください";
        }
    }
    public async Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct)
    {
        await io.WaitAsync(ct).ConfigureAwait(false);
        try { return Validate(action) is { } error ? new(Outcome.Rejected, error) : await ReleaseOneAsync(action.EndpointId, ct).ConfigureAwait(false); }
        finally { io.Release(); }
    }
    private MotherboardFanCommand Command(string endpoint, MotherboardFanOperation operation, int? duty = null, int? expectedDuty = null, long? deadline = null) =>
        new(identity ?? throw new IOException("マザーの物理識別がありません"), endpoint, operation, duty, expectedDuty, deadline);
    private async Task<CpuFanReading?> ExecuteAsync(MotherboardFanCommand command, CancellationToken ct)
    {
        var reading = await backend.ExecuteFanAsync(command, journal ?? throw new IOException("復旧記録がありません"), ct).ConfigureAwait(false);
        if (reading is not null)
        {
            if (!double.IsFinite(reading.Duty) || reading.Duty is < 0 or > 100 || reading.Rpm is < 0 or > 30000 ||
                clock.GetElapsedTime(reading.Timestamp).TotalSeconds is < 0 or >= 3) throw new IOException("マザーの取得結果が未取得・範囲外・古い状態です");
            lock (gate) measured[command.EndpointId] = (reading, owner.Revision);
        }
        return reading;
    }
    public async Task<DeviceDriverResult> ApplyCoolingAsync(CoolingActuation actuation, CancellationToken ct)
    {
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CoolingSafety safety;
            lock (gate)
            {
                if (Ready(actuation.EndpointId, requireCachedTelemetry: !actuation.Fallback) is { } error) return new(Outcome.Rejected, error);
                safety = Contract.Copy(cooling[actuation.EndpointId].Safety);
                if (!DeviceConfigurationRules.SameSafety(safety, actuation.Safety)) return new(Outcome.Rejected, "安全条件が変更されています");
            }
            var before = await ExecuteAsync(Command(actuation.EndpointId, MotherboardFanOperation.Read), ct).ConfigureAwait(false)
                ?? throw new IOException("端子の実測がありません");
            var started = HardwareWriteDeadline.Now; var authorization = actuation.Authorize();
            if (authorization.Error is { } errorMessage) return new(Outcome.Rejected, errorMessage, Failure: authorization.Failure);
            long? deadline = authorization.ValidFor is { } budget ? HardwareWriteDeadline.FromBudget(started, budget) : null;
            var current = (int)Math.Round(before.Duty); var duty = actuation.Fallback ? Math.Max(current, actuation.Duty) : actuation.Duty;
            lock (gate)
            {
                if (Ready(actuation.EndpointId, requireCachedTelemetry: !actuation.Fallback) is { } error) return new(Outcome.Rejected, error);
                if (duty is < 1 or > 100 || duty < safety.Minimum) return new(Outcome.Rejected, "端子の許容出力範囲外です");
                attempted.Add(actuation.EndpointId);
            }
            var actual = await ExecuteAsync(Command(actuation.EndpointId, MotherboardFanOperation.Write, duty, current, deadline), ct).ConfigureAwait(false);
            for (var i = 0; i < 10 && (actual is null || Math.Round(actual.Duty) != duty); i++)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                actual = await ExecuteAsync(Command(actuation.EndpointId, MotherboardFanOperation.Read), ct).ConfigureAwait(false);
            }
            var confirmed = actual is not null && Math.Round(actual.Duty) == duty;
            return new(confirmed ? Outcome.Confirmed : Outcome.Unknown, confirmed ? "端子の出力を確認しました" : "端子の出力を確認できません",
                actual is null ? null : (int)Math.Round(actual.Duty));
        }
        catch (MotherboardCommandRejectedException e)
        {
            lock (gate)
            {
                var port = Definition?.Ports.SingleOrDefault(p => p.Id == actuation.EndpointId);
                if (port is not null && journal is not null && !journal.Pending.Any(p => p.Chip == port.Chip && p.Channel == port.Channel)) attempted.Remove(actuation.EndpointId);
            }
            return new(Outcome.Rejected, e.Message, Failure: e.Failure);
        }
        catch (Exception e) { return new(Outcome.Unknown, "端子の操作結果は未確認です: " + e.Message); }
        finally { io.Release(); }
    }
    private async Task<DeviceDriverResult> ReleaseOneAsync(string endpoint, CancellationToken ct)
    {
        if (recoveryError is not null || journal is null) return new(Outcome.Unknown, recoveryError ?? "復旧記録がありません");
        var reading = await ExecuteAsync(Command(endpoint, MotherboardFanOperation.Release), ct).ConfigureAwait(false);
        lock (gate) { attempted.Remove(endpoint); calibrating.Remove(endpoint); }
        return new(Outcome.Confirmed, "変更前のマザー管理設定へ戻しました", reading is null ? null : (int)Math.Round(reading.Duty));
    }
    public async Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct)
    {
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string[] endpoints;
            lock (gate) endpoints = attempted.Union((journal?.Pending ?? []).Select(p => MotherboardDefinitionCatalog.BuiltIn.Boards
                .Single(b => b.Id == identity!.DefinitionId).Ports.Single(port => port.Chip == p.Chip && port.Channel == p.Channel).Id)).Order().ToArray();
            var result = new List<DeviceDriverReleaseResult>();
            foreach (var endpoint in endpoints)
            {
                try { result.Add(new(endpoint, await ReleaseOneAsync(endpoint, ct).ConfigureAwait(false))); }
                catch (Exception e) { result.Add(new(endpoint, new(Outcome.Unknown, "端子を復旧できません: " + e.Message))); }
            }
            return result.ToArray();
        }
        finally { io.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        await ReleaseAsync(DeviceReleaseReason.ServiceStopping, CancellationToken.None).ConfigureAwait(false);
    }
}
