using System.Collections.Concurrent;
using FlowDeck.Core;
using HidSharp;

namespace FlowDeck.Infrastructure;

internal sealed record AsrockDeviceConnection(string Path, string NativeIdentity, string ConnectionId, IdentityConfidence Confidence, int? DeviceRevision = null);
internal interface IAsrockDeviceBackend : IAsyncDisposable
{
    Task<AsrockDeviceConnection[]> DiscoverAsync(CancellationToken ct);
    void Check(AsrockDeviceConnection expected);
    IAsrockRgbSession Borrow(AsrockDeviceConnection expected);
    void CheckConflicts();
}

// All handles and recursive acquisitions of the legacy ASRock mutex live on one thread.
// A retained handle cannot be reopened silently against a replacement at the same path.
internal sealed class AsrockHidDeviceBackend : IAsrockDeviceBackend
{
    internal sealed record Detected(string Path, string? Serial);
    private sealed record Entry(AsrockDeviceConnection Connection, IAsrockRgbSession Session);
    private readonly Func<Detected[]> enumerate;
    private readonly Func<string, IAsrockRgbSession> connect;
    private readonly Action conflicts;
    private readonly Dictionary<string, Entry> opened = [];
    private readonly Dictionary<string, IAsrockRgbSession> retired = [];
    private readonly BlockingCollection<Action> work = new(64);
    private readonly SemaphoreSlim lifecycle = new(1);
    private readonly Task worker;
    private int threadId, disposed;
    private bool closed;
    public AsrockHidDeviceBackend() : this(EnumerateNative, path => new AsrockRgbSession(path, captureIdentity: true), AsrockRgbMonitor.CheckConflicts) { }
    internal AsrockHidDeviceBackend(Func<Detected[]> enumerate, Func<string, IAsrockRgbSession> connect, Action conflicts)
    {
        this.enumerate = enumerate; this.connect = connect; this.conflicts = conflicts;
        worker = Task.Factory.StartNew(() =>
        {
            threadId = Environment.CurrentManagedThreadId;
            foreach (var action in work.GetConsumingEnumerable()) action();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    private static Detected[] EnumerateNative() => DeviceList.Local.GetHidDevices(AsrockRgbMonitor.VendorId, AsrockRgbMonitor.ProductId).Take(33)
        .Select(d => new Detected(d.DevicePath, null)).ToArray();
    internal static string[] EnumeratePaths() => EnumerateNative().Select(d => d.Path).ToArray();
    // Recovery must never open a newly discovered, uncaptured controller as a side effect.
    internal static IAsrockDeviceBackend ForPaths(string[] paths, Action<string> beforeOpen)
    {
        var selected = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        return new AsrockHidDeviceBackend(() => EnumerateNative().Where(d => selected.Contains(d.Path)).ToArray(),
            path => { beforeOpen(path); return new AsrockRgbSession(path, captureIdentity: true); }, AsrockRgbMonitor.CheckConflicts);
    }
    private Task<T> Invoke<T>(Func<T> action, CancellationToken ct = default, bool closing = false)
    {
        ObjectDisposedException.ThrowIf(!closing && Volatile.Read(ref disposed) != 0, this);
        // Guards may reenter the current exchange, but cleanup must wait until that exchange returns.
        if (!closing && Environment.CurrentManagedThreadId == threadId) return Task.FromResult(action());
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Run()
        {
            try
            {
                ObjectDisposedException.ThrowIf(!closing && Volatile.Read(ref disposed) != 0, this);
                ct.ThrowIfCancellationRequested(); result.TrySetResult(action());
            }
            catch (Exception e) { result.TrySetException(e); }
        }
        try { if (!work.TryAdd(Run)) throw new IOException("ASRockの通信待機数が上限に達しました"); }
        catch (InvalidOperationException e) { result.TrySetException(new IOException("ASRockの通信を終了しました", e)); }
        catch (IOException e) { result.TrySetException(e); }
        return result.Task;
    }
    public Task<AsrockDeviceConnection[]> DiscoverAsync(CancellationToken ct) => Invoke(() =>
    {
        CheckConflicts();
        CloseRetired();
        var devices = enumerate();
        if (devices is not { Length: <= DeviceRegistry.MaximumDevices } || devices.Any(d => d is null || !DeviceConfigurationRules.Text(d.Path, 2048)) ||
            devices.DistinctBy(d => d.Path).Count() != devices.Length)
            throw new IOException("ASRock機器の台数または接続先を確認できません");
        foreach (var old in opened.Keys.Except(devices.Select(d => d.Path)).ToArray())
        { Retire(old, opened[old].Session); }
        CloseRetired();
        foreach (var item in devices)
        {
            var path = item.Path;
            if (opened.ContainsKey(path)) continue;
            IAsrockRgbSession session;
            try { session = connect(path); }
            catch (AsrockSessionOpenException e) { retired.Add(path, e.PendingSession); throw; }
            opened.Add(path, new(new(path, "location:" + SettingsTransfer.Hash(path), Guid.NewGuid().ToString("N"), IdentityConfidence.ConnectionBound), session));
        }
        // Trust only metadata captured after opening a retained session, never a cached enumerator serial.
        var identified = opened.Values.Select(e => (Entry: e, Serial: (e.Session as IAsrockIdentitySession)?.VerifiedSerial?.Trim(),
            Revision: (e.Session as IAsrockIdentitySession)?.DeviceRevision)).ToArray();
        foreach (var item in identified)
        {
            var serial = HardwareSensorCapture.UsefulSerial(item.Serial) && identified.Count(e => e.Serial == item.Serial) == 1 ? item.Serial : null;
            var c = item.Entry.Connection;
            var native = serial is null ? "location:" + SettingsTransfer.Hash(c.Path) : "serial:" + SettingsTransfer.Hash(new { Vendor = 0x26ce, Product = 0x01a2, Serial = serial });
            var revision = item.Revision is >= 0 and <= 65535 ? item.Revision : null;
            if (c.NativeIdentity != native || c.DeviceRevision != revision)
                opened[c.Path] = item.Entry with { Connection = c with { NativeIdentity = native, DeviceRevision = revision,
                    ConnectionId = Guid.NewGuid().ToString("N"), Confidence = serial is null ? IdentityConfidence.ConnectionBound : IdentityConfidence.SerialBound } };
        }
        return opened.Values.Select(e => e.Connection).ToArray();
    }, ct);
    private Entry Current(AsrockDeviceConnection expected) => opened.TryGetValue(expected.Path, out var entry) && entry.Connection == expected
        ? entry : throw new IOException("ASRock照明の接続が変更されました");
    private void Retire(string path, IAsrockRgbSession session)
    {
        opened.Remove(path);
        retired.Add(path, session);
    }
    private void CloseRetired()
    {
        var errors = new List<Exception>();
        foreach (var entry in retired.ToArray())
        {
            try { entry.Value.Dispose(); retired.Remove(entry.Key); }
            catch (Exception e) { errors.Add(e); }
        }
        if (errors.Count > 0) throw new AggregateException("ASRock接続の終了に失敗しました", errors);
    }
    public void Check(AsrockDeviceConnection expected) => _ = Invoke(() => Current(expected)).GetAwaiter().GetResult();
    public void CheckConflicts() => conflicts();
    public IAsrockRgbSession Borrow(AsrockDeviceConnection expected) { Check(expected); return new Borrowed(this, expected); }
    private sealed class Borrowed(AsrockHidDeviceBackend backend, AsrockDeviceConnection connection) : IAsrockGuardedRgbSession
    {
        private bool disposed;
        public byte[] Exchange(byte[] request) => Exchange(request, () => { });
        public byte[] Exchange(byte[] request, Action beforeExchange) => backend.Invoke(() =>
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var entry = backend.Current(connection);
            beforeExchange(); // Outside the catch: rejecting an old frame does not invalidate a healthy HID handle.
            try { return entry.Session.Exchange(request); }
            catch (Exception original)
            {
                backend.Retire(connection.Path, entry.Session);
                try { backend.CloseRetired(); }
                catch (Exception closing) { throw new AggregateException("ASRock通信と接続終了に失敗しました", original, closing); }
                throw;
            }
        }).GetAwaiter().GetResult();
        public void Dispose() => disposed = true; // The provider owns the underlying handle, not this operation.
    }
    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (closed) return;
            // Keep the owning thread alive when a close fails, so recursive mutex ownership can be retried there.
            await Invoke(() =>
            {
                foreach (var entry in opened.ToArray()) Retire(entry.Key, entry.Value.Session);
                CloseRetired(); return true;
            }, closing: true).ConfigureAwait(false);
            work.CompleteAdding(); await worker.ConfigureAwait(false); closed = true;
        }
        finally { lifecycle.Release(); }
    }
}
