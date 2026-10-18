using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed partial class EneMemoryDeviceDriver : IConfiguredLightingDriver, IDeviceLightingCompositionDriver, IDevicePollingDriver, ILegacyMigrationSource, IDeviceOperationPreparation, IDevicePhasedOperation, IDeviceSetupDriver
{
    internal const string Endpoint = "zone:0";
    private readonly object gate = new();
    private readonly SemaphoreSlim io = new(1);
    private readonly AsyncLocal<IDeviceHardwareExecution?> hardware = new();
    private readonly IEneMemoryDeviceBackend backend;
    private readonly string boot;
    private readonly EneMemoryRecovery? journal;
    private readonly string? journalError;
    private EneMemoryConnection? connection;
    private EneMemoryState? observed;
    private PhysicalDeviceBinding? binding;
    private LightingEndpointConfiguration? configuration;
    private EndpointSetup? setup;
    private Stream? active;
    private bool owned, faulted, disposed, nativeApplying;
    private string? readError;
    public string DeviceId { get; }
    public DeviceLightingLimits LightingLimits { get { lock (gate) return (EneMemoryDefinition.Find(connection?.DefinitionId ?? LightingDeviceDefinitionCatalog.EneId)
        ?? throw new IOException("メモリ照明の対応定義がありません")).Limits; } }
    public async Task<T> RunPhasedOperationAsync<T>(DevicePreparationKind kind, IDeviceHardwareExecution execution,
        Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        // Frames already prepare and finalize outside scheduling. Keep their existing timing boundary.
        if (kind == DevicePreparationKind.LightingFrame) return await execution.RunAsync(operation, ct).ConfigureAwait(false);
        var before = hardware.Value; hardware.Value = execution;
        try { return await operation(ct).ConfigureAwait(false); }
        finally { hardware.Value = before; }
    }
    private Task<T> ExecuteHardwareAsync<T>(EneMemoryConnection connection, bool full, Func<IEneMemorySession, T> operation, CancellationToken ct) =>
        hardware.Value is { } execution ? execution.RunAsync(token => backend.ExecuteAsync(connection, full, operation, token), ct) :
            backend.ExecuteAsync(connection, full, operation, ct);
    public Task<T> PrepareOperationAsync<T>(DevicePreparationKind kind, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        bool needsMetadata;
        lock (gate) needsMetadata = kind is not (DevicePreparationKind.Polling or DevicePreparationKind.LightingFrame) &&
            (kind != DevicePreparationKind.Release || active is not null || journal?.Pending is not null);
        return needsMetadata ? backend.PrepareOperationAsync(operation, ct) : operation(ct);
    }
    public PhysicalDeviceBinding? BindingCandidate { get { lock (gate) return Candidate(); } }
    public EndpointSetupOptions[] DescribeSetup()
    {
        lock (gate) return connection is null || observed is null ? [] :
            [new(Endpoint, [], [LightingExitAction.FixedColor, LightingExitAction.Off, LightingExitAction.RestoreOriginal], 8, 8, 8)];
    }
    internal EneMemoryDeviceDriver(string id, EneMemoryConnection connection, string directory, string boot, IEneMemoryDeviceBackend backend)
    {
        DeviceId = id; this.connection = connection; this.boot = boot; this.backend = backend;
        try { journal = new(directory, id); } catch (Exception e) { journalError = "メモリ照明の復旧記録を読めません: " + e.Message; }
    }
    internal void SetConnection(EneMemoryConnection? next)
    {
        lock (gate)
        {
            if (next != connection) { connectionPermission?.Revoke(); observed = null; owned = false; readError = "メモリ照明の接続が変わりました"; }
            connection = next;
        }
    }
    private PhysicalDeviceBinding? Candidate() => connection is { } c && observed is not null ? EneMemoryRecovery.BindingFor(DeviceId, c) : null;
    private void Guard(EneMemoryConnection c, PhysicalDeviceBinding b, CancellationToken ct, EneMemoryRecoveryRecord? record = null, Action? extra = null)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate) if (disposed || connection != c || Candidate() != b) throw new IOException("メモリ照明の個体・接続・構成が変わりました");
        backend.Check(c); if (record is not null) journal!.CheckCurrent(record); extra?.Invoke(); ct.ThrowIfCancellationRequested();
    }
    private string? Ready()
    {
        if (disposed || connection is null || observed is null) return "メモリ照明を取得できません";
        if (readError is not null) return readError;
        if (!ConnectionPermissionCurrent()) return "接続限定の照明設定が失効しました";
        if (journalError is not null) return journalError;
        if (!KnownCurrentModel()) return "メモリ照明の対応モデルを取得できません";
        if (binding is null || !currentConnectionControl && binding != Candidate()) return "メモリ照明の現在の接続情報を取得してください";
        if (currentConnectionControl)
            try { backend.Check(connection); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { return e.Message; }
        if (!DeviceLightingConfiguration.ValidExit(CurrentExit)) return "メモリ照明の終了方法が不正です";
        var saved = journal!.Pending;
        if (faulted || saved is not null && (!owned || saved.Phase != EneMemoryRecoveryPhase.Active)) return "前回のメモリ照明を復旧してください";
        if (active is null && !nativeApplying && (observed.Control[0] != 0 || saved is not null && !EneMemoryProtocol.Same(observed, saved.Mutation.After)))
            return "メモリ照明に別の設定が見つかりました";
        return null;
    }
    public DeviceObservation? Observe()
    {
        lock (gate)
        {
            if (connection is not { } c || observed is null) return null;
            var error = Ready();
            var recoveryRequired = journalError is not null || faulted || journal?.Pending is { } pending &&
                (!owned || pending.Phase != EneMemoryRecoveryPhase.Active);
            DeviceCapability Cap(DeviceFeature f, string? reason) => new(f, ImplementationSupport.Supported, VerificationEvidence.Automated,
                reason is null ? DeviceAvailability.Ready : recoveryRequired ? DeviceAvailability.Failed :
                readError is not null || currentConnectionControl ? DeviceAvailability.TemporarilyUnavailable : DeviceAvailability.NeedsSetup,
                reason is { Length: > 512 } ? reason[..512] : reason);
            return new(DeviceId, "ene-memory", "ENE RGB Memory", DeviceKind.Memory, IdentityConfidence.ConnectionBound,
                c.BusId + ":" + c.Address.ToString("x2") + ":" + c.ConnectionId, c.DefinitionId, EneMemoryDeviceBackend.ResourceDomains,
                [new(Endpoint, "RGB", EndpointKind.Lighting, [Cap(DeviceFeature.FixedColor, error), Cap(DeviceFeature.NativeEffect, error),
                    Cap(DeviceFeature.ColorFrames, error), Cap(DeviceFeature.RestoreLighting, null)], NativeLightingEffects: BuiltInLightingEffects.Memory())]);
        }
    }
    private void Cache(EneMemoryConnection c, EneMemoryState state)
    {
        if (!EneMemoryProtocol.Valid(state)) throw new IOException("メモリ照明の状態が不正です");
        lock (gate)
        {
            if (connection != c) throw new IOException("メモリ照明の取得中に接続が変わりました");
            observed = Contract.Copy(state); observedAt = System.Diagnostics.Stopwatch.GetTimestamp(); readError = null;
        }
    }
    private async Task ReadCoreAsync(bool full, CancellationToken ct)
    {
        EneMemoryConnection c; lock (gate) c = connection ?? throw new IOException("メモリ照明は切断されています");
        try
        {
            var authorization = hardware.Value;
            void Check()
            {
                ct.ThrowIfCancellationRequested(); authorization?.CheckAuthorization();
                lock (gate) if (disposed || connection != c) throw new IOException("メモリ照明の読取前に接続が変わりました");
                backend.Check(c);
            }
            var state = await ExecuteWithFreshnessRecoveryAsync(c, full, s => EneMemoryProtocol.Read(s, Check),
                new(), () => true, Check, ct).ConfigureAwait(false);
            Cache(c, state);
        }
        catch (Exception e) { lock (gate) readError = e.Message; throw; }
    }
    public async Task PollAsync(CancellationToken ct)
    {
        // Monitoring must not occupy the shared bus while a frame is saving its intent outside the reservation.
        if (!await io.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            EneMemoryConnection? streaming;
            lock (gate) { if (disposed) return; streaming = active is not null ? connection : null; }
            if (streaming is not null) backend.Check(streaming); else await ReadCoreAsync(false, ct).ConfigureAwait(false);
        }
        finally { io.Release(); }
    }
    public void ConfigureLighting(PhysicalDeviceBinding? binding, IEnumerable<LightingEndpointConfiguration> configurations, IEnumerable<RegisteredEndpoint> endpoints)
        => ConfigureLightingCore(binding, configurations, endpoints, false);
    private void ConfigureLightingCore(PhysicalDeviceBinding? binding, IEnumerable<LightingEndpointConfiguration> configurations,
        IEnumerable<RegisteredEndpoint> endpoints, bool useCurrentConnection)
    {
        var values = configurations.Take(2).Select(Contract.Copy).ToArray(); var registered = endpoints.Take(2).Select(Contract.Copy).ToArray();
        if (values.Length > 1 || registered.Length > 1 || values.Any(v => !DeviceLightingConfiguration.ValidEndpoint(v) || v.Target != new EndpointReference(DeviceId, Endpoint)) ||
            registered.Any(e => e is null || e.Id != Endpoint || e.Kind != EndpointKind.Lighting) || values.Length > registered.Length)
            throw new ArgumentException("メモリ照明の設定が不正です");
        if (!io.Wait(0)) throw new IOException("メモリ照明の操作完了後に設定してください");
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (journalError is not null) throw new IOException(journalError);
                if (active is not null || journal!.Pending is not null) throw new IOException("メモリ照明を終了・復旧してから設定してください");
                if (binding is not null && binding != Candidate()) throw new IOException("メモリ照明の関連付けが一致しません");
                connectionPermission?.Revoke(); connectionPermission = null;
                this.binding = binding; configuration = values.SingleOrDefault(); setup = registered.SingleOrDefault()?.Setup;
                currentConnectionControl = useCurrentConnection;
                faulted = false;
            }
        }
        finally { io.Release(); }
    }
    internal static BoardLightSetting NativeSetting(LightingProgram? program)
    {
        if (!DeviceConfigurationRules.ValidProgram(program) || program!.Reverse) throw new ArgumentException("メモリ照明の色・速度・方向が不正です");
        if (program.EffectId == "off" && program.Colors.Length == 0 && program.LedColors is null) return new(0, 0, 0, 0);
        var effect = program.EffectId == "fixed" ? MemoryLighting.Effects.Single(e => e.Id == 1) :
            MemoryLighting.Effects.SingleOrDefault(e => program.EffectId == "native/ene-memory/" + e.Id);
        if (effect is null || effect.Color && program.Colors.Length != 1 || !effect.Color && program.Colors.Length > 1 ||
            !effect.Color && effect.Id != 0 && program.Brightness != 100 ||
            program.LedColors is not null && (!effect.Color || program.LedColors.Length != 8)) throw new ArgumentException("未対応のメモリ照明効果または色指定です");
        var colors = program.LedColors ?? [program.Colors.FirstOrDefault() ?? new(0, 0, 0)];
        var scaled = colors.Select(c => effect.Color ? c.Scale(program.Brightness / 100d) : c).ToArray(); var first = scaled[0];
        var palette = program.LedColors is null ? null : Convert.ToHexString(scaled.SelectMany(c => new byte[] { (byte)c.R, (byte)c.B, (byte)c.G }).ToArray());
        return new(effect.Id, first.R, first.G, first.B, program.Speed, palette);
    }
    internal static LightingProgram Reported(EneMemoryState state)
    {
        if (state.Control[0] != 0) return new("fixed", [new(state.DirectColors[0], state.DirectColors[2], state.DirectColors[1])]);
        var setting = EneMemoryProtocol.NativeSetting(state)!;
        var effect = MemoryLighting.Effects.Single(e => e.Id == setting.Mode);
        RgbColor[]? palette = !effect.Color || setting.MemoryPalette is null ? null : Enumerable.Range(0, 8).Select(i =>
            new RgbColor(state.NativeColors[i * 3], state.NativeColors[i * 3 + 2], state.NativeColors[i * 3 + 1])).ToArray();
        return new(setting.Mode == 0 ? "off" : setting.Mode == 1 ? "fixed" : "native/ene-memory/" + setting.Mode,
            effect.Color ? [new(setting.Red, setting.Green, setting.Blue)] : [], setting.Speed, LedColors: palette);
    }
    private static EneMemoryState Exit(EneMemoryState original, LightingExitPolicy policy) => policy.Action == LightingExitAction.RestoreOriginal
        ? Contract.Copy(original) : EneMemoryProtocol.NativeTarget(original, NativeSetting(DeviceLightingConfiguration.ExitProgram(policy)));
    public string? Validate(DeviceEndpointAction action)
    {
        lock (gate)
        {
            if (action.EndpointId != Endpoint) return "未対応のメモリ照明端子です";
            if (action.Operation == DeviceOperation.StopLighting) return null;
            if (action.Operation != DeviceOperation.SetLighting) return "未対応のメモリ照明操作です";
            if (Ready() is { } error) return error;
            try { _ = NativeSetting(action.Lighting); return null; } catch (ArgumentException e) { return e.Message; }
        }
    }
    public async Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct)
    {
        if (Validate(action) is { } invalid) return new(Outcome.Rejected, invalid);
        if (action.Operation == DeviceOperation.StopLighting) return (await EndOrRecoverAsync(DeviceLightingStopReason.Normal, ct).ConfigureAwait(false)).Single().Result;
        await io.WaitAsync(ct).ConfigureAwait(false);
        EneMemoryRecoveryRecord? previous = null, prepared = null; var attempted = false; var retry = new FreshnessRetry();
        EneMemoryRecovery.ReadLease? lease = null;
        try
        {
            EneMemoryConnection c; PhysicalDeviceBinding b; LightingExitPolicy exit;
            lock (gate)
            {
                if (active is not null) return new(Outcome.Rejected, "同期の終了後にメモリ照明を設定してください");
                if (Validate(action) is { } error) return new(Outcome.Rejected, error);
                c = connection!; b = Candidate()!; exit = CurrentExit; nativeApplying = true;
            }
            var setting = NativeSetting(action.Lighting); previous = journal!.Pending;
            if (previous is not null) lease = journal.AcquireReadLease(previous);
            var authorization = hardware.Value;
            void Check() { CheckConnectionPermission(); authorization?.CheckAuthorization(); Guard(c, b, ct, extra: () => lease?.Check()); }
            var before = await ExecuteWithFreshnessRecoveryAsync(c, true, session => EneMemoryProtocol.Read(session, Check),
                retry, () => !attempted, Check, ct).ConfigureAwait(false);
            lease?.Dispose(); lease = null; Check();
            prepared = journal.Prepare(boot, previous?.Scope ?? new(b, c, Guid.NewGuid().ToString("N")),
                EneMemoryProtocol.Native(before, EneMemoryProtocol.NativeTarget(before, setting)), previous?.Exit ?? Exit(before, exit), previous);
            lock (gate) owned = true;
            lease = journal.AcquireReadLease(prepared);
            return await ExecuteWithFreshnessRecoveryAsync(c, false, session =>
            {
                Check();
                EneMemoryProtocol.ExecutePrepared(session, prepared.Mutation, Check, () => attempted = true);
                var actual = EneMemoryProtocol.Read(session, Check);
                if (!EneMemoryProtocol.Same(actual, prepared.Mutation.After)) throw new IOException("メモリ照明の読み戻しが一致しません");
                Check(); Cache(c, actual); return new DeviceDriverResult(Outcome.Confirmed, "メモリ照明の設定を読み戻しました", ReportedLighting: Reported(actual));
            }, retry, () => !attempted, Check, ct).ConfigureAwait(false);
        }
        catch (Exception e) { lease?.Dispose(); lease = null; return FailedMutation(e, attempted, prepared, previous); }
        finally { lease?.Dispose(); lock (gate) nativeApplying = false; io.Release(); }
    }
    private DeviceDriverResult FailedMutation(Exception error, bool attempted, EneMemoryRecoveryRecord? prepared, EneMemoryRecoveryRecord? previous)
    {
        if (!attempted)
        {
            try
            {
                if (prepared is not null) journal!.CancelUnsent(prepared, previous);
                lock (gate) owned = previous is not null && owned;
                return new(Outcome.Rejected, "メモリ照明は送信していません: " + error.Message,
                    Failure: error is FrameExpiredException ? DeviceDriverFailure.LightingFrameExpired : DeviceDriverFailure.None);
            }
            catch (Exception cleanup) { error = new IOException("未送信のメモリ照明記録を戻せません: " + cleanup.Message, error); }
        }
        lock (gate) faulted = true;
        return new(Outcome.Unknown, "メモリ照明の結果を確認できません: " + error.Message);
    }
    private static DeviceDriverReleaseResult[] Result(DeviceDriverResult value) => [new(Endpoint, value)];
    // Caller owns io. A verified recovery remains durable if only final file deletion fails.
    private async Task<DeviceDriverResult> RecoverCoreAsync(LightingProgram? normalStop, CancellationToken ct)
    {
        if (journalError is not null) return new(Outcome.Unknown, journalError);
        var saved = journal!.Pending;
        if (saved is null) { lock (gate) { owned = false; faulted = false; } return new(Outcome.Confirmed, "メモリ照明の復旧待ちはありません"); }
        EneMemoryRecovery.ReadLease? lease = null;
        try
        {
            EneMemoryConnection c; PhysicalDeviceBinding b;
            lock (gate) { c = connection ?? throw new IOException("メモリ照明は切断されています"); b = Candidate() ?? throw new IOException("メモリ照明を再取得してください"); }
            if (EneMemoryRecovery.Check(saved, boot, b, c) is { } mismatch) throw new IOException(mismatch);
            lease = journal.AcquireReadLease(saved);
            var authorization = hardware.Value;
            void Check() { authorization?.CheckAuthorization(); Guard(c, b, ct, extra: () => lease?.Check()); }
            var attempted = false; var retry = new FreshnessRetry();
            var actual = await ExecuteWithFreshnessRecoveryAsync(c, true, session => EneMemoryProtocol.Read(session, Check),
                retry, () => !attempted, Check, ct).ConfigureAwait(false);
            lease.Dispose(); lease = null; Check();
            if (saved.Phase != EneMemoryRecoveryPhase.Recovered)
            {
                var target = normalStop is null || saved.Phase == EneMemoryRecoveryPhase.Recovering ? null :
                    EneMemoryProtocol.NativeTarget(saved.Original, NativeSetting(new("fixed", normalStop.Colors, Brightness: normalStop.Brightness)));
                saved = journal.PrepareRecovery(saved, boot, b, c, actual, target);
                lease = journal.AcquireReadLease(saved);
                actual = await ExecuteWithFreshnessRecoveryAsync(c, false, session =>
                {
                    Check();
                    EneMemoryProtocol.ExecutePrepared(session, saved.Mutation, Check, () => attempted = true);
                    var value = EneMemoryProtocol.Read(session, Check);
                    if (!EneMemoryProtocol.Same(value, saved.Mutation.After)) throw new IOException("メモリ照明の復旧値が一致しません");
                    Check(); return value;
                }, retry, () => !attempted, Check, ct).ConfigureAwait(false);
                lease.Dispose(); lease = null; Check(); saved = journal.ConfirmRecovery(saved, actual);
            }
            else if (!EneMemoryProtocol.Same(actual, saved.Mutation.After)) throw new IOException("復旧済みのメモリ照明が変更されています");
            Check(); journal.Complete(saved); Cache(c, actual);
            lock (gate) { owned = false; faulted = false; }
            return new(Outcome.Confirmed, "メモリ照明を復旧しました", ReportedLighting: Reported(actual));
        }
        catch (Exception e) { lock (gate) faulted = true; return new(Outcome.Unknown, "メモリ照明の復旧を確認できません: " + e.Message); }
        finally { lease?.Dispose(); }
    }
    private async Task<DeviceDriverReleaseResult[]> EndOrRecoverAsync(DeviceLightingStopReason reason, CancellationToken ct)
    {
        Stream? stream; lock (gate) stream = active;
        if (stream is not null) return await stream.EndAsync(reason, ct).ConfigureAwait(false);
        await io.WaitAsync(ct).ConfigureAwait(false);
        try { return Result(await RecoverCoreAsync(null, ct).ConfigureAwait(false)); }
        finally { io.Release(); }
    }
    public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => EndOrRecoverAsync(reason switch
    {
        DeviceReleaseReason.NormalExit => DeviceLightingStopReason.SessionEnded, DeviceReleaseReason.ServiceStopping => DeviceLightingStopReason.ServiceStopping,
        DeviceReleaseReason.Suspended => DeviceLightingStopReason.Suspended, DeviceReleaseReason.ConfigurationChanged => DeviceLightingStopReason.ConfigurationChanged,
        _ => DeviceLightingStopReason.OwnerLost
    }, ct);
    public MigratedReference[] LegacyReferences()
    {
        lock (gate) return connection is { BusId: "amd-1022-790b-0b00-p0", Address: 0x71 or 0x73 } c && observed is not null
            ? [new("rgb", MemoryLighting.Identity(c.Address), Endpoint, new(DeviceId, Endpoint))] : [];
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) if (disposed) return;
        try { await ReleaseAsync(DeviceReleaseReason.ServiceStopping, CancellationToken.None).ConfigureAwait(false); }
        finally { lock (gate) disposed = true; }
    }
}
