using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed record AsrockRgbRecoveryRecord(int Version, string Boot, AsrockRgbSnapshot Snapshot,
    Dictionary<byte, BoardLightSetting> Desired, string Hash);

internal interface IAsrockStreamRecovery
{
    bool Exists { get; }
    void CheckOwnership() { }
    void Save(string boot, AsrockRgbSnapshot snapshot, Dictionary<byte, BoardLightSetting>? desired = null);
    void Expand(byte[] targets, Dictionary<byte, BoardLightSetting> desired) => throw new IOException("この復旧記録は対象追加に対応していません");
    bool SupportsReduction => false;
    void Reduce(byte[] targets) => throw new IOException("この復旧記録は対象縮小に対応していません");
    void Clear();
    void ClearUnsent() => Clear();
}

/// <summary>User-local write-ahead journal, excluded from settings export. It never stores Wi-Fi secrets.</summary>
internal sealed class AsrockRgbRecoveryStore(string directory) : IAsrockStreamRecovery
{
    private readonly string path = Path.Combine(directory, "asrock-rgb-recovery.json");
    public bool Exists => File.Exists(path);
    public AsrockRgbRecoveryRecord? Load()
    {
        if (!Exists) return null;
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 65536) throw new IOException("RGB復旧記録が大きすぎます");
            using var json = JsonDocument.Parse(input); SettingsTransfer.NoDuplicates(json.RootElement);
            var value = json.RootElement.Deserialize<AsrockRgbRecoveryRecord>(Contract.Json) ?? throw new IOException("RGB復旧記録が空です");
            var s = value.Snapshot;
            if (value.Version != 1 || string.IsNullOrWhiteSpace(value.Boot) || value.Boot.Length > 128 || s is null ||
                string.IsNullOrWhiteSpace(s.Identity) || s.Identity.Length > 2048 || s.Counts is not { Length: 8 } || s.Zones is null || s.Zones.Length is < 1 or > 8 ||
                s.Zones.Any(z => z is null || z.Address > 7 || z.Count is < 1 or > 255 || s.Counts[z.Address] != z.Count ||
                    (s.Available & (1 << z.Address)) == 0 || !BoardLighting.IsValid(z.Setting)) || s.Zones.DistinctBy(z => z.Address).Count() != s.Zones.Length ||
                Enumerable.Range(0, 8).Count(i => (s.Available & (1 << i)) != 0 && s.Counts[i] is not (0 or 0x1e)) != s.Zones.Length ||
                value.Desired is null || value.Desired.Count != s.Zones.Length || s.Zones.Any(z => !value.Desired.TryGetValue(z.Address, out var desired) || !BoardLighting.IsValid(desired)) ||
                value.Hash != Hash(value.Boot, s, value.Desired)) throw new IOException("RGB復旧記録を検証できません");
            return value;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException) { throw new IOException("RGB復旧記録が破損しています", e); }
    }
    private static string Hash(string boot, AsrockRgbSnapshot snapshot, Dictionary<byte, BoardLightSetting> desired)
        => SettingsTransfer.Hash(new { boot, snapshot, desired = desired.OrderBy(z => z.Key).ToArray() });
    public void Save(string boot, AsrockRgbSnapshot snapshot, Dictionary<byte, BoardLightSetting>? desired = null)
    {
        desired ??= snapshot.Zones.ToDictionary(z => z.Address, z => z.Setting);
        var value = new AsrockRgbRecoveryRecord(1, boot, snapshot, desired, Hash(boot, snapshot, desired));
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(output, value, Contract.Json); output.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Clear() => File.Delete(path);
    public void ArchivePreviousBoot() => File.Move(path, path + ".previous-boot-" + Guid.NewGuid().ToString("N"));
}

public static class AsrockRgbRecovery
{
    public static Task<string?> RecoverAsync(string directory) => Task.Run(() =>
    {
        var store = new AsrockRgbRecoveryStore(directory);
        var saved = store.Load();
        if (saved is null) return null;
        if (saved.Boot != BootIdentity()) { store.ArchivePreviousBoot(); return "以前のWindows起動時のRGB復旧記録を退避しました"; }
        using var session = new AsrockRgbSession(saved.Snapshot.Identity);
        var restored = new AsrockVolatileTransaction(saved.Snapshot, session.Exchange).Restore(saved.Desired);
        if (!restored.Restored) throw new IOException("マザー照明の復元を確認できません。" + string.Join(" / ", restored.Errors.Concat(restored.Zones.Where(z => z.Status != "verified").Select(z => $"ゾーン{z.Zone}: {z.Status}"))));
        store.Clear(); return "中断した照明同期の設定を復元しました";
    });
    internal static string BootIdentity()
    {
        try { return ServiceEnvironment.BootIdentity(); }
        catch (Exception e) { throw new IOException("RGB復旧用のWindows起動識別を取得できません", e); }
    }
}
