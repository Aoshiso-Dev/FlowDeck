using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// One synchronous patch-4 SDK command, expanded into its exact volatile HID exchanges.
// Prefixes describe readable native registers; Direct pixels are deliberately not inferred.
internal sealed record AsrockOpenRgbNativeOperation(uint Command, AsrockRgbSnapshot Before,
    byte[][] Packets, AsrockRgbSnapshot[] Prefixes)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public AsrockRgbSnapshot After => Prefixes[^1];
    internal static bool Same(AsrockRgbSnapshot a, AsrockRgbSnapshot b) => SettingsTransfer.Hash(a) == SettingsTransfer.Hash(b);
    internal static byte[] ZonePacket(byte address, BoardLightSetting setting, byte order)
    {
        var normal = (order & (1 << address)) != 0;
        var packet = new byte[65]; packet[1] = 0x10; packet[3] = address; packet[4] = setting.Mode;
        packet[5] = (byte)(normal ? setting.Red : setting.Green); packet[6] = (byte)(normal ? setting.Green : setting.Red);
        packet[7] = (byte)setting.Blue; packet[8] = (byte)setting.Speed; packet[9] = 255;
        return packet;
    }
    internal static AsrockRgbSnapshot WithOrder(AsrockRgbSnapshot state, byte order) => state with
    {
        Order = order,
        Zones = state.Zones.Select(z => ((state.Order ^ order) & (1 << z.Address)) == 0 ? z :
            z with { Setting = z.Setting with { Red = z.Setting.Green, Green = z.Setting.Red } }).ToArray()
    };
    private static AsrockRgbSnapshot Apply(AsrockRgbSnapshot before, byte[] packet)
    {
        if (packet is not { Length: 65 }) throw new IOException("ASRock操作のNativeパケットが不正です");
        if (packet[1] == 0x15 && packet.AsSpan().SequenceEqual(AsrockDirectProtocol.ColorOrder(packet[4])))
            return WithOrder(before, packet[4]);
        if (packet[1] != 0x10) throw new IOException("ASRock操作に未対応のNative命令があります");
        if (packet[3] == 255 && packet[4] is 0xe3 or 0xe4)
        {
            if (before.Order != 0 || before.Zones.Any(z => z.Setting.Mode != 15) || packet[0] != 0 || packet[2] != 0 || packet[64] != 0x65 ||
                packet[4] == 0xe3 && (packet[5] != 0 || packet[6] != 0 || packet[7] != 255 || packet[8] != 0 || packet[63] != 0) ||
                packet[4] == 0xe4 && packet[62..64].Any(b => b != 0))
                throw new IOException("ASRock Direct操作の開始状態が不正です");
            return Contract.Copy(before);
        }
        var zone = before.Zones.SingleOrDefault(z => z.Address == packet[3]) ?? throw new IOException("ASRock操作のゾーンがありません");
        if (packet[4] > 15) throw new IOException("ASRock操作の効果が範囲外です");
        var normal = (before.Order & (1 << zone.Address)) != 0;
        var setting = new BoardLightSetting(packet[4], packet[normal ? 5 : 6], packet[normal ? 6 : 5], packet[7], packet[8]);
        if (!packet.AsSpan().SequenceEqual(ZonePacket(zone.Address, setting, before.Order))) throw new IOException("ASRock操作のパケットに余分な値があります");
        return before with { Zones = before.Zones.Select(z => z.Address == zone.Address ? z with { Setting = setting } : z).ToArray() };
    }
    internal static AsrockOpenRgbNativeOperation Create(uint command, AsrockRgbSnapshot before, byte[][] packets)
    {
        var state = Contract.Copy(before); var prefixes = new List<AsrockRgbSnapshot>();
        foreach (var packet in packets) { state = Apply(state, packet); prefixes.Add(Contract.Copy(state)); }
        var result = new AsrockOpenRgbNativeOperation(command, Contract.Copy(before), packets.Select(p => p.ToArray()).ToArray(), prefixes.ToArray());
        if (!result.Valid()) throw new IOException("ASRockの操作手順が固定版と一致しません");
        return result;
    }
    internal bool Valid()
    {
        try
        {
            if (Command is not (1050 or 1051 or 1101) || Packets is not { Length: > 0 and <= 128 } || Prefixes?.Length != Packets.Length) return false;
            var state = Before;
            for (var i = 0; i < Packets.Length; i++) { state = Apply(state, Packets[i]); if (!Same(state, Prefixes[i])) return false; }
            var addresses = Before.Zones.Select(z => z.Address).Order().ToArray();
            if (Command == 1101) return Packets.Length == addresses.Length + 1 && Packets[0][1] == 0x15 &&
                Packets.Skip(1).Select(p => p[3]).SequenceEqual(addresses) && Packets.Skip(1).All(p => p[1] == 0x10);
            if (Packets[0][3] == 255) return Packets.All(p => p[3] == 255) && Packets[0][4] == 0xe3 && Packets.Skip(1).All(p => p[4] == 0xe4);
            return Packets.All(p => p[1] == 0x10 && p[3] < 8) &&
                (Command == 1051 ? Packets.Length == 1 : Packets.Select(p => p[3]).SequenceEqual(addresses));
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or NullReferenceException or IndexOutOfRangeException) { return false; }
    }
    internal void CheckCompleted(AsrockOpenRgbExecution execution)
    {
        AsrockOpenRgbOperationReceipt.CheckResult(execution.Before, execution.After, Command, execution.Acknowledgement);
        var io = execution.After.After; var baseline = execution.Before.After;
        if (!Valid() || execution.Command != Command || !execution.Completed || io.Attempted < baseline.Attempted ||
            io.Attempted - baseline.Attempted != (ulong)Packets.Length || io.Completed != io.Attempted ||
            io.Faulted || io.Stage != 3 || !io.Request.AsSpan().SequenceEqual(Packets[^1]))
            throw new IOException("ASRockの操作結果と予定したNative通信が一致しません");
    }
}

