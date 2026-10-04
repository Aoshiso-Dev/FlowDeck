using FlowDeck.Core;
using Microsoft.Win32.SafeHandles;

namespace FlowDeck.Infrastructure;

internal interface IAsrockMetadataProbe : IDisposable
{
    bool ResourcesClosed { get; }
    IHardwareContinuityWatch Watch();
    (string? Serial, int? Revision) Read();
}
internal sealed record AsrockHidContinuityReceipt(string Path, string? Serial, int Revision, string Handover);

// Proof belongs to this live notification lifetime, not to a saved USB path. A new capture has a
// new handover ID even if Windows reuses the path, serial and product revision. This is one input
// to the compiled detection scope, not an independent authorization to send RGB commands.
internal sealed class AsrockHidContinuity : IAsyncDisposable
{
    private sealed class Metadata : IAsrockMetadataProbe
    {
        private readonly SafeFileHandle handle;
        public Metadata(string path)
        {
            handle = AsrockHidIdentity.OpenMetadata(path);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("ASRockの属性ハンドルを開けません"); }
        }
        public bool ResourcesClosed => handle.IsClosed;
        public IHardwareContinuityWatch Watch() => WindowsDeviceHandleWatch.Create(handle);
        public (string? Serial, int? Revision) Read() => AsrockHidIdentity.Read(handle);
        public void Dispose() => handle.Dispose();
    }
    private readonly SemaphoreSlim gate = new(1, 1);
    private IAsrockMetadataProbe? metadata;
    private IHardwareContinuityWatch? watch;
    private DeviceStartupException? pendingStart;
    private volatile bool ready, closing, closed;
    public AsrockHidContinuityReceipt Receipt { get; private set; } = null!;
    public bool ResourcesClosed => closed;
    private AsrockHidContinuity() { }
    public static Task<AsrockHidContinuity> CaptureAsync(string path, CancellationToken ct = default) => CaptureAsync(path, p => new Metadata(p), ct);
    internal static async Task<AsrockHidContinuity> CaptureAsync(string path, Func<string, IAsrockMetadataProbe> open, CancellationToken ct = default)
    {
        if (!DeviceConfigurationRules.Text(path, 2048)) throw new ArgumentException("ASRockの接続先が不正です");
        ArgumentNullException.ThrowIfNull(open); ct.ThrowIfCancellationRequested();
        var owner = new AsrockHidContinuity();
        try
        {
            owner.metadata = open(path);
            try { owner.watch = owner.metadata.Watch(); }
            catch (DeviceStartupException pending) { owner.pendingStart = pending; throw; }
            owner.watch.Check(); ct.ThrowIfCancellationRequested();
            var first = owner.metadata.Read(); owner.watch.Check(); ct.ThrowIfCancellationRequested();
            var second = owner.metadata.Read(); owner.watch.Check(); ct.ThrowIfCancellationRequested();
            if (first != second || first.Revision is not (>= 0 and <= 65535) ||
                first.Serial is not null && !HardwareSensorCapture.UsefulSerial(first.Serial))
                throw new IOException("ASRockの属性が変わったか、リビジョンを確認できません");
            owner.Receipt = new(path, first.Serial?.Trim(), first.Revision.Value, Guid.NewGuid().ToString("N"));
            // CM handle notifications survive closing this metadata handle. Do not keep a handle
            // that would prevent query-remove, or any RGB protocol handle while OpenRGB owns it.
            owner.metadata.Dispose();
            if (!owner.metadata.ResourcesClosed) throw new IOException("ASRock属性ハンドルの終了が未確認です");
            owner.watch.Check(); owner.ready = true; owner.Check(); return owner;
        }
        catch (Exception failure)
        {
            await DeviceStartupException.RethrowAfterCleanupAsync(failure, owner, () => owner.ResourcesClosed).ConfigureAwait(false);
            throw;
        }
    }
    public void Check()
    {
        if (!ready || closing || closed || watch is null) throw new IOException("ASRockの引き渡し監視が有効ではありません");
        watch.Check();
    }
    public void Check(AsrockDeviceConnection connection)
    {
        Check();
        var location = "location:" + SettingsTransfer.Hash(connection.Path);
        var serial = Receipt.Serial is { } value ? "serial:" + SettingsTransfer.Hash(new { Vendor = 0x26ce, Product = 0x01a2, Serial = value }) : null;
        if (!connection.Path.Equals(Receipt.Path, StringComparison.OrdinalIgnoreCase) || connection.DeviceRevision != Receipt.Revision ||
            !DeviceServiceProtocol.Id(connection.ConnectionId) || connection.Confidence == IdentityConfidence.SerialBound && (serial is null || connection.NativeIdentity != serial) ||
            connection.Confidence == IdentityConfidence.ConnectionBound && connection.NativeIdentity != location ||
            connection.Confidence is not (IdentityConfidence.SerialBound or IdentityConfidence.ConnectionBound))
            throw new IOException("ASRockの再取得した個体・属性が引き渡し前と一致しません");
        Check();
    }
    public async ValueTask DisposeAsync()
    {
        closing = true;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (closed) return;
            if (watch is not null && !watch.ResourcesClosed)
            {
                watch.Dispose();
                if (!watch.ResourcesClosed) throw new IOException("ASRockの接続監視の終了が未確認です");
            }
            if (pendingStart is not null && !pendingStart.ResourcesClosed)
            {
                await pendingStart.DisposeAsync().ConfigureAwait(false);
                if (!pendingStart.ResourcesClosed) throw new IOException("ASRockの監視登録途中の資源が残っています");
            }
            if (metadata is not null && !metadata.ResourcesClosed)
            {
                metadata.Dispose();
                if (!metadata.ResourcesClosed) throw new IOException("ASRockの属性ハンドルの終了が未確認です");
            }
            closed = true;
        }
        finally { gate.Release(); }
    }
}
