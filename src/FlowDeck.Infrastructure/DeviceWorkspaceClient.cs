using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceWorkspaceSnapshot(DeviceServiceState State, ServiceConfigurationSnapshot Configuration,
    double ElapsedSeconds, bool Connected, string? Error, bool ExitAttempted = false, string? Session = null);

/// <summary>GUI/M5 boundary. Serializes state reads with commands; the pipe's renewal runs independently.</summary>
public sealed partial class DeviceWorkspaceClient(DeviceServicePipeClient client, TimeProvider? time = null) : IAsyncDisposable
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly SemaphoreSlim operations = new(1);
    private readonly object gate = new();
    private DeviceServiceState? state;
    private ServiceConfigurationSnapshot? configuration;
    private long requestedAt;
    private string? error;
    private bool released, disposed, exitAttempted;
    public bool IsDemo => client.IsDemo;
    // Command predicates must not clone the complete inventory for every visible button.
    public bool Connected
    {
        get { lock (gate) return state is not null && configuration is not null && !disposed && !exitAttempted && !released && client.Connected && error is null; }
    }
    public bool HasFreshState
    {
        get { lock (gate) return Connected && clock.GetElapsedTime(requestedAt).TotalSeconds is >= 0 and < 3; }
    }
    public bool NeedsReconnect => !disposed && !released && !exitAttempted && !client.Connected;
    public DeviceWorkspaceSnapshot? Snapshot
    {
        get
        {
            lock (gate) return state is null || configuration is null ? null : new(Contract.Copy(state), Contract.Copy(configuration),
                clock.GetElapsedTime(requestedAt).TotalSeconds, !disposed && !exitAttempted && !released && client.Connected && error is null, error, exitAttempted, client.Hello?.Session);
        }
    }
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try { await ConnectAndReadAsync(ct).ConfigureAwait(false); }
        catch (Exception e) { RecordError(e); throw; }
        finally { operations.Release(); }
    }
    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (disposed || released || exitAttempted) throw new IOException("終了処理を開始した接続は再開できません");
            if (client.Connected) return;
            var next = client.NextConnection(); await client.DisposeAsync().ConfigureAwait(false); client = next;
            await ConnectAndReadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) { RecordError(e); throw; }
        finally { operations.Release(); }
    }
    private async Task ConnectAndReadAsync(CancellationToken ct)
    {
        if (disposed || released || exitAttempted) throw new IOException("サービスの接続は終了済みです");
        await client.ConnectAsync(ct: ct).ConfigureAwait(false);
        try {
        if (!SupportsWorkspace(client.Hello))
            throw new IOException("汎用サービスの更新が必要です");
        var next = await client.ReadConfigurationAsync(ct).ConfigureAwait(false);
        lock (gate) configuration = next;
        await PrepareConnectionLightingCoreAsync(ct).ConfigureAwait(false);
        await ReadStateAsync(ct).ConfigureAwait(false);
        }
        catch { await client.DisposeAsync().ConfigureAwait(false); throw; }
    }
    internal static bool SupportsWorkspace(DeviceServiceHello? hello) => hello?.Features is { } features &&
        new[] { "device-catalog-v2", "endpoint-telemetry-v2", "cooling-policy-v2", "configuration-transfer-v2" }.All(features.Contains);
    public async Task RefreshAsync(bool readConfiguration = false, CancellationToken ct = default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            if (readConfiguration)
            {
                var next = await client.ReadConfigurationAsync(ct).ConfigureAwait(false);
                lock (gate) configuration = next;
            }
            await PrepareConnectionLightingCoreAsync(ct).ConfigureAwait(false);
            await ReadStateAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) { RecordError(e); throw; }
        finally { operations.Release(); }
    }
    private void EnsureOpen()
    {
        if (disposed || released || exitAttempted || !client.Connected) throw new IOException("サービスへ接続されていません");
    }
    private void RecordError(Exception e) { lock (gate) error = e.Message; }
    private async Task ReadStateAsync(CancellationToken ct)
    {
        var start = clock.GetTimestamp();
        if (client.Hello?.Features.Contains(DeviceStateTransfer.Feature) == true)
        {
            var next = await ReadStateSnapshotAsync(ct).ConfigureAwait(false);
            lock (gate) { state = next; requestedAt = start; error = null; }
            return;
        }
        var reply = await client.SendAsync(client.Request(DeviceOperation.State), ct).ConfigureAwait(false);
        if (!reply.Success || reply.State is null) throw new IOException(reply.Error ?? "サービスの状態を取得できません");
        lock (gate) { state = reply.State; requestedAt = start; error = null; }
    }
    private async Task<DeviceServiceState> ReadStateSnapshotAsync(CancellationToken ct)
    {
        var request = client.Request(DeviceOperation.ReadStateSnapshot) with { StateQuery = new() };
        using var snapshot = new DeviceStateAccumulator(client.Hello!, request.Id);
        do
        {
            ct.ThrowIfCancellationRequested();
            // Read-only cancellation between chunks preserves the shared cooling-control connection.
            var reply = await client.SendAsync(request).ConfigureAwait(false);
            if (!reply.Success || reply.StateChunk is not { } chunk) throw new IOException(reply.Error ?? "機器一覧の分割データを取得できません");
            snapshot.Append(chunk);
            if (!snapshot.IsComplete) request = client.Request(DeviceOperation.ReadStateSnapshot) with { StateQuery = new(snapshot.TransferId, snapshot.Offset) };
        } while (!snapshot.IsComplete);
        ct.ThrowIfCancellationRequested(); return snapshot.Finish();
    }
    public async Task<DeviceOperationResult> ApplyCoolingAsync(DeviceCoolingEdit edit, CancellationToken ct = default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            if (configuration?.Generation != edit.Version.ConfigurationGeneration || client.Hello?.Epoch != edit.Version.Epoch)
                throw new InvalidOperationException("接続または設定が変更されました。現在の設定を読み込んでください");
            if (edit.Operation is not (DeviceOperation.SetDuty or DeviceOperation.SetAutomatic or DeviceOperation.ReleaseCooling or DeviceOperation.ResumeCooling))
                throw new ArgumentException("冷却の操作が不正です");
            var reply = await client.SendAsync(client.Request(edit.Operation, topology: edit.Version.Topology,
                targets: [edit.Version.Target], duty: edit.Duty, profile: edit.Profile), ct).ConfigureAwait(false);
            var result = reply.Result ?? throw new IOException(reply.Error ?? "操作結果を取得できません");
            if (result.Targets.Any(t => t.Persistence is not null))
            {
                var next = await client.ReadConfigurationAsync(ct).ConfigureAwait(false);
                lock (gate) configuration = next;
            }
            await ReadStateAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch (Exception e) { if (!client.Connected) RecordError(e); throw; }
        finally { operations.Release(); }
    }
    public async Task<DeviceServiceReply> ReleaseAsync(CancellationToken ct = default)
    {
        // Finish the request already in flight, then release; do not start the remaining startup targets.
        startupStopRequested = true;
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (disposed || released) throw new IOException("サービスの接続は終了済みです");
            // ReleaseSession consumes ownership even when a physical restore fails. Only an explicit
            // exit retry creates another authenticated session, solely to retry outstanding releases.
            if (exitAttempted || !client.Connected)
            {
                var next = client.NextConnection(); await client.DisposeAsync().ConfigureAwait(false); client = next;
                await client.ConnectAsync(renew: false, ct: ct).ConfigureAwait(false);
            }
            lock (gate) exitAttempted = true;
            var reply = await client.SendAsync(client.Request(DeviceOperation.ReleaseSession), ct).ConfigureAwait(false);
            lock (gate) { released = reply.Success; error = reply.Success ? null : reply.Error; }
            return reply;
        }
        catch (Exception e) { RecordError(e); throw; }
        finally { operations.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        startupStopRequested = true;
        await operations.WaitAsync().ConfigureAwait(false);
        try { lock (gate) disposed = true; await client.DisposeAsync().ConfigureAwait(false); }
        finally { operations.Release(); }
    }
}
