using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>Compiled Control Hub driver. Uses a dedicated adapter/worker and never selects by enumeration index.</summary>
public sealed partial class HubDeviceDriver : IDevicePollingDriver, IDeviceTelemetrySource, IConfiguredCoolingDriver, IConfiguredLightingDriver, IDeviceLightingCompositionDriver, ILegacyMigrationSource, IDeviceSetupDriver, IDeviceCoolingCalibrationDriver
{
    public const string Provider = DeviceM5Selection.HubProvider, Definition = DeviceM5Selection.HubDefinition;
    internal static HubDriverDefinition DefinitionData { get; } = HubDeviceDefinitionCatalog.BuiltIn.Get(Definition);
    private static readonly string DefinitionDigest = DefinitionData.Digest;
    private readonly IHubEndpointAdapter adapter;
    private readonly HubEndpointRecoveryJournal journal;
    private readonly string nativeId;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim io = new(1);
    private readonly object gate = new();
    private readonly HashSet<string> attempted = [];
    private Dictionary<string, CoolingEndpointConfiguration> cooling = [];
    private PhysicalDeviceBinding? binding, candidate;
    private HubIdentity? identity;
    private HubIdentity? discovered;
    private bool discoveryManaged;
    private long discoveryRevision, openedDiscoveryRevision;
    private HubStatus? status;
    private bool connected, disposed;
    private bool adapterClosed;
    private Task? disposalWork, disposalRelease;
    private long connection;
    private long? sampleTimestamp;
    private string locator = "unopened";
    public string DeviceId { get; }
    public PhysicalDeviceBinding? BindingCandidate { get { lock (gate) return candidate; } }
    public HubStatus? CachedStatus { get { lock (gate) return status is null ? null : Contract.Copy(status); } }
    public EndpointSetupOptions[] DescribeSetup()
    {
        lock (gate) return !connected || status is null ? [] :
            status.Channels.Where(c => adapter.SupportsFanEndpoints && adapter.SupportsFanDeadlines && c.Presence == Presence.Present)
                .Select(c => new EndpointSetupOptions(c.Id, [DefinitionData.Cooling.ExitAction], [],
                    CoolingStopSupported: DefinitionData.Cooling.StopSupported, CoolingCalibrationSupported: DefinitionData.Cooling.CalibrationSupported))
            .Concat(status.Lighting.Where(l => LightingAdapter is { SupportsLightingEndpoints: true } &&
                (l.LedCount is null || l.LedCount >= 1 && l.LedCount <= DefinitionData.Lighting.MaximumLedsPerEndpoint))
                .Select(l => new EndpointSetupOptions(l.Id, [], DefinitionData.Lighting.ExitActions.ToArray(), 1,
                    DefinitionData.Lighting.MaximumLedsPerEndpoint, l.LedCount, l.Accessory))).ToArray();
    }
    internal void SetDiscovery(HubIdentity? next)
    {
        lock (gate)
        {
            if (next is not null && next.Id != nativeId) throw new ArgumentException("別個体のHubへ変更できません");
            if (!discoveryManaged || discovered != next)
            {
                discoveryManaged = true; discovered = next; discoveryRevision++;
                connected = false;
            }
        }
    }
    private void CheckDiscovery()
    {
        ObjectDisposedException.ThrowIf(adapterClosed, this);
        if (discoveryManaged && (discovered is null || identity != discovered || openedDiscoveryRevision != discoveryRevision))
            throw new IOException("Hubの個体・接続が変更されています。再取得してください");
    }
    public MigratedReference[] LegacyReferences()
    {
        lock (gate) return !connected || status is null ? [] : status.Channels.Select(c => c.Id).Concat(status.Lighting.Select(c => c.Id))
            .Select(id => new MigratedReference("hub", nativeId, id, new(DeviceId, id))).ToArray();
    }
    internal HubStatus? LegacySetupEvidence(AppSettings saved, PhysicalDeviceBinding expected)
    {
        lock (gate) return candidate == expected && connected && status is not null && identity is { Serial.Length: > 0 } && saved.Hardware.Hub is { Serial.Length: > 0 } previous &&
            previous.Id == identity.Id && previous.Serial == identity.Serial && saved.Hardware.WiringConfirmed &&
            saved.Hardware.WiringSignature == ControlCoordinator.WiringSignature(status) &&
            (saved.Hardware.Fans ?? []).Order().SequenceEqual(status.Channels.Where(c => c.Presence == Presence.Present).Select(c => c.Id).Order()) ? Contract.Copy(status) : null;
    }
    public CoolingEndpointConfiguration? CoolingConfiguration(string endpointId)
    { lock (gate) return cooling.TryGetValue(endpointId, out var value) ? Contract.Copy(value) : null; }
    public string? ValidateCoolingSetup(string endpointId) { lock (gate) return disposed ? "Hubドライバーは終了しています" : Ready(endpointId, policy: true); }
    public HubDeviceDriver(string installation, string nativeId, IHubEndpointAdapter adapter, string recoveryDirectory, TimeProvider? clock = null)
    {
        _ = DefinitionData;
        DeviceId = DeviceIdentity.Create(installation, Provider, nativeId); this.nativeId = nativeId; this.adapter = adapter;
        this.clock = clock ?? TimeProvider.System;
        journal = new(recoveryDirectory, DeviceId);
        lightingJournal = new(recoveryDirectory, DeviceId);
    }
    public void Configure(PhysicalDeviceBinding? binding, IEnumerable<CoolingEndpointConfiguration> configurations)
    {
        var values = configurations.Take(DefinitionData.Cooling.Endpoints.Count + 1).Select(Contract.Copy).ToArray();
        if (values.Length > DefinitionData.Cooling.Endpoints.Count || values.Any(c => c is null || c.Target is null || c.Target.DeviceId != DeviceId ||
            !DefinitionData.Cooling.Endpoints.Contains(c.Target.EndpointId) || !DeviceConfigurationRules.ValidSafety(c.Safety)) ||
            values.DistinctBy(c => c.Target).Count() != values.Length || binding is not null &&
            (binding.DeviceId != DeviceId || binding.ProviderIdentity != nativeId || binding.DefinitionId != Definition ||
             binding.Fingerprint?.Length != 64 || !binding.Fingerprint.All(Uri.IsHexDigit)))
            throw new ArgumentException("Hubの関連付け・冷却設定が不正です");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (lightingPreparing || calibrating.Count>0 || journal.Pending.Length != 0 || lightingJournal.Pending.Length != 0) throw new IOException("端子の制御を終了してから設定を変更してください");
            this.binding = binding; cooling = values.ToDictionary(c => c.Target.EndpointId);
        }
    }
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (gate) ObjectDisposedException.ThrowIf(disposed, this);
            await InitializeCoreAsync(ct).ConfigureAwait(false);
        }
        finally { io.Release(); }
    }
    // The caller holds io. Disposal may still need to reopen a lost connection to discharge its journal.
    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        try
        {
            HubIdentity? expected; long expectedRevision;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(adapterClosed, this);
                expected = discovered; expectedRevision = discoveryRevision;
                if (discoveryManaged && expected is null) throw new IOException("対象のHubを一意に検出できません");
            }
            var matches = (await adapter.ProbeAsync(ct).ConfigureAwait(false)).Where(h => h.Id == nativeId).ToArray();
            if (matches.Length != 1) throw new IOException("対象のHubを一意に検出できません");
            var detected = matches[0];
            lock (gate) if (discoveryManaged && (expected != detected || expectedRevision != discoveryRevision))
                throw new IOException("Hubの列挙後に接続情報が変更されています");
            var opened = await adapter.OpenAsync(detected, checked(++connection), ct).ConfigureAwait(false);
            lock (gate) { identity = detected; openedDiscoveryRevision = expectedRevision; locator = "connection:" + Guid.NewGuid().ToString("N"); Update(opened); }
        }
        catch { lock (gate) { connected = false; } throw; }
    }
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (gate) ObjectDisposedException.ThrowIf(disposed, this);
            await ReadCoreAsync(ct).ConfigureAwait(false);
        }
        finally { io.Release(); }
    }
    public Task PollAsync(CancellationToken ct)
    {
        lock (gate) return connected ? RefreshAsync(ct) : InitializeAsync(ct);
    }
    public DeviceSample[] ReadCachedSamples()
    {
        lock (gate)
        {
            if (disposed || status is null || sampleTimestamp is null) return [];
            return status.Channels.SelectMany(c => new DeviceSample[]
            {
                new(c.Id, DeviceFeature.Rpm, c.Rpm, sampleTimestamp.Value),
                new(c.Id, DeviceFeature.DutyRead, c.Duty, sampleTimestamp.Value)
            }).ToArray();
        }
    }
    private async Task ReadCoreAsync(CancellationToken ct)
    {
        try
        {
            lock (gate) CheckDiscovery();
            var value = await adapter.ReadAsync(ct).ConfigureAwait(false);
            lock (gate) Update(value);
        }
        catch { lock (gate) { connected = false; } throw; }
    }
    private Task ReadActuationPreflightAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            CheckDiscovery();
            // Reuse only a very recent age-aware sample for preparing the journal.
            // The worker still acquires a NEW report to check identity, wiring and
            // expected duty immediately before writing, then reads back the output.
            if (connected && LightingAdapter is { SupportsSmoothLightingFrames: true } && sampleTimestamp is { } sampled &&
                clock.GetElapsedTime(sampled).TotalMilliseconds is >= 0 and <= 100)
                return Task.CompletedTask;
        }
        return ReadCoreAsync(ct);
    }
    private async Task ReadRecoveryCoreAsync(CancellationToken ct)
    {
        bool reopened;
        lock (gate) reopened = !connected;
        if (reopened) await InitializeCoreAsync(ct).ConfigureAwait(false);
        try { await ReadCoreAsync(ct).ConfigureAwait(false); }
        catch (IOException) when (!reopened && !ct.IsCancellationRequested)
        {
            // A cancelled command terminates the worker; a replacement has no open Hub yet.
            // Reacquire once, then let release verify the saved identity, wiring and output.
            await InitializeCoreAsync(ct).ConfigureAwait(false);
            await ReadCoreAsync(ct).ConfigureAwait(false);
        }
    }
    private void Update(HubStatus value)
    {
        CheckDiscovery();
        if (identity is null || value is null || value.SampleAgeMilliseconds is < 0 or > 750 || value.Channels is null || value.Channels.Length != DefinitionData.Cooling.Endpoints.Count || value.Channels.Any(c => c is null ||
            !DefinitionData.Cooling.Endpoints.Contains(c.Id) || !Enum.IsDefined(c.Presence) || c.Duty is < 0 or > 100 || c.Rpm < 0 ||
            c.ControlMode is not (null or "" or "None" or "DC" or "PWM")) || value.Channels.DistinctBy(c => c.Id).Count() != value.Channels.Length ||
            value.Lighting is null || value.Lighting.Length > DefinitionData.Lighting.Endpoints.Count || value.Lighting.Any(l => l is null || !DefinitionData.Lighting.Endpoints.Contains(l.Id) ||
                !DeviceConfigurationRules.Text(l.Accessory, 256)) || value.Lighting.DistinctBy(l => l.Id).Count() != value.Lighting.Length ||
            value.Firmware is { Length: > 128 }) throw new IOException("Hubの取得情報が不正です");
        status = Contract.Copy(value); connected = true;
        sampleTimestamp = clock.GetTimestamp() - (long)Math.Ceiling(value.SampleAgeMilliseconds * (double)clock.TimestampFrequency / 1000);
        var serialBound = !string.IsNullOrWhiteSpace(identity.Serial);
        candidate = new(DeviceId, nativeId, Definition, SettingsTransfer.Hash(new
        { Definition, DefinitionDigest, Native = nativeId, Serial = serialBound ? identity.Serial : null, Path = serialBound ? null : identity.Path,
          Connection = serialBound ? null : locator,
          Wiring = ControlCoordinator.WiringSignature(value), value.Firmware }));
    }
    public DeviceObservation? Observe()
    {
        lock (gate)
        {
            if (disposed || !connected || status is null || identity is null) return null;
            var endpoints = status.Channels.Select(c =>
            {
                var error = Ready(c.Id, policy: true);
                DeviceCapability Cap(DeviceFeature f, bool ready) => new(f, ImplementationSupport.Supported,
                    VerificationEvidence.Automated, ready ? DeviceAvailability.Ready : DeviceAvailability.NeedsSetup, ready ? null : error ?? "現在値を取得できません");
                return new DeviceEndpoint(c.Id, c.Id.ToUpperInvariant(), EndpointKind.Cooling,
                    DefinitionData.Cooling.Features.Select(f => Cap(f, f switch { DeviceFeature.Rpm => c.Rpm is >= 0,
                        DeviceFeature.DutyRead => c.Duty is >= 0, DeviceFeature.ReleaseCooling => true, _ => error is null })).ToArray(), Role: "fan");
            }).Concat(status.Lighting.Select(LightingObservation)).ToArray();
            return new(DeviceId, Provider, MotherboardDisplay.ShortText(identity.Description, 160), DeviceKind.Controller,
                string.IsNullOrWhiteSpace(identity.Serial) ? IdentityConfidence.ConnectionBound : IdentityConfidence.SerialBound,
                locator + ":" + candidate!.Fingerprint, Definition, [DefinitionData.ResourceDomainPrefix + DeviceId], endpoints);
        }
    }
    private string? Ready(string channel, bool policy = false)
    {
        if(calibrating.Contains(channel)) return "この端子は試験中です";
        if (!connected || status is null) return "Hubが切断されています";
        if (!adapter.SupportsFanEndpoints) return "Workerの更新が必要です";
        if (policy && !adapter.SupportsFanDeadlines) return "温度の有効期限を確認できるWorkerへ更新してください";
        if (binding is null || binding != candidate) return "Hubの個体・構成の関連付けを確認してください";
        if (status.Channels.SingleOrDefault(c => c.Id == channel) is not { Presence: Presence.Present, Duty: >= 0, ControlMode: "PWM" or "DC" })
            return "接続した端子の現在の出力を取得できません";
        if (!cooling.TryGetValue(channel, out var config) || !config.WiringConfirmed) return "端子の配線・冷却設定を確認してください";
        if (DeviceConfigurationRules.SafetyReadiness(config.Safety) is { } error) return error;
        if (config.Safety.ExitAction != DefinitionData.Cooling.ExitAction) return "このHubでは終了時の固定出力を指定してください";
        if (config.Safety.Stop is not null && !DefinitionData.Cooling.StopSupported) return "このHub定義ではファン停止に対応していません";
        if (!policy && config.Safety.ManualProtection.Length != 0) return "温度保護を含む方針は共通サービスから操作してください";
        return null;
    }
    public string? Validate(DeviceEndpointAction action)
    {
        lock (gate)
        {
            if (disposed) return "Hubドライバーは終了しています";
            if (action is not null && HubLightingRecoveryJournal.IsLed(action.EndpointId)) return ValidateLightingAction(action);
            if (action is null || !HubEndpointRecoveryJournal.IsFan(action.EndpointId)) return "Hubの端子が不正です";
            if (action.Operation == DeviceOperation.ReleaseCooling) return null;
            if (action.Operation != DeviceOperation.SetDuty || action.Duty is not (>= 0 and <= 100) || action.Profile is not null || action.Lighting is not null)
                return "Hubのこの操作は未対応です";
            if (Ready(action.EndpointId) is { } error) return error;
            return action.Duty == 0 && cooling[action.EndpointId].Safety.Stop is not null ? "停止条件の実行エンジンが未接続です" : null;
        }
    }
    public async Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (HubLightingRecoveryJournal.IsLed(action.EndpointId)) return await ApplyLightingAsync(action, ct).ConfigureAwait(false);
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                if (Validate(action) is { } error) return new(Outcome.Rejected, error);
                attempted.Add(action.EndpointId);
            }
            if (action.Operation == DeviceOperation.ReleaseCooling) return await ReleaseOneAsync(action.EndpointId, ct).ConfigureAwait(false);
            await ReadActuationPreflightAsync(ct).ConfigureAwait(false);
            HubEndpointObligation obligation;
            lock (gate)
            {
                if (Validate(action) is { } error) return new(Outcome.Rejected, error);
                var safety = cooling[action.EndpointId].Safety;
                var current = status!.Channels.Single(c => c.Id == action.EndpointId).Duty!.Value;
                var duty = DeviceConfigurationRules.EffectiveGroupDuty(action.Duty!.Value, safety);
                obligation = journal.Prepare(nativeId, candidate!.Fingerprint, ControlCoordinator.WiringSignature(status),
                    action.EndpointId, safety.Exit!.Value, current, duty);
            }
            return await WriteCoreAsync(obligation, ct).ConfigureAwait(false);
        }
        finally { io.Release(); }
    }
    public async Task<DeviceDriverResult> ApplyCoolingAsync(CoolingActuation actuation, CancellationToken ct)
    {
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var expectedSafety = actuation.Safety;
            lock (gate)
            {
                if (ValidateCoolingSetup(actuation.EndpointId) is { } error) return new(Outcome.Rejected, error);
                attempted.Add(actuation.EndpointId);
            }
            await ReadActuationPreflightAsync(ct).ConfigureAwait(false);
            // Do not call back into the service while holding this driver's cache lock.
            var authorizationStarted = HardwareWriteDeadline.Now;
            var authorization = actuation.Authorize();
            if (authorization.Error is { } denied) return new(Outcome.Rejected, denied, Failure: authorization.Failure);
            long? deadline = authorization.ValidFor is { } validFor ? HardwareWriteDeadline.FromBudget(authorizationStarted, validFor) : null;
            HubEndpointObligation obligation;
            lock (gate)
            {
                if (ValidateCoolingSetup(actuation.EndpointId) is { } error) return new(Outcome.Rejected, error);
                var safety = cooling[actuation.EndpointId].Safety;
                if (SettingsTransfer.Hash(safety) != SettingsTransfer.Hash(expectedSafety)) return new(Outcome.Rejected, "計算後に端子の安全条件が変更されました");
                var current = status!.Channels.Single(c => c.Id == actuation.EndpointId).Duty!.Value;
                var duty = actuation.Fallback ? Math.Max(actuation.Duty, current) : actuation.Duty;
                if (duty is < 0 or > 100 || duty > 0 && duty < safety.Minimum || duty == 0 && safety.Stop is null)
                    return new(Outcome.Rejected, "計算出力が端子の許容範囲外です");
                if (HardwareWriteDeadline.Expired(deadline)) return new(Outcome.Rejected, "温度の有効期限を超えました",
                    Failure: DeviceDriverFailure.CoolingInputExpired);
                obligation = journal.Prepare(nativeId, candidate!.Fingerprint, ControlCoordinator.WiringSignature(status),
                    actuation.EndpointId, safety.Exit!.Value, current, duty);
            }
            return await WriteCoreAsync(obligation, ct, deadline).ConfigureAwait(false);
        }
        finally { io.Release(); }
    }
    private async Task<DeviceDriverResult> WriteCoreAsync(HubEndpointObligation obligation, CancellationToken ct, long? deadline = null)
    {
        HubResult result;
        try
        {
            lock (gate) CheckDiscovery();
            result = await adapter.SetFanEndpointAsync(obligation.Channel, obligation.IntendedDuty,
                new(nativeId, obligation.WiringSignature, obligation.BeforeDuty, deadline), ct).ConfigureAwait(false);
        }
        catch (Exception e) { lock (gate) { connected = false; } return new(Outcome.Unknown, "Hubの出力結果は未確認です: " + e.Message); }
        try
        {
        lock (gate)
        {
            CheckDiscovery();
            if (result.Status is not null) Update(result.Status);
            var row = result.Channels is { Length: 1 } ? result.Channels[0] : null;
            var duty = result.Status?.Channels.SingleOrDefault(c => c.Id == obligation.Channel)?.Duty;
            if (result.Outcome == Outcome.Rejected && result.Error == HardwareWriteDeadline.ExpiredError)
                return new(Outcome.Rejected, "通信待ちで温度の有効期限を超えました", duty, Failure: DeviceDriverFailure.CoolingInputExpired);
            if (result.Outcome == Outcome.Confirmed && row is { Write: "completed", Confirmation: "confirmed" } &&
                row.Id == obligation.Channel && row.ReportedDuty == obligation.IntendedDuty && duty == obligation.IntendedDuty &&
                candidate?.Fingerprint == obligation.Fingerprint)
                return new(Outcome.Confirmed, "端子の設定を確認しました", duty);
            return new(result.Outcome == Outcome.Rejected ? Outcome.Rejected : Outcome.Unknown,
                result.Error ?? "端子の設定を確認できません", duty);
        }
        }
        catch (Exception e) { lock (gate) { connected = false; } return new(Outcome.Unknown, "Hubの出力結果は未確認です: " + e.Message); }
    }
    private async Task<DeviceDriverResult> ReleaseOneAsync(string channel, CancellationToken ct)
    {
        var saved = journal.Pending.SingleOrDefault(p => p.Channel == channel);
        if (saved is null) { lock(gate) { calibrating.Remove(channel); attempted.Remove(channel); } return new(Outcome.Confirmed, "この端子の制御は終了しています"); }
        await ReadRecoveryCoreAsync(ct).ConfigureAwait(false);
        lock (gate)
        {
            if (candidate?.Fingerprint != saved.Fingerprint || candidate.ProviderIdentity != saved.HubId || status is null ||
                ControlCoordinator.WiringSignature(status) != saved.WiringSignature)
                return new(Outcome.Unknown, "復旧対象のHubの個体・構成が変更されています");
            var current = status.Channels.Single(c => c.Id == channel);
            if (current is not { Presence: Presence.Present, Duty: >= 0 }) return new(Outcome.Unknown, "復旧対象の現在値が未取得です");
            if (current.Duty == saved.ExitDuty)
            {
                journal.ConfirmReleased(saved); calibrating.Remove(channel); attempted.Remove(channel); return new(Outcome.Confirmed, "終了時出力を確認しました", current.Duty);
            }
            if (current.Duty != saved.BeforeDuty && current.Duty != saved.IntendedDuty)
                return new(Outcome.Unknown, "外部で変更された出力のため自動復旧しません", current.Duty);
            saved = journal.Prepare(saved.HubId, saved.Fingerprint, saved.WiringSignature, channel, saved.ExitDuty, current.Duty.Value, saved.ExitDuty);
        }
        var result = await WriteCoreAsync(saved, ct).ConfigureAwait(false);
        if (result.Outcome == Outcome.Confirmed) lock(gate) { journal.ConfirmReleased(saved); calibrating.Remove(channel); attempted.Remove(channel); }
        return result;
    }
    public async Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct)
    {
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string[] channels;
            lock (gate)
            {
                if (adapterClosed) return [];
                channels = attempted.Union(journal.Pending.Select(p => p.Channel)).Order().ToArray();
            }
            var results = new List<DeviceDriverReleaseResult>();
            foreach (var channel in channels)
            {
                DeviceDriverResult result;
                try { result = await ReleaseOneAsync(channel, ct).ConfigureAwait(false); }
                catch (Exception e) { result = new(Outcome.Unknown, "端子を復旧できません: " + e.Message); }
                results.Add(new(channel, result));
                if (result.Outcome == Outcome.Confirmed) lock (gate) { attempted.Remove(channel); calibrating.Remove(channel); }
            }
            results.AddRange(await ReleaseAllLightingCoreAsync(ct).ConfigureAwait(false));
            return results.ToArray();
        }
        finally { io.Release(); }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposalWork is { IsCompleted: false }) return new(disposalWork);
            if (adapterClosed) return ValueTask.CompletedTask;
            disposed = true;
            // Restoration is attempted once on this connection. Durable unresolved obligations survive closure.
            disposalRelease ??= Task.Run(() => ReleaseAsync(DeviceReleaseReason.ServiceStopping, CancellationToken.None));
            return new(disposalWork = Task.Run(CloseAdapterAsync));
        }
    }
    private async Task CloseAdapterAsync()
    {
        var errors = new List<Exception>();
        try { await disposalRelease!.ConfigureAwait(false); } catch (Exception e) { errors.Add(e); }
        await io.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                await adapter.DisposeAsync().ConfigureAwait(false);
                lock (gate) { adapterClosed = true; connected = false; }
            }
            catch (Exception e) { errors.Add(e); }
        }
        finally { io.Release(); }
        if (errors.Count > 0) throw new AggregateException("Hubの通信を終了できません", errors);
    }
}
