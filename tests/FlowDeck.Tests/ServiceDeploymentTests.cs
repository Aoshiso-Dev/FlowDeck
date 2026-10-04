using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class ServiceDeploymentTests
{
    private sealed class FakeScm(string install, string data) : IServiceDeploymentControl
    {
        public ServiceRegistration Registration { get; set; } = new(false);
        public List<string> Calls { get; } = [];
        public string? FailOnce { get; set; }
        public bool MutateSettingsOnStart { get; set; }
        public bool RestoreFanJournalOnStart { get; set; }
        public bool MutateRuntimeOnStart { get; set; }
        public Action? BeforeStartOnce { get; set; }
        public ServiceRegistration Inspect() => Registration;
        private void Step(string name)
        { Calls.Add(name); if (FailOnce == name) { FailOnce = null; throw new IOException("simulated " + name); } }
        public Task StopAsync() { Step("stop"); Registration = Registration with { Running = false }; return Task.CompletedTask; }
        public Task ConfigureAsync(string[] users, ServiceRegistration? restore = null)
        {
            Step("configure");
            var package = ServicePackage.Read(Path.Combine(data, ServiceDeployment.InstalledManifestName), install);
            Registration = new(true, false, '"' + Path.Combine(install, "FlowDeck.Service.exe") + '"', "LocalSystem", package.Manifest.Release);
            return Task.CompletedTask;
        }
        public Task StartAsync(string transactionId)
        {
            Assert.False(ServiceDeployment.AllowsClients(data)); Assert.Equal(transactionId, ServiceDeployment.ReadPending(data)!.Id);
            if (MutateSettingsOnStart) { new ServiceSettingsStore(data).Save(new()); MutateSettingsOnStart = false; }
            if (RestoreFanJournalOnStart) new HubFanRecoveryJournal(data).Clear();
            if (MutateRuntimeOnStart) { File.WriteAllText(Path.Combine(data, ServiceRuntimeSelection.FileName), "changed by startup"); MutateRuntimeOnStart = false; }
            var startup = BeforeStartOnce; BeforeStartOnce = null; startup?.Invoke();
            Step("start"); Registration = Registration with { Running = true }; return Task.CompletedTask;
        }
        public Task ActivateAsync(ServiceRegistration? restore = null)
        { Step("activate"); Registration = Registration with { Settings = restore?.Settings }; return Task.CompletedTask; }
        public Task RemoveAsync() { Step("remove"); Registration = new(false); return Task.CompletedTask; }
        public Task<IAsyncDisposable> AcquireHardwareAsync() { Step("ownership"); return Task.FromResult<IAsyncDisposable>(new Lease()); }
        private sealed class Lease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly ServiceTestDirectory directory = new();
        public string Root => directory.Path;
        public string Install => Path.Combine(Root, "programs", "Service");
        public string Data => Path.Combine(Root, "data", "Service");
        public FakeScm Scm { get; }
        public Fixture() => Scm = new(Install, Data);
        public ServiceDeployment Engine() => new(Install, Data, Scm, true);
        public ServicePackage Package(string version, bool devices = false)
        {
            var root = Path.Combine(Root, "package-" + version); var payload = Path.Combine(root, "payload");
            foreach (var name in ServicePackage.RequiredFiles)
            { var file = Path.Combine(payload, name); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, version + ":" + name); }
            ServiceEnvironment.Write(Path.Combine(root, ServicePackage.ManifestName), ServicePackage.CreateManifest(payload, version, devices ? ["legacy-v1", "devices-v2"] : null));
            return ServicePackage.Open(root);
        }
        public Task<ServiceDeploymentResult> Apply(string action, ServicePackage? package, ServiceDeployment? engine = null, AppSettings? seed = null) =>
            (engine ?? Engine()).ApplyAsync(action, package?.Root, package?.ManifestHash, [ServiceFixture.Sid], seed, emptySettings: seed is null);
        public string InstalledVersion() => ServicePackage.Read(Path.Combine(Data, ServiceDeployment.InstalledManifestName), Install).Manifest.Release;
        public void Dispose() => directory.Dispose();
    }
    [Fact] public async Task InstallUpdateAndRemovePreserveMachineDataAndKeepOldBinariesForReview()
    {
        using var f = new Fixture(); var first = await f.Apply("install", f.Package("1.20.0"), seed: DemoHub.CreateSettings());
        Assert.Equal("committed", first.Outcome); Assert.True(f.Scm.Registration.Running); Assert.Equal("1.20.0", f.InstalledVersion());
        Assert.True(ServiceDeployment.AllowsClients(f.Data)); Assert.Null(ServiceDeployment.ReadPending(f.Data));
        var settings = new ServiceSettingsStore(f.Data).Load(); Assert.Equal("demo", settings.Hardware.Hub!.Id);
        Assert.False(settings.Hardware.WiringConfirmed); Assert.Null(settings.LastConfirmedPolicy); Assert.False(settings.Startup.ResumeControl);
        File.WriteAllText(Path.Combine(f.Data, "diagnostic.log"), "retain me");
        var updated = await f.Apply("update", f.Package("1.21.0")); Assert.Equal("1.21.0", f.InstalledVersion());
        Assert.Equal("1.20.0", ServicePackage.Open(Path.Combine(updated.BackupDirectory, "old")).Manifest.Release);
        Assert.Equal("demo", new ServiceSettingsStore(f.Data).Load().Hardware.Hub!.Id);
        var removed = await f.Apply("remove", null); Assert.False(f.Scm.Registration.Installed); Assert.False(Directory.Exists(f.Install));
        Assert.Equal("1.21.0", ServicePackage.Open(Path.Combine(removed.BackupDirectory, "old")).Manifest.Release);
        Assert.Equal("retain me", File.ReadAllText(Path.Combine(f.Data, "diagnostic.log"))); Assert.True(File.Exists(Path.Combine(f.Data, "cooling-settings.json")));
    }
    [Theory]
    [InlineData("prepared")] [InlineData("backed-up")] [InlineData("old-move-before-record")] [InlineData("old-moved")]
    [InlineData("new-move-before-record")] [InlineData("new-moved")] [InlineData("configured")] [InlineData("started")] [InlineData("committed")]
    public async Task AbruptInterruptionAtEachBoundaryRecoversWithANewEngine(string boundary)
    {
        using var f = new Fixture(); await f.Apply("install", f.Package("1.20.0"), seed: DemoHub.CreateSettings());
        var beforeSettings = File.ReadAllBytes(Path.Combine(f.Data, "cooling-settings.json"));
        var next = f.Package("1.21.0"); var engine = f.Engine();
        engine.AfterStep = step => { if (step == boundary) throw new ServiceDeploymentCrash(); };
        await Assert.ThrowsAsync<ServiceDeploymentCrash>(() => f.Apply("update", next, engine));
        Assert.NotNull(ServiceDeployment.ReadPending(f.Data));
        Assert.Equal(boundary == "committed", ServiceDeployment.AllowsClients(f.Data));
        var result = await f.Engine().RecoverAsync(); Assert.NotNull(result);
        Assert.Equal(boundary == "committed" ? "1.21.0" : "1.20.0", f.InstalledVersion());
        Assert.True(f.Scm.Registration.Running); Assert.Null(ServiceDeployment.ReadPending(f.Data));
        Assert.Equal(beforeSettings, File.ReadAllBytes(Path.Combine(f.Data, "cooling-settings.json")));
        Assert.Null(await f.Engine().RecoverAsync());
    }
    [Theory] [InlineData("configure")] [InlineData("start")] [InlineData("activate")]
    public async Task OrdinarySetupFailureAutomaticallyRestoresOldFilesAndSettings(string operation)
    {
        using var f = new Fixture(); await f.Apply("install", f.Package("1.20.0"), seed: DemoHub.CreateSettings());
        var settings = File.ReadAllBytes(Path.Combine(f.Data, "cooling-settings.json"));
        f.Scm.FailOnce = operation; f.Scm.MutateSettingsOnStart = operation == "start";
        await Assert.ThrowsAsync<IOException>(() => f.Apply("update", f.Package("1.21.0")));
        Assert.Equal("1.20.0", f.InstalledVersion()); Assert.True(f.Scm.Registration.Running); Assert.Null(ServiceDeployment.ReadPending(f.Data));
        Assert.Equal(settings, File.ReadAllBytes(Path.Combine(f.Data, "cooling-settings.json")));
    }
    [Fact] public async Task PendingFanRecoveryPreventsReplacementAndRemainsAvailableForLaterRecovery()
    {
        using var f = new Fixture(); await f.Apply("install", f.Package("1.20.0"));
        new HubFanRecoveryJournal(f.Data).Save(new("device", "wiring", 70, 56, new() { ["fan1"] = 50 }));
        var path = Path.Combine(f.Data, "hub-fan-recovery.json"); var original = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<AggregateException>(() => f.Apply("update", f.Package("1.21.0")));
        Assert.Equal("1.20.0", f.InstalledVersion()); Assert.Equal(original, File.ReadAllBytes(path)); Assert.NotNull(ServiceDeployment.ReadPending(f.Data));
        Assert.DoesNotContain("1.21.0", f.Scm.Registration.Release);
        new HubFanRecoveryJournal(f.Data).Clear(); await f.Engine().RecoverAsync(); Assert.Null(ServiceDeployment.ReadPending(f.Data));
    }
    [Fact] public async Task InterruptedRollbackCanRestoreAgainWithoutLosingTheOriginalPayload()
    {
        using var f = new Fixture(); await f.Apply("install", f.Package("1.20.0"));
        var engine = f.Engine(); f.Scm.FailOnce = "start";
        engine.AfterStep = step => { if (step == "restore-move-before-record") throw new ServiceDeploymentCrash(); };
        await Assert.ThrowsAsync<AggregateException>(() => f.Apply("update", f.Package("1.21.0"), engine));
        await f.Engine().RecoverAsync(); Assert.Equal("1.20.0", f.InstalledVersion()); Assert.True(f.Scm.Registration.Running);
    }
    [Fact] public async Task FailedFirstInstallReturnsToNoServiceAndFailedRemovalRestoresStoppedService()
    {
        using var f = new Fixture(); var package = f.Package("1.20.0"); f.Scm.FailOnce = "start";
        await Assert.ThrowsAsync<IOException>(() => f.Apply("install", package));
        Assert.False(f.Scm.Registration.Installed); Assert.False(Directory.Exists(f.Install)); Assert.Null(ServiceDeployment.ReadPending(f.Data));
        await f.Apply("install", package); f.Scm.Registration = f.Scm.Registration with { Running = false };
        f.Scm.FailOnce = "remove"; await Assert.ThrowsAsync<IOException>(() => f.Apply("remove", null));
        Assert.Equal("1.20.0", f.InstalledVersion()); Assert.False(f.Scm.Registration.Running);
    }
    [Fact] public async Task TamperedBackupsBlockRollbackWithoutDiscardingThePendingRecord()
    {
        using var f = new Fixture(); await f.Apply("install", f.Package("1.20.0"));
        var engine = f.Engine(); engine.AfterStep = step => { if (step == "new-moved") throw new ServiceDeploymentCrash(); };
        await Assert.ThrowsAsync<ServiceDeploymentCrash>(() => f.Apply("update", f.Package("1.21.0"), engine));
        var state = ServiceDeployment.ReadPending(f.Data)!;
        File.AppendAllText(Path.Combine(f.Data, "deployment-backups", state.Id, "cooling-settings.json"), "tampered");
        await Assert.ThrowsAsync<IOException>(() => f.Engine().RecoverAsync()); Assert.NotNull(ServiceDeployment.ReadPending(f.Data));
        Assert.False(f.Scm.Registration.Running);
    }
    [Fact] public async Task ReviewedHashDowngradeAndUnmanagedRegistrationAreRejectedBeforeStoppingTheService()
    {
        using var f = new Fixture(); var old = f.Package("1.20.0"); var next = f.Package("1.21.0");
        await Assert.ThrowsAsync<IOException>(() => f.Engine().ApplyAsync("install", old.Root, new string('0', 64), [ServiceFixture.Sid])); Assert.Empty(f.Scm.Calls);
        await f.Apply("install", next); f.Scm.Calls.Clear();
        await Assert.ThrowsAsync<IOException>(() => f.Apply("update", old)); Assert.Empty(f.Scm.Calls);
        f.Scm.Registration = f.Scm.Registration with { BinaryPath = "arbitrary.exe" };
        await Assert.ThrowsAsync<IOException>(() => f.Apply("rollback", old)); Assert.Empty(f.Scm.Calls);
        f.Scm.Registration = f.Scm.Registration with { BinaryPath = '"' + Path.Combine(f.Install, "FlowDeck.Service.exe") + '"' };
        await f.Apply("rollback", old); Assert.Equal("1.20.0", f.InstalledVersion());
    }
    [Fact] public async Task InitialInstallRequiresAnExplicitMigrationOrEmptyConfigurationChoice()
    {
        using var f = new Fixture(); var package = f.Package("1.20.0");
        await Assert.ThrowsAsync<IOException>(() => f.Engine().ApplyAsync("install", package.Root, package.ManifestHash, [ServiceFixture.Sid]));
        Assert.Empty(f.Scm.Calls); Assert.False(Directory.Exists(f.Install));
    }
    [Fact] public async Task RecoveryMayStartTheVerifiedInstalledServiceToFinishOutstandingFanRestoration()
    {
        using var f = new Fixture(); await f.Apply("install", f.Package("1.20.0"));
        new HubFanRecoveryJournal(f.Data).Save(new("device", "wiring", 70, 56, new() { ["fan1"] = 50 }));
        f.Scm.RestoreFanJournalOnStart = true;
        await Assert.ThrowsAsync<IOException>(() => f.Apply("update", f.Package("1.21.0")));
        Assert.Equal("1.20.0", f.InstalledVersion()); Assert.Null(new HubFanRecoveryJournal(f.Data).Pending);
        Assert.True(f.Scm.Registration.Running); Assert.Null(ServiceDeployment.ReadPending(f.Data));
    }
    [Fact] public async Task RollbackPreservesTheCapturedStartupRecoveryAndSecuritySettings()
    {
        using var f = new Fixture(); await f.Apply("install", f.Package("1.20.0"));
        var original = new ServiceRegistrationSettings(3, false, 3600, [new(1, 10000)], "", "", false, "D:(A;;GA;;;SY)(A;;GA;;;BA)", "Custom FlowDeck", "custom description", 0, ["EventLog"]);
        f.Scm.Registration = f.Scm.Registration with { Settings = original };
        f.Scm.FailOnce = "start"; await Assert.ThrowsAsync<IOException>(() => f.Apply("update", f.Package("1.21.0")));
        Assert.Equal(SettingsTransfer.Hash(original), SettingsTransfer.Hash(f.Scm.Registration.Settings));
    }
    [Fact] public async Task FailedUpdateRestoresTheSelectedRuntimeAlongsideItsPreviousBinary()
    {
        using var f = new Fixture(); await InstallDevices(f);
        var selected = ServiceRuntimeSelection.Read(f.Data)!;
        f.Scm.MutateRuntimeOnStart = true; f.Scm.FailOnce = "activate";
        await Assert.ThrowsAsync<IOException>(() => f.Apply("update", f.Package("2.1.0", devices: true)));
        Assert.Equal("2.0.0", f.InstalledVersion()); Assert.Equal(selected, ServiceRuntimeSelection.Read(f.Data));
        Assert.True(f.Scm.Registration.Running);
    }
    [Fact] public void OlderDeploymentJournalsWithoutRuntimeSelectionRemainReadable()
    {
        using var f = new Fixture();
        var state = new ServiceDeploymentState(1, Guid.NewGuid().ToString("N"), "install", "backed-up", new(false), [ServiceFixture.Sid], new('a', 64),
            new() { ["service.json"] = null, ["cooling-settings.json"] = null, [ServiceDeployment.InstalledManifestName] = null });
        ServiceEnvironment.Write(Path.Combine(f.Data, ServiceDeployment.PendingName), new ServiceDeploymentDocument(state, SettingsTransfer.Hash(state)));
        Assert.Equal(state.Id, ServiceDeployment.ReadPending(f.Data)!.Id);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExplicitDeviceSetupStartsMonitoringAndKeepsLegacyConfiguration(bool update)
    {
        using var f = new Fixture(); byte[]? legacy = null;
        if (update) { await f.Apply("install", f.Package("1.20.0"), seed: DemoHub.CreateSettings()); legacy = File.ReadAllBytes(Path.Combine(f.Data, "cooling-settings.json")); }
        var package = f.Package("2.0.0", devices: true);
        var result = await f.Engine().ApplyAsync(update ? "update" : "install", package.Root, package.ManifestHash, [ServiceFixture.Sid], runtime: "devices-v2");
        Assert.Equal("committed", result.Outcome); var selected = ServiceRuntimeSelection.Read(f.Data)!;
        Assert.NotNull(selected); Assert.Empty(selected.ReadConfiguration(f.Data).Cooling.Endpoints);
        Assert.True(f.Scm.Registration.Running); Assert.True(ServiceDeployment.AllowsClients(f.Data));
        if (legacy is not null) Assert.Equal(legacy, File.ReadAllBytes(Path.Combine(f.Data, "cooling-settings.json")));
    }
    [Theory] [InlineData("runtime-prepared")] [InlineData("start")] [InlineData("activate")]
    public async Task FailedDeviceSwitchRestoresLegacySelectionAndCanRetryItsInitialGeneration(string boundary)
    {
        using var f = new Fixture(); await f.Apply("install", f.Package("1.20.0")); var next = f.Package("2.0.0", devices: true);
        var before = File.ReadAllBytes(Path.Combine(f.Data, "cooling-settings.json")); var engine = f.Engine();
        if (boundary == "runtime-prepared") engine.AfterStep = step => { if (step == boundary) throw new ServiceDeploymentCrash(); };
        else f.Scm.FailOnce = boundary;
        await Assert.ThrowsAnyAsync<Exception>(() => engine.ApplyAsync("update", next.Root, next.ManifestHash, [ServiceFixture.Sid], runtime: "devices-v2"));
        var initial = DeviceConfigurationCodec.Store(ServiceRuntimeSelection.ConfigurationDirectory(f.Data)).ReadActivePointer(); Assert.NotNull(initial);
        if (boundary == "runtime-prepared") await f.Engine().RecoverAsync();
        Assert.Equal("1.20.0", f.InstalledVersion()); Assert.Null(ServiceRuntimeSelection.Read(f.Data)); Assert.True(f.Scm.Registration.Running);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(f.Data, "cooling-settings.json")));
        await f.Engine().ApplyAsync("update", next.Root, next.ManifestHash, [ServiceFixture.Sid], runtime: "devices-v2");
        Assert.Equal(initial, DeviceConfigurationCodec.Store(ServiceRuntimeSelection.ConfigurationDirectory(f.Data)).ReadActivePointer());
    }
    [Fact] public async Task LegacyPackageCannotActivateOrReplaceDevicesWithoutAnExplicitRollback()
    {
        using var f = new Fixture(); var old = f.Package("1.20.0");
        await Assert.ThrowsAsync<IOException>(() => f.Engine().ApplyAsync("install", old.Root, old.ManifestHash, [ServiceFixture.Sid], runtime: "devices-v2"));
        Assert.Empty(f.Scm.Calls);
        var next = f.Package("2.0.0", devices: true); await f.Engine().ApplyAsync("install", next.Root, next.ManifestHash, [ServiceFixture.Sid], runtime: "devices-v2");
        var pointer = DeviceConfigurationCodec.Store(ServiceRuntimeSelection.ConfigurationDirectory(f.Data)).ReadActivePointer(); f.Scm.Calls.Clear();
        await Assert.ThrowsAsync<IOException>(() => f.Apply("rollback", old)); Assert.Empty(f.Scm.Calls);
        await f.Engine().ApplyAsync("rollback", old.Root, old.ManifestHash, [ServiceFixture.Sid], runtime: "legacy-v1");
        Assert.Null(ServiceRuntimeSelection.Read(f.Data)); Assert.Equal("1.20.0", f.InstalledVersion());
        Assert.Equal(pointer, DeviceConfigurationCodec.Store(ServiceRuntimeSelection.ConfigurationDirectory(f.Data)).ReadActivePointer());
    }
    [Fact] public async Task DeviceObligationsBlockBinaryReplacementEvenForAnAbsentUnregisteredHub()
    {
        using var f = new Fixture(); await f.Apply("install", f.Package("1.20.0"));
        var journal = new HubEndpointRecoveryJournal(ServiceRuntimeSelection.RecoveryDirectory(f.Data), "absent-hub");
        journal.Prepare("serial:missing", new('a', 64), "wiring", "fan1", 70, 50, 60);
        await Assert.ThrowsAsync<AggregateException>(() => f.Apply("update", f.Package("1.21.0")));
        Assert.Equal("1.20.0", f.InstalledVersion()); Assert.Single(journal.Pending); Assert.NotNull(ServiceDeployment.ReadPending(f.Data));
        journal.ConfirmReleased(journal.Pending.Single()); await f.Engine().RecoverAsync(); Assert.Null(ServiceDeployment.ReadPending(f.Data));
    }
    [Fact] public async Task ConcurrentSetupCannotStopTheServiceAndMalformedRecoveryCannotEscapeItsDirectory()
    {
        using var f = new Fixture(); await f.Apply("install", f.Package("1.20.0")); var next = f.Package("1.21.0"); f.Scm.Calls.Clear();
        using (var locked = new FileStream(Path.Combine(f.Data, "deployment.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => f.Apply("update", next));
        Assert.Empty(f.Scm.Calls);
        var state = new ServiceDeploymentState(1, "../escape", "update", "prepared", f.Scm.Registration, [ServiceFixture.Sid], next.ManifestHash);
        var pending = Path.Combine(f.Data, ServiceDeployment.PendingName);
        ServiceEnvironment.Write(pending, new ServiceDeploymentDocument(state, SettingsTransfer.Hash(state)));
        await Assert.ThrowsAsync<IOException>(() => f.Engine().RecoverAsync()); Assert.Empty(f.Scm.Calls);
        state = state with { Id = Guid.NewGuid().ToString("N"), Backups = new() { ["../escape"] = null } };
        ServiceEnvironment.Write(pending, new ServiceDeploymentDocument(state, SettingsTransfer.Hash(state)));
        Assert.Throws<IOException>(() => ServiceDeployment.AllowsClients(f.Data));
    }
    [Fact] public void InitialConfigurationUsesTheReviewedPortableArchiveAndRejectsChangedBytes()
    {
        using var f = new Fixture(); var path = Path.Combine(f.Root, "settings.flowdeck.json");
        var empty = SettingsTransfer.FromFiles(new Dictionary<string, byte[]>());
        var archive = SettingsTransfer.Create(empty with { Settings = SettingsTransfer.Portable(DemoHub.CreateSettings()) });
        File.WriteAllBytes(path, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(archive, Contract.Json));
        var seed = ServiceSeed.Read(path); Assert.Equal("demo", seed.Settings.Hardware.Hub!.Id);
        Assert.Equal(seed.Sha256, ServiceSeed.Read(path, seed.Sha256).Sha256);
        File.AppendAllText(path, " "); Assert.Throws<IOException>(() => ServiceSeed.Read(path, seed.Sha256));
        Assert.Throws<ArgumentException>(() => ServiceSeed.Read("relative.json"));
    }
}
