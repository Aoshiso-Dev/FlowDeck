using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class ServicePackageTests
{
    private static string Create(string directory)
    {
        var root = Path.Combine(directory, "package"); var payload = Path.Combine(root, "payload");
        foreach (var name in ServicePackage.RequiredFiles)
        { var target = Path.Combine(payload, name); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, "test-only " + name); }
        Save(root, ServicePackage.CreateManifest(payload, "1.20.0")); return root;
    }
    private static void Save(string root, ServicePackageManifest value) => ServiceEnvironment.Write(Path.Combine(root, ServicePackage.ManifestName), value);
    [Fact] public void RuntimeCapabilityIsBoundToTheReviewedManifestAndShownInSetupPlanning()
    {
        using var dir = new ServiceTestDirectory(); var root = Create(dir.Path); var old = ServicePackage.Open(root); Assert.False(old.SupportsDevices);
        Assert.False(ServiceDeploymentPlanning.Create("install", old, new(false), [ServiceFixture.Sid], false, runtime: "devices-v2").ReadyForAdministratorReview);
        Save(root, old.Manifest with { Runtimes = ["legacy-v1", "devices-v2"] }); var current = ServicePackage.Open(root); Assert.True(current.SupportsDevices);
        Assert.Throws<IOException>(() => ServicePackage.Open(root, old.ManifestHash));
        var plan = ServiceDeploymentPlanning.Create("install", current, new(false), [ServiceFixture.Sid], false, runtime: "devices-v2");
        Assert.True(plan.ReadyForAdministratorReview); Assert.Equal("devices-v2", plan.Runtime); Assert.Contains(plan.Steps, s => s.Contains("監視用設定"));
        foreach (var runtimes in new string[][] { [], ["devices-v2", "devices-v2"], ["external-driver"] })
        { Save(root, current.Manifest with { Runtimes = runtimes }); Assert.Throws<IOException>(() => ServicePackage.Open(root)); }
    }
    [Theory] [InlineData("install", "legacy-v1")] [InlineData("update", "arbitrary")] [InlineData("remove", "devices-v2")]
    [InlineData("rollback", "devices-v2")] [InlineData("update", "legacy-v1")]
    public void InvalidRuntimeTransitionsAreRejected(string action, string runtime) =>
        Assert.Throws<ArgumentException>(() => ServiceDeployment.ValidateRuntimeChoice(action, runtime));
    [Fact] public void PackageValidationChecksContentsExactFileSetAndReviewedManifestHashWithoutExecutingFiles()
    {
        using var dir = new ServiceTestDirectory(); var root = Create(dir.Path); var package = ServicePackage.Open(root);
        Assert.Equal(ServicePackage.RequiredFiles.Length, package.Manifest.Files.Count); Assert.Equal("1.20.0", package.Manifest.Release);
        Assert.Equal(package.ManifestHash, ServicePackage.Open(root, package.ManifestHash).ManifestHash);
        Assert.Throws<IOException>(() => ServicePackage.Open(root, new string('0', 64)));
        var executable = Path.Combine(root, "payload", "FlowDeck.Service.exe"); File.AppendAllText(executable, "changed");
        Assert.Throws<IOException>(() => ServicePackage.Open(root));
        File.WriteAllText(executable, "test-only FlowDeck.Service.exe");
        var extra = Path.Combine(root, "payload", "injected.dll"); File.WriteAllText(extra, "unlisted");
        Assert.Throws<IOException>(() => ServicePackage.Open(root)); File.Delete(extra);
        File.Delete(executable); Assert.Throws<IOException>(() => ServicePackage.Open(root));
    }
    [Theory]
    [InlineData("../escape.dll")] [InlineData("/absolute.dll")] [InlineData("C:/outside.dll")]
    [InlineData("runtime/../escape.dll")] [InlineData("runtime\\escape.dll")] [InlineData("runtime/NUL.txt")]
    [InlineData("runtime/space ")] [InlineData("runtime/file.dll:stream")] [InlineData("runtime//file.dll")]
    public void ManifestPathsCannotEscapeUseAlternateStreamsOrDeviceNames(string path)
    {
        using var dir = new ServiceTestDirectory(); var root = Create(dir.Path); var package = ServicePackage.Open(root);
        package.Manifest.Files[path] = new(0, new string('0', 64)); Save(root, package.Manifest);
        Assert.Throws<IOException>(() => ServicePackage.Open(root));
    }
    [Fact] public void ManifestRejectsCaseCollisionsUnknownFieldsDuplicateFieldsAndMissingRequiredRuntime()
    {
        using var dir = new ServiceTestDirectory(); var root = Create(dir.Path); var package = ServicePackage.Open(root);
        var path = Path.Combine(root, ServicePackage.ManifestName); var original = File.ReadAllText(path);
        File.WriteAllText(path, original.Insert(1, "\"version\":1,")); Assert.Throws<IOException>(() => ServicePackage.Open(root));
        File.WriteAllText(path, original.Insert(1, "\"run\":\"evil.exe\",")); Assert.Throws<JsonException>(() => ServicePackage.Open(root));
        package.Manifest.Files["FLOWDECK.SERVICE.exe"] = package.Manifest.Files["FlowDeck.Service.exe"]; Save(root, package.Manifest);
        Assert.Throws<IOException>(() => ServicePackage.Open(root));
        package.Manifest.Files.Remove("FLOWDECK.SERVICE.exe"); package.Manifest.Files.Remove("runtime/python/python.exe"); Save(root, package.Manifest);
        Assert.Throws<IOException>(() => ServicePackage.Open(root));
    }
    [Fact] public void PlanKeepsFixedDestinationsAndStopsForCompetingControllersUnmanagedServicesAndDowngrades()
    {
        using var dir = new ServiceTestDirectory(); var root = Create(dir.Path); var package = ServicePackage.Open(root);
        var initial = ServiceDeploymentPlanning.Create("install", package, new(false), [ServiceFixture.Sid], false);
        Assert.True(initial.ReadyForAdministratorReview); Assert.True(initial.RequiresAdministrator);
        Assert.Equal(ServiceEnvironment.InstallationDirectory, initial.InstallationDirectory); Assert.Equal(package.ManifestHash, initial.ManifestHash);
        Assert.NotEmpty(ServiceDeploymentPlanning.Create("install", package, new(false), [ServiceFixture.Sid], true).Blockers);
        var proper = new ServiceRegistration(true, true, '"' + Path.Combine(ServiceEnvironment.InstallationDirectory, "FlowDeck.Service.exe") + '"', "LocalSystem", "1.19.0");
        Assert.True(ServiceDeploymentPlanning.Create("update", package, proper, [ServiceFixture.Sid], true).ReadyForAdministratorReview);
        Assert.False(ServiceDeploymentPlanning.Create("update", package, proper with { BinaryPath = "unquoted.exe" }, [ServiceFixture.Sid], false).ReadyForAdministratorReview);
        Assert.False(ServiceDeploymentPlanning.Create("update", package, proper with { Account = "OtherUser" }, [ServiceFixture.Sid], false).ReadyForAdministratorReview);
        Assert.False(ServiceDeploymentPlanning.Create("update", package, proper with { Release = "1.21.0" }, [ServiceFixture.Sid], false).ReadyForAdministratorReview);
        Assert.True(ServiceDeploymentPlanning.Create("rollback", package, proper with { Release = "1.21.0" }, [ServiceFixture.Sid], false).ReadyForAdministratorReview);
        Assert.Contains(ServiceDeploymentPlanning.Create("remove", null, proper, [ServiceFixture.Sid], false).Steps, step => step.Contains("保持"));
        Assert.Throws<ArgumentException>(() => ServiceDeploymentPlanning.Create("install", package, new(false), ["S-1-1-0"], false));
        Assert.Throws<ArgumentException>(() => ServiceDeploymentPlanning.Create("run-arbitrary-command", package, new(false), [ServiceFixture.Sid], false));
        Assert.Single(Directory.GetDirectories(dir.Path)); // Planning did not create target directories.
    }
}
