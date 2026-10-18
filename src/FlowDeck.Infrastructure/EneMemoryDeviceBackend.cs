using System.Collections.Concurrent;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed record EneMemoryTransport(string Id, byte[] Candidates, Func<IEneMemoryIo> Open, Func<string> ReadPlatformFingerprint);
internal sealed record EneMemoryConnection(string BusId, byte Address, string NativeIdentity, string ConnectionId,
    string PlatformFingerprint, string DefinitionId, string ControllerFingerprint);
internal sealed class EneMemoryFreshnessException(string message) : IOException(message);
internal interface IEneMemorySession
{
    byte Read(ushort register, Action? beforeData = null);
    void Write(ushort register, byte value, Action beforeData);
    void WritePixel(int led, byte red, byte blue, byte green, Action beforeData);
}
internal interface IEneMemoryDeviceBackend : IAsyncDisposable
{
    Task<T> PrepareOperationAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct);
    Task<T> PrepareFreshOperationAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct);
    Task<EneMemoryConnection[]> DiscoverAsync(CancellationToken ct);
    Task<T> ExecuteAsync<T>(EneMemoryConnection expected, bool fullIdentification, Func<IEneMemorySession, T> operation, CancellationToken ct);
    void Check(EneMemoryConnection expected);
    void Invalidate();
}

