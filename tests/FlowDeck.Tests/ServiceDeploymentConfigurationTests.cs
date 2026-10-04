using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class ServiceDeploymentTests
{
    private static ConfigurationGenerationStore ConfigStore(Fixture f) => DeviceConfigurationCodec.Store(ServiceRuntimeSelection.ConfigurationDirectory(f.Data));
    private static async Task<ConfigurationGeneration> InstallDevices(Fixture f)
    {
        var package = f.Package("2.0.0", devices: true);
        await f.Engine().ApplyAsync("install", package.Root, package.ManifestHash, [ServiceFixture.Sid], runtime: "devices-v2");
        ChangeDeviceSettings(f, "dark");
        return ConfigStore(f).ReadActive()!;
    }
    private static void ChangeDeviceSettings(Fixture f, string theme)
    {
        var store = ConfigStore(f); var config = DeviceConfigurationCodec.Read(store.ReadActive()!.Files);
        store.Activate(store.Stage(DeviceConfigurationCodec.Write(config with { Preferences = config.Preferences with { Ui = new(theme) } })));
    }
    private static void AssertSameConfiguration(Fixture f, ConfigurationGeneration expected)
    {
        var actual = ConfigStore(f).ReadActive()!; Assert.Equal(expected.Pointer, actual.Pointer);
        foreach (var (name, bytes) in expected.Files) Assert.Equal(bytes, actual.Files[name]);
        Assert.Equal("dark", ServiceRuntimeSelection.Read(f.Data)!.ReadConfiguration(f.Data).Preferences.Ui.Theme);
    }
    private static string SnapshotDirectory(Fixture f, string id) => Path.Combine(f.Data, "deployment-backups", id, ServiceDeployment.ConfigurationSnapshotDirectory);
    [Fact] public async Task SuccessfulDeviceUpdatePreservesTheExactAdoptedGenerationAndItsVerifiedBackup()
    {
        using var f = new Fixture(); var before = await InstallDevices(f);
        var result = await f.Apply("update", f.Package("2.1.0", devices: true));
        Assert.Equal("committed", result.Outcome); AssertSameConfiguration(f, before);
        var backup = DeviceConfigurationCodec.Store(SnapshotDirectory(f, result.Id)).ReadActive()!;
        Assert.Equal(before.Pointer, backup.Pointer);
        foreach (var (name, bytes) in before.Files) Assert.Equal(bytes, backup.Files[name]);
        Assert.Equal("2.1.0", f.InstalledVersion()); Assert.True(f.Scm.Registration.Running);
    }
    [Theory] [InlineData("start")] [InlineData("activate")]
    public async Task FailedDeviceUpdateRestoresSettingsChangedByTheNewService(string failure)
    {
        using var f = new Fixture(); var before = await InstallDevices(f);
        ConfigurationPointer? rejected = null;
        f.Scm.BeforeStartOnce = () => { ChangeDeviceSettings(f, "light"); rejected = ConfigStore(f).ReadActivePointer(); };
        f.Scm.FailOnce = failure;
        await Assert.ThrowsAsync<IOException>(() => f.Apply("update", f.Package("2.1.0", devices: true)));
        Assert.NotEqual(before.Pointer, rejected); AssertSameConfiguration(f, before);
        Assert.Equal("light", DeviceConfigurationCodec.Read(ConfigStore(f).Read(rejected!).Files).Preferences.Ui.Theme);
        Assert.Equal("2.0.0", f.InstalledVersion()); Assert.True(f.Scm.Registration.Running); Assert.Null(ServiceDeployment.ReadPending(f.Data));
    }
    [Theory] [InlineData("current")] [InlineData("manifest")] [InlineData("component")] [InlineData("missing")]
    public async Task FailedStartupCanRestoreCorruptOrMissingGenerationDataWithoutDiscardingEvidence(string damage)
    {
        using var f = new Fixture(); var before = await InstallDevices(f); var store = ConfigStore(f);
        var root = Path.Combine(store.DirectoryPath, "generations", before.Pointer.Id);
        var file = damage switch { "current" => Path.Combine(store.DirectoryPath, "current.json"),
            "manifest" => Path.Combine(root, "manifest.json"), _ => Path.Combine(root, "cooling.json") };
        f.Scm.BeforeStartOnce = () => { if (damage == "missing") File.Delete(file); else File.WriteAllText(file, "damaged by new version"); };
        f.Scm.FailOnce = "start";
        await Assert.ThrowsAsync<IOException>(() => f.Apply("update", f.Package("2.1.0", devices: true)));
        AssertSameConfiguration(f, before); Assert.True(f.Scm.Registration.Running); Assert.Equal("2.0.0", f.InstalledVersion());
        if (damage == "current") Assert.Equal("damaged by new version", File.ReadAllText(Path.Combine(store.DirectoryPath, "current.json.bak")));
        else Assert.Single(Directory.GetDirectories(Path.Combine(store.DirectoryPath, "rejected-generations")));
    }
    [Theory]
    [InlineData("configuration-restore-staged")] [InlineData("configuration-restore-quarantined")]
    [InlineData("configuration-restore-generation")] [InlineData("configuration-restore-before-switch")]
    [InlineData("configuration-restore-switched")]
    public async Task InterruptedConfigurationRestorationCanRetryFromItsVerifiedBackup(string boundary)
    {
        using var f = new Fixture(); var before = await InstallDevices(f);
        f.Scm.BeforeStartOnce = () =>
        {
            File.WriteAllText(Path.Combine(ConfigStore(f).DirectoryPath, "generations", before.Pointer.Id, "cooling.json"), "damaged");
            File.WriteAllText(Path.Combine(ConfigStore(f).DirectoryPath, "current.json"), "damaged pointer");
        };
        f.Scm.FailOnce = "start"; var engine = f.Engine();
        engine.AfterStep = step => { if (step == boundary) throw new ServiceDeploymentCrash(); };
        await Assert.ThrowsAsync<AggregateException>(() => f.Apply("update", f.Package("2.1.0", devices: true), engine));
        Assert.NotNull(ServiceDeployment.ReadPending(f.Data)); Assert.False(f.Scm.Registration.Running);
        await f.Engine().RecoverAsync(); AssertSameConfiguration(f, before);
        Assert.Equal("2.0.0", f.InstalledVersion()); Assert.True(f.Scm.Registration.Running); Assert.Null(ServiceDeployment.ReadPending(f.Data));
    }
    [Theory] [InlineData("configuration-backed-up")] [InlineData("backed-up")] [InlineData("started")] [InlineData("committed")]
    public async Task AbruptDeviceUpdateRetainsTheOriginalGenerationUnlessAlreadyCommitted(string boundary)
    {
        using var f = new Fixture(); var before = await InstallDevices(f);
        f.Scm.BeforeStartOnce = () => ChangeDeviceSettings(f, "light"); var engine = f.Engine();
        engine.AfterStep = step => { if (step == boundary) throw new ServiceDeploymentCrash(); };
        await Assert.ThrowsAsync<ServiceDeploymentCrash>(() => f.Apply("update", f.Package("2.1.0", devices: true), engine));
        f.Scm.BeforeStartOnce = null; // Earlier boundaries never started the candidate.
        await f.Engine().RecoverAsync();
        if (boundary == "committed") Assert.Equal("light", ServiceRuntimeSelection.Read(f.Data)!.ReadConfiguration(f.Data).Preferences.Ui.Theme);
        else AssertSameConfiguration(f, before);
        Assert.Equal(boundary == "committed" ? "2.1.0" : "2.0.0", f.InstalledVersion());
        Assert.True(f.Scm.Registration.Running); Assert.Null(ServiceDeployment.ReadPending(f.Data));
    }
    [Theory]
    [InlineData("devices.json")] [InlineData("bindings.json")] [InlineData("cooling.json")] [InlineData("lighting.json")]
    [InlineData("dashboard.json")] [InlineData("preferences.json")] [InlineData("migration.json")]
    [InlineData("manifest.json")] [InlineData("current.json")] [InlineData("missing-sidecar")]
    public async Task IncompleteOrChangedDeviceBackupBlocksRollbackBeforeMovingBinariesOrSettings(string damaged)
    {
        using var f = new Fixture(); var before = await InstallDevices(f); var engine = f.Engine();
        engine.AfterStep = step => { if (step == "new-moved") throw new ServiceDeploymentCrash(); };
        await Assert.ThrowsAsync<ServiceDeploymentCrash>(() => f.Apply("update", f.Package("2.1.0", devices: true), engine));
        var state = ServiceDeployment.ReadPending(f.Data)!; var backup = SnapshotDirectory(f, state.Id);
        var path = damaged switch { "current.json" => Path.Combine(backup, damaged),
            "missing-sidecar" => Path.Combine(f.Data, "deployment-backups", state.Id, ServiceDeployment.ConfigurationSnapshotName),
            _ => Path.Combine(backup, "generations", before.Pointer.Id, damaged) };
        var saved = File.ReadAllBytes(path);
        if (damaged == "missing-sidecar") File.Delete(path); else File.AppendAllText(path, "changed");
        await Assert.ThrowsAnyAsync<Exception>(() => f.Engine().RecoverAsync());
        Assert.Equal("2.1.0", f.InstalledVersion()); AssertSameConfiguration(f, before);
        Assert.False(f.Scm.Registration.Running); Assert.NotNull(ServiceDeployment.ReadPending(f.Data));
        File.WriteAllBytes(path, saved); await f.Engine().RecoverAsync();
        Assert.Equal("2.0.0", f.InstalledVersion()); AssertSameConfiguration(f, before); Assert.True(f.Scm.Registration.Running);
    }
    [Theory] [InlineData("accepted")] [InlineData("provider-transition")]
    public async Task PendingConfigurationOrProviderRecoveryCannotBeErasedByBinaryRollback(string kind)
    {
        using var f = new Fixture(); var before = await InstallDevices(f); var root = ConfigStore(f).DirectoryPath;
        var path = Path.Combine(root, kind == "accepted" ? "configuration-application.json" : DeviceProviderTransition.FileName);
        f.Scm.BeforeStartOnce = () =>
        {
            if (kind == "accepted") ServiceEnvironment.Write(path, new ConfigurationApplication(1, Guid.NewGuid().ToString("N"), before.Pointer, before.Pointer, "accepted"));
            else File.WriteAllText(path, "service must recover this route");
        };
        f.Scm.FailOnce = "activate";
        await Assert.ThrowsAsync<AggregateException>(() => f.Apply("update", f.Package("2.1.0", devices: true)));
        var bytes = File.ReadAllBytes(path); Assert.Equal("2.1.0", f.InstalledVersion()); Assert.NotNull(ServiceDeployment.ReadPending(f.Data));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Engine().RecoverAsync()); Assert.Equal(bytes, File.ReadAllBytes(path));
        // Only the verified installed service is allowed to resolve its own in-flight record.
        f.Scm.BeforeStartOnce = () => File.Delete(path);
        await f.Engine().RecoverAsync(); Assert.Equal("2.0.0", f.InstalledVersion()); AssertSameConfiguration(f, before);
    }
    [Fact] public async Task DeviceSnapshotCannotBeSubstitutedFromAnotherDeploymentEvenWithAValidDocumentHash()
    {
        using var f = new Fixture(); _ = await InstallDevices(f); var engine = f.Engine();
        engine.AfterStep = step => { if (step == "new-moved") throw new ServiceDeploymentCrash(); };
        await Assert.ThrowsAsync<ServiceDeploymentCrash>(() => f.Apply("update", f.Package("2.1.0", devices: true), engine));
        var state = ServiceDeployment.ReadPending(f.Data)!;
        var path = Path.Combine(f.Data, "deployment-backups", state.Id, ServiceDeployment.ConfigurationSnapshotName);
        var document = ServiceEnvironment.Read<DeploymentConfigurationSnapshotDocument>(path);
        var foreign = document.State with { TransactionId = Guid.NewGuid().ToString("N") };
        ServiceEnvironment.Write(path, new DeploymentConfigurationSnapshotDocument(foreign, SettingsTransfer.Hash(foreign)));
        await Assert.ThrowsAsync<IOException>(() => f.Engine().RecoverAsync());
        Assert.Equal("2.1.0", f.InstalledVersion()); Assert.False(f.Scm.Registration.Running); Assert.NotNull(ServiceDeployment.ReadPending(f.Data));
    }
}
