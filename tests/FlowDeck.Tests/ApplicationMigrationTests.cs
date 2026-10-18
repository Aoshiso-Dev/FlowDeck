using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class ApplicationMigrationTests
{
    private static string CreateRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "FlowDeck-migration-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    [Fact]
    public void CompleteLegacyCopyPreservesOpaquePairingAndKeepsOriginal()
    {
        var root = CreateRoot(); var old = Path.Combine(root, "NzxtControlHub");
        Directory.CreateDirectory(Path.Combine(old, "hardware", "logs"));
        File.WriteAllText(Path.Combine(old, "hardware", "settings.json"), "{\"ui\":{\"theme\":\"dark\"}}");
        File.WriteAllBytes(Path.Combine(old, "hardware", "private.protected"), [1, 7, 3, 9]);
        File.WriteAllText(Path.Combine(old, "hardware", "logs", "control.jsonl"), "legacy log");
        var current = ApplicationDataMigration.EnsureRoot(root);
        Assert.Equal(Path.Combine(root, "FlowDeck"), current);
        Assert.Equal(File.ReadAllText(Path.Combine(old, "hardware", "settings.json")), File.ReadAllText(Path.Combine(current, "hardware", "settings.json")));
        Assert.Equal(new byte[] { 1, 7, 3, 9 }, File.ReadAllBytes(Path.Combine(current, "hardware", "private.protected")));
        Assert.True(File.Exists(Path.Combine(old, "hardware", "private.protected")));
        Assert.Equal("legacy log", File.ReadAllText(Path.Combine(current, "hardware", "logs", "control.jsonl")));
    }
    [Fact]
    public void ExistingFlowDeckDataIsNeverReplacedByLegacyData()
    {
        var root = CreateRoot(); var target = Path.Combine(root, "FlowDeck"); var old = Path.Combine(root, "NzxtControlHub");
        Directory.CreateDirectory(target); Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(target, "settings.json"), "current"); File.WriteAllText(Path.Combine(old, "settings.json"), "old");
        Assert.Equal(target, ApplicationDataMigration.EnsureRoot(root));
        Assert.Equal("current", File.ReadAllText(Path.Combine(target, "settings.json")));
    }
    [Fact]
    public void FailedCopyDoesNotPublishPartialDataAndCanBeRetried()
    {
        var root = CreateRoot(); var old = Path.Combine(root, "NzxtControlHub"); Directory.CreateDirectory(old);
        var locked = Path.Combine(old, "settings.json"); File.WriteAllText(locked, "original");
        using (var stream = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => ApplicationDataMigration.EnsureRoot(root));
        Assert.False(Directory.Exists(Path.Combine(root, "FlowDeck")));
        Assert.Empty(Directory.GetDirectories(root, ".FlowDeck-migration-*"));
        Assert.Equal("original", File.ReadAllText(Path.Combine(ApplicationDataMigration.EnsureRoot(root), "settings.json")));
    }
    [Fact]
    public void BundledPythonMovesButExternalPythonSelectionIsPreserved()
    {
        var root = CreateRoot(); var current = Path.Combine(root, "FlowDeck");
        var python = Path.Combine(current, "runtime", "python", "python.exe"); Directory.CreateDirectory(Path.GetDirectoryName(python)!); File.WriteAllText(python, "placeholder");
        var legacy = Path.Combine(root, "NzxtControlHub", "runtime", "python", "python.exe");
        Assert.Equal(python, ApplicationDataMigration.RelocateBundledPython(legacy, current));
        Assert.Null(ApplicationDataMigration.RelocateBundledPython(null, current));
        var custom = Path.Combine(root, "custom-python", "python.exe");
        Assert.Equal(custom, ApplicationDataMigration.RelocateBundledPython(custom, current));
    }
    [Fact]
    public void InstanceGuardRecognizesLegacyAndNewOwnersWithoutTakingHardwareLocks()
    {
        var prefix = @"Local\FlowDeck-test-" + Guid.NewGuid().ToString("N");
        using (var old = new Mutex(true, prefix + "Global-NzxtControlHub-1E71-2022"))
        {
            using var refused = new ApplicationInstance(false, prefix); Assert.False(refused.Acquired); old.ReleaseMutex();
        }
        using var current = new ApplicationInstance(false, prefix); Assert.True(current.Acquired);
        using var duplicate = new ApplicationInstance(false, prefix); Assert.False(duplicate.Acquired);
    }
}
