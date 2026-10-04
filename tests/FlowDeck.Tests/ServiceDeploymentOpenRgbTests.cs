using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class ServiceDeploymentTests
{
    private static ServicePackage AddOpenRgb(Fixture f, ServicePackage basis, OpenRgbRuntimePackageTests.Fixture helper) =>
        ServicePackagePreparation.WithOpenRgb(basis.Root,basis.ManifestHash,helper.Zip,
            Path.Combine(f.Root,"optional-" + basis.Manifest.Release),helper.Package);

    private static void SetOpenRgbPreference(Fixture f, bool enabled)
    {
        var store = DeviceConfigurationCodec.Store(ServiceRuntimeSelection.ConfigurationDirectory(f.Data));
        var active = store.ReadActive()!; var config = DeviceConfigurationCodec.Read(active.Files);
        store.Activate(store.Stage(DeviceConfigurationCodec.Write(config with
        { Preferences = config.Preferences with { Providers = new(enabled) } })));
    }
    private static async Task InstallOptional(Fixture f, ServicePackage package) =>
        await f.Engine().ApplyAsync("install",package.Root,package.ManifestHash,[ServiceFixture.Sid],runtime:"devices-v2");

    [Fact]
    public async Task OptionalRuntimeSharesTheServiceTransactionAndRemovalRetainsTheCompleteBackup()
    {
        using var f = new Fixture(); using var helper = new OpenRgbRuntimePackageTests.Fixture(helper:true);
        var package = AddOpenRgb(f,f.Package("2.0.0",devices:true),helper);
        await InstallOptional(f,package);
        Assert.Equal(package.Manifest.OpenRgb,ServicePackage.Read(Path.Combine(f.Data,ServiceDeployment.InstalledManifestName),f.Install).Manifest.OpenRgb);
        Assert.Equal(helper.Definition.Id,helper.Package.Inspect(Path.Combine(f.Install,ServicePackage.OpenRgbDirectory)).DefinitionId);
        Assert.False(ServiceRuntimeSelection.Read(f.Data)!.ReadConfiguration(f.Data).Preferences.Providers?.OpenRgbEnabled == true);
        var selection = ServiceRuntimeSelection.Read(f.Data);
        var removed = await f.Apply("remove",null);
        var backup = ServicePackage.Open(Path.Combine(removed.BackupDirectory,"old"));
        Assert.Equal(package.ManifestHash,backup.ManifestHash);
        Assert.Equal(helper.Definition.Id,helper.Package.Inspect(Path.Combine(backup.PayloadDirectory,ServicePackage.OpenRgbDirectory)).DefinitionId);
        Assert.False(Directory.Exists(f.Install)); Assert.False(f.Scm.Registration.Installed);
        Assert.Equal(selection,ServiceRuntimeSelection.Read(f.Data));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FailedUpdateRestoresOptionalRuntimePresenceAlongsideThePreviousService(bool previouslyInstalled)
    {
        using var f = new Fixture(); using var helper = new OpenRgbRuntimePackageTests.Fixture(helper:true);
        var before = f.Package("2.0.0",devices:true); var next = f.Package("2.1.0",devices:true);
        if(previouslyInstalled) before = AddOpenRgb(f,before,helper); else next = AddOpenRgb(f,next,helper);
        await InstallOptional(f,before); var config = ServiceRuntimeSelection.Read(f.Data)!.ReadConfiguration(f.Data);
        f.Scm.FailOnce = "start";
        await Assert.ThrowsAsync<IOException>(() => f.Apply("update",next));
        var restored = ServicePackage.Read(Path.Combine(f.Data,ServiceDeployment.InstalledManifestName),f.Install);
        Assert.Equal(before.ManifestHash,restored.ManifestHash);
        Assert.Equal(previouslyInstalled,Directory.Exists(Path.Combine(f.Install,ServicePackage.OpenRgbDirectory)));
        Assert.Equal(before.Manifest.OpenRgb,restored.Manifest.OpenRgb);
        Assert.Equal(SettingsTransfer.Hash(config),SettingsTransfer.Hash(ServiceRuntimeSelection.Read(f.Data)!.ReadConfiguration(f.Data)));
        Assert.True(f.Scm.Registration.Running); Assert.Null(ServiceDeployment.ReadPending(f.Data));
    }

    [Theory]
    [InlineData("prepared")] [InlineData("backed-up")] [InlineData("old-move-before-record")] [InlineData("old-moved")]
    [InlineData("new-move-before-record")] [InlineData("new-moved")] [InlineData("configured")] [InlineData("started")] [InlineData("committed")]
    public async Task InterruptedUpdateRestoresTheMatchingOptionalFilesAndManifest(string boundary)
    {
        using var f = new Fixture(); using var helper = new OpenRgbRuntimePackageTests.Fixture(helper:true);
        var before = AddOpenRgb(f,f.Package("2.0.0",devices:true),helper); await InstallOptional(f,before);
        var next = f.Package("2.1.0",devices:true); var engine = f.Engine();
        engine.AfterStep = stage => { if(stage == boundary) throw new ServiceDeploymentCrash(); };
        await Assert.ThrowsAsync<ServiceDeploymentCrash>(() => f.Apply("update",next,engine));
        await f.Engine().RecoverAsync();
        var installed = ServicePackage.Read(Path.Combine(f.Data,ServiceDeployment.InstalledManifestName),f.Install);
        var committed = boundary == "committed";
        Assert.Equal(committed ? next.ManifestHash : before.ManifestHash,installed.ManifestHash);
        Assert.Equal(!committed,Directory.Exists(Path.Combine(f.Install,ServicePackage.OpenRgbDirectory)));
        Assert.Equal(committed ? null : before.Manifest.OpenRgb,installed.Manifest.OpenRgb);
        Assert.True(f.Scm.Registration.Running); Assert.Null(ServiceDeployment.ReadPending(f.Data));
    }

    [Fact]
    public async Task SavedOpenRgbRouteRequiresTheOptionalRuntimeBeforeTheServiceIsStopped()
    {
        using var f = new Fixture(); using var helper = new OpenRgbRuntimePackageTests.Fixture(helper:true);
        var before = AddOpenRgb(f,f.Package("2.0.0",devices:true),helper); await InstallOptional(f,before);
        SetOpenRgbPreference(f,true); var next = f.Package("2.1.0",devices:true); f.Scm.Calls.Clear();
        var error = await Assert.ThrowsAsync<IOException>(() => f.Apply("update",next));
        Assert.Contains("OpenRGBを使用",error.Message); Assert.Empty(f.Scm.Calls);
        Assert.Equal("2.0.0",f.InstalledVersion()); Assert.Null(ServiceDeployment.ReadPending(f.Data));
        var plan = ServiceDeploymentPlanning.Create("update",next,
            new(true,true,'"'+Path.Combine(ServiceEnvironment.InstallationDirectory,"FlowDeck.Service.exe")+'"',"LocalSystem","2.0.0"),
            [ServiceFixture.Sid],false,currentRuntime:ServiceRuntimeSelection.Read(f.Data),installedOpenRgb:before.Manifest.OpenRgb,openRgbEnabled:true);
        Assert.False(plan.ReadyForAdministratorReview); Assert.Equal(before.Manifest.OpenRgb,plan.PreviousOpenRgb); Assert.Null(plan.OpenRgb);
        Assert.Contains(plan.Blockers,e => e.Contains("OpenRGBを使用"));
        var included = AddOpenRgb(f,next,helper); await f.Apply("update",included);
        Assert.Equal("2.1.0",f.InstalledVersion());
        Assert.True(ServiceRuntimeSelection.Read(f.Data)!.ReadConfiguration(f.Data).Preferences.Providers!.OpenRgbEnabled);
    }

    [Fact]
    public async Task RouteChangedAfterPreflightIsRecheckedAfterStopBeforeAnyBinaryMoves()
    {
        using var f = new Fixture(); using var helper = new OpenRgbRuntimePackageTests.Fixture(helper:true);
        var before = AddOpenRgb(f,f.Package("2.0.0",devices:true),helper); await InstallOptional(f,before);
        var engine = f.Engine(); var visited = new List<string>();
        engine.AfterStep = stage => { visited.Add(stage); if(stage == "prepared") SetOpenRgbPreference(f,true); };
        await Assert.ThrowsAsync<IOException>(() => f.Apply("update",f.Package("2.1.0",devices:true),engine));
        Assert.DoesNotContain("backed-up",visited); Assert.DoesNotContain("old-moved",visited);
        Assert.Equal(before.ManifestHash,ServicePackage.Read(Path.Combine(f.Data,ServiceDeployment.InstalledManifestName),f.Install).ManifestHash);
        Assert.True(ServiceRuntimeSelection.Read(f.Data)!.ReadConfiguration(f.Data).Preferences.Providers!.OpenRgbEnabled);
        Assert.True(f.Scm.Registration.Running); Assert.Null(ServiceDeployment.ReadPending(f.Data));
    }
}
