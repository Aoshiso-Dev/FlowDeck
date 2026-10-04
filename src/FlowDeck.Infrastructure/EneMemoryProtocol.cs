using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed record EneMemoryState(byte[] Control, byte[] NativeColors, byte[] DirectColors);
internal sealed record EneMemoryWrite(ushort Register, byte Value);
internal sealed record EneMemoryMutation(EneMemoryState Before, EneMemoryState After, EneMemoryWrite[] Writes);

// Wire data remains in the packaged controller driver. No bus address or R-B-G ordering enters v2 requests.
internal static class EneMemoryProtocol
{
    public static bool Valid(EneMemoryState? value) => value is { Control.Length: 4, NativeColors.Length: 24, DirectColors.Length: 24 } &&
        value.Control[0] <= 1 && value.Control[1] <= 5 && value.Control[2] <= 4 && value.Control[3] == 0;
    public static bool Same(EneMemoryState a, EneMemoryState b) => a.Control.SequenceEqual(b.Control) &&
        a.NativeColors.SequenceEqual(b.NativeColors) && a.DirectColors.SequenceEqual(b.DirectColors);
    public static EneMemoryState Read(IEneMemorySession session, Action? beforeData = null)
    {
        byte[] Range(ushort first, int count) => Enumerable.Range(0, count).Select(i => session.Read((ushort)(first + i), beforeData)).ToArray();
        return new(Range(0x8020, 4), Range(0x8160, 24), Range(0x8100, 24));
    }
    public static BoardLightSetting? NativeSetting(EneMemoryState state)
    {
        if (!Valid(state)) return null;
        var c = state.NativeColors;
        var uniform = Enumerable.Range(1, 7).All(i => c.AsSpan(i * 3, 3).SequenceEqual(c.AsSpan(0, 3)));
        return new(state.Control[1] == 5 ? (byte)14 : state.Control[1], c[0], c[2], c[1],
            state.Control[1] <= 1 ? 224 : (int)Math.Round(state.Control[2] * 255d / 4), uniform ? null : Convert.ToHexString(c));
    }
    private static byte[] Palette(RgbColor color) => Enumerable.Range(0, 8).SelectMany(_ => new byte[] { (byte)color.R, (byte)color.B, (byte)color.G }).ToArray();
    public static EneMemoryState NativeTarget(EneMemoryState before, BoardLightSetting setting)
    {
        if (!Valid(before) || !MemoryLighting.Valid(setting)) throw new ArgumentException("復元可能なENE照明設定が必要です");
        var colors = setting.MemoryPalette is { } p ? Convert.FromHexString(p) : Palette(new((byte)setting.Red, (byte)setting.Green, (byte)setting.Blue));
        var mode = setting.Mode == 14 ? (byte)5 : setting.Mode;
        return new([0, mode, mode <= 1 ? (byte)4 : (byte)Math.Round(setting.Speed * 4d / 255), 0], colors, before.DirectColors.ToArray());
    }
    public static EneMemoryMutation Native(EneMemoryState before, EneMemoryState after)
    {
        if (!Valid(before) || !Valid(after) || after.Control[0] != 0) throw new ArgumentException("ENEのNative復元設定が不正です");
        var writes = new List<EneMemoryWrite>();
        if (!before.DirectColors.SequenceEqual(after.DirectColors))
            writes.AddRange(after.DirectColors.Select((v, i) => new EneMemoryWrite((ushort)(0x8100 + i), v)));
        writes.AddRange(after.NativeColors.Select((v, i) => new EneMemoryWrite((ushort)(0x8160 + i), v)));
        for (var i = 1; i < 4; i++) writes.Add(new((ushort)(0x8020 + i), after.Control[i]));
        writes.Add(new(0x8020, 0)); writes.Add(new(0x80a0, 1));
        return new(Contract.Copy(before), Contract.Copy(after), writes.ToArray());
    }
    public static EneMemoryMutation Frame(EneMemoryState before, RgbColor color)
    {
        if (!Valid(before) || color?.Valid != true) throw new ArgumentException("ENEの同期フレームが不正です");
        var after = Contract.Copy(before); after.Control[0] = 1; after = after with { DirectColors = Palette(color) };
        var writes = after.DirectColors.Select((v, i) => new EneMemoryWrite((ushort)(0x8100 + i), v)).ToList();
        if (before.Control[0] == 0) { writes.Add(new(0x8020, 1)); writes.Add(new(0x80a0, 1)); }
        return new(Contract.Copy(before), after, writes.ToArray());
    }
    private static void ApplyByte(EneMemoryState state, EneMemoryWrite value)
    {
        if (value.Register is >= 0x8020 and <= 0x8023) state.Control[value.Register - 0x8020] = value.Value;
        else if (value.Register is >= 0x8160 and <= 0x8177) state.NativeColors[value.Register - 0x8160] = value.Value;
        else if (value.Register is >= 0x8100 and <= 0x8117) state.DirectColors[value.Register - 0x8100] = value.Value;
        else if (value.Register != 0x80a0 || value.Value != 1) throw new ArgumentException("ENEの書込み順序が不正です");
    }
    public static bool OwnedPrefix(EneMemoryState actual, EneMemoryMutation mutation)
    {
        if (!Valid(actual) || !ValidMutation(mutation)) return false;
        var partial = Contract.Copy(mutation.Before);
        if (Same(actual, partial)) return true;
        foreach (var write in mutation.Writes)
        {
            EneMemoryRgbBus.ValidateWrite(write.Register, write.Value); ApplyByte(partial, write);
            if (Same(actual, partial)) return true;
        }
        return false;
    }
    internal static bool ValidMutation(EneMemoryMutation? mutation)
    {
        if (mutation is null || !Valid(mutation.Before) || !Valid(mutation.After) || mutation.Writes is not { Length: > 0 and <= 53 } || mutation.Writes.Any(w => w is null)) return false;
        var expected = mutation.After.Control[0] == 0 ? Native(mutation.Before, mutation.After) :
            Frame(mutation.Before, new(mutation.After.DirectColors[0], mutation.After.DirectColors[2], mutation.After.DirectColors[1]));
        return Same(expected.After, mutation.After) && expected.Writes.SequenceEqual(mutation.Writes);
    }
    // The caller persists the complete mutation before calling this method. Guard runs at the actual data transfer,
    // after the register-pointer selection, and must recheck physical ownership, the durable record and any frame deadline.
    public static void ExecutePrepared(IEneMemorySession session, EneMemoryMutation mutation, Action guard, Action? beforeWrite = null, Action? readGuard = null)
    {
        ArgumentNullException.ThrowIfNull(guard);
        if (!ValidMutation(mutation))
            throw new ArgumentException("ENEの照明操作が不正です");
        foreach (var write in mutation.Writes) EneMemoryRgbBus.ValidateWrite(write.Register, write.Value);
        var target = Contract.Copy(mutation.Before);
        foreach (var write in mutation.Writes) ApplyByte(target, write);
        if (!Same(target, mutation.After)) throw new ArgumentException("ENEの書込み予定値が不一致です");
        if (!Same(Read(session, readGuard ?? guard), mutation.Before)) throw new IOException("ENEの照明設定が送信前に変更されました");
        void WriteGuard() { guard(); beforeWrite?.Invoke(); }
        for (var i = 0; i < mutation.Writes.Length; i++)
        {
            var write = mutation.Writes[i];
            if (write.Register is >= 0x8100 and <= 0x8115 && (write.Register - 0x8100) % 3 == 0 && i + 2 < mutation.Writes.Length &&
                mutation.Writes[i + 1].Register == write.Register + 1 && mutation.Writes[i + 2].Register == write.Register + 2)
            {
                session.WritePixel((write.Register - 0x8100) / 3, write.Value, mutation.Writes[i + 1].Value, mutation.Writes[i + 2].Value, WriteGuard); i += 2;
            }
            else session.Write(write.Register, write.Value, WriteGuard);
        }
    }
}
