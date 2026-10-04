using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class ConfigurationRetentionTests
{
    private const string Installation = "0123456789abcdef0123456789abcdef";
    private static ConfigurationGenerationStore Store(string path) => new(path, _ => { });
    private static Dictionary<string, byte[]> Files(int value) => ConfigurationGenerationStore.ComponentNames.ToDictionary(n => n,
        _ => Encoding.UTF8.GetBytes($$"""{"schemaVersion":2,"value":{{value}}}"""));
    private static string Root(string path, ConfigurationPointer pointer) => Path.Combine(path, "generations", pointer.Id);
    private static ConfigurationPointer Add(ConfigurationGenerationStore store, int value, bool maintain = true)
    {
        var staged = store.Stage(Files(value)); store.Activate(staged);
        if (maintain) Assert.Empty(store.MaintainHistory(staged.Target).Errors);
        return staged.Target;
    }
    [Fact] public void NormalEditsKeepRecentAdoptedGenerationsAndNeverCollectUnadoptedOrUnknownFiles()
    {
        using var dir = new ServiceTestDirectory(); var store = Store(dir.Path);
        var unadopted = store.Stage(Files(-1)); var unknown = Path.Combine(dir.Path, "generations", "unrecognized");
        Directory.CreateDirectory(unknown); File.WriteAllText(Path.Combine(unknown, "keep.txt"), "user data");
        var pointers = Enumerable.Range(0, 30).Select(i => Add(store, i)).ToArray();
        Assert.Equal(12, Directory.GetDirectories(Path.Combine(dir.Path, "generations")).Length);
        Assert.All(pointers[..20], p => Assert.False(Directory.Exists(Root(dir.Path, p))));
        Assert.All(pointers[20..], p => Assert.Equal(p, store.Read(p).Pointer));
        Assert.Equal(unadopted.Target, store.Read(unadopted.Target).Pointer);
        Assert.Equal("user data", File.ReadAllText(Path.Combine(unknown, "keep.txt")));
        Assert.Equal(pointers[^1], store.ReadActive()!.Pointer);
    }
    [Theory] [InlineData("applied")] [InlineData("rolled-back")] [InlineData("failed")] [InlineData("interrupted")] [InlineData("superseded")]
    public void ApplicationReferencesRemainProtectedBeyondTheRecentHistory(string phase)
    {
        using var dir = new ServiceTestDirectory(); var store = Store(dir.Path);
        var first = Add(store, 0); var second = Add(store, 1);
        ServiceEnvironment.Write(Path.Combine(dir.Path, "configuration-application.json"), new ConfigurationApplication(1, Guid.NewGuid().ToString("N"), first, second, phase));
        var rest = Enumerable.Range(2, 16).Select(i => Add(store, i)).ToArray();
        Assert.Equal(first, store.Read(first).Pointer); Assert.Equal(second, store.Read(second).Pointer);
        Assert.Equal(12, Directory.GetDirectories(Path.Combine(dir.Path, "generations")).Length);
        Assert.Equal(rest[^1], store.ReadActive()!.Pointer);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void PendingApplyOrProviderRecoveryDefersRemovalButKeepsAdoptionHistory(bool provider)
    {
        using var dir = new ServiceTestDirectory(); var store = Store(dir.Path);
        var first = Add(store, 0); var second = Add(store, 1);
        var journal = Path.Combine(dir.Path, provider ? "provider-transition.json" : "configuration-application.json");
        if (provider) File.WriteAllText(journal, "{}");
        else ServiceEnvironment.Write(journal, new ConfigurationApplication(1, Guid.NewGuid().ToString("N"), first, second, "accepted"));
        var active = second;
        for (var i = 2; i < 24; i++) active = Add(store, i);
        Assert.Equal(24, Directory.GetDirectories(Path.Combine(dir.Path, "generations")).Length);
        Assert.NotNull(store.MaintainHistory(active).Deferred); File.Delete(journal);
        var result = store.MaintainHistory(active); Assert.Equal(4, result.Removed); Assert.Equal(20, result.Tracked);
        for (var i = 0; i < 3; i++) Assert.Empty(store.MaintainHistory(active).Errors);
        Assert.Equal(10, Directory.GetDirectories(Path.Combine(dir.Path, "generations")).Length);
        Assert.Equal(active, store.ReadActive()!.Pointer);
    }
    [Theory] [InlineData("retention-prepared")] [InlineData("retention-file:bindings.json")]
    [InlineData("retention-file:manifest.json")] [InlineData("retention-removed")]
    public void CleanupInterruptionResumesFromDurableExactFilePlan(string stop)
    {
        using var dir = new ServiceTestDirectory(); var store = Store(dir.Path);
        var pointers = Enumerable.Range(0, 10).Select(i => Add(store, i)).ToArray();
        var active = Add(store, 10, maintain: false);
        store.Checkpoint = point => { if (point == stop) throw new IOException("simulated cleanup interruption"); };
        Assert.Single(store.MaintainHistory(active).Errors);
        Assert.Equal(active, store.ReadActive()!.Pointer); Assert.Equal(pointers[^1], store.Read(pointers[^1]).Pointer);
        var restarted = Store(dir.Path); var resumed = restarted.MaintainHistory(active);
        Assert.Empty(resumed.Errors); Assert.Equal(1, resumed.Removed); Assert.False(Directory.Exists(Root(dir.Path, pointers[0])));
        Assert.Equal(active, restarted.ReadActive()!.Pointer); Assert.Equal(10, Directory.GetDirectories(Path.Combine(dir.Path, "generations")).Length);
    }
    [Fact] public void UnknownFilesChangedDataAndNonemptyFoldersAreNotPartiallyDeleted()
    {
        using var dir = new ServiceTestDirectory(); var store = Store(dir.Path);
        var pointers = Enumerable.Range(0, 10).Select(i => Add(store, i)).ToArray();
        File.WriteAllText(Path.Combine(Root(dir.Path, pointers[0]), "unexpected.txt"), "keep");
        File.WriteAllText(Path.Combine(Root(dir.Path, pointers[1]), "devices.json"), "changed");
        Directory.CreateDirectory(Path.Combine(Root(dir.Path, pointers[2]), "extra"));
        var pending = Path.Combine(dir.Path, "provider-transition.json"); File.WriteAllText(pending, "{}");
        var active = pointers[^1]; for (var i = 10; i < 13; i++) active = Add(store, i);
        File.Delete(pending);
        var result = store.MaintainHistory(active); Assert.Equal(3, result.Errors.Length); Assert.Equal(0, result.Removed);
        Assert.All(pointers[..3], p => Assert.True(File.Exists(Path.Combine(Root(dir.Path, p), "manifest.json"))));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Root(dir.Path, pointers[0]), "unexpected.txt")));
        Assert.Equal("changed", File.ReadAllText(Path.Combine(Root(dir.Path, pointers[1]), "devices.json")));
        Assert.Equal(active, store.ReadActive()!.Pointer);
    }
    [Theory] [InlineData("history")] [InlineData("previous")] [InlineData("application")]
    public void BrokenRetentionOrRecoveryMetadataPreventsDeletion(string broken)
    {
        using var dir = new ServiceTestDirectory(); var store = Store(dir.Path);
        _ = Enumerable.Range(0, 10).Select(i => Add(store, i)).ToArray(); var active = Add(store, 10, maintain: false);
        var path = broken switch { "history" => "generation-retention.json", "previous" => "current.json.bak", _ => "configuration-application.json" };
        File.WriteAllText(Path.Combine(dir.Path, path), "{}"); Assert.Throws<IOException>(() => store.MaintainHistory(active));
        Assert.Equal(11, Directory.GetDirectories(Path.Combine(dir.Path, "generations")).Length);
    }
    [Fact] public void DeletionPlanCannotPointOutsideItsGenerationEvenWithARecomputedDocumentHash()
    {
        using var dir = new ServiceTestDirectory(); var store = Store(dir.Path); var active = Add(store, 1);
        var path = Path.Combine(dir.Path, "generation-retention.json"); var json = JsonNode.Parse(File.ReadAllText(path))!;
        var entry = json["state"]!["entries"]![0]!;
        var files = ConfigurationGenerationStore.ComponentNames.ToDictionary(n => n, _ => new string('a', 64));
        files.Add("manifest.json", active.ManifestHash); files.Add("../../outside.txt", new string('a', 64));
        entry["deleting"] = JsonSerializer.SerializeToNode(files, Contract.Json);
        json["sha256"] = SettingsTransfer.Hash(json["state"]!.Deserialize<JsonElement>());
        File.WriteAllText(path, json.ToJsonString()); var outside = Path.Combine(dir.Path, "outside.txt"); File.WriteAllText(outside, "keep");
        Assert.Throws<IOException>(() => store.MaintainHistory(active)); Assert.Equal("keep", File.ReadAllText(outside));
        Assert.Equal(active, store.ReadActive()!.Pointer);
    }
    [Fact] public void RollbackSnapshotsRemainIndependentAfterAnOldGenerationIsCollected()
    {
        using var dir = new ServiceTestDirectory(); var main = Path.Combine(dir.Path, "main"); var backup = Path.Combine(dir.Path, "backup");
        var store = Store(main); var first = Add(store, 0); Assert.Equal(first, store.SnapshotActiveTo(backup));
        var active = first; for (var i = 1; i < 15; i++) active = Add(store, i);
        Assert.False(Directory.Exists(Root(main, first))); Assert.Equal(first, Store(backup).ReadActive()!.Pointer);
        store.RestoreSnapshotFrom(backup, first); Assert.Empty(store.MaintainHistory(first).Errors);
        Assert.Equal(first, store.ReadActive()!.Pointer); Assert.Equal(active, store.Read(active).Pointer);
        Assert.Equal(first, Store(backup).ReadActive()!.Pointer);
    }
    private sealed class Log : IEventLog
    {
        public bool Fail;
        public readonly List<string> Categories = [];
        public void Write(string category, object data) { Categories.Add(category); if (Fail) throw new IOException("log unavailable"); }
    }
    [Fact] public async Task AuthorityMaintainsHistoryOnStartupAndAfterSavingWithoutFailingCommittedSettings()
    {
        using var dir = new ServiceTestDirectory(); var store = DeviceConfigurationCodec.Store(dir.Path);
        var initial = store.Stage(DeviceConfigurationCodec.Write(ServiceRuntimeBootstrap.MonitoringConfiguration(Installation))); store.Activate(initial);
        var log = new Log(); var authority = new DeviceConfigurationAuthority(store, Installation, store.ReadActive()!, retentionLog: log);
        Assert.NotNull(authority.LastRetention); Assert.True(File.Exists(Path.Combine(dir.Path, "generation-retention.json")));
        for (var i = 0; i < 14; i++)
        {
            var reply = await authority.SaveLightingLibraryAsync(new(LightingLibraryOperation.RememberColors,
                authority.Generation.Id, "palette", authority.Configuration.Lighting.Revision, Colors: [new(i, 34, 51)]), default);
            Assert.Equal(Outcome.Confirmed, reply.Outcome);
        }
        Assert.Equal(10, Directory.GetDirectories(Path.Combine(dir.Path, "generations")).Length); Assert.Contains("configuration-retention", log.Categories);
        File.WriteAllText(Path.Combine(dir.Path, "generation-retention.json"), "{}"); log.Fail = true;
        var last = await authority.SaveLightingLibraryAsync(new(LightingLibraryOperation.RememberColors,
            authority.Generation.Id, "palette", authority.Configuration.Lighting.Revision, Colors: [new(254, 34, 51)]), default);
        Assert.Equal(Outcome.Confirmed, last.Outcome); Assert.NotEmpty(authority.LastRetention!.Errors);
        Assert.Equal(authority.Generation, store.ReadActive()!.Pointer);
        Assert.Equal("#FE2233", authority.Configuration.Preferences.Palette.Recent[0]);
    }
}
