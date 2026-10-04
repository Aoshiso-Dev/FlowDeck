using System.Text;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class SettingsTransferTests
{
    private static readonly string GroupId = "11111111111111111111111111111111";
    private static readonly string SceneId = "22222222222222222222222222222222";
    private static string Folder() { var path = Path.Combine(Path.GetTempPath(), "FlowDeck-transfer-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static TransferContent Content()
    {
        var scene = new LightingScene(SceneId, "デスク", [new("demo", "led1", HubLighting.Preset("blue"))], [new("board-identity", 2, new(2, 1, 2, 3))]);
        return new(SettingsTransfer.Portable(DemoHub.CreateSettings()), new(Scenes: [scene]),
            new(Groups: [new(GroupId, "ケース", [new("demo", "led1")], [new("board-identity", 2)])], SyncTargets: [new("board-identity", 2)]),
            new(Names: []), new(Rules: []), new(["#12ABFF"], ["#334455"]), new() { ["all"] = new(BaseScene: scene), [GroupId] = new(BaseScene: scene, Brightness: 40, IsOff: true) },
            new("ASRock X870E Taichi White", ["demo/cpu", "demo/gpu"], [new("board-identity", 2)]));
    }
    private static void Save(string folder, TransferContent value, AppSettings? local = null)
    { foreach (var (file, bytes) in SettingsTransfer.ToFiles(value, local ?? new())) File.WriteAllBytes(Path.Combine(folder, file), bytes); }
    private static Dictionary<string, byte[]> Files(string folder) => Directory.EnumerateFiles(folder, "*.json").ToDictionary(p => Path.GetFileName(p)!, File.ReadAllBytes);
    private static void Same(Dictionary<string, byte[]> a, Dictionary<string, byte[]> b)
    { Assert.Equal(a.Keys.Order(), b.Keys.Order()); foreach (var (key, bytes) in a) Assert.Equal(bytes, b[key]); }
    private static SettingsArchive Changed(TransferContent source) => SettingsTransfer.Create(source with
    { Settings = source.Settings with { Ui = source.Settings.Ui with { Theme = "dark" }, Limits = source.Settings.Limits with { Minimum = 21 } }, Palette = new(["#00EEFF"], []) });

    [Fact] public void AllConfigurationSectionsRoundTripWithoutSecretsRuntimeAcknowledgementsOrExecutablePaths()
    {
        var folder = Folder(); var content = Content(); var settings = DemoHub.CreateSettings() with
        { PythonPath = @"C:\private\python.exe", M5 = new("COM14", "private-device", "B", "auto", "192.168.1.19", "private-interface"), Startup = new(true, true), LastConfirmedPolicy = new(FanMode.Manual, 50), Ui = new("light", 40, 60) };
        Save(folder, content, settings);
        File.WriteAllText(Path.Combine(folder, "m5-wifi.protected"), "DO-NOT-READ-SECRET");
        // Keep the protected file exclusively locked: export must not even open it.
        using var secret = File.Open(Path.Combine(folder, "m5-wifi.protected"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var destination = Path.Combine(Folder(), "settings.flowdeck.json"); var archive = new SettingsTransferStore(folder).Export(destination);
        var json = File.ReadAllText(destination);
        foreach (var forbidden in new[] { "DO-NOT-READ-SECRET", "private", "COM14", "192.168.1.19" }) Assert.DoesNotContain(forbidden, json);
        Assert.False(archive.Content.Settings.Hardware.WiringConfirmed); Assert.Null(archive.Content.Settings.LastConfirmedPolicy); Assert.Null(archive.Content.Settings.PythonPath);
        Assert.Equal(SettingsTransfer.Canonical(archive), SettingsTransfer.Canonical(SettingsTransfer.Read(File.ReadAllBytes(destination))));
        Assert.Single(archive.Content.Scenes.Scenes!); Assert.Single(archive.Content.Groups.Groups!); Assert.Equal(2, archive.Content.Adjustments.Count);
        Assert.Equal("#12ABFF", archive.Content.Palette.Favorites.Single());
    }
    [Fact] public void DefaultIncompleteSetupAndThreeEmptyProfilesCanBeTransferred()
    {
        var defaults = SettingsTransfer.FromFiles(new Dictionary<string, byte[]>()); var archive = SettingsTransfer.Create(defaults);
        Assert.Null(archive.Content.Settings.Limits.Minimum); Assert.All(archive.Content.Settings.Profiles, p => Assert.Null(p.Cpu));
        Assert.NotNull(SettingsTransfer.Read(JsonSerializer.SerializeToUtf8Bytes(archive, Contract.Json)));
    }
    [Fact] public void ServiceRestoreCommitsItsPendingMarkerAtomicallyAndNeverExportsIt()
    {
        var folder = Folder(); Save(folder, Content()); var store = new SettingsTransferStore(folder);
        store.Schedule(store.Preview(Changed(Content()))); Assert.True(store.ApplyAtStartup(service: true).Restored);
        var restored = JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(Path.Combine(folder, "settings.json")), Contract.Json)!;
        Assert.True(restored.ServiceConfigurationPending); Assert.Equal(21, restored.Limits.Minimum); Assert.False(restored.Hardware.WiringConfirmed);
        Assert.False(store.Capture().Settings.ServiceConfigurationPending);
        Assert.DoesNotContain("serviceConfigurationPending", JsonSerializer.Serialize(SettingsTransfer.Create(store.Capture()), Contract.Json));
        Assert.False(store.ApplyAtStartup(service: true).Attempted);
        Assert.True(JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(Path.Combine(folder, "settings.json")), Contract.Json)!.ServiceConfigurationPending);
    }
    [Fact] public void ChangedChecksumUnknownVersionUnknownFieldsDuplicatePropertiesAndOversizeAreRejected()
    {
        var archive = SettingsTransfer.Create(Content());
        Assert.Throws<IOException>(() => SettingsTransfer.Read(JsonSerializer.SerializeToUtf8Bytes(archive with { Sha256 = "bad" }, Contract.Json)));
        Assert.Throws<IOException>(() => SettingsTransfer.Read(JsonSerializer.SerializeToUtf8Bytes(archive with { Version = 9 }, Contract.Json)));
        var json = JsonSerializer.Serialize(archive, Contract.Json);
        Assert.Throws<IOException>(() => SettingsTransfer.Read(Encoding.UTF8.GetBytes(json.Insert(1, "\"unknown\":true,"))));
        Assert.Throws<IOException>(() => SettingsTransfer.Read(Encoding.UTF8.GetBytes(json.Insert(1, "\"Version\":1,"))));
        Assert.Throws<IOException>(() => SettingsTransfer.Read(new byte[SettingsTransfer.MaximumBytes + 1]));
    }
    [Fact] public void InvalidFanCurvesLimitsAndPortableBoundaryFieldsAreRejectedEvenWithRecomputedHash()
    {
        var c = Content();
        foreach (var s in new[] { c.Settings with { PythonPath = "evil.exe" }, c.Settings with { Startup = new(true, true) },
            c.Settings with { M5 = new(Port: "COM2") }, c.Settings with { LastConfirmedPolicy = new(FanMode.Manual, 90) },
            c.Settings with { Limits = c.Settings.Limits with { Minimum = 0 } }, c.Settings with { Ui = new("unsupported") },
            c.Settings with { Hardware = c.Settings.Hardware with { Fans = ["fan1", "fan1"] } },
            c.Settings with { Profiles = [c.Settings.Profiles[0] with { Cpu = new("cpu", [new(40, 90), new(30, 80)], 90, 75) }, c.Settings.Profiles[1], c.Settings.Profiles[2]] } })
            Assert.Throws<IOException>(() => SettingsTransfer.Create(c with { Settings = s }));
    }
    [Fact] public void InvalidCollectionsColorsAndGroupBrightnessReferencesAreRejected()
    {
        var c = Content();
        Assert.Throws<IOException>(() => SettingsTransfer.Create(c with { Palette = new(["#000000", "#000000"], []) }));
        Assert.Throws<IOException>(() => SettingsTransfer.Create(c with { Groups = new(Groups: [c.Groups.Groups![0], c.Groups.Groups[0]]) }));
        Assert.Throws<IOException>(() => SettingsTransfer.Create(c with { Adjustments = new() { [GroupId] = c.Adjustments[GroupId] with { Applied = true } } }));
        Assert.Throws<IOException>(() => SettingsTransfer.Create(c with { Adjustments = new() { [Guid.NewGuid().ToString("N")] = new() } }));
        Assert.Throws<IOException>(() => SettingsTransfer.Create(c with { Adjustments = new() { [GroupId] = new(BaseScene: c.Scenes.Scenes![0] with { Board = [] }) } }));
    }
    [Fact] public void DiffReportsBeforeAndAfterAndMismatchedDevicesRemainUnbound()
    {
        var c = Content(); var changed = Changed(c).Content;
        var diff = SettingsTransfer.Differences(c, changed);
        Assert.Contains(diff, d => d.Before == "21" || d.After == "21"); Assert.Contains(diff, d => d.After == "ダーク");
        var local = c with { Settings = c.Settings with { Hardware = c.Settings.Hardware with { Hub = new("different", "another", null, "path") } }, Devices = new("Other Board", [], []) };
        var warnings = SettingsTransfer.CheckDevices(c, local);
        Assert.Contains(warnings, w => w.Contains("異なる機器ID")); Assert.Contains(warnings, w => w.Contains("未確認のマザー照明"));
        Assert.Equal("demo", c.Settings.Hardware.Hub!.Id); Assert.Equal("board-identity", c.Scenes.Scenes![0].Board[0].Identity);
        Assert.Contains(warnings, w => w == "未取得のセンサー：demo/cpu");
    }
    [Fact] public async Task StalePreviewAndStaleScheduledRestoreCannotOverwriteLaterChanges()
    {
        var folder = Folder(); var c = Content(); Save(folder, c); var store = new SettingsTransferStore(folder);
        var preview = store.Preview(Changed(c)); var baseline = new JsonSettingsStore(folder);
        await baseline.SaveAsync((await baseline.LoadAsync()) with { Ui = new("dark", Width: 1250) });
        Assert.Throws<IOException>(() => store.Schedule(preview)); Assert.False(store.HasPending);
        preview = store.Preview(Changed(c)); store.Schedule(preview);
        await baseline.SaveAsync((await baseline.LoadAsync()) with { Ui = new("light", Width: 1400) });
        var before = Files(folder); var result = store.ApplyAtStartup();
        Assert.False(result.Restored); Assert.Contains("変わった", result.Message); Assert.False(store.HasPending); Same(before, Files(folder));
    }
    [Fact] public void SchedulingAndCancellingDoNotModifyAnyLiveConfiguration()
    {
        var folder = Folder(); var c = Content(); Save(folder, c); var before = Files(folder); var store = new SettingsTransferStore(folder);
        store.Schedule(store.Preview(Changed(c))); Assert.True(store.HasPending); Same(before, Files(folder));
        Assert.Throws<IOException>(() => store.Schedule(store.Preview(Changed(c)))); store.Cancel(); Assert.False(store.HasPending); Same(before, Files(folder));
        Assert.False(store.ApplyAtStartup().Attempted);
    }
    [Fact] public async Task StartupRestoresSettingsAsDraftsPreservesLocalEnvironmentAndCreatesUsableBackup()
    {
        var folder = Folder(); var c = Content(); Save(folder, c, new AppSettings { PythonPath = "local-python.exe", M5 = new("COM14", "local-id", "A", "auto", "192.168.1.19"), Startup = new(true, true), Ui = new(Left: 44, Top: 66) });
        var store = new SettingsTransferStore(folder); store.Schedule(store.Preview(Changed(c))); var result = store.ApplyAtStartup();
        Assert.True(result.Restored); Assert.False(store.HasPending); Assert.True(File.Exists(result.BackupPath));
        Assert.Equal("#12ABFF", SettingsTransfer.Read(File.ReadAllBytes(result.BackupPath!)).Content.Palette.Favorites.Single());
        var current = await new JsonSettingsStore(folder).LoadAsync();
        Assert.Equal("dark", current.Ui.Theme); Assert.Equal(21, current.Limits.Minimum); Assert.False(current.Hardware.WiringConfirmed);
        Assert.False(current.CpuFan.WiringConfirmed); Assert.All(current.BoardFans.Values, f => Assert.False(f.WiringConfirmed)); Assert.Null(current.LastConfirmedPolicy);
        Assert.True(current.Startup.Login); Assert.False(current.Startup.ResumeControl); Assert.Equal("COM14", current.M5.Port); Assert.Equal("local-python.exe", current.PythonPath);
        Assert.Equal(44, current.Ui.Left); Assert.Equal(66, current.Ui.Top);
        Assert.False((await new LightingAdjustmentStore(folder, GroupId).LoadAsync()).Applied); Assert.False(store.ApplyAtStartup().Attempted);
    }
    [Theory] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void FailureAtEveryWriteBoundaryRestoresExactOriginalBytes(int failAfter)
    {
        var folder = Folder(); var c = Content(); Save(folder, c); var before = Files(folder);
        var store = new SettingsTransferStore(folder) { AfterFileWritten = count => { if (count == failAfter) throw new IOException("injected disk failure"); } };
        store.Schedule(store.Preview(Changed(c))); var result = store.ApplyAtStartup();
        Assert.False(result.Restored); Assert.Contains("元の設定", result.Message); Same(before, Files(folder)); Assert.False(store.HasPending);
    }
    private sealed class SimulatedProcessExit : Exception;
    [Fact] public void InterruptedTransactionRollsBackBeforeNextStartupAndCannotAutomaticallyRetry()
    {
        var folder = Folder(); var c = Content(); Save(folder, c); var before = Files(folder);
        var store = new SettingsTransferStore(folder) { AfterFileWritten = count => { if (count == 4) throw new SimulatedProcessExit(); } };
        store.Schedule(store.Preview(Changed(c))); Assert.Throws<SimulatedProcessExit>(() => store.ApplyAtStartup());
        Assert.True(File.Exists(Path.Combine(folder, "settings-transfer", "journal.json")));
        var reopened = new SettingsTransferStore(folder); var result = reopened.ApplyAtStartup();
        Assert.True(result.Attempted); Assert.False(result.Restored); Same(before, Files(folder)); Assert.False(reopened.HasPending); Assert.False(reopened.ApplyAtStartup().Attempted);
    }
    [Fact] public void TamperedJournalAndPathTraversalStopBeforeWritingOutsideAllowlist()
    {
        var folder = Folder(); var c = Content(); Save(folder, c); var before = Files(folder);
        var store = new SettingsTransferStore(folder) { AfterFileWritten = _ => throw new SimulatedProcessExit() };
        store.Schedule(store.Preview(Changed(c))); Assert.Throws<SimulatedProcessExit>(() => store.ApplyAtStartup());
        var journalPath = Path.Combine(folder, "settings-transfer", "journal.json");
        var journal = JsonSerializer.Deserialize<SettingsRestoreJournal>(File.ReadAllText(journalPath), Contract.Json)!;
        journal.Before["../outside.json"] = [1, 2]; journal = journal with { Sha256 = SettingsTransfer.Hash(journal.Before) };
        File.WriteAllText(journalPath, JsonSerializer.Serialize(journal, Contract.Json)); var partiallyWritten = Files(folder);
        Assert.Throws<IOException>(() => new SettingsTransferStore(folder).ApplyAtStartup()); Same(partiallyWritten, Files(folder));
        Assert.False(File.Exists(Path.Combine(folder, "..", "outside.json")));
        foreach (var file in new[] { "../settings.json", "m5-wifi.protected", "lighting-adjustment-../.json", "settings.json:stream" }) Assert.False(SettingsTransferStore.Allowed(file));
    }
    [Fact] public void CorruptCurrentFilesAreNotSilentlyReplacedWithDefaultsDuringExportOrRestore()
    {
        var folder = Folder(); Save(folder, Content()); File.WriteAllText(Path.Combine(folder, "notifications.json"), "{broken");
        var store = new SettingsTransferStore(folder); Assert.Throws<IOException>(() => store.Capture()); Assert.Throws<IOException>(() => store.Preview(Changed(Content())));
        Assert.Equal("{broken", File.ReadAllText(Path.Combine(folder, "notifications.json"))); Assert.False(store.HasPending);
    }
    [Fact] public void AnExportCannotOverwriteTheSettingsDirectoryAndRawRuntimeChangesDoNotInvalidatePortableFingerprint()
    {
        var folder = Folder(); Save(folder, Content()); var store = new SettingsTransferStore(folder);
        Assert.Throws<IOException>(() => store.Export(Path.Combine(folder, "settings.json")));
        var s = DemoHub.CreateSettings(); var raw = new Dictionary<string, byte[]> { ["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(s, Contract.Json) };
        var before = SettingsTransfer.Fingerprint(SettingsTransfer.FromFiles(raw));
        raw["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(s with { LastConfirmedPolicy = new(FanMode.Manual, 70), Ui = s.Ui with { Left = 190, Top = 55 }, Startup = new(true, true) }, Contract.Json);
        Assert.Equal(before, SettingsTransfer.Fingerprint(SettingsTransfer.FromFiles(raw)));
    }
}
