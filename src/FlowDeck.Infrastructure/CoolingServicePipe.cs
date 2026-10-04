using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public static class CoolingPipeFrames
{
    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 2 or > CoolingServiceProtocol.MaximumFrameBytes) throw new IOException("サービス応答の長さが不正です");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false); return bytes;
    }
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Contract.Json);
        if (bytes.Length > CoolingServiceProtocol.MaximumFrameBytes) throw new IOException("サービス応答が大きすぎます");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false); await stream.WriteAsync(bytes, ct).ConfigureAwait(false); await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}

public sealed class CoolingServicePipeServer(CoolingServiceRuntime runtime, string[] allowedSids, IEventLog log,
    string pipeName = CoolingServiceProtocol.PipeName)
{
    public static PipeSecurity Security(IEnumerable<string> users)
    {
        var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        foreach (var sid in users.Distinct(StringComparer.Ordinal))
        {
            var identity = new SecurityIdentifier(sid);
            if (!identity.IsAccountSid()) throw new ArgumentException("許可するユーザーのSIDが不正です");
            security.AddAccessRule(new(identity, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        }
        return security;
    }
    public async Task RunAsync(CancellationToken ct)
    {
        var security = Security(allowedSids);
        while (!ct.IsCancellationRequested)
        {
            using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance | PipeOptions.WriteThrough, 8192, 8192, security);
            string? session = null;
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                string? sid = null;
                pipe.RunAsClient(() =>
                {
                    using var identity = WindowsIdentity.GetCurrent(true) ?? throw new UnauthorizedAccessException("Windowsユーザーを確認できません");
                    var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
                    if (identity.Groups?.Contains(network) == true) throw new UnauthorizedAccessException("ネットワーク経由の接続は許可されていません");
                    sid = identity.User?.Value;
                });
                if (sid is null || !allowedSids.Contains(sid, StringComparer.Ordinal)) throw new UnauthorizedAccessException("許可されていないWindowsユーザーです");
                var hello = await runtime.OpenSessionAsync(sid).ConfigureAwait(false); session = hello.Session;
                await CoolingPipeFrames.WriteAsync(pipe, hello, ct).ConfigureAwait(false);
                await using var stream = new CoolingServiceSessionStream(runtime, pipe, hello, log, ct);
                var requests = Channel.CreateBounded<CoolingServiceRequest>(new BoundedChannelOptions(64)
                { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
                var executor = ExecuteQueuedAsync(requests.Reader, stream);
                try
                {
                    while (pipe.IsConnected && !stream.Token.IsCancellationRequested)
                    {
                        var request = CoolingServiceProtocol.Read(await CoolingPipeFrames.ReadAsync(pipe, stream.Token).ConfigureAwait(false));
                        // The reader admits renewals while the ordered consumer waits for hardware.
                        if (request.Session != session) throw new UnauthorizedAccessException("接続に属さないセッションです");
                        if (request.Command == "renew") await stream.ReplyAsync(await runtime.ExecuteAsync(request).ConfigureAwait(false)).ConfigureAwait(false);
                        else if (!requests.Writer.TryWrite(request)) throw new IOException("サービスの待機中の要求が多すぎます");
                    }
                }
                finally { stream.Close(); requests.Writer.TryComplete(); await executor.ConfigureAwait(false); }
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException or ObjectDisposedException)
            { log.Write("servicePipe", new { error = e.Message, stopping = ct.IsCancellationRequested }); }
            finally { if (session is not null) await runtime.DisconnectAsync(session).ConfigureAwait(false); }
        }
    }
    private async Task ExecuteQueuedAsync(ChannelReader<CoolingServiceRequest> requests, CoolingServiceSessionStream stream)
    {
        try
        {
            await foreach (var request in requests.ReadAllAsync(stream.Token).ConfigureAwait(false))
            {
                stream.Token.ThrowIfCancellationRequested();
                stream.Executing = true;
                try
                {
                    var response = await runtime.ExecuteAsync(request).ConfigureAwait(false);
                    await stream.ReplyAsync(response).ConfigureAwait(false);
                }
                finally { stream.Executing = false; }
                if (request.Command == "release" || !runtime.HasOwner) break;
            }
        }
        finally { stream.Close(); }
    }
}

/// <summary>State and completed operations flow independently of a pending hardware command.</summary>
internal sealed class CoolingServiceSessionStream : IAsyncDisposable
{
    private readonly CoolingServiceRuntime runtime;
    private readonly Stream pipe;
    private readonly ServiceHello hello;
    private readonly IEventLog log;
    private readonly CancellationTokenSource stop;
    private readonly SemaphoreSlim writes = new(1);
    private readonly object operationSync = new();
    private bool closed;
    private readonly Channel<OperationResult> operations = Channel.CreateBounded<OperationResult>(new BoundedChannelOptions(256)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task publisher;
    public volatile bool Executing;
    public CancellationToken Token => stop.Token;
    public void Close() => stop.Cancel();
    public CoolingServiceSessionStream(CoolingServiceRuntime runtime, Stream pipe, ServiceHello hello, IEventLog log, CancellationToken ct)
    {
        this.runtime = runtime; this.pipe = pipe; this.hello = hello; this.log = log;
        stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        runtime.Coordinator.OperationCompleted += OnOperation;
        publisher = Task.Run(PublishAsync);
    }
    private void OnOperation(OperationResult result)
    {
        lock (operationSync)
        {
            if (closed) return;
            // Runtime namespaces client operation IDs; user-facing history keeps the caller's original ID.
            var prefix = hello.Session + "/";
            if (result.Id.StartsWith(prefix, StringComparison.Ordinal)) result = result with { Id = result.Id[prefix.Length..] };
            if (!operations.Writer.TryWrite(result))
            {
                log.Write("serviceStream", new { error = "操作履歴の配信が追いつかないため接続を終了します" });
                stop.Cancel();
            }
        }
    }
    public Task ReplyAsync(CoolingServiceReply reply) => WriteAsync(new(CoolingServiceProtocol.Version, hello.Epoch, hello.Session, "reply", reply, null));
    private async Task WriteAsync(CoolingServiceMessage message)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
        await writes.WaitAsync(deadline.Token).ConfigureAwait(false);
        try { await CoolingPipeFrames.WriteAsync(pipe, message, deadline.Token).ConfigureAwait(false); }
        catch { stop.Cancel(); throw; } // A partial write must never be followed by another frame.
        finally { writes.Release(); }
    }
    private async Task PublishAsync()
    {
        long sequence = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            do
            {
                if (!runtime.HasOwner && !Executing) throw new IOException("サービスの操作権が終了しました");
                var batch = new List<OperationResult>(32);
                while (batch.Count < 32 && operations.Reader.TryRead(out var operation)) batch.Add(operation);
                // State() reads cached observations only, preserving their original acquisition timestamps.
                var update = new ServiceCoolingUpdate(++sequence, runtime.State(), batch.ToArray());
                await WriteAsync(new(CoolingServiceProtocol.Version, hello.Epoch, hello.Session, "update", null, update)).ConfigureAwait(false);
            } while (await timer.WaitForNextTickAsync(Token).ConfigureAwait(false));
        }
        catch (Exception e)
        {
            if (!Token.IsCancellationRequested) log.Write("serviceStream", new { error = e.Message });
            stop.Cancel();
        }
    }
    public async ValueTask DisposeAsync()
    {
        runtime.Coordinator.OperationCompleted -= OnOperation;
        lock (operationSync) closed = true;
        await stop.CancelAsync(); await publisher.ConfigureAwait(false);
        writes.Dispose(); stop.Dispose();
    }
}

public sealed class ServiceRequestUncertainException(string requestId) : IOException("サービスの操作結果が未確認です。状態または処理結果を確認してください")
{
    public string RequestId { get; } = requestId;
}

/// <summary>One connection, one reader; replies continue to be consumed after a caller times out. Never resends writes.</summary>
public sealed class CoolingServicePipeClient : IAsyncDisposable
{
    private readonly string pipeName;
    private readonly Func<int> expectedServerPid;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writes = new(1);
    private readonly ConcurrentDictionary<string, (long Sequence, TaskCompletionSource<CoolingServiceReply> Completion)> pending = new();
    private readonly Channel<ServiceCoolingUpdate> updates = Channel.CreateBounded<ServiceCoolingUpdate>(new BoundedChannelOptions(16)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private NamedPipeClientStream? pipe;
    private Task? reader, heartbeat, notifications;
    private long sequence, renewalSequence, updateSequence;
    private bool disposed;
    private bool requireDemo;
    public ServiceHello? Hello { get; private set; }
    public bool Connected => Hello is not null && pipe?.IsConnected == true && reader?.IsCompleted == false;
    public event Action<ServiceCoolingState>? StateReceived;
    public event Action<OperationResult>? OperationReceived;
    public string? Error { get; private set; }
    public CoolingServicePipeClient() : this(CoolingServiceProtocol.PipeName, WindowsCoolingServiceIdentity.ProcessId) { }
    internal CoolingServicePipeClient(string testPipeName, Func<int> expectedPid)
    { pipeName = testPipeName; expectedServerPid = expectedPid; }
    public static CoolingServicePipeClient ForDemo(string pipeName, int serverPid)
    {
        const string prefix = "FlowDeck.Cooling.Test.";
        if (serverPid <= 0 || !pipeName.StartsWith(prefix, StringComparison.Ordinal) || !CoolingServiceProtocol.Id(pipeName[prefix.Length..]))
            throw new ArgumentException("専用のデモ通信名とプロセスIDが必要です");
        return new(pipeName, () => serverPid) { requireDemo = true };
    }
    public async Task ConnectAsync(CancellationToken ct = default, bool startHeartbeat = true)
    {
        if (pipe is not null || disposed) throw new InvalidOperationException("このサービス接続は使用済みです");
        var expected = expectedServerPid(); if (expected <= 0) throw new IOException("FlowDeckのサービスが起動していません");
        var next = new NamedPipeClientStream(".", pipeName, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough, TokenImpersonationLevel.Identification, HandleInheritability.None);
        try
        {
            await next.ConnectAsync(3000, ct).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(next.SafePipeHandle, out var pid) || pid != expected || expectedServerPid() != expected)
                throw new UnauthorizedAccessException("SCMのサービスと通信先のプロセスが一致しません");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(3));
            var hello = JsonSerializer.Deserialize<ServiceHello>(await CoolingPipeFrames.ReadAsync(next, deadline.Token).ConfigureAwait(false), Contract.Json)
                ?? throw new IOException("サービスから応答がありません");
            if (hello.Version != CoolingServiceProtocol.Version || !CoolingServiceProtocol.Id(hello.Epoch) || !CoolingServiceProtocol.Id(hello.Session) ||
                hello.LeaseSeconds != CoolingServiceProtocol.LeaseSeconds) throw new IOException("サービスの通信バージョンが一致しません");
            if (requireDemo && !hello.Demo) throw new UnauthorizedAccessException("デモ用クライアントは実機サービスへ接続できません");
            pipe = next; Hello = hello; notifications = DeliverUpdatesAsync(); reader = ReadRepliesAsync();
            if (startHeartbeat) heartbeat = HeartbeatAsync();
        }
        catch { next.Dispose(); throw; }
    }
    public async Task<CoolingServiceReply> CallAsync<T>(string command, T payload, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!Connected || Hello is null) throw new IOException(Error ?? "サービスに接続していません");
        var id = Guid.NewGuid().ToString("N"); var result = new TaskCompletionSource<CoolingServiceReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = false;
        try
        {
            await writes.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (pending.Count >= 64) throw new IOException("サービスの処理中の要求が多すぎます");
                var request = new CoolingServiceRequest(CoolingServiceProtocol.Version, Hello.Epoch, Hello.Session, id,
                    command == "renew" ? Interlocked.Increment(ref renewalSequence) : Interlocked.Increment(ref sequence), command, CoolingServiceProtocol.Payload(payload));
                _ = CoolingServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(request, Contract.Json));
                pending[id] = (request.Sequence, result);
                // Any failure during a framed write closes the stream so a partial frame cannot be reused.
                try { sent = true; await CoolingPipeFrames.WriteAsync(pipe!, request, lifetime.Token).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
                { pipe!.Dispose(); throw new ServiceRequestUncertainException(id); }
                catch { pipe!.Dispose(); throw; }
            }
            finally { writes.Release(); }
            try { return await result.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(8), ct).ConfigureAwait(false); }
            catch (TimeoutException) { throw new ServiceRequestUncertainException(id); }
            catch (OperationCanceledException) when (sent) { throw new ServiceRequestUncertainException(id); }
        }
        finally { pending.TryRemove(id, out _); }
    }
    public async Task<T?> InvokeAsync<T, TBody>(string command, TBody body, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var response = await CallAsync(command, body, timeout, ct).ConfigureAwait(false);
        if (!response.Success) throw new IOException(response.Error ?? "サービスが操作を拒否しました");
        return response.Payload.Deserialize<T>(Contract.Json);
    }
    private async Task ReadRepliesAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var bytes = await CoolingPipeFrames.ReadAsync(pipe!, lifetime.Token).ConfigureAwait(false);
                var message = CoolingServiceProtocol.ReadMessage(bytes, Hello!);
                if (message.Update is { } update)
                {
                    if (update.Sequence != updateSequence + 1) throw new IOException("サービスの状態配信に欠落または重複があります");
                    updateSequence = update.Sequence;
                    if (!updates.Writer.TryWrite(update)) throw new IOException("画面への状態・操作履歴の配信が追いつきません");
                    continue;
                }
                var reply = message.Reply!;
                if (pending.TryGetValue(reply.Id, out var completion))
                {
                    if (reply.Sequence != completion.Sequence) throw new IOException("サービス応答の要求番号が一致しません");
                    completion.Completion.TrySetResult(reply);
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or OperationCanceledException or ObjectDisposedException)
        {
            Error = lifetime.IsCancellationRequested ? null : e.Message;
            foreach (var completion in pending.Values) completion.Completion.TrySetException(new IOException("サービス接続が切断されました。操作結果は未確認です", e));
        }
        finally { updates.Writer.TryComplete(); pipe?.Dispose(); }
    }
    private async Task DeliverUpdatesAsync()
    {
        // UI callbacks never run on the pipe reader or the heartbeat loop.
        await foreach (var update in updates.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            Notify(StateReceived, update.State);
            foreach (var operation in update.Operations) Notify(OperationReceived, operation);
        }
    }
    private static void Notify<T>(Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (Action<T> handler in handlers.GetInvocationList()) try { handler(value); } catch { /* Listener failure cannot stop IPC or cooling. */ }
    }
    private async Task HeartbeatAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            do
            {
                _ = await InvokeAsync<bool, object?>("renew", null, ct: lifetime.Token).ConfigureAwait(false);
            } while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false));
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        { if (!lifetime.IsCancellationRequested) { Error = e.Message; pipe?.Dispose(); } }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        // Closing the pipe is also an authoritative release. The server performs and records restoration.
        await lifetime.CancelAsync(); pipe?.Dispose();
        if (reader is not null) await reader.ConfigureAwait(false); if (heartbeat is not null) await heartbeat.ConfigureAwait(false);
        if (notifications is not null) await notifications.ConfigureAwait(false);
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out int serverProcessId);
}

public static class WindowsCoolingServiceIdentity
{
    public static bool IsInstalled()
    {
        var manager = OpenSCManager(null, null, 1); if (manager == IntPtr.Zero) throw new IOException("Windowsサービス一覧を確認できません");
        try
        {
            var service = OpenService(manager, CoolingServiceProtocol.ServiceName, 4);
            if (service != IntPtr.Zero) { CloseServiceHandle(service); return true; }
            if (Marshal.GetLastWin32Error() == 1060) return false;
            throw new IOException("FlowDeckサービスの登録状態を確認できません");
        }
        finally { CloseServiceHandle(manager); }
    }
    // Reading service status needs no elevation. No install/start operation is performed here.
    public static int ProcessId()
    {
        var manager = OpenSCManager(null, null, 1); if (manager == IntPtr.Zero) return 0;
        try
        {
            var service = OpenService(manager, CoolingServiceProtocol.ServiceName, 4); if (service == IntPtr.Zero) return 0;
            try { return QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _) && status.CurrentState == 4 ? status.ProcessId : 0; }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    {
        public int ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, int access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, int access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatus status, int size, out int needed);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
}
