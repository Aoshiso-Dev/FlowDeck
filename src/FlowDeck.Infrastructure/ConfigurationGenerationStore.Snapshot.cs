namespace FlowDeck.Infrastructure;

public sealed partial class ConfigurationGenerationStore
{
    // Setup alone uses these operations, with the service stopped and its ownership lease held.
    // Preserve the exact manifest/pointer, rather than restaging the user's settings as a new generation.
    private Dictionary<string, byte[]> SnapshotFiles(ConfigurationPointer pointer)
    {
        _ = ReadGeneration(pointer);
        var root = Path.Combine(Generations, pointer.Id);
        var manifestBytes = Read(Path.Combine(root, "manifest.json"));
        if (Hash(manifestBytes) != pointer.ManifestHash) throw new IOException("退避中に設定一覧が変更されました");
        var manifest = Parse<ConfigurationManifest>(manifestBytes);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["manifest.json"] = manifestBytes };
        long total = 0;
        foreach (var (name, digest) in manifest.Files.Concat(manifest.LegacyFiles.Select(p =>
                     new KeyValuePair<string, string>("legacy/" + p.Key, p.Value))))
        {
            var bytes = Read(Path.Combine(root, name)); total += bytes.Length;
            if (total > MaximumTotalBytes || Hash(bytes) != digest) throw new IOException("退避中に設定ファイルが変更されました");
            files.Add(name, bytes);
        }
        return files;
    }
    internal ConfigurationPointer SnapshotActiveTo(string destination)
    {
        using var guard = Lock();
        var pointer = ReadPointer() ?? throw new IOException("退避する採用済み設定がありません");
        var files = SnapshotFiles(pointer);
        var backup = new ConfigurationGenerationStore(destination, validate);
        CheckPath(Path.Combine(backup.Root, "current.json"));
        if (Directory.Exists(backup.Root) || File.Exists(backup.Root)) throw new IOException("設定の退避先が既に存在します");
        foreach (var (name, bytes) in files) WriteNew(Path.Combine(backup.Generations, pointer.Id, name), bytes);
        _ = backup.ReadGeneration(pointer);
        backup.SwitchPointer(pointer);
        return pointer;
    }
    internal void RestoreSnapshotFrom(string source, ConfigurationPointer expected)
    {
        var backup = new ConfigurationGenerationStore(source, validate);
        Dictionary<string, byte[]> files;
        using (var backupGuard = backup.Lock())
        {
            if (backup.ReadPointer() != expected) throw new IOException("退避した設定世代が一致しません");
            files = backup.SnapshotFiles(expected);
        }
        using var guard = Lock();
        var target = Path.Combine(Generations, expected.Id);
        CheckPath(Path.Combine(target, "manifest.json"));
        var intact = false;
        try { _ = ReadGeneration(expected); intact = true; }
        catch (IOException) { /* A failed new version may have damaged the old generation. Keep it for diagnosis. */ }
        if (!intact)
        {
            var staged = new ConfigurationGenerationStore(Path.Combine(Root, "restore-" + Guid.NewGuid().ToString("N")), validate);
            foreach (var (name, bytes) in files) WriteNew(Path.Combine(staged.Generations, expected.Id, name), bytes);
            _ = staged.ReadGeneration(expected); Checkpoint?.Invoke("restore-staged");
            if (Directory.Exists(target))
            {
                var rejected = Path.Combine(Root, "rejected-generations", expected.Id + "-" + Guid.NewGuid().ToString("N"));
                CheckPath(rejected); Directory.CreateDirectory(Path.GetDirectoryName(rejected)!);
                Directory.Move(target, rejected); Checkpoint?.Invoke("restore-quarantined");
            }
            CheckPath(target); Directory.CreateDirectory(Generations);
            Directory.Move(Path.Combine(staged.Generations, expected.Id), target);
            Checkpoint?.Invoke("restore-generation");
        }
        // The current pointer itself may be missing or corrupt. The separately verified backup is authoritative.
        // Caller has first resolved all physical and route-switch obligations; none are overwritten here.
        try { if (ReadPointer() == expected) return; }
        catch (IOException) { /* Replace the damaged pointer only after restoring and validating the full generation. */ }
        Checkpoint?.Invoke("restore-before-switch"); SwitchPointer(expected); Checkpoint?.Invoke("restore-switched");
        _ = ReadGeneration(expected);
    }
}
