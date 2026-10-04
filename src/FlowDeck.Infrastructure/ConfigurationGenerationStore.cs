using System.Security.Cryptography;
using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record ConfigurationManifest(int Version, string Id, DateTimeOffset CreatedAt,
    Dictionary<string, string> Files, Dictionary<string, string> LegacyFiles);
public sealed record ConfigurationPointer(int Version, string Id, string ManifestHash);
public sealed record ConfigurationActivation(ConfigurationPointer Target, ConfigurationPointer? ExpectedCurrent);
public sealed record ConfigurationGeneration(ConfigurationPointer Pointer, IReadOnlyDictionary<string, byte[]> Files);

/// <summary>Immutable multi-file generations. Only a validated manifest pointer is atomically switched.</summary>
public sealed partial class ConfigurationGenerationStore(string directory, Action<IReadOnlyDictionary<string, byte[]>> validate)
{
    public static IReadOnlyList<string> ComponentNames { get; } = Array.AsReadOnly(new[]
        { "devices.json", "bindings.json", "cooling.json", "lighting.json", "dashboard.json", "preferences.json", "migration.json" });
    private const int MaximumFileBytes = 1024 * 1024, MaximumTotalBytes = 8 * 1024 * 1024;
    private string Root => Path.GetFullPath(directory);
    public string DirectoryPath => Root;
    private string Generations => Path.Combine(Root, "generations");
    private string Current => Path.Combine(Root, "current.json");
    internal Action<string>? Checkpoint { get; set; }
    private static bool Id(string? value) => Guid.TryParseExact(value, "N", out var id) && id.ToString("N") == value;
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool Digest(string? value) => value?.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void CheckPath(string path)
    {
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("設定の保存先にリンクは使用できません");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent is not null; parent = parent.Parent)
            if (parent.LinkTarget is not null) throw new IOException("設定の保存先にリンクは使用できません");
    }
    private FileStream Lock()
    {
        var path = Path.Combine(Root, "generation.lock"); CheckPath(path); Directory.CreateDirectory(Root);
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private static byte[] Read(string path, int maximum = MaximumFileBytes)
    {
        CheckPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new IOException("設定ファイルが大きすぎます");
        using var result = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = stream.Read(buffer)) > 0)
        { if (result.Length + count > maximum) throw new IOException("設定ファイルが大きすぎます"); result.Write(buffer, 0, count); }
        return result.ToArray();
    }
    private static T Parse<T>(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes); UniqueKeys(document.RootElement);
            return document.RootElement.Deserialize<T>(Contract.Json) ?? throw new IOException("設定形式が不正です");
        }
        catch (JsonException e) { throw new IOException("設定形式が不正です", e); }
    }
    private static void UniqueKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new IOException("設定の項目が重複しています"); UniqueKeys(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) UniqueKeys(value);
    }
    private static void WriteNew(string path, byte[] bytes)
    {
        CheckPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough);
        stream.Write(bytes); stream.Flush(true);
    }
    private ConfigurationPointer? ReadPointer()
    {
        CheckPath(Current);
        if (!File.Exists(Current)) return null;
        var value = Parse<ConfigurationPointer>(Read(Current, 4096));
        if (value.Version != 1 || !Id(value.Id) || !Digest(value.ManifestHash)) throw new IOException("設定の参照先が不正です");
        return value;
    }
    public ConfigurationGeneration? ReadActive()
    {
        using var guard = Lock(); var pointer = ReadPointer();
        return pointer is null ? null : ReadGeneration(pointer);
    }
    public ConfigurationPointer? ReadActivePointer()
    { using var guard = Lock(); return ReadPointer(); }
    public ConfigurationGeneration Read(ConfigurationPointer pointer)
    { using var guard = Lock(); return ReadGeneration(pointer); }
    private ConfigurationGeneration ReadGeneration(ConfigurationPointer pointer)
    {
        if (pointer.Version != 1 || !Id(pointer.Id) || !Digest(pointer.ManifestHash)) throw new IOException("設定世代が不正です");
        var root = Path.Combine(Generations, pointer.Id);
        var manifestBytes = Read(Path.Combine(root, "manifest.json"));
        if (Hash(manifestBytes) != pointer.ManifestHash) throw new IOException("設定一覧の整合性を確認できません");
        var manifest = Parse<ConfigurationManifest>(manifestBytes);
        if (manifest.Version != 1 || manifest.Id != pointer.Id || manifest.Files is null || manifest.LegacyFiles is null ||
            manifest.Files.Count != ComponentNames.Count || manifest.Files.Keys.Any(n => !ComponentNames.Contains(n)) ||
            manifest.Files.Values.Any(h => !Digest(h)) || manifest.LegacyFiles.Count > 256 ||
            manifest.LegacyFiles.Any(p => !SettingsTransferStore.Allowed(p.Key) || !Digest(p.Value))) throw new IOException("設定一覧の形式が不正です");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal); long total = 0;
        foreach (var (name, digest) in manifest.Files.Concat(manifest.LegacyFiles.Select(p => new KeyValuePair<string, string>("legacy/" + p.Key, p.Value))))
        {
            var bytes = Read(Path.Combine(root, name)); total += bytes.Length;
            if (total > MaximumTotalBytes || Hash(bytes) != digest) throw new IOException("設定ファイルの整合性を確認できません");
            if (!name.StartsWith("legacy/", StringComparison.Ordinal)) files[name] = bytes;
        }
        ValidateComponents(files); return new(pointer, files);
    }
    private void ValidateComponents(IReadOnlyDictionary<string, byte[]> files)
    {
        if (files.Count != ComponentNames.Count || files.Keys.Any(n => !ComponentNames.Contains(n)) ||
            files.Values.Any(b => b is null || b.Length > MaximumFileBytes) || files.Values.Sum(b => (long)b.Length) > MaximumTotalBytes)
            throw new IOException("設定ファイル一式が不足しているか不正です");
        foreach (var bytes in files.Values)
        {
            var element = Parse<JsonElement>(bytes);
            if (element.ValueKind != JsonValueKind.Object) throw new IOException("設定本文はオブジェクトが必要です");
        }
        // The owning version supplies the full schema and cross-file reference validator.
        validate(files);
    }
    public ConfigurationActivation Stage(IReadOnlyDictionary<string, byte[]> components, IReadOnlyDictionary<string, byte[]>? legacy = null)
    {
        var files = components.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
        var before = (legacy ?? new Dictionary<string, byte[]>()).ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
        ValidateComponents(files);
        if (before.Count > 256 || before.Any(p => !SettingsTransferStore.Allowed(p.Key) || p.Value.Length > MaximumFileBytes) ||
            files.Values.Concat(before.Values).Sum(b => (long)b.Length) > MaximumTotalBytes) throw new IOException("旧設定のバックアップ形式が不正です");
        using var guard = Lock(); var expected = ReadPointer();
        if (expected is not null) _ = ReadGeneration(expected);
        var id = Guid.NewGuid().ToString("N"); var root = Path.Combine(Generations, id);
        foreach (var (name, bytes) in files)
        { WriteNew(Path.Combine(root, name), bytes); Checkpoint?.Invoke("file:" + name); }
        foreach (var (name, bytes) in before) WriteNew(Path.Combine(root, "legacy", name), bytes);
        var manifest = new ConfigurationManifest(1, id, DateTimeOffset.UtcNow, files.ToDictionary(p => p.Key, p => Hash(p.Value)), before.ToDictionary(p => p.Key, p => Hash(p.Value)));
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Contract.Json);
        WriteNew(Path.Combine(root, "manifest.json"), manifestBytes);
        var target = new ConfigurationPointer(1, id, Hash(manifestBytes));
        _ = ReadGeneration(target); Checkpoint?.Invoke("staged");
        return new(target, expected);
    }
    public void Activate(ConfigurationActivation activation)
    {
        using var guard = Lock();
        if (ReadPointer() != activation.ExpectedCurrent) throw new IOException("確認後に設定世代が変更されました");
        _ = ReadGeneration(activation.Target); Checkpoint?.Invoke("before-switch");
        SwitchPointer(activation.Target);
    }
    private void SwitchPointer(ConfigurationPointer target)
    {
        var temporary = Path.Combine(Root, "current-" + Guid.NewGuid().ToString("N") + ".tmp");
        WriteNew(temporary, JsonSerializer.SerializeToUtf8Bytes(target, Contract.Json));
        try
        {
            CheckPath(Current); CheckPath(Current + ".bak");
            if (File.Exists(Current)) File.Replace(temporary, Current, Current + ".bak");
            else File.Move(temporary, Current);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public ConfigurationActivation PrepareRollback(ConfigurationPointer target)
    {
        using var guard = Lock(); _ = ReadGeneration(target);
        return new(target, ReadPointer());
    }
}
