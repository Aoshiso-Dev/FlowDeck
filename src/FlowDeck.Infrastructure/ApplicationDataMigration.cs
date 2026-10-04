namespace FlowDeck.Infrastructure;

public static class ApplicationDataMigration
{
    public const string ProductDirectory = "FlowDeck";
    public const string LegacyDirectory = "NzxtControlHub";

    public static string EnsureRoot(string localApplicationData)
    {
        var parent = Path.GetFullPath(localApplicationData);
        var target = Path.Combine(parent, ProductDirectory);
        if (Directory.Exists(target)) return target; // Never overwrite an existing FlowDeck installation.
        var legacy = Path.Combine(parent, LegacyDirectory);
        if (!Directory.Exists(legacy)) { Directory.CreateDirectory(target); return target; }
        var staging = Path.Combine(parent, ".FlowDeck-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            CopyDirectory(legacy, staging);
            Directory.Move(staging, target); // Publish the complete copy atomically, including encrypted pairing data.
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
        return target;
    }

    private static void CopyDirectory(string source, string target)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("設定フォルダー内のリンクを確認してください。旧設定は変更していません。");
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("設定ファイルのリンクは移行できません。");
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var child = Path.Combine(target, Path.GetFileName(directory));
            Directory.CreateDirectory(child); CopyDirectory(directory, child);
        }
    }

    public static string? RelocateBundledPython(string? configuredPath, string applicationDirectory)
    {
        if (string.IsNullOrEmpty(configuredPath)) return configuredPath;
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
        var parent = Path.GetDirectoryName(directory);
        if (parent is null) return configuredPath;
        var oldPath = Path.Combine(parent, LegacyDirectory, "runtime", "python", "python.exe");
        var current = Path.Combine(directory, "runtime", "python", "python.exe");
        return Path.GetFullPath(configuredPath).Equals(oldPath, StringComparison.OrdinalIgnoreCase) && File.Exists(current) ? current : configuredPath;
    }
}
