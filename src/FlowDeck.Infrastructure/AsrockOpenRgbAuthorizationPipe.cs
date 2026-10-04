using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace FlowDeck.Infrastructure;

// One pipe, one exact owned child, one sequence. A failure ends this channel; no reconnect or
// automatic permission. The decision callback must check the durable operation/continuity and
// must not perform SDK or HID IO (which would recurse into this pending exchange).
internal sealed class AsrockOpenRgbAuthorizationPipe : IAsyncDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly CancellationTokenSource stopping = new();
    private readonly Action<AsrockAuthorizationRequest, CancellationToken> authorize;
    private readonly TimeSpan requestTimeout;
    private readonly AsrockAuthorizationProcess parent;
    private AsrockAuthorizationProcess? child;
    private readonly object sync = new();
    private Task? running, closing;
    private volatile bool disposed;
    public string Name { get; } = AsrockOpenRgbAuthorizationProtocol.PipePrefix + Guid.NewGuid().ToString("N");
    public uint ParentId => parent.Id;
    public ulong ParentCreatedFileTime => parent.CreatedFileTime;
    public bool ResourcesClosed => disposed;
    private Exception? failure;
    public Exception? Failure => Volatile.Read(ref failure);
    private void RecordFailure(Exception error) => Interlocked.CompareExchange(ref failure, error, null);

    public AsrockOpenRgbAuthorizationPipe(Action<AsrockAuthorizationRequest, CancellationToken> authorize, TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(authorize); this.authorize = authorize;
        this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(1);
        if (this.requestTimeout < TimeSpan.FromMilliseconds(100) || this.requestTimeout > TimeSpan.FromSeconds(5)) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        parent = new((uint)Environment.ProcessId);
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value ?? throw new UnauthorizedAccessException("通信路のWindows所有者がありません");
            // LocalSystem is not an account SID, and is already allowed by Security().
            var users = identity.User!.IsAccountSid() ? new[] { sid } : Array.Empty<string>();
            var security = CoolingServicePipeServer.Security(users);
            pipe = NamedPipeServerStreamAcl.Create(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance | PipeOptions.WriteThrough, 16384, 16384, security);
        }
        catch { parent.Dispose(); stopping.Dispose(); throw; }
    }
    public void Bind(uint childId, ulong childCreatedFileTime)
    {
        lock (sync)
        {
            if (closing is not null || disposed || running is not null || child is not null || childCreatedFileTime == 0 || childId == ParentId)
                throw new InvalidOperationException("OpenRGB認可通信の所有者は一度だけ設定できます");
            child = new(childId, childCreatedFileTime);
        }
    }
    public Task RunAsync(CancellationToken ct = default)
    {
        lock (sync)
        {
            if (closing is not null || disposed || running is not null || child is null) throw new InvalidOperationException("OpenRGB認可通信の開始状態が不正です");
            return running = Task.Run(() => ServeAsync(ct));
        }
    }
    private void CheckPeer()
    {
        parent.Check(); child!.Check();
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var actual) || actual != child.Id)
            throw new UnauthorizedAccessException("所有していないOpenRGBプロセスからの認可要求です");
    }
    private async Task ServeAsync(CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping.Token);
        var observer = ObserveChildAsync(lifetime);
        try
        {
            using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                connecting.CancelAfter(TimeSpan.FromSeconds(10));
                await pipe.WaitForConnectionAsync(connecting.Token).ConfigureAwait(false);
            }
            CheckPeer(); ulong expected = 1;
            while (true)
            {
                // An idle channel is allowed; a partial request has a bounded completion time.
                var header = new byte[4]; await pipe.ReadExactlyAsync(header.AsMemory(0, 1), lifetime.Token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); deadline.CancelAfter(requestTimeout);
                await pipe.ReadExactlyAsync(header.AsMemory(1), deadline.Token).ConfigureAwait(false);
                var bytes = new byte[AsrockOpenRgbAuthorizationProtocol.Length(header, AsrockOpenRgbAuthorizationProtocol.MaximumRequestBytes)];
                await pipe.ReadExactlyAsync(bytes, deadline.Token).ConfigureAwait(false);
                CheckPeer(); var request = AsrockOpenRgbAuthorizationProtocol.Read(bytes);
                if (request.Sequence != expected++) throw new IOException("OpenRGB通信認可の連番が一致しません");
                Exception? denied = null;
                try { deadline.Token.ThrowIfCancellationRequested(); authorize(request, deadline.Token); }
                catch (Exception error) { denied = error; }
                // A callback which returned late can never publish permission.
                deadline.Token.ThrowIfCancellationRequested(); CheckPeer();
                await AsrockOpenRgbAuthorizationProtocol.WriteAsync(pipe, AsrockOpenRgbAuthorizationProtocol.Reply(request.Sequence, denied is null),
                    AsrockOpenRgbAuthorizationProtocol.MaximumReplyBytes, deadline.Token).ConfigureAwait(false);
                if (denied is not null) throw new IOException("OpenRGBの機器通信を許可できません", denied);
            }
        }
        catch (Exception error)
        {
            if (!stopping.IsCancellationRequested && !ct.IsCancellationRequested) RecordFailure(error);
            throw;
        }
        finally
        {
            lifetime.Cancel(); pipe.Dispose(); await observer.ConfigureAwait(false);
        }
    }
    private async Task ObserveChildAsync(CancellationTokenSource lifetime)
    {
        try
        {
            while (true) { await Task.Delay(100, lifetime.Token).ConfigureAwait(false); child!.Check(); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { RecordFailure(error); lifetime.Cancel(); }
    }
    public ValueTask DisposeAsync()
    {
        lock (sync) return new(closing ??= CloseAsync());
    }
    private async Task CloseAsync()
    {
        if (disposed) return;
        stopping.Cancel(); pipe.Dispose();
        if (running is not null) try { await running.ConfigureAwait(false); } catch (Exception) { }
        child?.Dispose(); parent.Dispose(); stopping.Dispose(); disposed = true;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
