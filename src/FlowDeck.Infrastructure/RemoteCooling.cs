using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>User-process facade. Only typed, authenticated RPCs can reach hardware.</summary>
public sealed class RemoteControlCoordinator : IControlCoordinator, IMotherboardSource
{
    private readonly object sync = new();
    private readonly SemaphoreSlim connections = new(1), edits = new(1), power = new(1);
    private readonly Func<CoolingServicePipeClient> clientFactory;
    private readonly IEventLog log;
    private readonly bool demo;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<long, LightingLease> leases = [];
    private AppSettings local;
    private ServiceCoolingState? state;
    private CoolingServicePipeClient? client;
    private Task? watcher;
    private string offlineEpoch = Guid.NewGuid().ToString("N");
    private string? error = "サービスに接続していません";
    private bool disposed, everConnected, sleepPending;
    public bool Connected => client?.Connected == true;
    public bool ConfigurationPending { get { lock (sync) return local.ServiceConfigurationPending; } }
    public string? ConnectionError => Connected ? state?.LifecycleError : client?.Error ?? error;
    public IBoardFanControllers BoardFans { get; }
    public IHubAdapter Hub { get; }
    public RemoteControlCoordinator(AppSettings settings, Func<CoolingServicePipeClient> clientFactory, IEventLog log, bool demo = false)
    {
        local = Contract.Copy(settings); this.clientFactory = clientFactory; this.log = log; this.demo = demo;
        BoardFans = new RemoteBoardFans(this); Hub = new RemoteHub(this);
    }
    public event Action<ControlSnapshot>? Changed;
    public event Action<OperationResult>? OperationCompleted;
    public event Action<AppSettings>? SettingsChanged;
    public string Epoch { get { lock (sync) return state?.Control.ControllerEpoch ?? offlineEpoch; } }
    public AppSettings Settings { get { lock (sync) return Contract.Copy(local); } }
    public HubStatus LastStatus { get { lock (sync) return Contract.Copy(state?.Hub ?? new([], [])); } }
    public BoardSnapshot Board { get { lock (sync) return Contract.Copy(Connected ? state?.Board ?? BoardSnapshot.Empty :
        (state?.Board ?? BoardSnapshot.Empty) with { Error = ConnectionError, Timestamp = null }); } }
    public ControlSnapshot Snapshot
    {
        get
        {
            lock (sync)
            {
                var snapshot = state?.Control ?? new(offlineEpoch, 0, 0, 0, 0, false, false, FanMode.Unknown, null, null,
                    null, null, Outcome.Unknown, [], [], null, local.Lighting, false, false, true, error, false, error!, null, demo);
                if (Connected && local.ServiceConfigurationPending) return Contract.Copy(snapshot with { HubReady = false, CanResume = false, Faulted = true,
                    Fault = "復元設定をサービスへ反映してください", Readiness = "復元設定の反映待ち" });
                return Contract.Copy(Connected ? snapshot : snapshot with { HubReady = false, Mode = FanMode.Unknown, ConfirmedDuty = null,
                    TargetDuty = null, Confirmation = Outcome.Unknown, TelemetryTimestamp = null, Sensors = [], FanBusy = false, LightingBusy = false,
                    Faulted = true, Fault = ConnectionError, CanResume = false, Readiness = ConnectionError ?? "サービス未接続" });
            }
        }
    }
    public string? ProfileReadiness(string id) { lock (sync) return !Connected ? ConnectionError : state?.ProfileReadiness.GetValueOrDefault(id) ??
        (state?.ProfileReadiness.ContainsKey(id) == true ? null : "プリセットがありません"); }
    private CoolingServicePipeClient RequireClient() => Connected ? client! : throw new IOException(ConnectionError ?? "サービスに接続していません");
    public bool SupportsMemoryRgb => client?.Hello?.Capabilities?.Contains("memory-rgb-v1") == true;
    public bool SupportsMemorySync => client?.Hello?.Capabilities?.Contains("memory-stream-v1") == true;
    public async Task<string> BeginMemorySyncAsync(MemoryRgbTarget[] targets)
    {
        if (!SupportsMemorySync) throw new IOException("メモリ同期にはサービス1.23.0以降が必要です");
        return await RequireClient().InvokeAsync<string,MemoryRgbBegin>("memory-stream-begin",new(targets)).ConfigureAwait(false) ?? throw new IOException("メモリ同期の応答が空です");
    }
    public async Task WriteMemorySyncAsync(string lease,RgbColor color) =>
        _ = await RequireClient().InvokeAsync<bool,MemoryRgbFrame>("memory-stream-frame",new(lease,color)).ConfigureAwait(false);
    public async Task EndMemorySyncAsync(string lease,bool keepLast) =>
        _ = await RequireClient().InvokeAsync<bool,MemoryRgbEnd>("memory-stream-end",new(lease,keepLast)).ConfigureAwait(false);
    public async Task<RgbInventory> ReadMemoryRgbAsync()
    {
        if (!SupportsMemoryRgb) throw new IOException("メモリRGBにはサービス1.22.0以降が必要です");
        return await RequireClient().InvokeAsync<RgbInventory, object?>("memory-rgb-read", null).ConfigureAwait(false) ?? throw new IOException("メモリRGB応答が空です");
    }
    public async Task<BoardLightApplyResult> ApplyMemoryRgbAsync(string identity, BoardLightChange[] changes)
    {
        if (!SupportsMemoryRgb) throw new IOException("メモリRGBにはサービス1.22.0以降が必要です");
        return await RequireClient().InvokeAsync<BoardLightApplyResult, ServiceMemoryLightChange>("memory-rgb-apply", new(identity, changes)).ConfigureAwait(false)
            ?? throw new IOException("メモリRGBの適用結果が空です");
    }
    public async Task ConnectAsync()
    {
        await connections.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) throw new ObjectDisposedException(nameof(RemoteControlCoordinator));
            if (Connected) return;
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
            var next = clientFactory(); client = next;
            next.StateReceived += value => { if (ReferenceEquals(client, next)) Accept(value); };
            next.OperationReceived += value => { if (ReferenceEquals(client, next)) Notify(OperationCompleted, value); };
            lock (sync) { state = null; leases.Clear(); }
            try
            {
                if (!next.Connected) await next.ConnectAsync().ConfigureAwait(false);
                everConnected = true; error = null;
                await TickAsync().ConfigureAwait(false);
            }
            catch (Exception e) { error = e.Message; await next.DisposeAsync().ConfigureAwait(false); Notify(Changed, Snapshot); throw; }
        }
        finally { connections.Release(); }
    }
    private void Accept(ServiceCoolingState incoming)
    {
        AppSettings? saved = null;
        lock (sync)
        {
            if (!Connected || incoming.Control.ControllerEpoch != client!.Hello!.Epoch || state is { } previous && incoming.StateSequence <= previous.StateSequence) return;
            var settingsChanged = state is null || incoming.SettingsRevision != state.SettingsRevision;
            state = Contract.Copy(incoming); local = MergeService(local, state); error = incoming.LifecycleError;
            if (settingsChanged) saved = Contract.Copy(local);
        }
        if (saved is not null) Notify(SettingsChanged, saved);
        Notify(Changed, Snapshot);
    }
    private static AppSettings MergeService(AppSettings settings, ServiceCoolingState current) => settings.ServiceConfigurationPending ? settings : current.Configuration.Merge(settings) with
    { Lighting = current.Control.Lighting, LightingPorts = current.LightingPorts, LastConfirmedPolicy = current.LastConfirmedPolicy };
    private static void Notify<T>(Action<T>? handlers, T value)
    { if (handlers is not null) foreach (Action<T> handler in handlers.GetInvocationList()) try { handler(value); } catch { } }
    public void Start() => watcher ??= Task.Run(async () =>
    {
        var wasConnected = Connected;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                var connected = Connected;
                if (wasConnected && !connected) { lock (sync) leases.Clear(); Notify(Changed, Snapshot); }
                wasConnected = connected;
            }
        }
        catch (OperationCanceledException) { }
    });
    public async Task TickAsync()
    {
        var current = RequireClient();
        var value = await current.InvokeAsync<ServiceCoolingState, object?>("state", null).ConfigureAwait(false) ?? throw new IOException("サービス状態が空です");
        if (ReferenceEquals(client, current)) Accept(value);
    }
    public async Task<HubIdentity[]> ProbeAsync() => await RequireClient().InvokeAsync<HubIdentity[], object?>("probe", null).ConfigureAwait(false) ?? [];
    private void RequireConfiguration() { if (ConfigurationPending) throw new IOException("復元設定をサービスへ反映してから操作してください"); }
    public async Task OpenAsync(HubIdentity identity, bool reconnect = false)
    {
        RequireConfiguration();
        Accept(await RequireClient().InvokeAsync<ServiceCoolingState, ServiceOpenHub>("open", new(identity.Id)).ConfigureAwait(false) ?? throw new IOException("サービス状態が空です"));
    }
    public async Task ApplyPendingConfigurationAsync()
    {
        await edits.WaitAsync().ConfigureAwait(false);
        try
        {
            AppSettings desired; long revision;
            lock (sync) { if (!local.ServiceConfigurationPending) return; desired = Contract.Copy(local); revision = state?.SettingsRevision ?? -1; }
            var result = await RequireClient().InvokeAsync<ServiceCoolingState, ServiceConfigurationImport>("import-configuration",
                new(ServiceFanConfiguration.From(desired), revision, desired.Lighting, desired.LightingPorts)).ConfigureAwait(false) ?? throw new IOException("設定の反映結果が空です");
            Accept(result);
            if (result.LifecycleError is not null) throw new IOException(result.LifecycleError);
            AppSettings saved;
            lock (sync) { local = MergeService(local with { ServiceConfigurationPending = false, Startup = local.Startup with { ResumeControl = false } }, state!); saved = Contract.Copy(local); }
            Notify(SettingsChanged, saved); Notify(Changed, Snapshot);
        }
        finally { edits.Release(); }
    }
    public void UpdateSettings(AppSettings next, bool controlConditions = true) => throw new InvalidOperationException("サービスの設定変更には非同期操作を使用してください");
    public async Task UpdateSettingsAsync(AppSettings next, bool controlConditions = true)
    {
        AppSettings baseline; long revision;
        lock (sync) { baseline = Contract.Copy(local); revision = state?.SettingsRevision ?? -1; }
        await edits.WaitAsync().ConfigureAwait(false);
        try
        {
            var configuration = ServiceFanConfiguration.From(next);
            if (SettingsTransfer.Hash(configuration) != SettingsTransfer.Hash(ServiceFanConfiguration.From(baseline)))
            {
                RequireConfiguration();
                var reply = await RequireClient().InvokeAsync<ServiceCoolingState, ServiceConfigure>("configure", new(configuration, revision)).ConfigureAwait(false);
                Accept(reply ?? throw new IOException("サービス設定の応答が空です"));
                if (reply.LifecycleError is not null) throw new IOException(reply.LifecycleError);
            }
            AppSettings saved;
            lock (sync)
            {
                T Field<T>(T current, T before, T desired)
                {
                    if (SettingsTransfer.Hash(before) == SettingsTransfer.Hash(desired)) return current;
                    if (SettingsTransfer.Hash(current) != SettingsTransfer.Hash(before)) throw new IOException("CONFLICT: 画面の設定が別の操作で変わりました");
                    return desired;
                }
                local = local with { Ui = Field(local.Ui, baseline.Ui, next.Ui), M5 = Field(local.M5, baseline.M5, next.M5),
                    Motherboard = Field(local.Motherboard, baseline.Motherboard, next.Motherboard), Dashboard = Field(local.Dashboard, baseline.Dashboard, next.Dashboard),
                    Startup = Field(local.Startup, baseline.Startup, next.Startup), PythonPath = Field(local.PythonPath, baseline.PythonPath, next.PythonPath) };
                saved = Contract.Copy(local);
            }
            Notify(SettingsChanged, saved); Notify(Changed, Snapshot);
        }
        finally { edits.Release(); }
    }
    public AutoProfile SaveProfile(AutoProfile profile, long expectedProfileRevision) => throw new InvalidOperationException("サービスの設定変更には非同期操作を使用してください");
    public async Task<AutoProfile> SaveProfileAsync(AutoProfile profile, long expectedProfileRevision)
    {
        RequireConfiguration();
        long revision; lock (sync) revision = state?.SettingsRevision ?? -1;
        var saved = await RequireClient().InvokeAsync<AutoProfile, ServiceProfileChange>("profile", new(profile, expectedProfileRevision, revision)).ConfigureAwait(false)
            ?? throw new IOException("保存結果が空です");
        await TickAsync().ConfigureAwait(false); return saved;
    }
    public async Task<OperationResult> RequestAsync(ControlRequest request)
    {
        try
        {
            RequireConfiguration();
            var result = await RequireClient().InvokeAsync<OperationResult, ControlRequest>("control", request, TimeSpan.FromMinutes(2)).ConfigureAwait(false)
                ?? throw new IOException("操作結果が空です");
            try { await TickAsync().ConfigureAwait(false); } catch (IOException e) { log.Write("serviceRefresh", new { error = e.Message }); }
            return result;
        }
        catch (IOException e)
        {
            var s = Snapshot;
            var result = new OperationResult(request.Id, request.Source, request.Command, Outcome.Unknown, e.Message, DateTimeOffset.UtcNow,
                s.ControlRevision, s.LightingRevision, s.DeviceGeneration, ErrorCode: "SERVICE_UNCONFIRMED");
            Notify(OperationCompleted, result); return result;
        }
    }
    public async Task<OperationResult> StopControlAsync()
    {
        if (!Connected)
        {
            var s = Snapshot;
            return new(Guid.NewGuid().ToString("N"), "PC", "StopControl", everConnected ? Outcome.Unknown : Outcome.Confirmed,
                ConnectionError ?? "サービス未接続", DateTimeOffset.UtcNow, s.ControlRevision, s.LightingRevision, s.DeviceGeneration);
        }
        var result = await RequireClient().InvokeAsync<OperationResult, object?>("stop", null).ConfigureAwait(false) ?? throw new IOException("終了結果が空です");
        await TickAsync().ConfigureAwait(false); return result;
    }
    public async Task RestoreStartupAsync()
    {
        ServiceCoolingState? captured;
        lock (sync) { if (!local.Startup.ResumeControl || local.ServiceConfigurationPending) return; captured = state; }
        if (captured?.LastConfirmedPolicy is not { } policy) return;
        Accept(await RequireClient().InvokeAsync<ServiceCoolingState, ServiceStartupResume>("restore-startup", new(captured.SettingsRevision, SettingsTransfer.Hash(policy))).ConfigureAwait(false)
            ?? throw new IOException("再開結果が空です"));
    }
    public void Suspend() => _ = PrepareSleepAsync();
    public async Task PrepareSleepAsync()
    {
        await power.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed || sleepPending) return;
            sleepPending = true;
            try { if (Connected) await RequireClient().InvokeAsync<bool, object?>("suspend", null).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            { log.Write("serviceSleepPrepare", new { error = e.Message }); }
            finally { await DisconnectAsync("Windowsのスリープ").ConfigureAwait(false); }
        }
        finally { power.Release(); }
    }
    private async Task DisconnectAsync(string reason)
    {
        await connections.WaitAsync().ConfigureAwait(false);
        try { error = reason; if (client is not null) await client.DisposeAsync().ConfigureAwait(false); lock (sync) leases.Clear(); Notify(Changed, Snapshot); }
        finally { connections.Release(); }
    }
    public async Task ResumeFromSleepAsync()
    {
        await power.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            // The GUI may miss Suspend while its last published state still looks ready.
            // Ask the service on every resume notification; duplicate resume is idempotent there.
            var deadline = Environment.TickCount64 + 20000;
            while (true)
            {
                try
                {
                    await ConnectAsync().ConfigureAwait(false);
                    Accept(await RequireClient().InvokeAsync<ServiceCoolingState, object?>("resume", null).ConfigureAwait(false) ?? throw new IOException("復帰状態が空です"));
                    break;
                }
                catch (IOException) when (Environment.TickCount64 < deadline && !disposed)
                { await DisconnectAsync("Windowsの復帰を待っています").ConfigureAwait(false); await Task.Delay(500, lifetime.Token).ConfigureAwait(false); }
            }
            ServiceCoolingState? captured;
            lock (sync) { captured = state; sleepPending = false; }
            if (!ConfigurationPending && captured?.SleepResumeToken is { } token)
                Accept(await RequireClient().InvokeAsync<ServiceCoolingState, ServiceSleepResume>("restore-sleep", new(token, captured.SettingsRevision), TimeSpan.FromMinutes(2)).ConfigureAwait(false)
                    ?? throw new IOException("スリープ前の制御の再開結果が空です"));
        }
        finally { power.Release(); }
    }
    public string? LightingTargetError(string hubId, string[] channels)
    {
        var s = Snapshot; var settings = Settings;
        if (!Connected || !s.HubReady || s.Faulted || !DashboardDisplay.Fresh(s.TelemetryTimestamp)) return ConnectionError ?? "Hubの最新状態を確認できません";
        return settings.Hardware.Hub?.Id != hubId || channels.Length == 0 || channels.Distinct().Count() != channels.Length ||
            channels.Any(c => settings.Hardware.Leds?.Contains(c) != true || !LastStatus.Lighting.Any(l => l.Id == c)) ||
            !settings.Hardware.WiringConfirmed || settings.Hardware.WiringSignature != ControlCoordinator.WiringSignature(LastStatus)
            ? "照明の機器と接続対象を確認してください" : null;
    }
    public LightingLease BeginLightingStream(string[] channels) => throw new InvalidOperationException("同期の開始には非同期操作を使用してください");
    public async Task<LightingLease> BeginLightingStreamAsync(string[] channels)
    {
        RequireConfiguration();
        ServiceLightBegin request;
        lock (sync) request = new(local.Hardware.Hub?.Id ?? "", channels, state?.Control.LightingRevision ?? -1, state?.Control.DeviceGeneration ?? -1);
        var lease = await RequireClient().InvokeAsync<LightingLease, ServiceLightBegin>("light-begin", request).ConfigureAwait(false)
            ?? throw new IOException("同期の操作権を取得できません");
        await TickAsync().ConfigureAwait(false); lock (sync) { leases.Clear(); leases[lease.Revision] = Contract.Copy(lease); } return lease;
    }
    public bool LightingLeaseValid(LightingLease lease)
    {
        lock (sync) return Connected && leases.TryGetValue(lease.Revision, out var saved) && SettingsTransfer.Hash(saved) == SettingsTransfer.Hash(lease) &&
            state?.Control is { HubReady: true, Faulted: false } s && s.LightingRevision == lease.Revision && s.DeviceGeneration == lease.Generation;
    }
    public void EndLightingStream(LightingLease lease) => throw new InvalidOperationException("同期の終了には非同期操作を使用してください");
    public async Task EndLightingStreamAsync(LightingLease lease)
    {
        lock (sync) { if (!leases.Remove(lease.Revision)) return; }
        if (!Connected) return;
        await RequireClient().InvokeAsync<bool, LightingLease>("light-end", lease).ConfigureAwait(false); await TickAsync().ConfigureAwait(false);
    }
    public async Task<bool> SendLightingFrameAsync(LightingLease lease, Func<RgbColor> sample, Func<RgbColor, Task>? companion = null)
    {
        if (!LightingLeaseValid(lease)) throw new IOException("照明の操作権またはサービス接続が変わりました");
        var color = sample();
        var sent = await RequireClient().InvokeAsync<bool, ServiceLightFrame>("light-frame", new(lease, color)).ConfigureAwait(false);
        if (sent && companion is not null) await companion(color).ConfigureAwait(false); return sent;
    }
    private CpuFanState FanState(string id)
    {
        lock (sync)
        {
            var current = state?.Fans.GetValueOrDefault(id) ?? new();
            return Contract.Copy(Connected ? current : current with { Mode = "fault", Reading = null, Message = ConnectionError ?? "サービス未接続" });
        }
    }
    private async Task FanCommand(string command, string id, CpuFanSettings? expected = null, int? duty = null)
    {
        if (command != "board-restore") RequireConfiguration();
        long revision;
        lock (sync)
        {
            if (expected is not null && SettingsTransfer.Hash(expected) != SettingsTransfer.Hash(MotherboardFans.Settings(local, id)))
                throw new IOException("CONFLICT: ファン設定が変更されました");
            revision = state?.SettingsRevision ?? -1;
        }
        await RequireClient().InvokeAsync<CpuFanState, ServiceBoardCommand>(command, new(id, revision, duty)).ConfigureAwait(false); await TickAsync().ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; await lifetime.CancelAsync();
        await DisconnectAsync("サービス接続を終了しました").ConfigureAwait(false); if (watcher is not null) await watcher.ConfigureAwait(false);
    }
    private sealed class RemoteBoardFans(RemoteControlCoordinator owner) : IBoardFanControllers
    {
        private readonly Dictionary<string, IBoardFanController> controllers = MotherboardFans.Ports.ToDictionary(p => p.Id, p => (IBoardFanController)new RemoteBoardFan(owner, p.Id));
        public IBoardFanController this[string id] => controllers[MotherboardFans.Find(id).Id];
        public IReadOnlyDictionary<string, CpuFanState> States => controllers.ToDictionary(p => p.Key, p => p.Value.State);
        public void Start() { }
        public void Resume() { }
        public async Task RestoreAsync()
        { if (!owner.Connected && !owner.everConnected) return; owner.Accept(await owner.RequireClient().InvokeAsync<ServiceCoolingState, object?>("board-restore-all", null).ConfigureAwait(false) ?? throw new IOException("復元結果が空です")); }
        public Task SuspendAsync() => RestoreAsync();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask; // The coordinator closes the owner session and releases all cooling.
    }
    private sealed class RemoteBoardFan(RemoteControlCoordinator owner, string id) : IBoardFanController
    {
        public CpuFanState State => owner.FanState(id);
        public Task ManualAsync(CpuFanSettings settings, int duty) => owner.FanCommand("board-manual", id, settings, duty);
        public Task AutoAsync(CpuFanSettings settings) => owner.FanCommand("board-auto", id, settings);
        public Task TestAsync() => owner.FanCommand("board-test", id);
        public Task TickAsync() => owner.TickAsync();
        public Task RestoreAsync() => owner.FanCommand("board-restore", id);
        public Task SuspendAsync() => RestoreAsync();
        public void Resume() { }
        public void Start() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class RemoteHub(RemoteControlCoordinator owner) : IHubAdapter
    {
        public bool IsDemo => owner.demo;
        public string Version { get { lock (owner.sync) return owner.state?.WorkerVersion ?? "サービス未接続"; } }
        public Task<HubIdentity[]> ProbeAsync(CancellationToken ct = default) => owner.ProbeAsync();
        public async Task<HubStatus> OpenAsync(HubIdentity identity, long generation, CancellationToken ct = default) { await owner.OpenAsync(identity); return owner.LastStatus; }
        public async Task<HubStatus> ReadAsync(CancellationToken ct = default) { await owner.TickAsync(); return owner.LastStatus; }
        public Task<HubResult> SetFanGroupAsync(string[] channels, int duty, CancellationToken ct = default) => throw new InvalidOperationException("サービスのコーディネーターを使用してください");
        public Task<HubResult> SetLightingAsync(string[] channels, string preset, CancellationToken ct = default) => throw new InvalidOperationException("サービスのコーディネーターを使用してください");
        public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
