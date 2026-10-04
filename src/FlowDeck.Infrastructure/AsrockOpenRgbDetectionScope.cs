using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// Owns the complete pre-detection capture, native handles and live connection proofs. Packaged
// routing constructs this scope only after its helper acceptance preflight succeeds. Binding
// supplies only durably captured paths to its pre-open guard.
internal sealed class AsrockOpenRgbDetectionScope : IOpenRgbDetectionScope, IOpenRgbNativeShutdownScope, IOpenRgbStartupAuthorizationScope
{
    private sealed class Entry(string path, AsrockHidContinuity continuity)
    {
        public readonly string Path = path;
        public readonly AsrockHidContinuity Continuity = continuity;
        public AsrockOpenRgbDetectionRecovery? Journal;
        public AsrockOpenRgbRecoveryIdentity? Identity;
        public OpenRgbBoundDevice? Bound;
        public AsrockOpenRgbNativeExecutor? Transport;
        public AsrockOpenRgbDetectionAuthorization? Authorization;
    }
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly object transportGate = new();
    private readonly string installation, directory, boot;
    private readonly Action checkDirectRoute;
    private readonly Action<uint?> checkConflicts;
    private readonly Func<string[]> enumerate;
    private readonly Func<string[], Action<string>, IAsrockDeviceBackend> createBackend;
    private readonly Func<string, CancellationToken, Task<AsrockHidContinuity>> captureContinuity;
    private Entry[] entries = [];
    private string[]? paths;
    private IAsrockDeviceBackend? captureBackend, restoreBackend;
    private DeviceStartupException? pendingStart;
    private OpenRgbOwnedProcess? helper;
    private DeviceShutdownRecovery[] shutdownRecoveries = [];
    private volatile bool prepared, failed, closing, closed, helperAttached, inventoryAccepted;
    public string[] ResourceDomains => AsrockDeviceProvider.DefinitionData.ResourceDomains.ToArray();
    public bool ResourcesClosed => closed;

    internal AsrockOpenRgbDetectionScope(string installation, string directory, string boot, Action checkDirectRoute)
        : this(installation, directory, boot, checkDirectRoute, AsrockHidDeviceBackend.EnumeratePaths,
            AsrockHidDeviceBackend.ForPaths, AsrockHidContinuity.CaptureAsync, AsrockRgbMonitor.CheckConflicts) { }

    internal AsrockOpenRgbDetectionScope(string installation, string directory, string boot, Action checkDirectRoute,
        Func<string[]> enumerate, Func<string[], Action<string>, IAsrockDeviceBackend> createBackend,
        Func<string, CancellationToken, Task<AsrockHidContinuity>> captureContinuity, Action<uint?> checkConflicts)
    {
        if (!DeviceServiceProtocol.Id(installation) || !Path.IsPathFullyQualified(directory) || !DeviceConfigurationRules.Text(boot, 128))
            throw new ArgumentException("ASRockの引き渡し識別・保存先が不正です");
        ArgumentNullException.ThrowIfNull(checkDirectRoute); ArgumentNullException.ThrowIfNull(enumerate);
        ArgumentNullException.ThrowIfNull(createBackend); ArgumentNullException.ThrowIfNull(captureContinuity); ArgumentNullException.ThrowIfNull(checkConflicts);
        this.installation = installation; this.directory = directory; this.boot = boot; this.checkDirectRoute = checkDirectRoute;
        this.enumerate = enumerate; this.createBackend = createBackend; this.captureContinuity = captureContinuity; this.checkConflicts = checkConflicts;
    }