// Each operation acquires/releases the shared SMBus mutex on the same dedicated thread.
// Keeping it held between frames would prevent independent sensor and cooling work.
internal sealed class EneMemoryDeviceBackend : IEneMemoryDeviceBackend
{
    private sealed record Entry(EneMemoryConnection Connection, long IdentifiedAt, long PlatformAt);
    private sealed record PlatformProof(string? Fingerprint, long CapturedAt, string? Error);
    private sealed record MetadataBatch(long Sequence, long InvalidationRevision, Dictionary<string, PlatformProof> Platforms);
    private sealed class StaleMetadataException(string message) : IOException(message);
    private readonly object gate = new();
    private readonly EneMemoryTransport[] transports;
    private readonly Dictionary<(string Bus, byte Address), Entry> current = [];
    private readonly BlockingCollection<Action> work = new(64);
    private readonly BlockingCollection<Action> metadataWork = new(64);
    private readonly AsyncLocal<MetadataBatch?> prepared = new();
    private readonly Dictionary<string, (long Sequence, PlatformProof Proof)> usedMetadata = [];
    private readonly Dictionary<IEneMemoryIo, string> pendingBuses = new(ReferenceEqualityComparer.Instance);
    private readonly TimeProvider clock;
    private readonly Task worker, metadataWorker;
    private int threadId, disposed;
    private Task? disposalWork;
    private bool resourcesClosed;
    private long invalidationRevision;
    private long metadataSequence;
    private string[] problems = [];
    internal string[] Problems { get { lock (gate) return problems.ToArray(); } }
    internal static string[] ResourceDomains => LightingDeviceDefinitionCatalog.BuiltIn.Get(LightingDeviceDefinitionCatalog.EneId, "ene-dram-rgb").ResourceDomains.ToArray();
    public EneMemoryDeviceBackend() : this([new("amd-1022-790b-0b00-p0", [0x71, 0x73],
        () => new EneMemoryRgbBus(), EneMemoryPlatformCapture.ReadFingerprint)]) { }
    internal EneMemoryDeviceBackend(EneMemoryTransport[] transports, TimeProvider? clock = null)
    {
        if (transports is not { Length: > 0 and <= 8 } || transports.Any(t => t is null || !DeviceConfigurationRules.Key(t.Id) ||
            t.Candidates is not { Length: > 0 and <= 16 } || t.Candidates.Distinct().Count() != t.Candidates.Length ||
            t.Candidates.Any(a => !EneMemoryRgbProbe.Addresses.Contains(a)) || t.Open is null || t.ReadPlatformFingerprint is null) ||
            transports.DistinctBy(t => t.Id).Count() != transports.Length || transports.Sum(t => t.Candidates.Length) > DeviceRegistry.MaximumDevices)
            throw new ArgumentException("ENEバスの登録が不正です");
        this.transports = transports.Select(t => t with { Candidates = t.Candidates.ToArray() }).ToArray(); this.clock = clock ?? TimeProvider.System;
        worker = Task.Factory.StartNew(() =>
        {
            threadId = Environment.CurrentManagedThreadId;
            foreach (var action in work.GetConsumingEnumerable()) action();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        metadataWorker = Task.Factory.StartNew(() =>
        {
            foreach (var action in metadataWork.GetConsumingEnumerable()) action();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    public Task<T> PrepareOperationAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
        PrepareOperationCoreAsync(operation, false, ct);
    public Task<T> PrepareFreshOperationAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
        PrepareOperationCoreAsync(operation, true, ct);
    private async Task<T> PrepareOperationCoreAsync<T>(Func<CancellationToken, Task<T>> operation, bool fresh, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this); ct.ThrowIfCancellationRequested();
        if (!fresh && prepared.Value is not null) return await operation(ct).ConfigureAwait(false);
        var batch = await Enqueue(metadataWork, () =>
        {
            long revision; lock (gate) revision = invalidationRevision;
            var values = new Dictionary<string, PlatformProof>(StringComparer.Ordinal);
            foreach (var transport in transports)
            {
                ct.ThrowIfCancellationRequested();
                try { values.Add(transport.Id, new(Platform(transport), clock.GetTimestamp(), null)); }
                catch (Exception e) { values.Add(transport.Id, new(null, clock.GetTimestamp(), e.Message)); }
            }
            return new MetadataBatch(++metadataSequence, revision, values);
        }, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested(); var previous = prepared.Value; prepared.Value = batch;
        try { return await operation(ct).ConfigureAwait(false); }
        finally { prepared.Value = previous; }
    }
    private PlatformProof Metadata(MetadataBatch batch, EneMemoryTransport transport)
    {
        var proof = batch.Platforms[transport.Id];
        lock (gate)
        {
            var latest = usedMetadata.GetValueOrDefault(transport.Id);
            if (batch.InvalidationRevision != invalidationRevision ||
                batch.Sequence < latest.Sequence && proof.Fingerprint != latest.Proof.Fingerprint)
                throw new StaleMetadataException("メモリ構成の事前取得が古くなりました");
            // A failed capture is not an age-only expiry. Never retry it as fresh evidence or
            // publish it as the latest successful platform proof, even after its TTL expires.
            if (proof.Error is not null) throw new IOException("メモリ構成を取得できません: " + proof.Error);
            if (clock.GetElapsedTime(proof.CapturedAt).TotalSeconds > 2)
                throw new EneMemoryFreshnessException("メモリ構成の事前取得が古くなりました");
            if (batch.Sequence >= latest.Sequence) usedMetadata[transport.Id] = (batch.Sequence, proof);
            else proof = latest.Proof;
        }
        if (proof.Error is not null) throw new IOException("メモリ構成を取得できません: " + proof.Error);
        return proof;
    }
    private static Task<T> Enqueue<T>(BlockingCollection<Action> queue, Func<T> action, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Run() { try { ct.ThrowIfCancellationRequested(); completion.TrySetResult(action()); } catch (Exception e) { completion.TrySetException(e); } }
        try { if (!queue.TryAdd(Run)) throw new IOException("メモリRGBの待機数が上限に達しました"); }
        catch (InvalidOperationException e) { completion.TrySetException(new IOException("メモリRGBの取得は終了しました", e)); }
        catch (IOException e) { completion.TrySetException(e); }
        return completion.Task;
    }
    private Task<T> Invoke<T>(Func<T> action, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Environment.CurrentManagedThreadId == threadId) { ct.ThrowIfCancellationRequested(); return Task.FromResult(action()); }
        return Enqueue(work, action, ct);
    }
    private static string Platform(EneMemoryTransport transport)
    {
        var proof = transport.ReadPlatformFingerprint();
        if (proof is not { Length: 64 } || !proof.All(Uri.IsHexDigit)) throw new IOException("メモリ構成の指紋が不正です");
        return proof.ToUpperInvariant();
    }
    private void Invalidate(string bus)
    { lock (gate) foreach (var key in current.Keys.Where(k => k.Bus == bus).ToArray()) current.Remove(key); }
    public void Invalidate() { lock (gate) { invalidationRevision++; current.Clear(); } }
    private void RetainBus(string id, IEneMemoryIo bus)
    {
        lock (gate)
        {
            pendingBuses.TryAdd(bus, id);
            // An unreleased shared bus is not a usable connection for any DIMM.
            invalidationRevision++; current.Clear();
        }
    }
    private void RetryPendingBuses()
    {
        if (Environment.CurrentManagedThreadId != threadId) throw new IOException("SMBusの終了スレッドが一致しません");
        KeyValuePair<IEneMemoryIo, string>[] pending; lock (gate) pending = pendingBuses.ToArray();
        var errors = new List<Exception>();
        foreach (var entry in pending)
        {
            try { entry.Key.Dispose(); lock (gate) pendingBuses.Remove(entry.Key); }
            catch (Exception e) { errors.Add(new IOException(entry.Value + ": SMBusの解放が未完了です", e)); }
        }
        if (errors.Count != 0) throw new AggregateException("SMBusの終了を再試行できませんでした", errors);
    }
    private T WithBus<T>(EneMemoryTransport transport, Func<IEneMemoryIo, T> operation)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (pendingBuses.Count != 0) throw new IOException("未終了のSMBusを解放してから再取得してください");
        }
        IEneMemoryIo bus;
        try { bus = transport.Open() ?? throw new IOException("SMBusの取得結果がありません"); }
        catch (EneMemoryBusOpenException e) { RetainBus(transport.Id, e.PendingBus); throw; }
        Exception? failed = null;
        try { return operation(bus); }
        catch (Exception e) { failed = e; throw; }
        finally
        {
            try { bus.Dispose(); }
            catch (Exception closing)
            {
                RetainBus(transport.Id, bus);
                if (failed is not null) throw new AggregateException("SMBusの操作と終了に失敗しました", failed, closing);
                throw;
            }
        }
    }
    private int discoveryInFlight;
    public async Task<EneMemoryConnection[]> DiscoverAsync(CancellationToken ct)
    {
        lock (gate) discoveryInFlight++;
        try { return await DiscoverCoreAsync(ct).ConfigureAwait(false); }
        finally { lock (gate) discoveryInFlight--; }
    }
    internal void WithPreparationEvidence(EneMemoryConnection expected, Action publish)
    {
        lock (gate)
        {
            Check(expected);
            var entry = current[(expected.BusId, expected.Address)];
            if (discoveryInFlight != 0 || pendingBuses.Count != 0 || !Fresh(entry) ||
                clock.GetElapsedTime(entry.IdentifiedAt).TotalSeconds < 0 || clock.GetElapsedTime(entry.PlatformAt).TotalSeconds < 0)
                throw new IOException("メモリRGBの新鮮で確定した接続情報がありません");
            publish();
        }
    }
    private Task<EneMemoryConnection[]> DiscoverCoreAsync(CancellationToken ct)
    {
        if (prepared.Value is not { } metadata) return PrepareOperationAsync(DiscoverCoreAsync, ct);
        return Invoke(() =>
    {
        var found = new List<EneMemoryConnection>(); var errors = new List<string>();
        try { RetryPendingBuses(); }
        catch (Exception e) { lock (gate) problems = [e.Message]; return []; }
        foreach (var transport in transports)
        {
            ct.ThrowIfCancellationRequested();
            long revision; lock (gate) revision = invalidationRevision;
            try
            {
                var platformProof = Metadata(metadata, transport); var platform = platformProof.Fingerprint!;
                var next = WithBus(transport, bus =>
                {
                    var values = new Dictionary<(string, byte), Entry>();
                    if (bus is not IEneMemoryIdentifyingIo identifying) throw new IOException("バスがコントローラーの照合に対応していません");
                    foreach (var address in transport.Candidates)
                    {
                        ct.ThrowIfCancellationRequested(); var identity = identifying.IdentifyController(address);
                        if (EneMemoryDefinition.Resolve(identity) is not { } definition) continue;
                        var native = "location:" + SettingsTransfer.Hash(new { Bus = transport.Id, Address = address });
                        var proof = EneMemoryDefinition.Proof(identity!); Entry? previous;
                        lock (gate) current.TryGetValue((transport.Id, address), out previous);
                        var retained = previous is not null && previous.Connection.PlatformFingerprint == platform && previous.Connection.ControllerFingerprint == proof;
                        var connection = retained ? previous!.Connection : new(transport.Id, address, native, Guid.NewGuid().ToString("N"), platform, definition.Id, proof);
                        values.Add((transport.Id, address), new(connection, clock.GetTimestamp(), platformProof.CapturedAt));
                    }
                    return values;
                });
                _ = Metadata(metadata, transport);
                lock (gate)
                {
                    if (revision != invalidationRevision) throw new IOException("取得中にメモリRGBの接続が失効しました");
                    foreach (var old in current.Keys.Where(k => k.Bus == transport.Id).ToArray()) current.Remove(old);
                    foreach (var entry in next) current.Add(entry.Key, entry.Value);
                }
                found.AddRange(next.Values.Select(e => e.Connection));
            }
            catch (OperationCanceledException) { Invalidate(transport.Id); throw; }
            catch (Exception e) { if (e is not (StaleMetadataException or EneMemoryFreshnessException)) Invalidate(transport.Id); errors.Add(transport.Id + ": " + e.Message); }
        }
        lock (gate)
        {
            problems = errors.ToArray();
            return found.Where(c => current.TryGetValue((c.BusId, c.Address), out var entry) && entry.Connection == c).ToArray();
        }
        }, ct);
    }
    public void Check(EneMemoryConnection expected)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (!current.TryGetValue((expected.BusId, expected.Address), out var entry) || entry.Connection != expected)
                throw new IOException("メモリRGBの接続・個体候補・構成が変更されています");
        }
    }
    public Task<T> ExecuteAsync<T>(EneMemoryConnection expected, bool fullIdentification, Func<IEneMemorySession, T> operation, CancellationToken ct)
    {
        var metadata = prepared.Value;
        if (fullIdentification && metadata is null) return PrepareOperationAsync(t => ExecuteAsync(expected, true, operation, t), ct);
        return Invoke(() =>
    {
        Check(expected); var transport = transports.Single(t => t.Id == expected.BusId);
        if (!fullIdentification)
        {
            lock (gate) if (!Fresh(current[(expected.BusId, expected.Address)]))
                throw new EneMemoryFreshnessException("メモリRGBの完全照合が古いため、再取得が必要です");
        }
        Session? session = null; var moduleCheck = false;
        PlatformProof? platform = null;
        try
        {
            if (fullIdentification) platform = Metadata(metadata!, transport);
            if (fullIdentification && platform!.Fingerprint != expected.PlatformFingerprint)
            { Invalidate(expected.BusId); throw new IOException("マザー・BIOS・DIMM構成が変更されています"); }
            Check(expected); ct.ThrowIfCancellationRequested();
            return WithBus(transport, bus =>
            {
            moduleCheck = true; ct.ThrowIfCancellationRequested();
            if (fullIdentification)
            {
                var identity = (bus as IEneMemoryIdentifyingIo)?.IdentifyController(expected.Address);
                if (EneMemoryDefinition.Resolve(identity)?.Id != expected.DefinitionId || EneMemoryDefinition.Proof(identity!) != expected.ControllerFingerprint)
                    throw new IOException("ENEコントローラーの完全照合に失敗しました");
                lock (gate)
                {
                    _ = Metadata(metadata!, transport);
                    Check(expected); current[(expected.BusId, expected.Address)] = new(expected, clock.GetTimestamp(), platform!.CapturedAt);
                }
            }
            else if (bus is not IEneMemoryStreamIo fast || !fast.VerifyPreviouslyIdentified(expected.Address))
                throw new IOException("ENEコントローラーの接続確認に失敗しました");
            Check(expected); ct.ThrowIfCancellationRequested();
            session = new(this, expected, bus, !fullIdentification);
            try { return operation(session); }
            finally { session.Close(); }
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is StaleMetadataException or EneMemoryFreshnessException) { throw; }
        catch
        {
            if (session is null && !moduleCheck) Invalidate(expected.BusId);
            else if (session is null) { lock (gate) current.Remove((expected.BusId, expected.Address)); }
            else if (session.TransportFailed) { lock (gate) current.Remove((expected.BusId, expected.Address)); }
            throw;
        }
        }, ct);
    }
    private bool Fresh(Entry entry) => clock.GetElapsedTime(entry.IdentifiedAt).TotalSeconds <= 2 && clock.GetElapsedTime(entry.PlatformAt).TotalSeconds <= 2;
    private sealed class Session(EneMemoryDeviceBackend owner, EneMemoryConnection expected, IEneMemoryIo bus, bool requireRecent) : IEneMemorySession
    {
        private bool closed;
        public bool TransportFailed { get; private set; }
        public void Close() => closed = true;
        private void Check()
        {
            if (closed || Environment.CurrentManagedThreadId != owner.threadId) throw new IOException("メモリRGBの操作範囲外です");
            owner.Check(expected);
            if (requireRecent)
            {
                lock (owner.gate) if (!owner.Fresh(owner.current[(expected.BusId, expected.Address)]))
                    throw new EneMemoryFreshnessException("メモリRGBの完全照合が古くなりました");
            }
        }
        private T Io<T>(Func<Action, T> operation, Action? beforeData)
        {
            Check(); var denied = false;
            void Guard()
            {
                try { Check(); beforeData?.Invoke(); Check(); }
                catch { denied = true; throw; }
            }
            try { return operation(Guard); } catch { if (!denied) TransportFailed = true; throw; }
        }
        public byte Read(ushort register, Action? beforeData = null)
        {
            EneMemoryRgbBus.ValidateRead(register);
            return Io(guard => { if (bus is IEneMemoryGuardedIo guarded) return guarded.Read(expected.Address, register, guard);
                guard(); return bus.Read(expected.Address, register); }, beforeData);
        }
        public void Write(ushort register, byte value, Action beforeData)
        {
            EneMemoryRgbBus.ValidateWrite(register, value); ArgumentNullException.ThrowIfNull(beforeData);
            _ = Io(guard => { if (bus is IEneMemoryGuardedIo guarded) guarded.Write(expected.Address, register, value, guard);
                else { guard(); bus.Write(expected.Address, register, value); } return true; }, beforeData);
        }
        public void WritePixel(int led, byte red, byte blue, byte green, Action beforeData)
        {
            if (led is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(led)); ArgumentNullException.ThrowIfNull(beforeData);
            if (bus is IEneMemoryGuardedIo guarded)
                _ = Io(guard => { guarded.WritePixel(expected.Address, led, red, blue, green, guard); return true; }, beforeData);
            else for (var i = 0; i < 3; i++) Write((ushort)(0x8100 + led * 3 + i), new[] { red, blue, green }[i], beforeData);
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (resourcesClosed) return ValueTask.CompletedTask;
            if (Interlocked.Exchange(ref disposed, 1) == 0) { Invalidate(); metadataWork.CompleteAdding(); }
            if (disposalWork is null || disposalWork.IsCompleted) disposalWork = CloseAsync();
            return new(disposalWork);
        }
    }
    private async Task CloseAsync()
    {
        // Queue behind in-flight operations, then keep the owning thread alive if a close fails.
        await Enqueue(work, () => { RetryPendingBuses(); return true; }, CancellationToken.None).ConfigureAwait(false);
        work.CompleteAdding();
        await Task.WhenAll(worker, metadataWorker).ConfigureAwait(false);
        lock (gate) resourcesClosed = true;
    }
}
