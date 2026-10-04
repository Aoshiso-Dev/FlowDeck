using System.Security.Cryptography;

namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private sealed record DiagnosticTarget(EndpointReference Target, long? Generation, Outcome Outcome, int? ReportedDuty);
    private sealed record DiagnosticEvent(long Sequence, DateTimeOffset Time, DeviceOperation? Operation,
        DeviceConfigurationOperation? ConfigurationOperation, DeviceReleaseReason? ReleaseReason, string? DeviceId, double Milliseconds,
        Outcome Outcome, DiagnosticTarget[] Targets, bool FailedWithoutTarget);
    private sealed record DiagnosticTransfer(string Id, string Owner, DeviceDiagnosticScope Scope, bool IncludeHardware, long Started, byte[] Data, string Hash);
    private readonly Queue<DiagnosticEvent> diagnosticEvents = new();
    private long diagnosticSequence;
    private DiagnosticTransfer? diagnosticTransfer;
    private bool diagnosticCapturing;
    private Task diagnosticWork = Task.CompletedTask;
    private readonly IDeviceDiagnosticSystemSource? diagnosticSystem;
    private double DiagnosticElapsed(long start) => Math.Max(0, clock.GetElapsedTime(start).TotalMilliseconds);
    private void RecordDiagnosticOperation(DeviceServiceRequest request, DeviceServiceReply reply, long start)
    {
        if (request.Operation is DeviceOperation.State or DeviceOperation.ReadStateSnapshot or DeviceOperation.Renew or DeviceOperation.Result or DeviceOperation.ReadDiagnostics or
            DeviceOperation.ReadNotifications or DeviceOperation.ReportM5Connection || request.Operation == DeviceOperation.Configuration &&
            request.Configuration?.Operation is DeviceConfigurationOperation.Snapshot or DeviceConfigurationOperation.SnapshotWithCandidates or DeviceConfigurationOperation.ReadSnapshot) return;
        var targets = reply.Result?.Targets.Select(t => new DiagnosticTarget(t.Target, t.Generation, t.Outcome, t.ReportedDuty)).ToArray() ?? [];
        AddDiagnosticEvent(new(++diagnosticSequence, clock.GetUtcNow(), request.Operation, request.Configuration?.Operation, null, null,
            DiagnosticElapsed(start), reply.Result?.Outcome ?? (reply.Success ? Outcome.Confirmed : Outcome.Rejected), targets, !reply.Success && targets.Length == 0));
    }
    private void AddDiagnosticEvent(DiagnosticEvent value)
    {
        diagnosticEvents.Enqueue(value);
        while (diagnosticEvents.Count > DeviceDiagnostics.HistoryLimit) diagnosticEvents.Dequeue();
    }
    private void RecordDiagnosticRelease(IDeviceControlDriver driver, DeviceReleaseReason reason, long start, DeviceDriverReleaseResult[]? results, bool failed)
    {
        var targets = results?.Select(r => new DiagnosticTarget(new(driver.DeviceId, r.EndpointId), null, r.Result.Outcome, r.Result.ReportedDuty)).ToArray() ?? [];
        AddDiagnosticEvent(new(++diagnosticSequence, clock.GetUtcNow(), null, null, reason, driver.DeviceId, DiagnosticElapsed(start),
            failed ? Outcome.Unknown : targets.Length == 0 ? Outcome.Confirmed : Aggregate(targets.Select(t => t.Outcome).ToArray()), targets, failed && targets.Length == 0));
    }
    private DeviceServiceReply ReadDiagnosticChunkCore(DeviceServiceRequest request)
    {
        var query = request.Diagnostics!; var transfer = diagnosticTransfer;
        if (transfer is null || transfer.Owner != request.Session || transfer.Id != query.TransferId || transfer.Scope != query.Scope || transfer.IncludeHardware != query.IncludeHardware ||
            clock.GetElapsedTime(transfer.Started).TotalSeconds is < 0 or >= DeviceDiagnostics.TransferSeconds || query.Offset >= transfer.Data.Length)
            return Reject(request, "診断の取得期限または接続が変わりました。もう一度作成してください");
        return DiagnosticChunkReply(request, transfer, query.Offset);
    }
    private DeviceServiceReply DiagnosticChunkReply(DeviceServiceRequest request, DiagnosticTransfer transfer, int offset) => Reply(request) with
    {
        Diagnostics = new(transfer.Id, transfer.Scope, transfer.Data.Length, transfer.Hash, offset,
            transfer.Data.AsSpan(offset, Math.Min(DeviceDiagnostics.ChunkBytes, transfer.Data.Length - offset)).ToArray())
    };
    private void QueueDiagnosticsCore(DeviceServiceRequest request, TaskCompletionSource<DeviceServiceReply> completion)
    {
        if (request.Diagnostics!.TransferId is not null) { completion.SetResult(ReadDiagnosticChunkCore(request)); return; }
        if (diagnosticCapturing) { completion.SetResult(Reject(request, "診断情報を作成中です")); return; }
        diagnosticCapturing = true; pendingCount++; diagnosticTransfer = null;
        var token = sessionLifetime!.Token;
        diagnosticWork = Task.Run(async () =>
        {
            try
            {
                DeviceDiagnosticReport report;
                DeviceDiagnosticKernelDriver[]? kernel = null;
                if (diagnosticSystem is not null)
                {
                    try { kernel = await diagnosticSystem.ReadKernelDriversAsync(token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch { kernel = [new("PawnIO", DeviceDiagnosticReadState.Failed, DeviceDiagnosticReadState.NotObserved, null,
                        DeviceDiagnosticReadState.NotObserved, null, null)]; }
                }
                var environment = DeviceDiagnostics.EnvironmentInfo(kernel);
                DeviceDiagnosticConfigurationCapture? config = null;
                if (request.Diagnostics.Scope.HasFlag(DeviceDiagnosticScope.Configuration) && configurationHandler is IDeviceDiagnosticConfiguration source)
                    config = await source.ReadDiagnosticsAsync(token).ConfigureAwait(false);
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    // Only cached observations/samples are read. No poll, recovery, setting adoption or hardware operation is scheduled.
                    report = CaptureDiagnosticsCore(request.Diagnostics.Scope, config?.Configuration, environment, request.Diagnostics.IncludeHardware);
                }
                if (config is not null && configurationHandler is IDeviceDiagnosticConfiguration after)
                {
                    var current = await after.ReadDiagnosticsAsync(token).ConfigureAwait(false);
                    report = report with { ConfigurationStableDuringCapture = config.Generation == current.Generation && !report.ConfigurationChanging };
                }
                var bytes = DeviceDiagnostics.Encode(report);
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (request.Session != session || stopping || releasing) throw new IOException("診断の接続が終了しました");
                    diagnosticTransfer = new(Guid.NewGuid().ToString("N"), request.Session, request.Diagnostics.Scope, request.Diagnostics.IncludeHardware, clock.GetTimestamp(), bytes,
                        Convert.ToHexStringLower(SHA256.HashData(bytes)));
                    completion.TrySetResult(DiagnosticChunkReply(request, diagnosticTransfer, 0));
                }
            }
            catch (Exception) { lock (gate) completion.TrySetResult(Reject(request, "診断情報を作成できませんでした。接続と対象範囲を確認してください")); }
            finally { lock (gate) { diagnosticCapturing = false; pendingCount--; TrimRequests(); } }
        });
    }
    private DeviceDiagnosticReport CaptureDiagnosticsCore(DeviceDiagnosticScope scope, DeviceDiagnosticConfiguration? configuration, DeviceDiagnosticEnvironment environment, bool includeHardware)
    {
        var inventory = registry.Snapshot;
        var deviceIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var endpointIds = new Dictionary<EndpointReference, string>();
        var providerIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var resourceIds = new Dictionary<string, string>(StringComparer.Ordinal);
        static string Alias(Dictionary<string, string> map, string raw, string prefix)
        { if (!map.TryGetValue(raw, out var value)) map[raw] = value = prefix + (map.Count + 1); return value; }
        string Device(string id) => Alias(deviceIds, id, "device-");
        string Endpoint(EndpointReference target)
        { if (!endpointIds.TryGetValue(target, out var value)) endpointIds[target] = value = "endpoint-" + (endpointIds.Count + 1); return value; }
        EndpointReference Target(EndpointReference target) => new(Device(target.DeviceId), Endpoint(target));
        var telemetry = scope.HasFlag(DeviceDiagnosticScope.Measurements) ? TelemetryCore(inventory).ToLookup(t => t.Target) : null;
        var devices = inventory.Devices.Select(instance =>
        {
            var d = instance.Device;
            var type = drivers.GetValueOrDefault(d.Id)?.GetType();
            var implementation = type?.Namespace?.StartsWith("FlowDeck.", StringComparison.Ordinal) == true ? type.Name : null;
            return new DeviceDiagnosticDevice(Device(d.Id), Alias(providerIds, d.Provider, "provider-"),
                d.ResourceDomains.Select(r => Alias(resourceIds, r, "resource-")).ToArray(), d.Kind, d.IdentityConfidence, d.DefinitionId is not null,
                instance.Generation, instance.Connected, implementation, d.Endpoints.Select(e =>
                {
                    var target = new EndpointReference(d.Id, e.Id); var execution = states.GetValueOrDefault(target);
                    return new DeviceDiagnosticEndpoint(Endpoint(target), e.Kind, e.Capabilities.Select(c => new DeviceDiagnosticCapability(c.Feature,
                        c.Support, c.Evidence, c.Availability, c.Reason is not null)).ToArray(), e.NativeLightingEffects?.Length ?? 0,
                        telemetry?[target].Select(t => new DeviceDiagnosticMeasurement(t.Feature, t.Value, t.AgeSeconds, t.Stale, t.Problem is not null, t.Issue)).ToArray(),
                        execution is null ? null : new(execution.Generation, execution.PolicyRevision, execution.Outcome, execution.Owned,
                            execution.RequestedDuty, execution.ReportedDuty, execution.Message is not null,
                            recoveryRequired.Contains(target) || lightingRecoveryRequired.Contains(target), execution.Cooling?.Mode,
                            execution.Cooling?.Faulted == true, execution.Cooling?.CanResume == true, execution.Cooling?.Restarting == true,
                            execution.Lighting?.Running, execution.Lighting?.Frames, execution.Lighting?.DroppedFrames,
                            execution.Lighting?.MaximumWriteMilliseconds, execution.Lighting?.Fault is not null));
                }).ToArray(), includeHardware ? (instance.Connected ? DeviceDiagnosticHardwareRules.Capture(drivers.GetValueOrDefault(d.Id)) :
                    new(DeviceDiagnosticReadState.Missing, [])) : null);
        }).ToArray();
        var events = scope.HasFlag(DeviceDiagnosticScope.Operations) ? diagnosticEvents.Select(e => new DeviceDiagnosticEvent(e.Sequence, e.Time, e.Operation,
            e.ConfigurationOperation, e.ReleaseReason, e.DeviceId is null ? null : Device(e.DeviceId), e.Milliseconds, e.Outcome, e.Targets.Select(t =>
                new DeviceDiagnosticTarget(Device(t.Target.DeviceId), Endpoint(t.Target), t.Generation, t.Outcome, t.ReportedDuty)).ToArray(), e.FailedWithoutTarget)).ToArray() : null;
        return new(includeHardware ? 2 : 1, clock.GetUtcNow(), demo, scope, environment, releasing, configurationChanging, lifecycleError is not null,
            inventory.TopologyRevision, devices, configuration is null ? null : configuration with {
                Cooling = configuration.Cooling.Select(c => c with { Target = Target(c.Target) }).ToArray() }, configuration is not null,
            false, events, events is null ? 0 : diagnosticSequence - diagnosticEvents.Count,
            ["credentials", "wifi-settings", "machine-name", "user-name", "serials", "paths", "raw-identifiers", "user-labels", "free-form-messages",
                "raw-logs", "loaded-kernel-driver-version-not-observed",
                .. environment.InstalledKernelDriverVersion is null ? new[] { "installed-kernel-driver-version-not-observed" } : []]);
    }
}
