using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class ServiceDeploymentTests
{
    // Only the SCM and protected root are substituted. The real review, transaction, generation
    // preparation, file replacement, rollback and final package verification run on inert payloads.
    private static ServiceSetupSession SetupSession(Fixture fixture, ServicePackage package, Action confirmed)
    {
        return new(_ =>
        {
            var registration = fixture.Scm.Registration;
            if (registration.Installed) registration = registration with
            { BinaryPath = '"' + Path.Combine(ServiceEnvironment.InstallationDirectory, "FlowDeck.Service.exe") + '"' };
            return Task.FromResult(ServiceDeploymentPlanning.Create(registration.Installed ? "update" : "install", package,
                registration, [ServiceFixture.Sid], false, runtime: "devices-v2", currentRuntime: ServiceRuntimeSelection.Read(fixture.Data)));
        }, async plan =>
        {
            await fixture.Engine().ApplyAsync(plan.Action, package.Root, plan.ManifestHash, plan.AllowedUsers, runtime: plan.Runtime);
            return new(false, 0);
        }, plan =>
        {
            confirmed();
            var installed = ServicePackage.Read(Path.Combine(fixture.Data, ServiceDeployment.InstalledManifestName), fixture.Install, plan.ManifestHash);
            return Task.FromResult(fixture.Scm.Registration.Running && installed.SupportsDevices &&
                ServiceRuntimeSelection.Read(fixture.Data) is not null && ServiceDeployment.ReadPending(fixture.Data) is null);
        });
    }

    [Fact] public async Task ReviewedSetupInstallsTheRealGenericTransactionIntoIsolatedDirectories()
    {
        using var f = new Fixture(); var package = f.Package("2.0.0", true); var confirmations = 0;
        var setup = SetupSession(f, package, () => confirmations++);
        await setup.ReviewAsync(); Assert.Equal(ServiceSetupState.Ready, setup.State); Assert.Empty(f.Scm.Calls);
        await setup.ApplyAsync(); Assert.Equal(ServiceSetupState.Succeeded, setup.State); Assert.Equal(1, confirmations);
        Assert.Equal("2.0.0", f.InstalledVersion()); Assert.Null(ServiceDeployment.ReadPending(f.Data));
        var config = ServiceRuntimeSelection.Read(f.Data)!.ReadConfiguration(f.Data);
        Assert.NotNull(config); Assert.Equal(ServiceFixture.Sid, ServiceEnvironment.Read<ServiceInstallation>(Path.Combine(f.Data, "service.json")).AllowedUsers.Single());
    }

    [Theory][InlineData("none")][InlineData("configure")][InlineData("start")][InlineData("activate")]
    public async Task ReviewedUpdateKeepsTheGenericConfigurationAndReportsRollbackAsFailure(string failure)
    {
        using var f = new Fixture(); var initial = f.Package("2.0.0", true);
        await f.Engine().ApplyAsync("install", initial.Root, initial.ManifestHash, [ServiceFixture.Sid], runtime: "devices-v2");
        var before = JsonSerializer.Serialize(ServiceRuntimeSelection.Read(f.Data)!.ReadConfiguration(f.Data), Contract.Json);
        var confirmations = 0; var setup = SetupSession(f, f.Package("2.0.1", true), () => confirmations++);
        await setup.ReviewAsync(); Assert.Equal("update", setup.Plan!.Action);
        if (failure != "none") f.Scm.FailOnce = failure;
        await setup.ApplyAsync();
        Assert.Equal(failure == "none" ? ServiceSetupState.Succeeded : ServiceSetupState.Failed, setup.State);
        Assert.Equal(failure == "none" ? 1 : 0, confirmations);
        Assert.Equal(failure == "none" ? "2.0.1" : "2.0.0", f.InstalledVersion());
        Assert.Equal(before, JsonSerializer.Serialize(ServiceRuntimeSelection.Read(f.Data)!.ReadConfiguration(f.Data), Contract.Json));
        Assert.True(f.Scm.Registration.Running); Assert.Null(ServiceDeployment.ReadPending(f.Data));
    }
}
