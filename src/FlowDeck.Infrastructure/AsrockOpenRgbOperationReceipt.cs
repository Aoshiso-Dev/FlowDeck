using System.Text.Json;

namespace FlowDeck.Infrastructure;

internal enum AsrockOpenRgbOperationStage { Idle, Running, Completed, Failed }
internal sealed record AsrockOpenRgbIoReceipt(ulong Attempted, ulong Completed, int Stage, bool Faulted,
    int WriteResult, int ReadResult, byte[] Request, byte[] Response)
{
    public AsrockOpenRgbIoReceipt Copy() => this with { Request = Request.ToArray(), Response = Response.ToArray() };
    public bool Same(AsrockOpenRgbIoReceipt other) => Attempted == other.Attempted && Completed == other.Completed &&
        Stage == other.Stage && Faulted == other.Faulted && WriteResult == other.WriteResult && ReadResult == other.ReadResult &&
        Request.SequenceEqual(other.Request) && Response.SequenceEqual(other.Response);
}

// This describes execution and HID replies in the pinned helper, never emitted light.
internal sealed record AsrockOpenRgbOperationReceipt(ulong Sequence, uint Command, AsrockOpenRgbOperationStage Stage,
    AsrockOpenRgbIoReceipt Before, AsrockOpenRgbIoReceipt After)
{
    public const uint QueryCommand = 0x46440001;
    public AsrockOpenRgbOperationReceipt Copy() => this with { Before = Before.Copy(), After = After.Copy() };
    public bool Same(AsrockOpenRgbOperationReceipt other) => Sequence == other.Sequence && Command == other.Command && Stage == other.Stage &&
        Before.Same(other.Before) && After.Same(other.After);
    private static IOException Invalid() => new("OpenRGBのASRock操作記録が不正です");
    private static void Fields(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid();
        var remaining = names.ToHashSet(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!remaining.Remove(property.Name)) throw Invalid();
        if (remaining.Count != 0) throw Invalid();
    }
    private static byte[] Bytes(JsonElement value, int count)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != count) throw Invalid();
        return value.EnumerateArray().Select(v => v.GetByte()).ToArray();
    }
    private static AsrockOpenRgbIoReceipt Io(JsonElement element)
    {
        Fields(element, "attempted", "completed", "stage", "faulted", "writeResult", "readResult", "request", "response");
        var value = new AsrockOpenRgbIoReceipt(element.GetProperty("attempted").GetUInt64(), element.GetProperty("completed").GetUInt64(),
            element.GetProperty("stage").GetInt32(), element.GetProperty("faulted").GetBoolean(),
            element.GetProperty("writeResult").GetInt32(), element.GetProperty("readResult").GetInt32(),
            Bytes(element.GetProperty("request"), 65), Bytes(element.GetProperty("response"), 64));
        if (value.Completed > value.Attempted || value.Attempted - value.Completed > 1 || value.Stage is < 0 or > 3) throw Invalid();
        if (value.Stage == 0)
        {
            if (value.Attempted != 0 || value.Faulted || value.WriteResult != -2 || value.ReadResult != -2 ||
                value.Request.Any(b => b != 0) || value.Response.Any(b => b != 0)) throw Invalid();
            return value;
        }
        if (value.Attempted == 0 || value.Request[0] != 0) throw Invalid();
        var command = value.Request[1]; var selector = value.Request[3];
        if (!(command == 0x14 && selector is >= 1 and <= 3 || command == 0x11 && selector <= 7 || command == 0x15 && selector == 3 ||
            command == 0x10 && (selector <= 7 && value.Request[4] <= 15 || selector == 255 && value.Request[4] is 0xe3 or 0xe4))) throw Invalid();
        if (value.Stage == 1 && (!value.Faulted || value.Completed == value.Attempted || value.WriteResult == 65 || value.ReadResult != -2 || value.Response.Any(b => b != 0))) throw Invalid();
        if (value.Stage == 2 && (!value.Faulted || value.Completed == value.Attempted || value.WriteResult != 65)) throw Invalid();
        if (value.Stage == 3 && (value.Completed != value.Attempted || value.WriteResult != 65 || value.ReadResult != 64 ||
            value.Faulted && value.Attempted != ulong.MaxValue || (command is 0x14 or 0x11) && value.Response[3] != selector)) throw Invalid();
        return value;
    }
    public static AsrockOpenRgbOperationReceipt Parse(byte[] data)
    {
        try
        {
            if (data.Length is < 2 or > 8192) throw Invalid();
            using var document = JsonDocument.Parse(OpenRgbSdkProtocol.NullText(data, 8192), new JsonDocumentOptions { MaxDepth = 6 });
            var root = document.RootElement;
            Fields(root, "version", "sequence", "command", "stage", "before", "after");
            if (root.GetProperty("version").GetInt32() != 1) throw Invalid();
            var value = new AsrockOpenRgbOperationReceipt(root.GetProperty("sequence").GetUInt64(), root.GetProperty("command").GetUInt32(),
                (AsrockOpenRgbOperationStage)root.GetProperty("stage").GetInt32(), Io(root.GetProperty("before")), Io(root.GetProperty("after")));
            if (value.Stage is < AsrockOpenRgbOperationStage.Idle or > AsrockOpenRgbOperationStage.Failed) throw Invalid();
            if (value.Stage == AsrockOpenRgbOperationStage.Idle)
            {
                if (value.Sequence != 0 || value.Command != 0 || !value.Before.Same(value.After) || value.Before.Faulted) throw Invalid();
            }
            else if (value.Sequence == 0 || value.Command is not (1000 or 1050 or 1051 or 1052 or 1101)) throw Invalid();
            if (value.Stage == AsrockOpenRgbOperationStage.Completed &&
                (value.Command == 1000 || value.Before.Faulted || value.After.Faulted || value.Before.Completed != value.Before.Attempted ||
                 value.After.Completed != value.After.Attempted || value.After.Attempted < value.Before.Attempted)) throw Invalid();
            return value;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw new IOException("OpenRGBのASRock操作記録を解析できません", error); }
    }
    public static void CheckResult(AsrockOpenRgbOperationReceipt previous, AsrockOpenRgbOperationReceipt current, uint command, uint ack)
    {
        if (ack is > 5 || command is not (1050 or 1051 or 1052 or 1101)) throw Invalid();
        // A rejected request may never enter the device operation. Its previous receipt must remain intact.
        if (ack != 0 && current.Same(previous)) return;
        if (previous.Sequence == ulong.MaxValue || current.Sequence != previous.Sequence + 1 || current.Command != command ||
            !current.Before.Same(previous.After) || current.Stage != (ack == 0 ? AsrockOpenRgbOperationStage.Completed : AsrockOpenRgbOperationStage.Failed))
            throw new IOException("OpenRGBのASRock操作と通信記録が対応しません");
    }
}
