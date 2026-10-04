using System.Security.Cryptography;
using System.Text.Json;

namespace FlowDeck.Core;

public sealed record DeviceStateQuery(string? TransferId = null, int Offset = 0);
public sealed record DeviceStateChunk(string TransferId, int TotalBytes, string Sha256, int Offset, byte[] Data);

public static class DeviceStateTransfer
{
    public const string Feature = "state-snapshot-chunks-v2";
    public const int MaximumBytes = 16 * 1024 * 1024, ChunkBytes = 192 * 1024, LifetimeSeconds = 10;
    public static bool Valid(DeviceStateQuery? query) => query is { Offset: >= 0 and < MaximumBytes } &&
        query.Offset % ChunkBytes == 0 && (query.TransferId is null ? query.Offset == 0 : DeviceServiceProtocol.Id(query.TransferId));
    public static bool Valid(DeviceStateChunk? chunk) => chunk is not null && DeviceServiceProtocol.Id(chunk.TransferId) &&
        chunk.TotalBytes is > 0 and <= MaximumBytes && DeviceConfigurationProtocol.Digest(chunk.Sha256) &&
        chunk.Offset >= 0 && chunk.Offset < chunk.TotalBytes && chunk.Offset % ChunkBytes == 0 &&
        chunk.Data is not null && chunk.Data.Length == Math.Min(ChunkBytes, chunk.TotalBytes - chunk.Offset);
    public static byte[] Encode(DeviceServiceReply reply, int maximum = MaximumBytes)
    {
        if (maximum is < 1 or > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximum));
        using var stream = new SnapshotStream(maximum);
        JsonSerializer.Serialize(stream, reply, Contract.Json); return stream.ToArray();
    }
    private sealed class SnapshotStream(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        private void Check(int count)
        { if (Position + count > maximum) throw new IOException("機器一覧・測定値が通信のサイズ上限を超えました"); }
    }
}

/// <summary>One immutable service snapshot; no partial state is published to consumers.</summary>
public sealed class DeviceStateAccumulator(DeviceServiceHello hello, string requestId) : IDisposable
{
    private readonly MemoryStream bytes = new();
    private DeviceStateChunk? first;
    public string? TransferId => first?.TransferId;
    public int Offset => checked((int)bytes.Length);
    public bool IsComplete => first is not null && Offset == first.TotalBytes;
    public void Append(DeviceStateChunk chunk)
    {
        if (IsComplete || !DeviceStateTransfer.Valid(chunk) || chunk.Offset != Offset || first is not null &&
            (chunk.TransferId != first.TransferId || chunk.TotalBytes != first.TotalBytes || chunk.Sha256 != first.Sha256))
            throw new IOException("機器一覧の分割データが一致しません。再取得してください");
        first ??= chunk;
        bytes.Write(chunk.Data);
    }
    public DeviceServiceState Finish()
    {
        if (!IsComplete) throw new IOException("機器一覧の取得が完了していません");
        var data = bytes.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(data)) != first!.Sha256)
            throw new IOException("機器一覧の内容が一致しません。再取得してください");
        var reply = DeviceServiceProtocol.ReadStateSnapshot(data, hello);
        if (reply.Id != requestId) throw new IOException("別の要求の機器一覧を破棄しました");
        return reply.State!;
    }
    public void Dispose() => bytes.Dispose();
}
