namespace FlowDeck.Core;

/// <summary>Single decision owner for PC controls. No device IO runs under the state lock.</summary>
public sealed partial class ControlCoordinator : IControlCoordinator
{
    private readonly object sync = new();
    private readonly PriorityGate usb = new();
    private readonly IHubAdapter hub;
    private readonly ITemperatureSource temperatures;
    private readonly IEventLog log;
    private readonly TimeProvider clock;
    private readonly DescentLimiter descent = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, (ControlRequest Request, Task<OperationResult> Task, long Time)> requests = new();
    private AppSettings settings;
    private HubStatus status = new([], []);
    private SensorReading[] sensors = [];
    private long? telemetryAt;
    private long controlRevision, lightingRevision, generation, sequence;
    private bool ready, managing, fanBusy, lightingBusy, faulted, fallbackAttempted, stopping, connecting, suspended;
    private FanMode mode = FanMode.Monitor;
    private FanPolicy? desired, confirmed;
    private int? target, common;
    private Outcome confirmation = Outcome.Unknown;
    private LightingState lighting;
    private string? fault;
    private OperationResult? operation;
    private Task? loop;
    private Task<OperationResult>? stopTask;
    private Task<SensorReading[]>? temperatureRead;
    private int tickBusy;
    private long reconnectAfter;
    private int reconnectStep;
    public string Epoch { get; } = Guid.NewGuid().ToString("N");
    public event Action<ControlSnapshot>? Changed;
    public event Action<OperationResult>? OperationCompleted;
    public event Action<AppSettings>? SettingsChanged;
    public ControlCoordinator(IHubAdapter hub, ITemperatureSource temperatures, AppSettings settings, IEventLog log, TimeProvider? clock = null)
    {
        this.hub = hub; this.temperatures = temperatures; this.settings = Contract.Copy(settings); this.log = log;
        this.clock = clock ?? TimeProvider.System; lighting = settings.Lighting with { Generation = -1 };
        MigrateLightingPorts();
    }
    public AppSettings Settings { get { lock (sync) return Contract.Copy(settings); } }
    public HubStatus LastStatus { get { lock (sync) return Contract.Copy(status); } }
    public ControlSnapshot Snapshot { get { lock (sync) return SnapshotLocked(); } }
    public string? ProfileReadiness(string id)
    {
        lock (sync)
        {
            var profile = settings.Profiles.SingleOrDefault(p => p.Id == id);
            return profile is null ? "プリセットがありません" : ReadinessLocked(new(FanMode.Auto, Profile: profile,
                Limits: settings.Limits, CpuSensorId: settings.CpuSensorId, GpuSensorId: settings.GpuSensorId), false);
        }
    }
    public void Start() => loop ??= Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        try { do { await TickAsync(); } while (await timer.WaitForNextTickAsync(lifetime.Token)); }
        catch (OperationCanceledException) { }
    });
    public async Task<HubIdentity[]> ProbeAsync()
    {
        using var lease = await usb.EnterAsync(4);
        return await hub.ProbeAsync(lifetime.Token);
    }
    public async Task OpenAsync(HubIdentity identity, bool reconnect = false)
    {
        lock (sync)
        {
            if (connecting || stopping || suspended || (reconnect && settings.Hardware.Hub?.Id != identity.Id) || (!reconnect && (managing || fanBusy || lightingBusy)))
                throw new InvalidOperationException("制御を終了してから接続先を変更してください");
            connecting = true; ready = false; common = null; telemetryAt = null; generation++;
            // Drafts from the old physical connection must not apply to the reopened Hub.
            controlRevision++; lightingRevision++;
            if (settings.Hardware.Hub?.Id != identity.Id)
                settings = settings with { Hardware = new(identity), LastConfirmedPolicy = null, LightingPorts = [] };
        }
        Publish();
        try
        {
            using var lease = await usb.EnterAsync(2);
            await hub.CloseAsync(lifetime.Token);
            var data = await hub.OpenAsync(identity, generation, lifetime.Token);
            lock (sync)
            {
                status = data; telemetryAt = clock.GetTimestamp(); ready = true; reconnectStep = 0;
                if (settings.Hardware.WiringConfirmed && WiringErrorLocked() is { } error) MarkFaultLocked(error);
                common = CommonLocked();
            }
        }
        catch (Exception e)
        {
            lock (sync) { MarkDisconnectedLocked(e.Message); }
            throw;
        }
        finally { lock (sync) connecting = false; Publish(); NotifySettings(); }
    }
    public static string WiringSignature(HubStatus data) => string.Join(";", data.Channels.OrderBy(c => c.Id)
        .Select(c => $"{c.Id}:{c.ControlMode ?? "none"}")) + "|" + string.Join(";", data.Lighting.OrderBy(c => c.Id).Select(c => $"{c.Id}:{c.Accessory}"));
    public async Task CloseHubAsync()
    {
        lock (sync)
        {
            if (managing || fanBusy || lightingBusy || connecting || stopping || suspended)
                throw new InvalidOperationException("先にHubの制御を終了してください");
            connecting = true; ready = false; common = null; telemetryAt = null; generation++; controlRevision++; lightingRevision++;
            settings = settings with { Hardware = new(), LastConfirmedPolicy = null, Lighting = new(), LightingPorts = [] };
            status = new([], []); lighting = new(); confirmed = null; desired = null; target = null;
            mode = FanMode.Monitor; faulted = false; fault = null; confirmation = Outcome.Unknown;
        }
        Publish();
        try { using var lease = await usb.EnterAsync(2); await hub.CloseAsync(lifetime.Token); }
        catch (Exception e) { lock (sync) MarkDisconnectedLocked(e.Message); throw; }
        finally { lock (sync) connecting = false; Publish(); NotifySettings(); }
    }
    private string? WiringErrorLocked()
    {
        if (!settings.Hardware.WiringConfirmed || settings.Hardware.Fans is not { Length: > 0 }) return "接続ファンの実配線を確認してください";
        if (settings.Hardware.WiringSignature != WiringSignature(status)) return "検出構成が変わりました。制御終了後に配線を再確認してください";
        var detected = status.Channels.Where(c => c.ControlMode is "DC" or "PWM").Select(c => c.Id).Order().ToArray();
        if (!detected.SequenceEqual(settings.Hardware.Fans.Order())) return "検出した全ファンと確認済み配線が一致しません";
        return null;
    }
    public void RestoreLightingPreferences(LightingState saved, Dictionary<string, HubPortLighting> ports)
    {
        lock (sync)
        {
            if (managing || fanBusy || lightingBusy || connecting || stopping) throw new InvalidOperationException("先に制御と照明操作を終了してください");
            lighting = new(Effect: saved.Effect, Channels: saved.Channels);
            settings = settings with { Lighting = lighting, LightingPorts = Contract.Copy(ports).ToDictionary(p => p.Key, p => p.Value with { Outcome = Outcome.Unknown, SentAt = null }) };
            lightingRevision++;
        }
        NotifySettings(); Publish();
    }
    public void UpdateSettings(AppSettings next, bool controlConditions = true)
    {
        lock (sync)
        {
            if (controlConditions && (managing || fanBusy || lightingBusy || connecting || stopping))
                throw new InvalidOperationException("先に制御を終了（監視のみへ）してください");
            if (next.SchemaVersion != 1) throw new InvalidOperationException("未対応の設定形式です");
            var lightingConditions = settings.Hardware.Hub?.Id != next.Hardware.Hub?.Id
                || settings.Hardware.WiringSignature != next.Hardware.WiringSignature
                || !(settings.Hardware.Leds ?? []).SequenceEqual(next.Hardware.Leds ?? [])
                || !(settings.Hardware.LedDetails ?? []).SequenceEqual(next.Hardware.LedDetails ?? []);
            if (lightingConditions && (lightingBusy || connecting || stopping))
                throw new InvalidOperationException("照明・接続・終了処理の完了を待ってください");
            settings = Contract.Copy(next);
            if (lightingConditions) lightingRevision++;
            if (controlConditions) { controlRevision++; confirmed = null; desired = null; faulted = false; fault = null; mode = FanMode.Monitor; }
        }
        NotifySettings(); Publish();
    }
    public AutoProfile SaveProfile(AutoProfile profile, long expectedProfileRevision)
    {
        AutoProfile saved;
        lock (sync)
        {
            var old = settings.Profiles.Single(p => p.Id == profile.Id);
            if (old.Revision != expectedProfileRevision) throw new InvalidOperationException("CONFLICT: プリセットが変更されています");
            if (FanPolicyEngine.ValidateProfile(profile, settings.Limits) is { } error) throw new InvalidOperationException(error);
            saved = Contract.Copy(profile with { Revision = old.Revision + 1 });
            settings = settings with { Profiles = settings.Profiles.Select(p => p.Id == saved.Id ? saved : p).ToArray() };
        }
        NotifySettings(); Publish(); return saved;
    }
    public Task<OperationResult> RequestAsync(ControlRequest request)
    {
        Task<OperationResult> task;
        lock (sync)
        {
            foreach (var key in requests.Where(kv => kv.Value.Task.IsCompleted &&
                clock.GetElapsedTime(kv.Value.Time).TotalMinutes > 10).Select(kv => kv.Key).ToArray()) requests.Remove(key);
            if (requests.TryGetValue(request.Id, out var existing))
                return existing.Request == request ? existing.Task : Task.FromResult(ResultLocked(request, Outcome.Rejected, "同じ要求IDの内容が異なります", "DUPLICATE_ID"));
            if (request.ControllerEpoch != Epoch) return RejectLocked(request, "古い起動に対する要求です", "STALE_SESSION");
            if (stopping || suspended || connecting) return RejectLocked(request, "接続・終了処理中です", "BUSY");
            var rgb = request.Command == "SetLighting";
            if (request.ExpectedRevision != (rgb ? lightingRevision : controlRevision)) return RejectLocked(request, "別の操作で設定が変更されました。最新状態を確認してください", "CONFLICT");
            if (rgb ? lightingBusy : fanBusy) return RejectLocked(request, "同じ対象を適用中です", "BUSY");
            if (!ready || !Fresh(telemetryAt)) return RejectLocked(request, "Hubの最新状態を確認できません", "NOT_READY");
            FanPolicy? policy = null;
            int? duty = null;
            if (rgb)
            {
                if (request.LightingEffect is null ? request.Lighting is not ("blue" or "white" or "purple" or "off") : !HubLighting.Valid(request.LightingEffect)) return RejectLocked(request, "照明候補が不正です", "OUT_OF_RANGE");
                if (!ValidLightingChannelsLocked(request.LightingChannels ?? settings.Hardware.Leds) || WiringErrorLocked() is not null)
                    return RejectLocked(request, "接続照明を確認してください", "NOT_READY");
                lightingBusy = true; lightingRevision++; lighting = lighting with { Requested = request.Lighting ?? request.LightingEffect?.Mode, Outcome = Outcome.Pending };
            }
            else
            {
                if (request.Command is not ("SetManualDuty" or "SelectAutoProfile" or "ResumeControl")) return RejectLocked(request, "未対応の要求です", "OUT_OF_RANGE");
                if (faulted && request.Command != "ResumeControl") return RejectLocked(request, "異常の原因を確認して制御を再開してください", "NOT_READY");
                if (request.Command == "ResumeControl")
                {
                    if (!faulted) return RejectLocked(request, "復旧待ちではありません", "NOT_READY");
                    policy = confirmed;
                    if (ReadinessLocked(policy, true) is { } recoveryError) return RejectLocked(request, recoveryError, "NOT_READY");
                }
                else if (request.Command == "SetManualDuty")
                {
                    if (request.Duty is not { } d || !FanPolicyEngine.IsDutyAllowed(d, settings.Limits)) return RejectLocked(request, "出力が許容範囲外です", "OUT_OF_RANGE");
                    policy = new(FanMode.Manual, d, Limits: settings.Limits, CpuSensorId: settings.CpuSensorId, GpuSensorId: settings.GpuSensorId);
                }
                else
                {
                    var profile = settings.Profiles.SingleOrDefault(p => p.Id == request.ProfileId);
                    if (profile is null || profile.Revision != request.ProfileRevision) return RejectLocked(request, "プリセットの版が変更されています", "CONFLICT");
                    policy = new(FanMode.Auto, Profile: Contract.Copy(profile), Limits: settings.Limits, CpuSensorId: settings.CpuSensorId, GpuSensorId: settings.GpuSensorId);
                }
                if (ReadinessLocked(policy, request.Command == "ResumeControl") is { } error) return RejectLocked(request, error, "NOT_READY");
                if (policy is not null)
                {
                    duty = CalculateLocked(policy);
                    if (duty == 0 && StopErrorLocked(policy) is { } stopError) return RejectLocked(request, stopError, "NOT_READY");
                }
                controlRevision++; fanBusy = true; desired = policy; target = duty;
            }
            operation = ResultLocked(request, Outcome.Accepted, "適用中");
            var revision = rgb ? lightingRevision : controlRevision;
            var device = generation;
            var completion = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            task = completion.Task;
            if (requests.Count >= 256)
            {
                var oldest = requests.Where(kv => kv.Value.Task.IsCompleted).OrderBy(kv => kv.Value.Time).FirstOrDefault();
                if (oldest.Key is not null) requests.Remove(oldest.Key);
            }
            requests.Add(request.Id, (request, task, clock.GetTimestamp()));
            _ = Task.Run(async () => completion.TrySetResult(await ExecuteAsync(request, policy, duty, revision, device)));
        }
        Publish(); return task;
    }
    private async Task<OperationResult> ExecuteAsync(ControlRequest request, FanPolicy? policy, int? duty, long revision, long device)
    {
        var rgb = request.Command == "SetLighting";
        OperationResult result;
        var lightingIssued = false;
        try
        {
            using var lease = await usb.EnterAsync(2);
            lock (sync)
            {
                if (device != generation || revision != (rgb ? lightingRevision : controlRevision) || stopping || suspended ||
                    !ready || !rgb && faulted && request.Command != "ResumeControl") throw new InvalidOperationException("条件が変わったため未実行です");
                if (!rgb && policy is not null && ReadinessLocked(policy, request.Command == "ResumeControl") is { } error) throw new InvalidOperationException(error);
                if (!rgb && policy is not null) duty = CalculateLocked(policy);
                if (!rgb && duty == 0 && policy is not null && StopErrorLocked(policy) is { } stopError) throw new InvalidOperationException(stopError);
                if (!rgb && duty is not null) managing = true;
            }
            HubResult io;
            if (rgb) lightingIssued = true;
            if (rgb) io = request.LightingEffect is { } effect
                ? await hub.SetLightingEffectAsync(request.LightingChannels ?? Settings.Hardware.Leds!, effect, lifetime.Token)
                : await hub.SetLightingAsync(Settings.Hardware.Leds!, request.Lighting!, lifetime.Token);
                else if (duty is { } d) io = await WriteFanAsync(d, policy!, revision, request.Command == "ResumeControl", lifetime.Token);
            else io = new(Outcome.Confirmed);
            lock (sync)
            {
                if (device != generation || revision != (rgb ? lightingRevision : controlRevision))
                    io = io with { Outcome = Outcome.Unknown, Error = "世代が変わった応答です" };
                if (io.Status is not null) AcceptTelemetryLocked(io.Status);
                if (rgb)
                {
                    lighting = io.Outcome == Outcome.WrittenUnverified
                        ? new(request.Lighting ?? request.LightingEffect?.Mode, request.Lighting ?? request.LightingEffect?.Mode, io.Outcome, clock.GetUtcNow(), generation, request.LightingEffect, request.LightingChannels ?? settings.Hardware.Leds)
                        : lighting with { Outcome = io.Outcome };
                    if (io.Outcome == Outcome.WrittenUnverified) settings = settings with { Lighting = lighting };
                    if (device == generation) RecordLightingPortsLocked(request.LightingEffect is null ? settings.Hardware.Leds! : request.LightingChannels ?? settings.Hardware.Leds!,
                        request.LightingEffect ?? HubLighting.Preset(request.Lighting!), io.Outcome);
                }
                else if (io.Outcome == Outcome.Confirmed)
                {
                    confirmed = Contract.Copy(policy); desired = confirmed; mode = policy?.Mode ?? FanMode.Monitor;
                    managing = policy is not null;
                    faulted = false; fault = null; fallbackAttempted = false; common = duty ?? CommonLocked(); target = duty;
                    confirmation = Outcome.Confirmed;
                    if (duty is { } value) descent.Reset(value, clock.GetTimestamp());
                    settings = settings with { LastConfirmedPolicy = confirmed };
                }
                else { common = null; confirmation = io.Outcome; MarkFaultLocked(io.Error ?? "全ファンの出力を確認できません"); }
                result = ResultLocked(request, io.Outcome, Message(io.Outcome), io.Error, io.Channels);
            }
        }
        catch (Exception e)
        {
            lock (sync)
            {
                if (e is not InvalidOperationException) MarkDisconnectedLocked(e.Message);
                if (!rgb && managing) MarkFaultLocked(e.Message);
                result = ResultLocked(request, e is InvalidOperationException ? Outcome.Rejected : Outcome.Unknown,
                    e is InvalidOperationException ? e.Message : "結果を確認できません。診断を確認してください", e.GetType().Name);
                if (rgb) lighting = lighting with { Outcome = result.Outcome };
                if (rgb && lightingIssued && device == generation) RecordLightingPortsLocked(request.LightingEffect is null ? settings.Hardware.Leds! : request.LightingChannels ?? settings.Hardware.Leds!,
                    request.LightingEffect ?? HubLighting.Preset(request.Lighting!), Outcome.Unknown);
            }
            log.Write("exception", new { request.Id, exception = e.ToString() });
        }
        lock (sync) { if (rgb) lightingBusy = false; else fanBusy = false; operation = result; }
        Complete(result); NotifySettings(); Publish();
        await FallbackAsync();
        return result;
    }
    private async Task<HubResult> WriteFanAsync(int duty, FanPolicy policy, long revision, bool recovering, CancellationToken ct)
    {
        bool restart;
        lock (sync) restart = duty > 0 && status.Channels.Any(c => settings.Hardware.Fans!.Contains(c.Id) && c.Duty == 0) && policy.Limits?.AllowStop == true;
        if (restart && policy.Limits?.Stop is { } s)
        {
            var boostDuty = Math.Max(duty, s.RestartDuty);
            long device; lock (sync) device = generation;
            var boost = await hub.SetFanGroupAsync(Settings.Hardware.Fans!, boostDuty, ct);
            if (boost.Outcome != Outcome.Confirmed) return boost;
            lock (sync)
            {
                if (device != generation || revision != controlRevision || suspended || stopping)
                    return boost with { Outcome = Outcome.Unknown, Error = "再始動中に制御条件が変わりました" };
                if (boost.Status is not null) AcceptTelemetryLocked(boost.Status);
            }
            Publish();
            var started = clock.GetTimestamp();
            var lastRead = started;
            while (clock.GetElapsedTime(started).TotalSeconds < s.RestartSeconds)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
                lock (sync) if (stopping || faulted && !recovering || suspended || revision != controlRevision)
                    return boost with { Outcome = Outcome.Unknown, Error = "再始動中に制御条件が変わりました" };
                if (clock.GetElapsedTime(lastRead).TotalSeconds >= 1)
                {
                    var report = await hub.ReadAsync(ct);
                    lock (sync)
                    {
                        if (device != generation || revision != controlRevision || suspended || stopping)
                            return boost with { Outcome = Outcome.Unknown, Error = "再始動中に制御条件が変わりました" };
                        AcceptTelemetryLocked(report);
                        boost = boost with { Status = report, Channels = settings.Hardware.Fans!.Select(id =>
                        {
                            var reported = report.Channels.SingleOrDefault(c => c.Id == id)?.Duty;
                            return new ChannelResult(id, "completed", reported, reported == boostDuty ? "confirmed" : reported is null ? "unknown" : "mismatch");
                        }).ToArray() };
                        var wiringError = WiringErrorLocked();
                        if (wiringError is not null || common != boostDuty)
                            return boost with { Outcome = Outcome.Unknown, Error = wiringError ?? "再始動出力の報告値が変わりました" };
                    }
                    lastRead = clock.GetTimestamp();
                    Publish();
                }
            }
        }
        return await hub.SetFanGroupAsync(Settings.Hardware.Fans!, duty, ct);
    }
    public async Task TickAsync()
    {
        if (Interlocked.Exchange(ref tickBusy, 1) != 0) return;
        try
        {
            lock (sync) if (stopping || suspended || lifetime.IsCancellationRequested) return;
            try
            {
                temperatureRead ??= temperatures.ReadAsync(lifetime.Token);
                var readings = await temperatureRead.WaitAsync(TimeSpan.FromSeconds(1), lifetime.Token);
                temperatureRead = null;
                lock (sync) sensors = readings;
            }
            catch (TimeoutException) { lock (sync) sensors = []; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch (Exception e) { temperatureRead = null; lock (sync) sensors = []; log.Write("temperature", new { error = e.Message }); }
            bool poll, reconnect;
            HubIdentity? identity;
            lock (sync)
            {
                if (managing && !faulted && confirmed is not null && SensorErrorLocked(confirmed, false) is { } error) MarkFaultLocked(error);
                if (managing && !Fresh(telemetryAt)) MarkFaultLocked("Hub状態が古いため出力未確認です");
                poll = ready && !connecting && !fanBusy && !lightingBusy;
                identity = settings.Hardware.Hub;
                reconnect = !ready && !connecting && identity is not null && clock.GetTimestamp() >= reconnectAfter;
            }
            Publish();
            await FallbackAsync();
            if (reconnect)
            {
                try { await OpenAsync(identity!, true); }
                catch (Exception e)
                {
                    log.Write("reconnect", new { error = e.Message });
                    lock (sync) { var delay = new[] { 1, 2, 5 }[Math.Min(reconnectStep++, 2)]; reconnectAfter = clock.GetTimestamp() + delay * clock.TimestampFrequency; }
                }
            }
            if (poll)
            {
                try
                {
                    using var lease = await usb.EnterAsync(4);
                    bool stillPoll; long readRevision, readGeneration;
                    lock (sync) { stillPoll = ready && !connecting && !stopping && !suspended && !fanBusy && !lightingBusy; readRevision = controlRevision; readGeneration = generation; }
                    if (stillPoll)
                    {
                        var data = await hub.ReadAsync(lifetime.Token);
                        lock (sync)
                        {
                            if (readGeneration == generation)
                            {
                                AcceptTelemetryLocked(data);
                                if (settings.Hardware.WiringConfirmed && WiringErrorLocked() is { } error) MarkFaultLocked(error);
                                // A newly admitted command may be waiting for this read to release USB.
                                // Its target must not be compared with the preceding hardware observation.
                                if (readRevision == controlRevision && !fanBusy && managing && !faulted && common != target)
                                    MarkFaultLocked("要求出力とHubの報告値が一致しません");
                            }
                        }
                    }
                }
                catch (Exception e) when (lifetime.IsCancellationRequested && e is OperationCanceledException or IOException) { return; }
                catch (Exception e) { lock (sync) MarkDisconnectedLocked(e.Message); log.Write("hub", new { error = e.ToString() }); }
            }
            await FallbackAsync();
            await AutomaticAsync();
            Publish();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { log.Write("tick", new { error = e.ToString() }); }
        finally { Volatile.Write(ref tickBusy, 0); }
    }
    private async Task AutomaticAsync()
    {
        FanPolicy policy;
        long rev, device;
        lock (sync)
        {
            if (!ready || !managing || faulted || fanBusy || stopping || suspended || confirmed?.Mode != FanMode.Auto) return;
            policy = confirmed; rev = controlRevision; device = generation;
        }
        using var lease = await usb.EnterAsync(3);
        int duty;
        lock (sync)
        {
            if (rev != controlRevision || device != generation || faulted || fanBusy || stopping || suspended || !ready) return;
            if (ReadinessLocked(policy, false) is { } error) { MarkFaultLocked(error); return; }
            var raw = CalculateLocked(policy);
            if (raw == 0 && StopErrorLocked(policy) is not null) raw = policy.Limits!.Minimum!.Value;
            duty = descent.Next(raw, clock.GetTimestamp(), policy.Profile!.DescentDelaySeconds!.Value, policy.Profile.DescentPerSecond!.Value, clock);
            if (duty > 0 && duty < policy.Limits!.Minimum) duty = policy.Limits.Minimum.Value;
            target = duty;
            if (common == duty) return;
        }
        try
        {
            var io = await WriteFanAsync(duty, policy, rev, false, lifetime.Token);
            lock (sync)
            {
                if (io.Status is not null) AcceptTelemetryLocked(io.Status);
                if (rev != controlRevision || device != generation) return;
                confirmation = io.Outcome;
                if (io.Outcome != Outcome.Confirmed) { common = null; MarkFaultLocked(io.Error ?? "自動出力を確認できません"); }
            }
            log.Write("automatic", new { duty, outcome = io.Outcome, channels = io.Channels, controlRevision = rev, deviceGeneration = device });
        }
        catch (Exception e) { lock (sync) MarkDisconnectedLocked(e.Message); }
    }
    private async Task FallbackAsync()
    {
        long device;
        lock (sync)
        {
            if (!faulted || fallbackAttempted || !ready || !managing || stopping || suspended) return;
            fallbackAttempted = true; device = generation;
        }
        using var lease = await usb.EnterAsync(1);
        int duty;
        lock (sync)
        {
            if (stopping || suspended || !ready || device != generation || settings.Limits.Fallback is not { } fallback) return;
            duty = Math.Max(fallback, status.Channels.Where(c => settings.Hardware.Fans!.Contains(c.Id)).Max(c => c.Duty ?? 0));
            target = duty;
        }
        HubResult io;
        try { io = await hub.SetFanGroupAsync(Settings.Hardware.Fans!, duty, lifetime.Token); }
        catch (Exception e) { io = new(Outcome.Unknown, Error: e.Message); lock (sync) MarkDisconnectedLocked(e.Message); }
        OperationResult result;
        lock (sync)
        {
            if (io.Status is not null) AcceptTelemetryLocked(io.Status);
            common = io.Outcome == Outcome.Confirmed ? duty : null;
            mode = io.Outcome == Outcome.Confirmed ? FanMode.Fallback : FanMode.Unknown;
            confirmation = io.Outcome;
            result = new(Guid.NewGuid().ToString("N"), "異常時制御", "Fallback", io.Outcome,
                io.Outcome == Outcome.Confirmed ? "異常時出力の報告を確認。復旧後に制御を再開してください" : "異常時出力も確認できません",
                clock.GetUtcNow(), controlRevision, lightingRevision, generation, io.Channels, io.Error);
        }
        Complete(result); Publish();
    }
    public Task<OperationResult> StopControlAsync()
    {
        TaskCompletionSource<OperationResult> completion;
        lock (sync)
        {
            if (stopTask is { IsCompleted: false }) return stopTask;
            stopping = true; controlRevision++;
            lightingRevision++;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stopTask = completion.Task;
        }
        Publish();
        _ = Task.Run(async () =>
        {
            try { completion.TrySetResult(await StopCoreAsync()); }
            catch (Exception e)
            {
                lock (sync) { stopping = false; common = null; MarkFaultLocked("終了処理が完了していません: " + e.Message); }
                completion.TrySetException(e);
            }
        });
        return completion.Task;
    }
    private async Task<OperationResult> StopCoreAsync()
    {
        using var lease = await usb.EnterAsync(0);
        HubResult io;
        bool write; int? exit;
        lock (sync) { write = managing; exit = settings.Limits.Exit; }
        try
        {
            io = !write ? new(Outcome.Confirmed) : exit is null ? new(Outcome.Unknown, Error: "終了時出力が未設定です")
                : await hub.SetFanGroupAsync(Settings.Hardware.Fans!, exit.Value, lifetime.Token);
        }
        catch (Exception e) { io = new(Outcome.Unknown, Error: e.Message); }
        OperationResult result;
        lock (sync)
        {
            if (io.Status is not null) AcceptTelemetryLocked(io.Status);
            confirmation = io.Outcome;
            if (io.Outcome == Outcome.Confirmed)
            {
                managing = false; mode = FanMode.Monitor; desired = null; target = null; faulted = false; fault = null; fallbackAttempted = false;
            }
            else { common = null; MarkFaultLocked("終了時出力を確認できません"); fallbackAttempted = true; }
            stopping = false;
            result = new(Guid.NewGuid().ToString("N"), "PC", "StopControl", io.Outcome,
                io.Outcome == Outcome.Confirmed ? "制御を終了しました（監視のみ）" : "終了時出力が未確認です", clock.GetUtcNow(),
                controlRevision, lightingRevision, generation, io.Channels, io.Error);
            operation = result;
        }
        Complete(result); Publish(); return result;
    }
    public void Suspend()
    {
        lock (sync) { suspended = true; ready = false; common = null; telemetryAt = null; sensors = []; controlRevision++; lightingRevision++; generation++; }
        Publish();
    }
    public async Task ResumeFromSleepAsync()
    {
        HubIdentity? identity; FanPolicy? previous; bool wasFaulted;
        lock (sync) { suspended = false; identity = settings.Hardware.Hub; previous = confirmed; wasFaulted = faulted; }
        if (identity is null) return;
        try
        {
            await OpenAsync(identity, true); await TickAsync();
            if (previous is not null && managing && !wasFaulted)
            {
                lock (sync) { MarkFaultLocked("スリープ復帰後の再確認"); fallbackAttempted = true; }
                await RequestAsync(new(Guid.NewGuid().ToString("N"), "復帰", Epoch, Snapshot.ControlRevision, "ResumeControl"));
            }
        }
        catch (Exception e) { log.Write("resume", new { error = e.Message }); }
    }
    public async Task RestoreStartupAsync()
    {
        var saved = Settings;
        if (!saved.Startup.ResumeControl || saved.LastConfirmedPolicy is null) return;
        lock (sync) { confirmed = saved.LastConfirmedPolicy; MarkFaultLocked("起動時の確認中"); fallbackAttempted = true; }
        await RequestAsync(new(Guid.NewGuid().ToString("N"), "起動", Epoch, Snapshot.ControlRevision, "ResumeControl"));
    }
    private int CalculateLocked(FanPolicy p) => p.Mode == FanMode.Manual ? p.ManualDuty!.Value :
        FanPolicyEngine.Calculate(p.Profile!, p.Limits!, sensors.Where(s => s.Celsius is not null).ToDictionary(s => s.Id, s => s.Celsius!.Value));
    private string? ReadinessLocked(FanPolicy? p, bool recovery)
    {
        if (!ready || !Fresh(telemetryAt)) return "Hubの最新状態を確認できません";
        if (WiringErrorLocked() is { } wire) return wire;
        if (FanPolicyEngine.ValidateLimits(p?.Limits ?? settings.Limits) is { } limits) return limits;
        if (p?.Mode == FanMode.Auto && FanPolicyEngine.ValidateProfile(p.Profile!, p.Limits!) is { } profile) return profile;
        if (p is not null && SensorErrorLocked(p, recovery) is { } sensor) return sensor;
        if (recovery && CommonLocked() is null) return "全対象の出力一致を再確認してください";
        return null;
    }
    private string? SensorErrorLocked(FanPolicy p, bool recovery)
    {
        var required = new List<(string? Id, double? Overheat, double? Resume)>();
        if (p.Mode == FanMode.Auto) required.AddRange(new[] { p.Profile!.Cpu, p.Profile.Gpu }.OfType<SensorCurve>().Select(c => ((string?)c.SensorId, (double?)c.Overheat, (double?)c.ResumeBelow)));
        if (p.Mode == FanMode.Manual)
        {
            if (p.Limits!.ProtectManualCpu) required.Add((p.CpuSensorId, p.Limits.CpuOverheat, p.Limits.CpuResumeBelow));
            if (p.Limits.ProtectManualGpu) required.Add((p.GpuSensorId, p.Limits.GpuOverheat, p.Limits.GpuResumeBelow));
        }
        foreach (var r in required)
        {
            var s = sensors.SingleOrDefault(s => s.Id == r.Id);
            if (s?.Celsius is not { } temperature || !double.IsFinite(temperature) || !Fresh(s.Timestamp)) return "必要な温度を取得できません（無効または3秒以上経過）";
            if (temperature >= (recovery ? r.Resume : r.Overheat)) return recovery ? "温度が復帰条件に達していません" : "過熱温度に達しました";
        }
        if (p.Mode == FanMode.Manual && p.ManualDuty == 0) return StopErrorLocked(p);
        return null;
    }
    private string? StopErrorLocked(FanPolicy p)
    {
        if (p.Limits?.AllowStop != true || p.Limits.Stop is not { } stop) return "ファン停止が許可されていません";
        var cpu = sensors.SingleOrDefault(s => s.Id == p.CpuSensorId);
        var gpu = sensors.SingleOrDefault(s => s.Id == p.GpuSensorId);
        return cpu?.Celsius is not { } c || gpu?.Celsius is not { } g || !Fresh(cpu.Timestamp) || !Fresh(gpu.Timestamp)
            || c >= stop.CpuBelow || g >= stop.GpuBelow ? "停止条件の温度を確認できません" : null;
    }
    private void AcceptTelemetryLocked(HubStatus data) { status = data; telemetryAt = clock.GetTimestamp(); common = CommonLocked(); }
    private int? CommonLocked()
    {
        if (!ready || !Fresh(telemetryAt) || settings.Hardware.Fans is not { Length: > 0 } ids) return null;
        var values = ids.Select(id => status.Channels.SingleOrDefault(c => c.Id == id)?.Duty).ToArray();
        return values.All(v => v is not null) && values.Distinct().Count() == 1 ? values[0] : null;
    }
    private bool Fresh(long? at) => at is { } t && clock.GetElapsedTime(t).TotalSeconds < 3;
    private void MarkFaultLocked(string reason)
    {
        if (!faulted) { faulted = true; fallbackAttempted = false; controlRevision++; log.Write("fault", new { reason, controlRevision, generation }); }
        fault = reason; mode = FanMode.Unknown;
    }
    private void MarkDisconnectedLocked(string reason)
    {
        ready = false; common = null; telemetryAt = null; confirmation = Outcome.Unknown;
        if (managing) MarkFaultLocked("Hub切断・結果未確認: " + reason);
    }
    private ControlSnapshot SnapshotLocked()
    {
        var fresh = ready && Fresh(telemetryAt);
        return new(Epoch, sequence, controlRevision, lightingRevision, generation, ready, managing, mode, desired, confirmed,
            target, fresh ? common : null, confirmation,
            status.Channels.Select(c => !fresh ? c with { Rpm = null, Duty = null, Presence = Presence.Unknown } :
                settings.Hardware.WiringConfirmed && WiringErrorLocked() is null && !settings.Hardware.Fans!.Contains(c.Id)
                    ? c with { Presence = Presence.Absent, Rpm = null, Duty = null } : c).ToArray(),
            sensors.Select(s => Fresh(s.Timestamp) ? s : s with { Celsius = null }).ToArray(), telemetryAt, lighting,
            fanBusy, lightingBusy, faulted, fault, faulted && !fanBusy && !stopping && ReadinessLocked(confirmed, true) is null,
            ReadinessLocked(null, false) ?? "制御可能", operation, hub.IsDemo);
    }
    private OperationResult ResultLocked(ControlRequest r, Outcome outcome, string message, string? error = null, ChannelResult[]? channels = null)
        => new(r.Id, r.Source, r.Command, outcome, message, clock.GetUtcNow(), controlRevision, lightingRevision, generation, channels, error);
    private Task<OperationResult> RejectLocked(ControlRequest r, string message, string code)
    {
        var result = ResultLocked(r, Outcome.Rejected, message, code); operation = result; Complete(result); return Task.FromResult(result);
    }
    private static string Message(Outcome o) => o switch
    {
        Outcome.Confirmed => "全対象の出力報告を確認しました", Outcome.WrittenUnverified => "送信済み（発光状態は読み戻せません）",
        Outcome.Partial => "一部未適用。チャンネル別の結果を確認してください", Outcome.Failed => "適用失敗",
        Outcome.Rejected => "要求を受け付けられません", _ => "結果未確認"
    };
    private void Complete(OperationResult result) { log.Write("operation", result); OperationCompleted?.Invoke(result); }
    private void NotifySettings() => SettingsChanged?.Invoke(Settings);
    private void Publish() { ControlSnapshot snapshot; lock (sync) { sequence++; snapshot = SnapshotLocked(); } Changed?.Invoke(snapshot); }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (loop is not null) await loop;
        Task[] pending; lock (sync) pending = requests.Values.Select(v => v.Task).ToArray();
        await Task.WhenAll(pending);
        using var lease = await usb.EnterAsync(0);
        await hub.DisposeAsync(); await temperatures.DisposeAsync(); lifetime.Dispose();
    }
}
