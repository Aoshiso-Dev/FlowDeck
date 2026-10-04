using System.Buffers.Binary;
using System.Text.Json;

namespace FlowDeck.Infrastructure;

internal enum AsrockAuthorizationPhase { BeforeOpen, BeforeSerial, BeforeWrite, BeforeRead, AfterRead }
internal sealed record AsrockAuthorizationOrigin(bool Sdk, uint Protocol, uint Command, uint Controller, ushort ServerPort, ushort PeerPort);
internal sealed record AsrockAuthorizationRequest(ulong Sequence, AsrockAuthorizationPhase Phase, string Path,
    AsrockAuthorizationOrigin Origin, AsrockOpenRgbIoReceipt Io);

// Separate from completed operation receipts: these are proposals and observations inside an
// in-progress exchange. Decoding a valid request never grants hardware permission.
internal static class AsrockOpenRgbAuthorizationProtocol
{
    public const int MaximumRequestBytes = 16384;
    public const int MaximumReplyBytes = 128;
    public const string PipePrefix = "FlowDeck.OpenRgb.Authorization.";
    private static IOException Invalid() => new("OpenRGBの通信認可要求が不正です");
    private static void Fields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var remaining = fields.ToHashSet(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject()) if (!remaining.Remove(field.Name)) throw Invalid();
        if (remaining.Count != 0) throw Invalid();
    }
    private static byte[] Bytes(JsonElement value, int length)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != length) throw Invalid();
        return value.EnumerateArray().Select(e => e.GetByte()).ToArray();
    }
    public static AsrockAuthorizationRequest Read(byte[] data)
    {
        try
        {
            if (data.Length is < 2 or > MaximumRequestBytes) throw Invalid();
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 5 }); var value = document.RootElement;
            Fields(value, "version", "sequence", "phase", "path", "origin", "io");
            var sequence = value.GetProperty("sequence").GetUInt64(); var phase = (AsrockAuthorizationPhase)value.GetProperty("phase").GetInt32();
            var path = value.GetProperty("path").GetString();
            if (value.GetProperty("version").GetInt32() != 1 || sequence is 0 or ulong.MaxValue ||
                phase is < AsrockAuthorizationPhase.BeforeOpen or > AsrockAuthorizationPhase.AfterRead ||
                path is null || path.Length is < 1 or > 2048 || path.Any(c => c is < '!' or > '~')) throw Invalid();
            var origin = value.GetProperty("origin"); Fields(origin, "sdk", "protocol", "command", "controller", "serverPort", "peerPort");
            var source = new AsrockAuthorizationOrigin(origin.GetProperty("sdk").GetBoolean(), origin.GetProperty("protocol").GetUInt32(),
                origin.GetProperty("command").GetUInt32(), origin.GetProperty("controller").GetUInt32(),
                origin.GetProperty("serverPort").GetUInt16(), origin.GetProperty("peerPort").GetUInt16());
            if (source.Sdk ? source.Protocol != 6 || source.Command is not (1050 or 1051 or 1052 or 1101) || source.ServerPort < 1024 || source.PeerPort == 0 :
                source.Protocol != 0 || source.Command != 0 || source.Controller != 0 || source.ServerPort != 0 || source.PeerPort != 0) throw Invalid();
            var io = value.GetProperty("io"); Fields(io, "attempted", "completed", "stage", "faulted", "writeResult", "readResult", "request", "response");
            var state = new AsrockOpenRgbIoReceipt(io.GetProperty("attempted").GetUInt64(), io.GetProperty("completed").GetUInt64(),
                io.GetProperty("stage").GetInt32(), io.GetProperty("faulted").GetBoolean(), io.GetProperty("writeResult").GetInt32(), io.GetProperty("readResult").GetInt32(),
                Bytes(io.GetProperty("request"), 65), Bytes(io.GetProperty("response"), 64));
            Validate(phase, state);
            return new(sequence, phase, path, source, state);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw new IOException("OpenRGBの通信認可要求を解析できません", e); }
    }
    internal static void Validate(AsrockAuthorizationPhase phase, AsrockOpenRgbIoReceipt io)
    {
        if (phase is < AsrockAuthorizationPhase.BeforeOpen or > AsrockAuthorizationPhase.AfterRead ||
            io.Request is not { Length: 65 } || io.Response is not { Length: 64 } || io.Faulted || io.Completed > io.Attempted) throw Invalid();
        if (phase == AsrockAuthorizationPhase.BeforeOpen)
        {
            if (io.Attempted != 0 || io.Stage != 0 || io.WriteResult != -2 || io.ReadResult != -2 || io.Request.Any(b => b != 0) || io.Response.Any(b => b != 0)) throw Invalid();
            return;
        }
        if (io.Attempted == 0 || io.Request[0] != 0) throw Invalid();
        var command = io.Request[1]; var selector = io.Request[3];
        if (!(command == 0x14 && selector is >= 1 and <= 3 || command == 0x11 && selector <= 7 || command == 0x15 && selector == 3 ||
            command == 0x10 && (selector <= 7 && io.Request[4] <= 15 || selector == 255 && io.Request[4] is 0xe3 or 0xe4))) throw Invalid();
        if (phase == AsrockAuthorizationPhase.BeforeSerial)
        {
            if (io.Attempted != io.Completed || io.Stage != 3 || io.WriteResult != 65 || io.ReadResult != 64 ||
                (command is 0x14 or 0x11) && io.Response[3] != selector) throw Invalid();
            return;
        }
        if (io.Attempted - io.Completed != 1) throw Invalid();
        if (phase == AsrockAuthorizationPhase.BeforeWrite)
        {
            if (io.Stage != 1 || io.WriteResult != -2 || io.ReadResult != -2 || io.Response.Any(b => b != 0)) throw Invalid();
        }
        else if (io.Stage != 2 || io.WriteResult != 65 || (phase == AsrockAuthorizationPhase.BeforeRead ?
            io.ReadResult != -2 || io.Response.Any(b => b != 0) : io.ReadResult != 64 || (command is 0x14 or 0x11) && io.Response[3] != selector)) throw Invalid();
    }
    public static byte[] Reply(ulong sequence, bool allow) => JsonSerializer.SerializeToUtf8Bytes(new { version = 1, sequence, allow });
    public static async Task WriteAsync(Stream stream, byte[] data, int maximum, CancellationToken ct)
    {
        if (data.Length is < 2 || data.Length > maximum) throw Invalid();
        var length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, data.Length);
        await stream.WriteAsync(length, ct).ConfigureAwait(false); await stream.WriteAsync(data, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
    public static int Length(byte[] header, int maximum)
    {
        if (header.Length != 4) throw Invalid(); var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        return length is >= 2 && length <= maximum ? length : throw Invalid();
    }
}