internal sealed partial class AsrockOpenRgbNativePlan
{
    internal AsrockOpenRgbNativeOperation Prepare(AsrockRgbSnapshot before, OpenRgbSdkController controller, OpenRgbLightingStep step)
    {
        CheckCompletedState(controller);
        if (before.Identity != original.Identity || before.Available != original.Available || !before.Counts.SequenceEqual(original.Counts) ||
            !before.Zones.Select(z => z.Address).Order().SequenceEqual(original.Zones.Select(z => z.Address).Order()) ||
            before.Order != 0 && before.Order != AsrockOpenRgbDetectionRecovery.DetectionOrder(original))
            throw new IOException("ASRock操作前のNative構成が一致しません");
        var mutation = OpenRgbLightingPlan.Build(controller, OpenRgbLightingPlan.State(controller), [step]);
        var after = OpenRgbLightingPlan.WithState(controller, mutation.After); CheckCompletedState(after);
        var packets = new List<byte[]>(); uint command;
        if (step.ModeIndex is not null)
        {
            if (step.Zone is not null) throw new IOException("ASRockのゾーン別SDK効果には対応していません");
            command = 1101; var expected = PredictCompletedModeUpdate(after);
            packets.Add(AsrockDirectProtocol.ColorOrder(expected.Order));
            packets.AddRange(expected.Zones.OrderBy(z => z.Address).Select(z => AsrockOpenRgbNativeOperation.ZonePacket(z.Address, z.Setting, expected.Order)));
        }
        else
        {
            command = step.Zone is null ? 1050u : 1051u;
            if (step.Zone is { } zone) _ = Address(zone);
            if (after.ActiveMode == 15)
            {
                // Pinned WriteAllZones pads to the sum of hardware maxima, not configured LED counts.
                var bytes = new byte[checked(((int)after.Zones.Sum(z => (long)z.MaximumLeds) + 8) * 3)]; var offset = 0;
                foreach (var color in after.Colors) { bytes[offset++] = (byte)color.G; bytes[offset++] = (byte)color.R; bytes[offset++] = (byte)color.B; }
                for (var read = 0; read < bytes.Length;)
                {
                    var first = read == 0; var packet = new byte[65]; packet[1] = 0x10; packet[3] = 255; packet[4] = first ? (byte)0xe3 : (byte)0xe4;
                    packet[64] = 0x65; if (first) packet[7] = 255;
                    var count = Math.Min(first ? 54 : 57, bytes.Length - read);
                    Array.Copy(bytes, read, packet, first ? 9 : 5, count); read += count; packets.Add(packet);
                }
            }
            else
            {
                for (var i = 0; i < addresses.Length; i++)
                {
                    if (original.Counts[addresses[i]] == 0 || step.Zone is { } selected && selected != i) continue;
                    var address = (byte)addresses[i]; var cached = before.Zones.Single(z => z.Address == address).Setting;
                    var color = after.Colors[OpenRgbLightingPlan.Offset(after, i)];
                    packets.Add(AsrockOpenRgbNativeOperation.ZonePacket(address, cached with { Red = color.R, Green = color.G, Blue = color.B }, before.Order));
                }
            }
        }
        return AsrockOpenRgbNativeOperation.Create(command, before, packets.ToArray());
    }
}
