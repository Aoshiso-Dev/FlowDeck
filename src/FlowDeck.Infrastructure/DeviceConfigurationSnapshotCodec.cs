using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>Stored settings and live setup metadata share one immutable, digest-checked transfer.</summary>
public static class DeviceConfigurationSnapshotCodec
{
    public static void Validate(ServiceConfigurationSnapshot snapshot)
    {
        if (!DeviceServiceProtocol.Id(snapshot.Generation) || snapshot.TopologyRevision is not >= 0 ||
            snapshot.Candidates is null || !DeviceConfigurationProtocol.Valid(new DeviceConfigurationReply(Outcome.Confirmed, "", snapshot.Generation, Candidates: snapshot.Candidates)) ||
            !DeviceSetup.ValidCandidates(snapshot.Setup)) throw new IOException("設定候補の形式・世代が不正です");
        DeviceConfigurationCodec.Validate(snapshot.Configuration);
    }
    public static byte[] Serialize(ServiceConfigurationSnapshot snapshot, int maximum = DeviceConfigurationProtocol.MaximumSnapshotBytes)
    {
        if (maximum is < 1 or > DeviceConfigurationProtocol.MaximumSnapshotBytes) throw new ArgumentOutOfRangeException(nameof(maximum));
        Validate(snapshot);
        // Keep the upload/storage limit separate from the metadata download envelope.
        _ = DeviceConfigurationCodec.SerializePackage(snapshot.Configuration);
        using var stream = new SnapshotStream(maximum);
        JsonSerializer.Serialize(stream, snapshot, Contract.Json); return stream.ToArray();
    }
    public static ServiceConfigurationSnapshot Deserialize(byte[] bytes, string generation)
    {
        if (bytes.Length is < 2 or > DeviceConfigurationProtocol.MaximumSnapshotBytes) throw new IOException("設定と候補の取得サイズが不正です");
        try
        {
            using var document = JsonDocument.Parse(bytes); DeviceConfigurationCodec.Unique(document.RootElement);
            var snapshot = document.RootElement.Deserialize<ServiceConfigurationSnapshot>(Contract.Json) ?? throw new IOException("設定と候補が空です");
            Validate(snapshot);
            if (snapshot.Generation != generation) throw new IOException("設定と候補の取得世代が一致しません");
            _ = DeviceConfigurationCodec.SerializePackage(snapshot.Configuration);
            return snapshot;
        }
        catch (JsonException e) { throw new IOException("設定と候補の取得形式が不正です", e); }
    }
    private sealed class SnapshotStream(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        private void Check(int count)
        { if (Position + count > maximum) throw new IOException("設定と候補が取得サイズの上限を超えました"); }
    }
}