    private string[] Enumerate()
    {
        var result = enumerate();
        if (result is not { Length: <= DeviceRegistry.MaximumDevices } || result.Any(p => !DeviceConfigurationRules.Text(p, 2048)) ||
            result.Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length)
            throw new IOException("ASRockの検出対象が不正または重複しています");
        return result.ToArray();
    }
    private void CheckPaths()
    {
        if (paths is { } expected && !new HashSet<string>(expected, StringComparer.OrdinalIgnoreCase).SetEquals(Enumerate()))
            throw new IOException("OpenRGBへ引き渡すASRock機器の一覧が変わりました");
    }
    private void CheckOwnership(bool native)
    {
        checkDirectRoute();
        uint? allowed = null;
        if (helper is { ResourcesClosed: false } owner)
        {
            if (native) throw new IOException("OpenRGBの終了前にNative接続を開けません");
            owner.CheckOwned(); allowed = owner.ProcessId;
        }
        checkConflicts(allowed);
        if (allowed is not null) helper!.CheckOwned();
    }
    public void CheckRoute()
    {
        if (closing || closed || failed) throw new IOException("ASRockの引き渡しの終了・復旧が必要です");
        CheckOwnership(false); CheckPaths();
        foreach (var entry in entries) entry.Continuity.Check();
    }
    private void CheckOpen(string path)
    {
        CheckOwnership(true);
        var entry = entries.SingleOrDefault(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("保存していないASRock機器は開けません");
        entry.Continuity.Check();
    }
    private void Native(Entry entry, IAsrockDeviceBackend backend, AsrockDeviceConnection connection, bool capture,
        Action<Func<byte[], byte[]>, Action> action)
    {
        void Guard()
        {
            CheckOwnership(true); entry.Continuity.Check(connection); backend.Check(connection);
            if (capture) CheckPaths();
        }
        Guard();
        using var session = backend.Borrow(connection);
        byte[] Exchange(byte[] request)
        {
            if (session is IAsrockGuardedRgbSession queued) return queued.Exchange(request, Guard);
            Guard(); return session.Exchange(request);
        }
        action(Exchange, Guard);
    }
    public async Task PrepareAsync(CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CheckRoute(); ct.ThrowIfCancellationRequested();
            if (prepared) return;
            try
            {
                paths = Enumerate();
                foreach (var path in paths)
                {
                    CheckRoute(); ct.ThrowIfCancellationRequested();
                    AsrockHidContinuity continuity;
                    try { continuity = await captureContinuity(path, ct).ConfigureAwait(false); }
                    catch (DeviceStartupException pending) { pendingStart = pending; throw; }
                    entries = [.. entries, new(path, continuity)];
                }
                CheckRoute(); ct.ThrowIfCancellationRequested();
                if (entries.Length > 0)
                {
                    captureBackend = createBackend(paths.ToArray(), CheckOpen);
                    var connections = await captureBackend.DiscoverAsync(ct).ConfigureAwait(false);
                    if (connections.Length != entries.Length || connections.DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase).Count() != connections.Length)
                        throw new IOException("ASRockの検出前接続が一致しません");
                    foreach (var entry in entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        var connection = connections.SingleOrDefault(c => c.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase))
                            ?? throw new IOException("保存対象のASRock機器を取得できません");
                        entry.Continuity.Check(connection);
                        var id = DeviceIdentity.Create(installation, "asrock-usb", connection.NativeIdentity);
                        var journal = new AsrockOpenRgbDetectionRecovery(directory, id);
                        // Existing records belong to an earlier handover, never to this new watch.
                        if (journal.Pending is not null) throw new IOException("以前のASRock検出復旧記録が残っています");
                        entry.Journal = journal;
                        entry.Identity = new(installation, id, boot, entry.Continuity.Receipt.Handover, entry.Continuity.Receipt.Revision);
                        Native(entry, captureBackend, connection, true, (exchange, guard) =>
                            journal.Capture(entry.Identity, entry.Path, exchange, () => { ct.ThrowIfCancellationRequested(); guard(); }));
                    }
                    await captureBackend.DisposeAsync().ConfigureAwait(false); captureBackend = null;
                }
                CheckRoute(); ct.ThrowIfCancellationRequested();
                foreach (var entry in entries) entry.Authorization = new(entry.Journal!, entry.Identity!, CheckRoute);
                prepared = true;
            }
            catch { failed = true; throw; }
        }
        finally { lifecycle.Release(); }
    }
    public OpenRgbStartupProfile BindStartup(OpenRgbStartupProfile template)
    {
        CheckRoute();
        if (!prepared || captureBackend is not null || helper is not null || paths is null ||
            entries.Any(e => e.Journal?.Pending is null || e.Identity is null))
            throw new IOException("ASRockの検出前保存と接続終了が完了していません");
        return template.WithCapturedAsrockPaths(paths);
    }
    public void AttachHelper(OpenRgbOwnedProcess owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!prepared || failed || closing || closed || helperAttached || helper is not null && !ReferenceEquals(helper, owner))
            throw new IOException("ASRockの検出準備が完了していません");
        // Retain the exact owner even if the following checks fail. PID equality alone is not a lease.
        helper = owner; owner.CheckRunning(); CheckRoute(); helperAttached = true;
    }
    public void AttachPreparedHelper(OpenRgbOwnedProcess owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!prepared || failed || closing || closed || helper is not null || helperAttached)
            throw new IOException("ASRockの検出準備が完了していません");
        owner.CheckSuspended(); helper = owner; CheckRoute();
    }
    public void CheckInventory(OpenRgbSdkController[] controllers)
    {
        try { CheckInventoryCore(controllers); inventoryAccepted = true; }
        catch { inventoryAccepted = false; throw; }
    }
    private void CheckInventoryCore(OpenRgbSdkController[] controllers)
    {
        // A read-only recheck must not transiently revoke the last accepted inventory.
        // Actual inventory/connection changes have their independent SDK generation guard.
        // A failed recheck revokes readiness before the exception reaches the provider.
        CheckRoute();
        if (!prepared || helper is null || controllers.Length != entries.Length ||
            controllers.DistinctBy(c => c.Id).Count() != controllers.Length ||
            controllers.DistinctBy(c => c.Location, StringComparer.OrdinalIgnoreCase).Count() != controllers.Length)
            throw new IOException("OpenRGBの検出全体と保存済み機器が一致しません");
        helper.CheckRunning();
        foreach (var entry in entries)
        {
            (entry.Authorization ?? throw new IOException("ASRockの検出認可がありません")).RequireCompleted();
            var controller = controllers.SingleOrDefault(c => c.Location.Equals("HID: " + entry.Path, StringComparison.OrdinalIgnoreCase));
            var before = entry.Journal?.Pending?.Before ?? throw new IOException("ASRockの検出前の記録がありません");
            if (controller is null) throw new IOException("OpenRGBに保存対象のASRock機器がありません");
            _ = new AsrockOpenRgbNativePlan(before, controller);
        }
        CheckRoute();
    }
    public void CheckControl()
    {
        CheckRoute();
        // Readiness is earned by the complete captured detection/inventory, not by accepting
        // an SDK connection. Every native write still needs the durable operation's permit.
        // Protected product startup and packaged routing have their own acceptance gates.
        if (!prepared || !helperAttached || helper is null || captureBackend is not null || !inventoryAccepted || entries.Length == 0)
            throw new IOException("ASRockの検出・機器一覧・所有プロセスの確認が完了していません");
        helper.CheckRunning();
        foreach (var entry in entries)
            (entry.Authorization ?? throw new IOException("ASRockの検出認可がありません")).RequireCompleted();
        CheckRoute();
    }
    public IOpenRgbLightingTransport CreateLightingTransport(OpenRgbBoundDevice device)
    {
        lock (transportGate)
        {
            CheckRoute();
            if (!prepared || helper is null || device.Snapshot.Target.Structure != OpenRgbSdkProtocol.Structure(device.Snapshot.Controller))
                throw new IOException("ASRockのSDK操作対象を照合できません");
            helper.CheckRunning();
            var entry = entries.SingleOrDefault(e => device.Snapshot.Controller.Location.Equals("HID: " + e.Path, StringComparison.OrdinalIgnoreCase))
                ?? throw new IOException("検出前に保存していないASRock機器は操作できません");
            (entry.Authorization ?? throw new IOException("ASRockの検出認可がありません")).RequireCompleted();
            if (entry.Transport is not null)
            {
                if (entry.Bound?.Connection != device.Connection || entry.Bound.Snapshot.Target != device.Snapshot.Target)
                    throw new IOException("ASRockのSDK接続・一覧世代が変わりました。引き渡しを終了してください");
                return entry.Transport;
            }
            var pending = entry.Journal?.Pending ?? throw new IOException("ASRockの検出前復旧記録がありません");
            if (pending.Scope != entry.Identity) throw new IOException("ASRockの復旧所有者が変わりました");
            var plan = new AsrockOpenRgbNativePlan(pending.Before, device.Snapshot.Controller);
            entry.Bound = device;
            return entry.Transport = new(device.Connection, device.Snapshot.Target, plan, entry.Journal!, entry.Identity!);
        }
    }
    internal void AuthorizeOperation(AsrockAuthorizationRequest request, CancellationToken ct)
    {
        if (!request.Origin.Sdk) throw new IOException("ASRockのSDK操作認可では検出を許可できません");
        Authorize(request, ct);
    }
    public void Authorize(AsrockAuthorizationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); CheckRoute();
        Entry entry;
        lock (transportGate)
        {
            if (!prepared || captureBackend is not null || helper is null) throw new IOException("ASRockの子プロセスと検出準備を照合できません");
            helper.CheckRunning();
            entry = entries.SingleOrDefault(e => e.Path == request.Path) ?? throw new IOException("ASRockの認可対象がありません");
        }
        if (!request.Origin.Sdk)
            (entry.Authorization ?? throw new IOException("ASRockの検出認可がありません")).Authorize(request, ct);
        else
        {
            (entry.Authorization ?? throw new IOException("ASRockの検出認可がありません")).RequireCompleted();
            (entry.Transport ?? throw new IOException("ASRockの通信を認可する操作対象がありません")).Authorization.Authorize(request, ct);
        }
    }
    public void CheckNativeShutdown(OpenRgbRecoveryRecord[] records)
    {
        lock (transportGate)
        {
            if (!prepared || failed || closing || closed || helper is null)
                throw new IOException("ASRockの検出前復旧を使う終了条件がありません");
            // No discovery, SDK request or native open here. The helper may already be dead.
            // Live continuity and physical state are checked by the native owner before restoring.
            var covered = new HashSet<OpenRgbRecoveryRecord>();
            var recoveries = new List<DeviceShutdownRecovery>();
            foreach (var entry in entries)
            {
                if (entry.Identity is null || entry.Journal is null) throw new IOException("ASRockの検出前記録がありません");
                var selected = records.Where(r => entry.Bound is { } bound && r.Scope.Target == bound.Snapshot.Target &&
                    r.Scope.Binding == OpenRgbDeviceIdentities.Binding(r.Scope.Binding.DeviceId, bound)).ToArray();
                entry.Journal.CheckSdkCoverage(entry.Identity, selected);
                covered.UnionWith(selected);
                foreach (var group in selected.GroupBy(r => r.Scope.Binding.DeviceId))
                {
                    var surfaces = OpenRgbDeviceIdentities.Surfaces(entry.Bound!.Snapshot.Controller);
                    var whole = group.Any(r => r.Scope.Zone is null);
                    var zones = group.Where(r => r.Scope.Zone is not null).Select(r => r.Scope.Zone).ToHashSet();
                    var results = surfaces.Where(s => whole || zones.Contains(s.Zone)).Select(s => new DeviceDriverReleaseResult(s.Endpoint,
                        new(Outcome.Confirmed, "終了設定へのNative復旧と記録整理を確認しました"))).ToArray();
                    if (results.Length == 0 || group.Any(r => r.Scope.Zone is { } z && !surfaces.Any(s => s.Zone == z)))
                        throw new IOException("ASRockの復旧端子をSDKの保存対象へ対応付けられません");
                    recoveries.Add(new(Guid.NewGuid().ToString("N"), group.Key, OpenRgbDeviceIdentities.Locator(entry.Bound.Snapshot.Target),
                        OpenRgbDeviceProvider.Definition, results));
                }
            }
            if (records.DistinctBy(r => (r.Scope.Binding.DeviceId, r.Scope.Zone)).Count() != records.Length || covered.Count != records.Length)
                throw new IOException("Native復旧へ対応付けられていないOpenRGB操作が残っています");
            shutdownRecoveries = recoveries.ToArray(); // Not exposed until the entire native close succeeds.
        }
    }
    public DeviceShutdownRecovery[] ReadNativeShutdownRecoveries()
    {
        lock (transportGate)
        {
            if (!closed) throw new IOException("Native復旧と記録整理の完了を確認する前には結果を公開できません");
            return Contract.Copy(shutdownRecoveries);
        }
    }
    public async ValueTask DisposeAsync()
    {
        closing = true;
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (closed) return;
            if (helper is { ResourcesClosed: false }) throw new IOException("OpenRGBの終了を確認するまでASRockを復元できません");
            if (captureBackend is not null) { await captureBackend.DisposeAsync().ConfigureAwait(false); captureBackend = null; }
            if (restoreBackend is not null) { await restoreBackend.DisposeAsync().ConfigureAwait(false); restoreBackend = null; }
            if (pendingStart is not null) { await pendingStart.DisposeAsync().ConfigureAwait(false); pendingStart = null; }
            var errors = new List<Exception>();
            var eligible = new List<Entry>();
            foreach (var entry in entries.Where(e => e.Journal?.Pending is { Phase: not AsrockOpenRgbRecoveryPhase.Restored }))
            {
                try { entry.Continuity.Check(); eligible.Add(entry); }
                catch (Exception e) { errors.Add(e); }
            }
            var pending = eligible.ToArray();
            if (pending.Length > 0)
            {
                try
                {
                    // The backend calls CheckOpen again before opening each individual handle.
                    CheckOwnership(true);
                    restoreBackend ??= createBackend(pending.Select(e => e.Path).ToArray(), CheckOpen);
                    var connections = await restoreBackend.DiscoverAsync(CancellationToken.None).ConfigureAwait(false);
                    foreach (var entry in pending)
                    {
                        try
                        {
                            var connection = connections.SingleOrDefault(c => c.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase))
                                ?? throw new IOException("復元対象のASRock機器が切断されています");
                            Native(entry, restoreBackend, connection, false, (exchange, guard) =>
                            {
                                if (!entry.Journal!.Restore(entry.Identity!, exchange, guard).Restored)
                                    throw new IOException("ASRockの検出前状態への復元が未完了です");
                            });
                        }
                        catch (Exception e) { errors.Add(e); }
                    }
                }
                catch (Exception e) { errors.Add(e); }
            }
            if (restoreBackend is not null)
            {
                try { await restoreBackend.DisposeAsync().ConfigureAwait(false); restoreBackend = null; }
                catch (Exception e) { errors.Add(e); }
            }
            if (errors.Count > 0) throw new AggregateException("ASRockの検出復旧・接続終了が残っています", errors);
            foreach (var entry in entries.Where(e => e.Journal?.Pending is { Phase: AsrockOpenRgbRecoveryPhase.Restored }))
            {
                try { entry.Journal!.CompleteSdkSettlement(entry.Identity!); }
                catch (Exception e) { errors.Add(e); }
            }
            if (errors.Count > 0) throw new AggregateException("ASRockの復旧済み記録の整理が残っています", errors);
            foreach (var entry in entries)
            {
                try { await entry.Continuity.DisposeAsync().ConfigureAwait(false); }
                catch (Exception e) { errors.Add(e); }
            }
            if (errors.Count > 0) throw new AggregateException("ASRockの接続監視の終了が残っています", errors);
            closed = true;
        }
        finally { lifecycle.Release(); }
    }
}
