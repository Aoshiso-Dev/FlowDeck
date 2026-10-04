using System.Security.Cryptography;

namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private sealed record StateTransfer(string Id, string Owner, long Started, byte[] Data, string Hash);
    private StateTransfer? stateTransfer;
    private bool stateCapturing;
    private Task stateSnapshotWork = Task.CompletedTask;
    private DeviceServiceReply LegacyStateReply(DeviceServiceRequest request)
    {
        var reply = Reply(request, state: State());
        try { _ = DeviceStateTransfer.Encode(reply, DeviceServiceProtocol.MaximumFrameBytes); return reply; }
        catch (IOException) { return Reject(request, "機器一覧が1回の通信上限を超えました。分割取得に対応するFlowDeck GUIへ更新してください"); }
    }
    private DeviceServiceReply StateChunkReply(DeviceServiceRequest request, StateTransfer transfer, int offset) => Reply(request) with {
        StateChunk = new(transfer.Id, transfer.Data.Length, transfer.Hash, offset,
            transfer.Data.AsSpan(offset, Math.Min(DeviceStateTransfer.ChunkBytes, transfer.Data.Length - offset)).ToArray())
    };
    private void QueueStateSnapshotCore(DeviceServiceRequest request, TaskCompletionSource<DeviceServiceReply> completion)
    {
        var query = request.StateQuery!;
        if (query.TransferId is not null)
        {
            var transfer = stateTransfer;
            if (transfer is null || transfer.Owner != request.Session || transfer.Id != query.TransferId || query.Offset >= transfer.Data.Length ||
                clock.GetElapsedTime(transfer.Started).TotalSeconds is < 0 or >= DeviceStateTransfer.LifetimeSeconds)
                completion.SetResult(Reject(request, "機器一覧の取得期限または接続が変わりました。再取得してください"));
            else completion.SetResult(StateChunkReply(request, transfer, query.Offset));
            return;
        }
        if (stateCapturing) { completion.SetResult(Reject(request, "機器一覧を作成中です")); return; }
        stateCapturing = true; stateTransfer = null; pendingCount++;
        var token = sessionLifetime!.Token; var startedAt = clock.GetTimestamp();
        stateSnapshotWork = Task.Run(() =>
        {
            try
            {
                DeviceServiceReply captured;
                lock (gate) { token.ThrowIfCancellationRequested(); captured = Reply(request, state: State()); }
                // Serialization runs outside the runtime gate. Control and lease renewal remain independent.
                var bytes = DeviceStateTransfer.Encode(captured);
                var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (request.Session != session || stopping || releasing || clock.GetElapsedTime(startedAt).TotalSeconds is < 0 or >= DeviceStateTransfer.LifetimeSeconds)
                        throw new IOException("機器一覧の取得が期限切れになりました。再取得してください");
                    stateTransfer = new(Guid.NewGuid().ToString("N"), request.Session, startedAt, bytes, digest);
                    completion.TrySetResult(StateChunkReply(request, stateTransfer, 0));
                }
            }
            catch (Exception e)
            { lock (gate) completion.TrySetResult(Reject(request, e is IOException ? e.Message : "機器一覧を取得できませんでした。再取得してください")); }
            finally { lock (gate) { stateCapturing = false; pendingCount--; TrimRequests(); } }
        });
    }
}
