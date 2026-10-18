using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>Authoritative cooling loops and one authenticated, expiring GUI owner. No UI or user secrets.</summary>
public sealed partial class CoolingServiceRuntime : IAsyncDisposable
{
    private readonly SemaphoreSlim commands = new(1);
    private readonly object stateCapture = new();
    private readonly object ownerSync = new();
    private long stateSequence;
    private readonly HashSet<string> allowedSids;
    private readonly IHubAdapter hub;
    private readonly IMotherboardSource motherboard;
    private readonly IEventLog log;
    private readonly TimeProvider leaseClock;
    private readonly Action<AppSettings>? persistSettings;
    private readonly IMemoryRgbControl? memoryRgb;
    private string? memoryStreamLease;
    private long memoryFrameAt;
    private IMemoryRgbStreamControl MemoryStream => memoryRgb as IMemoryRgbStreamControl ?? throw new IOException("メモリ同期にはサービス1.23.0以降が必要です");
    private async Task StopMemoryStreamAsync(bool keepLast)
    {
        if (memoryStreamLease is not { } lease) return;
        try { await MemoryStream.EndAsync(lease,keepLast); }
        finally { memoryStreamLease = null; }
    }
    private readonly object persistenceSync = new();
    private string? persistenceError;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, (string Hash, CoolingServiceReply Reply)> replies = [];
    private readonly Queue<string> replyOrder = new();
    private readonly Dictionary<long, LightingLease> lightingLeases = [];
    private string? ownerSession, ownerSid;
    private long lastSequence, lastContact, settingsRevision;
    private long lastRenewal;
    private Task? watchdog;
    private bool initialized, closing, disposed, powerSuspended;
    public ControlCoordinator Coordinator { get; }
    public MotherboardFanControllers Fans { get; }
    public string? LifecycleError { get; private set; }
    public long SettingsRevision => Interlocked.Read(ref settingsRevision);
    public string Epoch => Coordinator.Epoch;
    public bool HasOwner => Volatile.Read(ref ownerSession) is not null;

