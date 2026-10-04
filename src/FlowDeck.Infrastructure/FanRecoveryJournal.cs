using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public record FanRegisterRecovery(string Chip, int Channel, byte OriginalMode, byte OriginalCommand,
    byte BeforeMode, byte BeforeCommand, byte IntendedMode, byte IntendedCommand);
public interface IFanRecoveryJournal
{
    FanRegisterRecovery? Find(string chip, int channel);
    void Save(FanRegisterRecovery value);
    void Clear(string chip, int channel);
}
public sealed record FanRecoveryDocument(int Version, string BootIdentity, FanRegisterRecovery[] Entries, string Sha256, PhysicalDeviceBinding? Binding = null);

/// <summary>Service-private write-ahead records. These files are not part of portable user settings.</summary>
public sealed class FanRecoveryJournal : IFanRecoveryJournal
{
    private readonly string path, bootIdentity;
    private readonly PhysicalDeviceBinding? binding;
    private readonly object sync = new();
    private FanRecoveryDocument document;
    public bool PreviousBoot { get { lock (sync) return document.BootIdentity != bootIdentity; } }
    public FanRegisterRecovery[] Pending { get { lock (sync) return document.Entries.ToArray(); } }
    public FanRecoveryJournal(string directory, string bootIdentity, PhysicalDeviceBinding? binding = null)
    {
        if (string.IsNullOrWhiteSpace(bootIdentity) || bootIdentity.Length > 128 || bootIdentity.Any(char.IsControl)) throw new ArgumentException("Windowsの起動識別が必要です");
        if (binding is not null && (!DeviceConfigurationRules.Key(binding.DeviceId) || !DeviceConfigurationRules.Text(binding.ProviderIdentity, 1024) ||
            !MotherboardDefinitionCatalog.BuiltIn.Boards.Any(b => b.Id == binding.DefinitionId) || binding.Fingerprint is not { Length: 64 } || !binding.Fingerprint.All(Uri.IsHexDigit)))
            throw new ArgumentException("マザーの物理関連付けが不正です");
        this.bootIdentity = bootIdentity; this.binding = binding; path = Path.Combine(directory, "fan-recovery.json");
        document = Create([]);
        if (!File.Exists(path)) return;
        CheckPath();
        if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("ファン復旧記録が大きすぎます");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 1024 * 1024) throw new IOException("ファン復旧記録が大きすぎます");
            using var json = JsonDocument.Parse(stream); SettingsTransfer.NoDuplicates(json.RootElement);
            var loaded = json.RootElement.Deserialize<FanRecoveryDocument>(Contract.Json) ?? throw new IOException("ファン復旧記録が空です");
            if (loaded.Version != (binding is null ? 1 : 2) || string.IsNullOrWhiteSpace(loaded.BootIdentity) || loaded.BootIdentity.Length > 128 || loaded.Entries is null || loaded.Entries.Length > MotherboardFans.Ports.Count ||
                loaded.Entries.Any(e => !Valid(e)) || loaded.Entries.DistinctBy(e => (e.Chip, e.Channel)).Count() != loaded.Entries.Length ||
                (loaded.Version == 2) != (loaded.Binding is not null) || loaded.Sha256 != Hash(loaded.BootIdentity, loaded.Entries, loaded.Binding))
                throw new IOException("ファン復旧記録を検証できません");
            if (loaded.Binding?.DeviceId != binding?.DeviceId || loaded.Binding != binding && loaded.BootIdentity == bootIdentity && loaded.Entries.Length != 0)
                throw new IOException("復旧記録のマザー個体・構成が一致しません");
            document = loaded.Binding != binding && loaded.BootIdentity == bootIdentity ? Create([]) : loaded;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException) { throw new IOException("ファン復旧記録が破損しています", e); }
    }
    private static string Hash(string boot, FanRegisterRecovery[] entries, PhysicalDeviceBinding? binding) => binding is null ? SettingsTransfer.Hash(new { boot, entries }) : SettingsTransfer.Hash(new { boot, entries, binding });
    private FanRecoveryDocument Create(FanRegisterRecovery[] entries) => new(binding is null ? 1 : 2, bootIdentity, entries, Hash(bootIdentity, entries, binding), binding);
    private static bool Valid(FanRegisterRecovery? value)
    {
        if (value is null || !MotherboardFans.Ports.Any(p => p.Chip == value.Chip && p.Channel == value.Channel)) return false;
        var map = NctFanMap.For(value.Chip, value.Channel);
        return !map.SharedMode || (value.OriginalMode & ~map.Mask) == 0 && (value.BeforeMode & ~map.Mask) == 0 && (value.IntendedMode & ~map.Mask) == 0;
    }
    private void CurrentBoot()
    { if (document.BootIdentity != bootIdentity) throw new IOException("以前のWindows起動時の記録です。現在のBIOS設定へ自動復元できません"); }
    public FanRegisterRecovery? Find(string chip, int channel)
    { lock (sync) { CurrentBoot(); return document.Entries.SingleOrDefault(e => e.Chip == chip && e.Channel == channel); } }
    public void Save(FanRegisterRecovery value)
    {
        lock (sync)
        {
            CurrentBoot(); if (!Valid(value)) throw new ArgumentException("対象のファン復旧記録が不正です");
            var old = document.Entries.SingleOrDefault(e => e.Chip == value.Chip && e.Channel == value.Channel);
            if (old is not null && (value.OriginalMode != old.OriginalMode || value.OriginalCommand != old.OriginalCommand))
                throw new IOException("変更前のファン設定を上書きできません");
            Store(Create(document.Entries.Where(e => e.Chip != value.Chip || e.Channel != value.Channel).Append(value).OrderBy(e => e.Chip).ThenBy(e => e.Channel).ToArray()));
        }
    }
    public void Clear(string chip, int channel)
    { lock (sync) { CurrentBoot(); Store(Create(document.Entries.Where(e => e.Chip != chip || e.Channel != channel).ToArray())); } }
    // A reboot reinitializes firmware. Preserve the old record for diagnosis without replaying its register values.
    public void ArchivePreviousBoot()
    {
        lock (sync)
        {
            if (!PreviousBoot) return; CheckPath();
            File.Move(path, path + ".previous-boot-" + Guid.NewGuid().ToString("N")); document = Create([]);
        }
    }
    private void CheckPath()
    {
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("ファン復旧記録にリンクは使用できません");
        for (var folder = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!); folder is not null; folder = folder.Parent)
            if (folder.LinkTarget is not null) throw new IOException("ファン復旧フォルダーにリンクは使用できません");
    }
    private void Store(FanRecoveryDocument next)
    {
        CheckPath(); Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, next, Contract.Json); stream.Flush(true); }
            File.Move(temporary, path, true); document = next;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
