using System.Text;
using System.Text.Json;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class ConfigurationGenerationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "FlowDeck-generations-" + Guid.NewGuid().ToString("N"));
    private ConfigurationGenerationStore Store() => new(directory, files =>
    {
        foreach (var data in files.Values)
        {
            using var json = JsonDocument.Parse(data);
            if (json.RootElement.GetProperty("schemaVersion").GetInt32() != 2) throw new IOException("Wrong schema");
        }
    });
    private static Dictionary<string, byte[]> Files(int value = 1) => ConfigurationGenerationStore.ComponentNames.ToDictionary(n => n,
        _ => Encoding.UTF8.GetBytes($$"""{"schemaVersion":2,"value":{{value}}}"""));
    [Fact]
    public void StagingPreservesExactLegacyBytesAndDoesNotActivateUntilCommit()
    {
        var store = Store(); var legacy = Encoding.UTF8.GetBytes("{ \"schemaVersion\": 1 }\r\n"); var files = Files();
        var plan = store.Stage(files, new Dictionary<string, byte[]> { ["settings.json"] = legacy });
        Assert.Null(store.ReadActive());
        Assert.Equal(legacy, File.ReadAllBytes(Path.Combine(directory, "generations", plan.Target.Id, "legacy", "settings.json")));
        files["devices.json"][0] = 0;
        store.Activate(plan); var active = Store().ReadActive();
        Assert.Equal(plan.Target, active!.Pointer); Assert.Equal((byte)'{', active.Files["devices.json"][0]);
        Assert.Equal(7, active.Files.Count);
    }
    [Theory]
    [InlineData("file:devices.json")] [InlineData("file:preferences.json")] [InlineData("staged")]
    public void InterruptedStagingCannotReplaceTheCurrentGeneration(string phase)
    {
        var store = Store(); var first = store.Stage(Files()); store.Activate(first);
        store.Checkpoint = point => { if (point == phase) throw new IOException("Simulated interruption"); };
        Assert.Throws<IOException>(() => store.Stage(Files(2)));
        Assert.Equal(first.Target, Store().ReadActive()!.Pointer);
    }
    [Fact]
    public void LastMomentFailureLeavesEveryOldComponentSelected()
    {
        var store = Store(); var first = store.Stage(Files()); store.Activate(first); var next = store.Stage(Files(2));
        store.Checkpoint = point => { if (point == "before-switch") throw new IOException("Simulated interruption"); };
        Assert.Throws<IOException>(() => store.Activate(next));
        var current = Store().ReadActive()!; Assert.Equal(first.Target, current.Pointer);
        Assert.All(current.Files.Values, bytes => Assert.Contains("\"value\":1", Encoding.UTF8.GetString(bytes)));
    }
    [Fact]
    public void ConcurrentActivationCannotOverwriteANewerChoice()
    {
        var store = Store(); var first = store.Stage(Files()); var competing = store.Stage(Files(2)); store.Activate(first);
        Assert.Throws<IOException>(() => store.Activate(competing)); Assert.Equal(first.Target, store.ReadActive()!.Pointer);
    }
    [Fact]
    public void CorruptComponentOrLegacyBackupPreventsActivation()
    {
        var store = Store(); var plan = store.Stage(Files(), new Dictionary<string, byte[]> { ["settings.json"] = [1, 2, 3] });
        var backup = Path.Combine(directory, "generations", plan.Target.Id, "legacy", "settings.json");
        File.WriteAllBytes(backup, [1, 2, 4]); Assert.Throws<IOException>(() => store.Activate(plan)); Assert.Null(store.ReadActive());
        var other = store.Stage(Files());
        File.WriteAllText(Path.Combine(directory, "generations", other.Target.Id, "devices.json"), "{}");
        Assert.Throws<IOException>(() => store.Activate(other)); Assert.Null(store.ReadActive());
    }
    [Fact]
    public void InvalidOrIncompleteSetsAndPathNamesAreRejectedBeforeWriting()
    {
        var store = Store(); var missing = Files(); missing.Remove("bindings.json"); Assert.Throws<IOException>(() => store.Stage(missing));
        var traversing = Files(); traversing.Remove("bindings.json"); traversing["../bindings.json"] = Encoding.UTF8.GetBytes("{}");
        Assert.Throws<IOException>(() => store.Stage(traversing));
        var duplicate = Files(); duplicate["bindings.json"] = Encoding.UTF8.GetBytes("{\"schemaVersion\":2,\"SchemaVersion\":3}");
        Assert.Throws<IOException>(() => store.Stage(duplicate));
        Assert.Throws<IOException>(() => store.Stage(Files(), new Dictionary<string, byte[]> { ["../settings.json"] = [1] }));
        Assert.False(Directory.Exists(directory));
    }
    [Fact]
    public void CorruptCurrentPointerNeverFallsBackSilently()
    {
        var store = Store(); var plan = store.Stage(Files()); store.Activate(plan);
        File.WriteAllText(Path.Combine(directory, "current.json"), "{}"); Assert.Throws<IOException>(() => store.ReadActive());
    }
    [Fact]
    public void AtomicActivationAndExplicitRollbackRetainBothValidatedGenerations()
    {
        var store = Store(); var first = store.Stage(Files()); store.Activate(first); var second = store.Stage(Files(2)); store.Activate(second);
        Assert.Equal(second.Target, store.ReadActive()!.Pointer);
        var rollback = store.PrepareRollback(first.Target); Assert.Equal(second.Target, rollback.ExpectedCurrent); store.Activate(rollback);
        Assert.Equal(first.Target, Store().ReadActive()!.Pointer);
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
