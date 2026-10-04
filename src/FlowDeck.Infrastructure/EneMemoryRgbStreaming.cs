using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public record MemoryRgbTarget(string Identity, BoardLightSetting Expected);
public record MemoryRgbBegin(MemoryRgbTarget[] Targets);
public record MemoryRgbFrame(string Lease, RgbColor Color);
public record MemoryRgbEnd(string Lease, bool KeepLast);
public interface IMemoryRgbStreamControl
{
    Task<string> BeginAsync(MemoryRgbTarget[] targets);
    Task FrameAsync(string lease, RgbColor color);
    Task EndAsync(string lease, bool keepLast);
}
public sealed partial class EneMemoryRgbControl
{
    private record Module(string Identity, BoardLightSetting Original, string Before, string After);
    private record StreamState(string Id, Module[] Modules, RgbColor? Last = null);
    private StreamState? stream;
    private bool streamFailed;
    private string? JournalPath => recoveryDirectory is null ? null : Path.Combine(recoveryDirectory, "memory-rgb-stream.json");
    private static byte[] DirectColors(IEneMemoryIo bus, byte address) => Enumerable.Range(0,24).Select(i => bus.Read(address, (ushort)(0x8100+i))).ToArray();
    private static void RequireIdentity(IEneMemoryIo bus, byte address) { if (!bus.Identify(address)) throw new IOException("同期するメモリを確認できません"); }
    private void SaveStream(StreamState state)
    {
        if (JournalPath is not { } path) return;
        Directory.CreateDirectory(recoveryDirectory!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Contract.Json);
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { file.Write(bytes); file.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
    private void ClearStreamJournal() { if (JournalPath is { } path && File.Exists(path)) File.Delete(path); }
    private static bool OwnedColors(byte[] actual, Module module)
    {
        var before = Convert.FromHexString(module.Before); var after = Convert.FromHexString(module.After);
        // A crash may interrupt a sequential frame after any byte. No arbitrary third-party palette is accepted.
        return Enumerable.Range(0,25).Any(n => actual.AsSpan(0,n).SequenceEqual(after.AsSpan(0,n)) && actual.AsSpan(n).SequenceEqual(before.AsSpan(n)));
    }
    private void RestoreNative(StreamState state)
    {
        using var bus = open(); var errors = new List<string>();
        foreach (var module in state.Modules)
        {
            try
            {
                var address = MemoryLighting.Address(module.Identity); RequireIdentity(bus,address);
                if (bus.Read(address,0x8020) == 0) continue; // Another native operation already owns this DIMM.
                if (Read(bus,address,true).Setting != module.Original || !OwnedColors(DirectColors(bus,address),module))
                    throw new IOException("別の照明設定を検出したため復帰を保留しました");
                bus.Write(address,0x8020,0); bus.Write(address,0x80a0,1);
                if (Read(bus,address).Setting != module.Original) throw new IOException("メモリの復帰を確認できません");
            }
            catch (Exception e) { errors.Add(module.Identity + ": " + e.Message); }
        }
        if (errors.Count != 0) throw new IOException(string.Join(" / ",errors));
        ClearStreamJournal();
    }
    private void RecoverStream()
    {
        if (stream is not null || JournalPath is not { } path || !File.Exists(path)) return;
        if (new FileInfo(path).Length > 8192) throw new IOException("メモリ照明の復旧記録が不正です");
        var state = JsonSerializer.Deserialize<StreamState>(File.ReadAllBytes(path),Contract.Json);
        if (state is null || !Guid.TryParseExact(state.Id,"N",out _) || state.Modules is not { Length: >= 1 and <= 2 } ||
            state.Modules.Select(m => m.Identity).Distinct().Count() != state.Modules.Length ||
            state.Modules.Any(m => !MemoryLighting.IsIdentity(m.Identity) || !MemoryLighting.Valid(m.Original) ||
                m.Before.Length != 48 || m.After.Length != 48 || m.Before.Concat(m.After).Any(c => !Uri.IsHexDigit(c))))
            throw new IOException("メモリ照明の復旧記録が不正です");
        RestoreNative(state);
    }
    // Service migration restores only an existing journal; it does not enumerate or start a new lighting effect.
    public Task RecoverInterruptedStreamAsync() => Task.Run(RecoverStream);
    public Task<string> BeginAsync(MemoryRgbTarget[] targets)
    {
        if (targets is not { Length: >= 1 and <= 2 } || targets.Any(t => t is null || !MemoryLighting.IsIdentity(t.Identity) || !MemoryLighting.Valid(t.Expected)) || targets.Select(t => t.Identity).Distinct().Count() != targets.Length)
            throw new ArgumentException("同期するメモリの対象が不正です");
        var captured = Contract.Copy(targets);
        return Task.Run(() =>
        {
            if (stream is not null) throw new IOException("メモリはすでに同期中です");
            RecoverStream(); using var bus = open(); var modules = new List<Module>();
            foreach (var target in captured)
            {
                var address = MemoryLighting.Address(target.Identity); RequireIdentity(bus,address);
                if (Read(bus,address).Setting != target.Expected) throw new IOException("メモリの設定が変わりました。再取得してください");
                var colors = Convert.ToHexString(DirectColors(bus,address));
                modules.Add(new(target.Identity,target.Expected,colors,colors));
            }
            stream = new(Guid.NewGuid().ToString("N"),modules.ToArray()); streamFailed = false; return stream.Id;
        });
    }
    public Task FrameAsync(string lease, RgbColor color)
    {
        if (color is null || !color.Valid) throw new ArgumentException("不正なメモリ同期色です");
        return Task.Run(() =>
        {
            var current = stream;
            if (current is null || current.Id != lease || streamFailed) throw new IOException("メモリ同期の操作権が無効です");
            try
            {
                using var bus = open();
                foreach (var module in current.Modules)
                {
                    var a = MemoryLighting.Address(module.Identity);
                    if (bus is IEneMemoryStreamIo fast) { if(!fast.VerifyPreviouslyIdentified(a)) throw new IOException("メモリの識別が変わりました"); }
                    else RequireIdentity(bus,a);
                    if (bus.Read(a,0x8021) != MemoryLighting.NativeMode(module.Original.Mode) || bus.Read(a,0x8020) != (current.Last is null ? 0 : 1) ||
                        Convert.ToHexString(DirectColors(bus,a)) != module.After) throw new IOException("別のメモリ照明操作を検出しました");
                }
                var colors = MemoryLighting.Colors(new(1,color.R,color.G,color.B)); var next = Convert.ToHexString(colors);
                var pending = current with { Modules = current.Modules.Select(m => m with { Before=m.After, After=next }).ToArray(), Last=color };
                SaveStream(pending); stream = pending;
                foreach (var module in pending.Modules)
                {
                    var a = MemoryLighting.Address(module.Identity);
                    if(bus is IEneMemoryStreamIo fast)
                        for(var led=0;led<8;led++) fast.WritePixel(a,led,colors[led*3],colors[led*3+1],colors[led*3+2]);
                    else for (var i=0;i<24;i++) bus.Write(a,(ushort)(0x8100+i),colors[i]);
                    if (current.Last is null) { bus.Write(a,0x8020,1); bus.Write(a,0x80a0,1); }
                    // Check the first/last pixels immediately; the entire palette is checked before the next frame and at stop.
                    if (bus.Read(a,0x8020) != 1 || new[] {0,1,2,21,22,23}.Any(i => bus.Read(a,(ushort)(0x8100+i)) != colors[i])) throw new IOException("メモリ同期色の読み戻しが一致しません");
                }
            }
            catch { streamFailed = true; throw; }
        });
    }
    public async Task EndAsync(string lease, bool keepLast)
    {
        var current = stream;
        if (current is null || current.Id != lease) throw new IOException("メモリ同期の操作権が無効です");
        try { await Task.Run(() => RestoreNative(current)); }
        finally { stream = null; }
        if (keepLast && !streamFailed && current.Last is { } color)
            foreach (var module in current.Modules)
            {
                var result = await ApplyAsync(module.Identity,[new(0,module.Original,new(1,color.R,color.G,color.B))]);
                if (!result.Verified) throw new IOException("停止時のメモリ固定色を確認できません");
            }
    }
}
