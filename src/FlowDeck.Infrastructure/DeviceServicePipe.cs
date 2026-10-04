using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using FlowDeck.Core;
using Microsoft.Win32.SafeHandles;

namespace FlowDeck.Infrastructure;

/// <summary>Major-version-separated IPC. Authentication is taken from the Windows pipe token, not JSON.</summary>
public sealed class DeviceServicePipeServer(DeviceControlRuntime runtime, string[] allowedUsers, IEventLog log,
    string pipeName = DeviceServiceProtocol.PipeName, Func<bool>? allowsClients = null)
{
    public async Task RunAsync(CancellationToken ct, Action? listening = null)
    {
        var security = CoolingServicePipeServer.Security(allowedUsers);
        while (!ct.IsCancellationRequested)
        {
            using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance | PipeOptions.WriteThrough, 8192, 8192, security);
            listening?.Invoke();
            while (allowsClients?.Invoke() == false) await Task.Delay(200, ct).ConfigureAwait(false);
            string? session = null; using var connected = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var writer = new SemaphoreSlim(1); var pending = new List<Task>();
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                if (allowsClients?.Invoke() == false) throw new IOException("サービス更新の確定を待っています");
                string? user = null;
                pipe.RunAsClient(() =>
                {
                    using var identity = WindowsIdentity.GetCurrent(true) ?? throw new UnauthorizedAccessException("Windowsユーザーを確認できません");
                    if (identity.Groups?.Contains(new SecurityIdentifier(WellKnownSidType.NetworkSid, null)) == true)
                        throw new UnauthorizedAccessException("ネットワーク接続は許可されていません");
                    user = identity.User?.Value;
                });
                if (user is null || !allowedUsers.Contains(user, StringComparer.Ordinal)) throw new UnauthorizedAccessException("許可されていないWindowsユーザーです");
                var hello = runtime.OpenSession(user); session = hello.Session;
                await CoolingPipeFrames.WriteAsync(pipe, hello, connected.Token).ConfigureAwait(false);
                while (pipe.IsConnected && !connected.IsCancellationRequested)
                {
                    var request = DeviceServiceProtocol.Read(await CoolingPipeFrames.ReadAsync(pipe, connected.Token).ConfigureAwait(false));
                    if (request.Session != session) throw new UnauthorizedAccessException("接続に属さないセッションです");
                    pending.RemoveAll(t => t.IsCompleted);
                    if (pending.Count >= 64) throw new IOException("待機要求が上限に達しました");
                    // Reading continues while a driver is awaiting hardware, so renewals can keep the owner alive.
                    pending.Add(RespondAsync(request));
                }
                async Task RespondAsync(DeviceServiceRequest request)
                {
                    try
                    {
                        var response = await runtime.ExecuteAsync(request).ConfigureAwait(false);
                        await writer.WaitAsync(connected.Token).ConfigureAwait(false);
                        try { await CoolingPipeFrames.WriteAsync(pipe, response, connected.Token).ConfigureAwait(false); }
                        finally { writer.Release(); }
                    }
                    catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
                    { log.Write("devicePipeReply", new { error = e.Message }); connected.Cancel(); }
                }
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException or ObjectDisposedException)
            { log.Write("devicePipe", new { error = e.Message, stopping = ct.IsCancellationRequested }); }
            finally
            {
                connected.Cancel();
                if (session is not null) await runtime.DisconnectAsync(session).ConfigureAwait(false);
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>Client with an authenticated server PID, bounded pending calls, and independent lease renewals.</summary>
public sealed class DeviceServicePipeClient : IAsyncDisposable
{
    private readonly string pipeName;
    private readonly Func<int> expectedServerPid;
    private readonly bool requireDemo;
    private readonly SemaphoreSlim writer = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationTokenSource stopRenewals = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DeviceServiceReply>> pending = new();
    private NamedPipeClientStream? pipe;
    private Task reader = Task.CompletedTask, heartbeat = Task.CompletedTask;
    private long sequence;
    private bool disposed;
    public DeviceServiceHello? Hello { get; private set; }
    public bool IsDemo => requireDemo || Hello?.Demo == true;
    public bool ServerAuthenticated { get; private set; }
    public bool Connected => !disposed && pipe?.IsConnected == true && !lifetime.IsCancellationRequested;
    public DeviceServicePipeClient() : this(WindowsCoolingServiceIdentity.ProcessId) { }
    // Preserve the authenticated PID resolver and demo restriction; never reuse a consumed session.
    internal DeviceServicePipeClient NextConnection() => new(expectedServerPid, pipeName, requireDemo);
    public DeviceServicePipeClient(Func<int> expectedServerPid, string pipeName = DeviceServiceProtocol.PipeName, bool requireDemo = false)
    { this.expectedServerPid = expectedServerPid; this.pipeName = pipeName; this.requireDemo = requireDemo; }
    public static DeviceServicePipeClient ForDemo(string pipeName, int processId)
    {
        if (!DeviceWorkspaceOptions.DemoPipe(pipeName))
            throw new ArgumentException("専用のデモパイプが必要です");
        return new(() => processId, pipeName, true);
    }
    public async Task ConnectAsync(bool renew = true, CancellationToken ct = default)
    {
        if (pipe is not null || disposed) throw new InvalidOperationException("接続済みまたは終了済みです");
        var expected = expectedServerPid(); if (expected < 1) throw new IOException("サービスの実行プロセスを確認できません");
        var connection = new NamedPipeClientStream(".", pipeName, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough, TokenImpersonationLevel.Identification, HandleInheritability.None);
        try
        {
            await connection.ConnectAsync(3000, ct).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(connection.SafePipeHandle, out var actual) || actual != expected || expectedServerPid() != expected)
                throw new UnauthorizedAccessException("確認したサービスと接続先のプロセスが一致しません");
            ServerAuthenticated = true;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(3));
            var hello = DeviceServiceProtocol.ReadHello(await CoolingPipeFrames.ReadAsync(connection, deadline.Token).ConfigureAwait(false));
            if (requireDemo && !hello.Demo) throw new UnauthorizedAccessException("デモ用クライアントは実機へ接続できません");
            Hello = hello; pipe = connection; reader = ReadAsync(); if (renew) heartbeat = RenewAsync();
        }
        catch { connection.Dispose(); throw; }
    }
    public DeviceServiceRequest Request(DeviceOperation operation, string source = "PC", long topology = 0,
        DeviceTargetVersion[]? targets = null, int? duty = null, CoolingTemperatureProfile? profile = null, LightingProgram? lighting = null, string? resultId = null,
        DeviceConfigurationRequest? configuration = null, LightingLibraryRequest? lightingLibrary = null, LightingPresetRequest? lightingPreset = null,
        LightingAdjustmentRequest? lightingAdjustment = null)
    {
        var hello = Hello ?? throw new IOException("サービスへ接続されていません");
        return new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), Interlocked.Increment(ref sequence), source,
            operation, topology, targets, duty, profile, lighting, resultId, configuration, lightingLibrary, lightingPreset, lightingAdjustment);
    }
    public async Task<DeviceServiceReply> SendAsync(DeviceServiceRequest request, CancellationToken ct = default)
    {
        request = DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(request, Contract.Json));
        var connection = pipe; var hello = Hello;
        if (connection is null || hello is null || lifetime.IsCancellationRequested || request.Epoch != hello.Epoch || request.Session != hello.Session)
            throw new IOException("サービスへの接続が無効です");
        if (pending.Count >= 64) throw new IOException("サービスの待機要求が上限に達しました");
        var completion = new TaskCompletionSource<DeviceServiceReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(request.Id, completion)) throw new IOException("この要求はすでに送信中です");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (request.Operation == DeviceOperation.ReleaseSession)
            {
                // A release consumes the server session before physical restoration completes.
                // Drain any renewal already sent and stop future ticks before sending the release;
                // otherwise its expected rejection could close the pipe ahead of the exit reply.
                stopRenewals.Cancel();
                await heartbeat.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            await writer.WaitAsync(deadline.Token).ConfigureAwait(false);
            try { await CoolingPipeFrames.WriteAsync(connection, request, deadline.Token).ConfigureAwait(false); }
            finally { writer.Release(); }
            return await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch { lifetime.Cancel(); connection.Dispose(); throw; }
        finally { pending.TryRemove(request.Id, out _); }
    }
    private async Task ReadAsync()
    {
        Exception failure = new IOException("サービスの接続が終了しました");
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var reply = DeviceServiceProtocol.ReadReply(await CoolingPipeFrames.ReadAsync(pipe!, lifetime.Token).ConfigureAwait(false), Hello!);
                if (!pending.TryGetValue(reply.Id, out var completion)) throw new IOException("要求と対応しないサービス応答です");
                completion.TrySetResult(reply);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or JsonException or ObjectDisposedException) { failure = e; }
        finally { lifetime.Cancel(); pipe?.Dispose(); foreach (var c in pending.Values) c.TrySetException(failure); }
    }
    private async Task RenewAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        using var delay = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, stopRenewals.Token);
        try
        {
            while (await timer.WaitForNextTickAsync(delay.Token).ConfigureAwait(false))
            {
                if (stopRenewals.IsCancellationRequested) break;
                // Stopping the timer must not cancel an already sent renewal or its response reader.
                if (!(await SendAsync(Request(DeviceOperation.Renew), lifetime.Token).ConfigureAwait(false)).Success) throw new IOException("操作権の延長が拒否されました");
            }
        }
        catch (OperationCanceledException) when (stopRenewals.IsCancellationRequested && !lifetime.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { lifetime.Cancel(); pipe?.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; lifetime.Cancel(); pipe?.Dispose();
        await Task.WhenAll(reader, heartbeat).ConfigureAwait(false); stopRenewals.Dispose(); lifetime.Dispose();
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out int serverProcessId);
}
