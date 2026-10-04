using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public interface IMemoryRgbControl
{
    Task<RgbInventory> ReadAsync();
    Task<BoardLightApplyResult> ApplyAsync(string identity, BoardLightChange[] changes);
}
public interface IEneMemoryIo : IDisposable
{
    bool Identify(byte address);
    byte Read(byte address, ushort register);
    void Write(byte address, ushort register, byte value);
}
public record ServiceMemoryLightChange(string Identity, BoardLightChange[] Changes);

/// <summary>Typed native lighting only; all requests are restricted to the two physically identified DIMMs.</summary>
public sealed partial class EneMemoryRgbControl(Func<IEneMemoryIo>? open = null, string? recoveryDirectory = null) : IMemoryRgbControl, IMemoryRgbStreamControl
{
    private readonly Func<IEneMemoryIo> open = open ?? (() => new EneMemoryRgbBus());
    public Task<RgbInventory> ReadAsync() => Task.Run(() =>
    {
        if (stream is not null) throw new IOException("メモリの同期中です");
        RecoverStream();
        using var bus = open(); var devices = new List<RgbDevice>();
        foreach (byte address in new byte[] { 0x71, 0x73 })
        {
            if (!bus.Identify(address)) continue;
            var state = Read(bus, address);
            var label = address == 0x71 ? "メモリ 1" : "メモリ 2";
            devices.Add(new((uint)devices.Count, label, "G.SKILL / ENE", "8 LED", "AUDA0-E6K5-0101", $"AMD SMBus 0 / 0x{address:X2}",
                MemoryLighting.Effects.Select(e => e.Name).ToArray(), -1,
                [new(label, 1, 8, 8, 8, state.Setting is { } s ? BoardLighting.Name(s.Mode) : "未対応の設定", 0, state.Setting)], MemoryLighting.Identity(address)));
        }
        return new RgbInventory(devices.ToArray(), DateTimeOffset.UtcNow);
    });
    public Task<BoardLightApplyResult> ApplyAsync(string identity, BoardLightChange[] changes)
    {
        var address = MemoryLighting.Address(identity);
        if (changes is not { Length: 1 } || changes[0] is not { Zone: 0 } change || !MemoryLighting.Valid(change.Expected) || !MemoryLighting.Valid(change.Desired))
            throw new ArgumentException("メモリの対象・元設定・効果が不正です");
        var captured = Contract.Copy(change);
        return Task.Run(() =>
        {
            if (stream is not null) throw new IOException("メモリの同期中です");
            RecoverStream();
            using var bus = open();
            if (!bus.Identify(address)) throw new IOException("対象のメモリRGBを確認できません");
            var before = Read(bus, address);
            if (before.Setting != captured.Expected) throw new IOException("メモリの照明設定が変わりました。再取得してください");
            var desired = MemoryLighting.Normalize(captured.Desired);
            try
            {
                // R-B-G order. No flash-save, address assignment, SPD, timing, voltage or port-selection writes.
                var colors = MemoryLighting.Colors(desired);
                for (var i = 0; i < colors.Length; i++) bus.Write(address, (ushort)(0x8160 + i), colors[i]);
                bus.Write(address, 0x8021, MemoryLighting.NativeMode(desired.Mode));
                bus.Write(address, 0x8022, (byte)Math.Round(desired.Speed * 4d / 255));
                bus.Write(address, 0x8023, 0);
                bus.Write(address, 0x8020, 0);
                bus.Write(address, 0x80a0, 1);
                var after = Read(bus, address).Setting;
                return new BoardLightApplyResult([new(0, after == desired ? "verified" : "mismatch", after)]);
            }
            catch (Exception e) when (e is IOException or TimeoutException)
            {
                // An uncertain write is never blindly replayed or hidden by a successful cached SDK state.
                return new BoardLightApplyResult([new(0, "unknown", Error: e.Message)]);
            }
        });
    }
    private static (BoardLightSetting? Setting, byte[] Colors) Read(IEneMemoryIo bus, byte address, bool allowDirect = false)
    {
        var state = Enumerable.Range(0, 4).Select(i => bus.Read(address, (ushort)(0x8020 + i))).ToArray();
        var colors = Enumerable.Range(0, 24).Select(i => bus.Read(address, (ushort)(0x8160 + i))).ToArray();
        if (state[0] > (allowDirect ? 1 : 0) || state[1] > 5 || state[2] > 4 || state[3] != 0) return (null, colors);
        // Scenes and temporary identification retain the exact eight-LED palette.
        return (MemoryLighting.Normalize(new(MemoryLighting.SceneMode(state[1]), colors[0], colors[2], colors[1], (int)Math.Round(state[2] * 255d / 4), MemoryLighting.Palette(colors))), colors);
    }
}