    public CoolingServiceRuntime(IHubAdapter hub, ITemperatureSource temperatures, IMotherboardSource motherboard,
        Func<MotherboardFanPort, ICpuFanDriver> drivers, AppSettings settings, IEnumerable<string> allowedSids, IEventLog log,
        TimeProvider? leaseClock = null, Action<AppSettings>? persistSettings = null, IMemoryRgbControl? memoryRgb = null)
    {
        this.hub = hub; this.motherboard = motherboard; this.log = log; this.leaseClock = leaseClock ?? TimeProvider.System;
        this.persistSettings = persistSettings;
        this.memoryRgb = memoryRgb;
        this.allowedSids = allowedSids.Where(s => !string.IsNullOrWhiteSpace(s)).ToHashSet(StringComparer.Ordinal);
        if (this.allowedSids.Count == 0) throw new ArgumentException("操作を許可するWindowsユーザーが必要です");
        ValidateConfiguration(ServiceFanConfiguration.From(settings));
        // Only a later, explicit startup-resume operation may consume a server-owned confirmed policy.
        Coordinator = new(hub, temperatures, settings with { Startup = new(), PythonPath = null }, log);
        Fans = new(drivers, () => Coordinator.Snapshot.Sensors.Concat(motherboard.Board.Sensors.Where(s => s.Kind == "temperature")
            .Select(s => new SensorReading(s.Id, s.SourceName, "motherboard", motherboard.Board.Error is null && s.Problem is null ? s.Value : null,
                motherboard.Board.Timestamp ?? 0))).ToArray(), log);
        Coordinator.SettingsChanged += OnSettings;
    }
    private void OnSettings(AppSettings settings) { Interlocked.Increment(ref settingsRevision); InvalidateChangedSleepSettings(); Persist(settings); }
    private void Persist(AppSettings settings)
    {
        lock (persistenceSync)
        {
            try { persistSettings?.Invoke(Coordinator.Settings); Volatile.Write(ref persistenceError, null); }
            catch (Exception e)
            {
                Volatile.Write(ref persistenceError, "サービス設定の保存に失敗しました: " + e.Message);
                log.Write("serviceSettings", new { error = e.Message });
            }
        }
    }
    public async Task InitializeAsync(bool startLoops = true)
    {
        await commands.WaitAsync();
        try
        {
            if (initialized || closing) throw new InvalidOperationException("サービスは初期化済みまたは終了中です");
            if (Coordinator.Settings.Hardware.Hub is { } selected)
            {
                var found = (await Coordinator.ProbeAsync()).SingleOrDefault(h => h.Id == selected.Id);
                if (found is not null) await Coordinator.OpenAsync(found);
            }
            await Coordinator.TickAsync(); initialized = true;
            if (startLoops) { Coordinator.Start(); Fans.Start(); watchdog = WatchdogAsync(); }
        }
        finally { commands.Release(); }
    }
    // verifiedSid comes from the connected pipe's Windows token, never the JSON payload.
    public async Task<ServiceHello> OpenSessionAsync(string verifiedSid)
    {
        await commands.WaitAsync();
        try
        {
            if (!allowedSids.Contains(verifiedSid)) throw new UnauthorizedAccessException("このWindowsユーザーには操作権限がありません");
            if (!initialized || closing || powerSuspended) throw new IOException("サービスは準備中・スリープ中・終了中です");
            await ExpireOwnerCoreAsync();
            if (LifecycleError is not null || persistenceError is not null) throw new IOException("サービスの準備を確認できません: " + LifecycleError + persistenceError);
            if (ownerSession is not null) throw new IOException("別のFlowDeckがサービスを使用しています");
            lock (ownerSync) { ownerSession = Guid.NewGuid().ToString("N"); ownerSid = verifiedSid; lastContact = leaseClock.GetTimestamp(); lastSequence = 0; lastRenewal = 0; }
            replies.Clear(); replyOrder.Clear(); lightingLeases.Clear();
            log.Write("serviceOwner", new { state = "connected", sid = verifiedSid });
            return new(CoolingServiceProtocol.Version, Epoch, ownerSession, SettingsRevision, CoolingServiceProtocol.LeaseSeconds, hub.IsDemo, memoryRgb is null ? [] : memoryRgb is IMemoryRgbStreamControl ? ["memory-rgb-v1","memory-stream-v1"] : ["memory-rgb-v1"]);
        }
        finally { commands.Release(); }
    }
    public async Task<CoolingServiceReply> ExecuteAsync(CoolingServiceRequest incoming)
    {
        // Canonical validation also protects in-process callers and gives the pipe a single protocol implementation.
        var request = CoolingServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(incoming, Contract.Json));
        // Renewal has a separate monotonically increasing sequence and never waits for a hardware operation.
        // It cannot resurrect an expired owner, execute a command, or replay a cached operation.
        if (request.Command == "renew")
        {
            lock (ownerSync)
            {
                if (closing || powerSuspended || ownerSession is null || request.Epoch != Epoch || request.Session != ownerSession ||
                    request.Payload.ValueKind != JsonValueKind.Null || request.Sequence <= lastRenewal ||
                    leaseClock.GetElapsedTime(lastContact).TotalSeconds >= CoolingServiceProtocol.LeaseSeconds)
                    return Reply(request, false, "サービスの生存確認が無効または期限切れです", null);
                lastRenewal = request.Sequence; lastContact = leaseClock.GetTimestamp();
                return Reply(request, true, null, true);
            }
        }
        await commands.WaitAsync();
        try
        {
            await ExpireOwnerCoreAsync();
            if (closing || ownerSession is null || request.Epoch != Epoch || request.Session != ownerSession)
                return Reply(request, false, "接続セッションが無効です", null);
            var hash = SettingsTransfer.Hash(request);
            if (replies.TryGetValue(request.Id, out var previous))
                return previous.Hash == hash ? previous.Reply : Reply(request, false, "同じ要求IDの内容が異なります", null);
            if (request.Sequence <= lastSequence) return Reply(request, false, "古い要求番号です", null);
            lastSequence = request.Sequence;
            lock (ownerSync) lastContact = leaseClock.GetTimestamp();
            CoolingServiceReply response;
            try { response = Reply(request, true, null, await DispatchAsync(request)); }
            catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or JsonException or UnauthorizedAccessException or NotSupportedException or NullReferenceException or AggregateException)
            {
                log.Write("serviceRequestRejected", new { command = request.Command, id = request.Id, error = e.Message });
                response = Reply(request, false, e.Message, null);
            }
            replies[request.Id] = (hash, response); replyOrder.Enqueue(request.Id);
            while (replyOrder.Count > 128) replies.Remove(replyOrder.Dequeue());
            return response;
        }
        finally { commands.Release(); }
    }
    private CoolingServiceReply Reply(CoolingServiceRequest r, bool ok, string? error, object? value) =>
        new(CoolingServiceProtocol.Version, Epoch, r.Session, r.Id, r.Sequence, ok, error, CoolingServiceProtocol.Payload(value));
    public ServiceCoolingState State()
    {
        lock (stateCapture)
        {
        AppSettings settings; long revision;
        do { revision = SettingsRevision; settings = Coordinator.Settings; } while (revision != SettingsRevision);
        var snapshot = Coordinator.Snapshot;
        var session = Volatile.Read(ref ownerSession);
        if (session is not null && snapshot.Operation is { } operation && operation.Id.StartsWith(session + "/", StringComparison.Ordinal))
            snapshot = snapshot with { Operation = operation with { Id = operation.Id[(session.Length + 1)..] } };
        return new(snapshot, ServiceFanConfiguration.From(settings), revision, Coordinator.LastStatus,
            Contract.Copy(settings.LightingPorts), Contract.Copy(motherboard.Board), Fans.States.ToDictionary(p => p.Key, p => p.Value),
            settings.Profiles.ToDictionary(p => p.Id, p => Coordinator.ProfileReadiness(p.Id)), LifecycleError ?? persistenceError, hub.Version,
            ++stateSequence, settings.LastConfirmedPolicy, SleepResumeToken());
        }
    }
    private async Task<object?> DispatchAsync(CoolingServiceRequest r)
    {
        if (corePowerSuspended && r.Command is not ("state" or "heartbeat" or "result" or "release" or "suspend" or "resume"))
            throw new IOException("スリープからの復帰処理を待ってください");
        if (persistenceError is not null && r.Command is not ("state" or "heartbeat" or "stop" or "board-restore" or "board-restore-all" or "result" or "release" or "suspend"))
            throw new IOException(persistenceError);
        if (r.Command is not ("state" or "heartbeat" or "probe" or "result" or "light-target" or "light-valid" or "memory-rgb-read" or "suspend" or "resume" or "restore-sleep")) ClearSleepTicket();
        switch (r.Command)
        {
            case "state": case "heartbeat": return State();
            case "probe": return await Coordinator.ProbeAsync();
            case "memory-rgb-read": return await (memoryRgb ?? throw new IOException("メモリRGBは未対応です")).ReadAsync();
            case "memory-stream-begin":
                if (memoryStreamLease is not null) throw new IOException("メモリは同期中です");
                memoryStreamLease = await MemoryStream.BeginAsync(CoolingServiceProtocol.Body<MemoryRgbBegin>(r).Targets);
                memoryFrameAt = leaseClock.GetTimestamp(); return memoryStreamLease;
            case "memory-stream-frame":
            {
                var frame = CoolingServiceProtocol.Body<MemoryRgbFrame>(r);
                if (frame.Lease != memoryStreamLease || memoryStreamLease is null) throw new IOException("メモリの操作権が無効です");
                await MemoryStream.FrameAsync(frame.Lease,frame.Color); memoryFrameAt = leaseClock.GetTimestamp(); return true;
            }
            case "memory-stream-end":
            {
                var end = CoolingServiceProtocol.Body<MemoryRgbEnd>(r);
                if (end.Lease != memoryStreamLease || memoryStreamLease is null) throw new IOException("メモリの操作権が無効です");
                await StopMemoryStreamAsync(end.KeepLast); return true;
            }
            case "memory-rgb-apply":
            {
                var change = CoolingServiceProtocol.Body<ServiceMemoryLightChange>(r);
                var result = await (memoryRgb ?? throw new IOException("メモリRGBは未対応です")).ApplyAsync(change.Identity, change.Changes);
                log.Write("memoryRgbApply", new { change.Identity, change.Changes, result });
                return result;
            }
            case "open":
            {
                var id = CoolingServiceProtocol.Body<ServiceOpenHub>(r).HubId;
                var identity = (await Coordinator.ProbeAsync()).SingleOrDefault(h => h.Id == id) ?? throw new IOException("指定したHubを検出できません");
                await Coordinator.OpenAsync(identity); return State();
            }
            case "configure":
            {
                var change = CoolingServiceProtocol.Body<ServiceConfigure>(r); RequireSettingsRevision(change.ExpectedRevision);
                ValidateConfiguration(change.Configuration);
                var current = Coordinator.Settings; var next = change.Configuration.Merge(current);
                if (next.Hardware.Hub?.Id != current.Hardware.Hub?.Id) throw new IOException("先に検出したHubへ接続してください");
                next = next with { Hardware = next.Hardware with { Hub = current.Hardware.Hub } };
                foreach (var p in MotherboardFans.Ports)
                    if (Fans[p.Id].State.Managing && SettingsTransfer.Hash(MotherboardFans.Settings(current, p.Id)) != SettingsTransfer.Hash(MotherboardFans.Settings(next, p.Id)))
                        throw new IOException(p.Name + "をマザーボード管理へ戻してください");
                static object HubConditions(AppSettings s) => new { s.Hardware, s.Limits, s.CpuSensorId, s.GpuSensorId, s.Profiles };
                var hubChanged = SettingsTransfer.Hash(HubConditions(current)) != SettingsTransfer.Hash(HubConditions(next));
                if (hubChanged) next = next with { LastConfirmedPolicy = null };
                Coordinator.UpdateSettings(next, hubChanged); return State();
            }
            case "import-configuration":
            {
                var change = CoolingServiceProtocol.Body<ServiceConfigurationImport>(r); RequireSettingsRevision(change.ExpectedRevision);
                ValidateConfiguration(change.Configuration);
                var portable = SettingsTransfer.FromFiles(new Dictionary<string, byte[]>());
                SettingsTransfer.Validate(portable with { Settings = SettingsTransfer.Portable(change.Configuration.Merge(new()) with { Lighting = change.Lighting, LightingPorts = change.LightingPorts }) });
                if (Coordinator.Snapshot is { Managing: true } or { FanBusy: true } or { LightingBusy: true } || Fans.States.Values.Any(f => f.Managing || f.Mode == "test"))
                    throw new IOException("すべてのファン制御と照明操作を終了してから復元してください");
                var config = change.Configuration;
                HubIdentity? found = null;
                if (config.Hardware.Hub is { } target)
                    found = (await Coordinator.ProbeAsync()).SingleOrDefault(h => h.Id == target.Id) ?? throw new IOException("復元先のHubを検出できません。指定した個体の接続を確認してください");
                if (found is null) await Coordinator.CloseHubAsync();
                else if (Coordinator.Settings.Hardware.Hub?.Id != found.Id || !Coordinator.Snapshot.HubReady) await Coordinator.OpenAsync(found);
                var next = config.Merge(Coordinator.Settings);
                next = next with { Hardware = next.Hardware with { Hub = found, WiringConfirmed = false },
                    CpuFan = next.CpuFan with { WiringConfirmed = false }, ChassisFan = next.ChassisFan with { WiringConfirmed = false },
                    BoardFans = next.BoardFans.ToDictionary(p => p.Key, p => p.Value with { WiringConfirmed = false }),
                    LastConfirmedPolicy = null, Lighting = new(), LightingPorts = [] };
                Coordinator.UpdateSettings(next); Coordinator.RestoreLightingPreferences(change.Lighting, change.LightingPorts); return State();
            }
            case "profile":
            {
                var change = CoolingServiceProtocol.Body<ServiceProfileChange>(r); RequireSettingsRevision(change.ExpectedSettingsRevision);
                var current = Coordinator.Settings;
                var next = current with { Profiles = current.Profiles.Select(p => p.Id == change.Profile.Id ? change.Profile : p).ToArray() };
                ValidateConfiguration(ServiceFanConfiguration.From(next));
                return Coordinator.SaveProfile(change.Profile, change.ExpectedProfileRevision);
            }
            case "control":
            {
                var command = CoolingServiceProtocol.Body<ControlRequest>(r);
                if (string.IsNullOrWhiteSpace(command.Id) || command.Id.Length > 256 || command.Id.Any(char.IsControl) ||
                    string.IsNullOrWhiteSpace(command.Source) || command.Source.Length > 64 || command.Source.Any(char.IsControl)) throw new IOException("操作の識別子が不正です");
                var result = await Coordinator.RequestAsync(command with { Id = r.Session + "/" + command.Id });
                return result with { Id = command.Id };
            }
            case "stop": return await Coordinator.StopControlAsync();
            case "board-manual": case "board-auto": case "board-test": case "board-restore":
            {
                var change = CoolingServiceProtocol.Body<ServiceBoardCommand>(r);
                if (r.Command != "board-restore") RequireSettingsRevision(change.ExpectedSettingsRevision);
                var port = MotherboardFans.Find(change.Port); var controller = Fans[port.Id];
                if (r.Command != "board-restore" && (!motherboard.Board.IsFresh() || !MotherboardFans.Detected(motherboard.Board).Contains(port)))
                    throw new IOException("このマザーファンを現在の機器で検出できません");
                var config = MotherboardFans.Settings(Coordinator.Settings, port.Id);
                switch (r.Command)
                {
                    case "board-manual": await controller.ManualAsync(config, change.Duty ?? throw new ArgumentException("出力が必要です")); break;
                    case "board-auto": await controller.AutoAsync(config); break;
                    case "board-test": await controller.TestAsync(); break;
                    default: await controller.RestoreAsync(); break;
                }
                return controller.State;
            }
            case "board-restore-all": await Fans.RestoreAsync(); return State();
            case "light-target":
            { var target = CoolingServiceProtocol.Body<ServiceLightTarget>(r); return Coordinator.LightingTargetError(target.HubId, target.Channels); }
            case "light-begin":
            {
                var target = CoolingServiceProtocol.Body<ServiceLightBegin>(r);
                if (target.HubId != Coordinator.Settings.Hardware.Hub?.Id) throw new IOException("照明のHubが一致しません");
                var lease = Coordinator.BeginLightingStream(target.Channels, target.ExpectedRevision, target.ExpectedGeneration); lightingLeases.Clear(); lightingLeases[lease.Revision] = lease; return lease;
            }
            case "light-valid":
            { var lease = CoolingServiceProtocol.Body<LightingLease>(r); return IsLightLease(lease) && Coordinator.LightingLeaseValid(lease); }
            case "light-end":
            {
                var lease = CoolingServiceProtocol.Body<LightingLease>(r);
                if (!IsLightLease(lease)) throw new IOException("照明の対象または世代が一致しません");
                Coordinator.EndLightingStream(lease); lightingLeases.Remove(lease.Revision); return true;
            }
            case "light-frame":
            {
                var frame = CoolingServiceProtocol.Body<ServiceLightFrame>(r);
                if (!IsLightLease(frame.Lease) || frame.Color is null || !frame.Color.Valid) throw new IOException("照明フレームが不正です");
                return await Coordinator.SendLightingFrameAsync(frame.Lease, () => frame.Color);
            }
            case "suspend":
                await SuspendPowerCoreAsync(); return true;
            case "resume":
                if (powerSuspended) throw new IOException("Windowsの復帰通知を待っています");
                await ResumePowerCoreAsync(); return State();
            case "restore-sleep":
                await RestoreSleepAsync(CoolingServiceProtocol.Body<ServiceSleepResume>(r)); return State();
            case "restore-startup":
            {
                var resume = CoolingServiceProtocol.Body<ServiceStartupResume>(r); RequireSettingsRevision(resume.ExpectedRevision);
                await ResumeConfirmedPolicyAsync(resume.PolicyHash);
                return State();
            }
            case "result":
            { var id = CoolingServiceProtocol.Body<ServiceResultQuery>(r).RequestId; return replies.TryGetValue(id, out var result) ? result.Reply : null; }
            case "release": await ReleaseCoreAsync("GUIからの通常終了"); return LifecycleError is null ? true : throw new IOException(LifecycleError);
            default: throw new IOException("未対応のサービス要求です");
        }
    }
    private bool IsLightLease(LightingLease lease) => lightingLeases.TryGetValue(lease.Revision, out var saved) && SettingsTransfer.Hash(saved) == SettingsTransfer.Hash(lease);
    private void RequireSettingsRevision(long expected) { if (expected != SettingsRevision) throw new IOException("CONFLICT: サービスの設定が変わっています"); }
    private static void ValidateConfiguration(ServiceFanConfiguration config)
    {
        var data = SettingsTransfer.FromFiles(new Dictionary<string, byte[]>());
        SettingsTransfer.Validate(data with { Settings = SettingsTransfer.Portable(config.Merge(new())) });
    }
    public async Task DisconnectAsync(string session)
    {
        await commands.WaitAsync();
        try { if (ownerSession == session) await ReleaseCoreAsync("GUIの接続が切断されました"); }
        finally { commands.Release(); }
    }
    private async Task<bool> StopCoolingCoreAsync()
    {
        var errors = new List<string>();
        try { await StopMemoryStreamAsync(false); } catch (Exception e) { log.Write("memorySyncRecovery",new { error=e.Message }); }
        // Each restoration is independent; a disconnected Hub must not prevent motherboard restoration.
        try { await Fans.RestoreAsync(); } catch (Exception e) { errors.Add(e.Message); }
        try { var result = await Coordinator.StopControlAsync(); if (result.Outcome != Outcome.Confirmed) errors.Add(result.Message); }
        catch (Exception e) { errors.Add(e.Message); }
        lightingLeases.Clear(); LifecycleError = errors.Count == 0 ? null : string.Join(" / ", errors);
        return LifecycleError is null;
    }
    private async Task ReleaseCoreAsync(string reason, bool preserveSleep = false)
    {
        if (!preserveSleep) ClearSleepTicket();
        lock (ownerSync) { ownerSession = null; ownerSid = null; }
        await StopCoolingCoreAsync();
        log.Write("serviceOwner", new { state = "released", reason, error = LifecycleError });
    }
    private async Task ExpireOwnerCoreAsync()
    {
        bool expired;
        lock (ownerSync)
        {
            expired = ownerSession is not null && leaseClock.GetElapsedTime(lastContact).TotalSeconds >= CoolingServiceProtocol.LeaseSeconds;
            if (expired) ownerSession = null;
        }
        if (expired)
            await ReleaseCoreAsync("GUIの応答期限が切れました");
    }
    public async Task SweepAsync()
    {
        await commands.WaitAsync();
        try
        {
            await ExpireOwnerCoreAsync();
            if (memoryStreamLease is not null && leaseClock.GetElapsedTime(memoryFrameAt).TotalSeconds >= 3)
                try { await StopMemoryStreamAsync(false); } catch (Exception e) { log.Write("memorySyncRecovery",new { error=e.Message }); }
            ExpireSleepTicket();
            if (persistenceError is not null) { await StopCoolingCoreAsync(); Persist(Coordinator.Settings); }
            if (ownerSession is null && LifecycleError is not null) await StopCoolingCoreAsync();
        }
        finally { commands.Release(); }
    }
    public async Task SuspendForPowerAsync()
    {
        await commands.WaitAsync();
        try
        {
            powerSuspended = true;
            await SuspendPowerCoreAsync();
        }
        finally { commands.Release(); }
    }
    public async Task ResumeForPowerAsync()
    {
        await commands.WaitAsync();
        try { await ResumePowerCoreAsync(); powerSuspended = false; }
        finally { commands.Release(); }
    }
    private async Task WatchdogAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try { while (await timer.WaitForNextTickAsync(lifetime.Token)) await SweepAsync(); }
        catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        closing = true; await lifetime.CancelAsync(); if (watchdog is not null) await watchdog;
        await commands.WaitAsync();
        try
        {
            if (disposed) return;
            await ReleaseCoreAsync("サービスを終了しています");
            try { await Fans.DisposeAsync(); }
            finally { Coordinator.SettingsChanged -= OnSettings; await Coordinator.DisposeAsync(); disposed = true; }
        }
        finally { commands.Release(); }
    }
}
