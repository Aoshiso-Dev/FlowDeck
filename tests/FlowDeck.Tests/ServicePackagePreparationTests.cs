using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class ServicePackagePreparationTests
{
    internal static ServicePackage Basis(string root, bool devices = true)
    {
        var payload = Path.Combine(root, "payload");
        foreach (var name in ServicePackage.RequiredFiles)
        {
            var path = Path.Combine(payload, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "inert-service-fixture:" + name);
        }
        ServiceEnvironment.Write(Path.Combine(root, ServicePackage.ManifestName),
            ServicePackage.CreateManifest(payload, "2.0.0", devices ? ["devices-v2"] : null));
        return ServicePackage.Open(root);
    }
    private static ServicePackage Prepare(OpenRgbRuntimePackageTests.Fixture f, ServicePackage basis, string destination,
        CancellationToken ct = default, Action<string>? checkpoint = null) =>
        ServicePackagePreparation.WithOpenRgb(basis.Root, basis.ManifestHash, f.Zip, destination, f.Package, ct, checkpoint);

    [Fact]
    public void CompositionPinsAllOptionalFilesAndLeavesTheOriginalPackageAndArchiveIntact()
    {
        using var f = new OpenRgbRuntimePackageTests.Fixture(helper: true);
        var basis = Basis(Path.Combine(f.Root, "base")); var zip = File.ReadAllBytes(f.Zip);
        var composed = Prepare(f, basis, Path.Combine(f.Root, "with-openrgb"));
        Assert.Equal(basis.Manifest.Release, composed.Manifest.Release);
        Assert.NotEqual(basis.ManifestHash, composed.ManifestHash);
        Assert.Equal(new(f.Definition.Id, f.Package.DefinitionDigest), composed.Manifest.OpenRgb);
        Assert.Equal(basis.Manifest.Files.Count + 35, composed.Manifest.Files.Count);
        foreach (var entry in basis.Manifest.Files) Assert.Equal(entry.Value, composed.Manifest.Files[entry.Key]);
        Assert.Equal(f.Definition.Id, f.Package.Inspect(Path.Combine(composed.PayloadDirectory, ServicePackage.OpenRgbDirectory)).DefinitionId);
        Assert.Equal(basis.ManifestHash, ServicePackage.Open(basis.Root).ManifestHash);
        Assert.Null(ServicePackage.Open(basis.Root).Manifest.OpenRgb); Assert.Equal(zip, File.ReadAllBytes(f.Zip));
        Assert.Empty(Directory.GetDirectories(f.Root, ".flowdeck-service-*"));
        var plan = ServiceDeploymentPlanning.Create("install", composed, new(false), [ServiceFixture.Sid], false, runtime: "devices-v2");
        Assert.Equal(composed.Manifest.OpenRgb, plan.OpenRgb); Assert.True(plan.ReadyForAdministratorReview);
    }

    [Fact]
    public void EarlierDeviceServicePackageWithoutOptionalRuntimeSupportCannotBeComposed()
    {
        using var f = new OpenRgbRuntimePackageTests.Fixture(helper:true);
        var basis = Basis(Path.Combine(f.Root,"base"));
        ServiceEnvironment.Write(Path.Combine(basis.Root,ServicePackage.ManifestName),basis.Manifest with { OptionalRuntimeFormat = null });
        basis = ServicePackage.Open(basis.Root); Assert.True(basis.SupportsDevices); Assert.False(basis.SupportsOptionalOpenRgb);
        var target = Path.Combine(f.Root,"target");
        Assert.Throws<IOException>(() => Prepare(f,basis,target)); Assert.False(Path.Exists(target));
        Assert.Empty(Directory.GetDirectories(f.Root,".flowdeck-service-*"));
    }

    [Fact]
    public void RecompositionReplacesTheWholeOptionalRuntimeWithoutCarryingAnOldModule()
    {
        using var f = new OpenRgbRuntimePackageTests.Fixture(helper: true);
        var basis = Basis(Path.Combine(f.Root, "base"));
        var old = Prepare(f, basis, Path.Combine(f.Root, "old"));
        File.WriteAllText(Path.Combine(old.PayloadDirectory, ServicePackage.OpenRgbDirectory, "obsolete.dll"), "old inert module");
        ServiceEnvironment.Write(Path.Combine(old.Root, ServicePackage.ManifestName),
            ServicePackage.CreateManifest(old.PayloadDirectory, old.Manifest.Release, old.Manifest.Runtimes, old.Manifest.OpenRgb));
        old = ServicePackage.Open(old.Root);
        var next = Prepare(f, old, Path.Combine(f.Root, "new"));
        Assert.Equal(basis.Manifest.Files.Count + 35, next.Manifest.Files.Count);
        Assert.False(File.Exists(Path.Combine(next.PayloadDirectory, ServicePackage.OpenRgbDirectory, "obsolete.dll")));
        Assert.True(File.Exists(Path.Combine(old.PayloadDirectory, ServicePackage.OpenRgbDirectory, "obsolete.dll")));
        Assert.Equal(old.ManifestHash, ServicePackage.Open(old.Root).ManifestHash);
    }

    [Theory]
    [InlineData("hash")] [InlineData("archive")] [InlineData("existing")] [InlineData("inside-source")]
    [InlineData("legacy")] [InlineData("official")] [InlineData("relative")]
    public void UnsupportedInputsCannotOverwriteOrPublishAServicePackage(string invalid)
    {
        using var f = new OpenRgbRuntimePackageTests.Fixture(helper: invalid != "official");
        var basis = Basis(Path.Combine(f.Root, "base"), invalid != "legacy");
        var target = Path.Combine(f.Root, "target"); var hash = basis.ManifestHash;
        if (invalid == "hash") hash = new('0',64);
        if (invalid == "archive") File.AppendAllText(f.Zip,"changed");
        if (invalid == "existing") { Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target,"keep.txt"),"keep"); }
        if (invalid == "inside-source") target = Path.Combine(basis.Root,"target");
        if (invalid == "relative") target = "relative-target";
        Assert.Throws<IOException>(() => ServicePackagePreparation.WithOpenRgb(basis.Root,hash,f.Zip,target,f.Package));
        Assert.Equal(basis.ManifestHash,ServicePackage.Open(basis.Root).ManifestHash);
        if (invalid == "existing") Assert.Equal("keep",File.ReadAllText(Path.Combine(target,"keep.txt")));
        else Assert.False(Path.Exists(target));
        Assert.Empty(Directory.GetDirectories(f.Root, ".flowdeck-service-*"));
    }

    [Theory]
    [InlineData("copied:FlowDeck.Service.exe")] [InlineData("runtime-prepared")] [InlineData("verified")]
    public void CancellationRemovesOnlyTheNewStagingFiles(string boundary)
    {
        using var f = new OpenRgbRuntimePackageTests.Fixture(helper:true);
        var basis = Basis(Path.Combine(f.Root,"base")); using var cancel = new CancellationTokenSource();
        var target = Path.Combine(f.Root,"target");
        Assert.ThrowsAny<OperationCanceledException>(() => Prepare(f,basis,target,cancel.Token,
            stage => { if(stage == boundary) cancel.Cancel(); }));
        Assert.False(Path.Exists(target)); Assert.Empty(Directory.GetDirectories(f.Root,".flowdeck-service-*"));
        Assert.Equal(basis.ManifestHash,ServicePackage.Open(basis.Root).ManifestHash);
    }

    [Fact]
    public void ConcurrentDestinationCreationIsPreservedAndThePreparedStageIsRemoved()
    {
        using var f = new OpenRgbRuntimePackageTests.Fixture(helper:true);
        var basis = Basis(Path.Combine(f.Root,"base")); var target = Path.Combine(f.Root,"target");
        Assert.Throws<IOException>(() => Prepare(f,basis,target,checkpoint: stage =>
        {
            if(stage != "verified") return;
            Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target,"keep.txt"),"created elsewhere");
        }));
        Assert.Equal("created elsewhere", File.ReadAllText(Path.Combine(target,"keep.txt")));
        Assert.Single(Directory.GetFiles(target)); Assert.Empty(Directory.GetDirectories(f.Root,".flowdeck-service-*"));
    }

    [Fact]
    public void ChangesToTheReviewedSourceDuringCopyAreNotRehashedIntoAnAcceptedPackage()
    {
        using var f = new OpenRgbRuntimePackageTests.Fixture(helper:true);
        var basis = Basis(Path.Combine(f.Root,"base")); var target = Path.Combine(f.Root,"target");
        Assert.Throws<IOException>(() => Prepare(f,basis,target,checkpoint: stage =>
        {
            if(stage == "copied:FlowDeck.Service.exe")
                File.AppendAllText(Path.Combine(basis.PayloadDirectory,"worker","hub_worker.py"),"changed");
        }));
        Assert.False(Path.Exists(target)); Assert.Empty(Directory.GetDirectories(f.Root,".flowdeck-service-*"));
    }

    [Theory]
    [InlineData("missing-definition")] [InlineData("missing-files")] [InlineData("legacy")]
    [InlineData("foreign-id")] [InlineData("digest")] [InlineData("unknown-path")] [InlineData("missing-exe")]
    public void OptionalFilesAndDescriptorMustAgreeBeforePlanningOrDeployment(string invalid)
    {
        using var f = new OpenRgbRuntimePackageTests.Fixture(helper:true);
        var basis = Basis(Path.Combine(f.Root,"base")); var package = Prepare(f,basis,Path.Combine(f.Root,"target"));
        var value = package.Manifest;
        value = invalid switch
        {
            "missing-definition" => value with { OpenRgb = null },
            "missing-files" => basis.Manifest with { OpenRgb = value.OpenRgb },
            "legacy" => value with { Runtimes = null },
            "foreign-id" => value with { OpenRgb = value.OpenRgb! with { DefinitionId = "arbitrary-runtime" } },
            "digest" => value with { OpenRgb = value.OpenRgb! with { DefinitionDigest = "wrong" } },
            _ => value
        };
        if(invalid == "unknown-path") value.Files["optional/Other/anything.dll"] = value.Files[ServicePackage.OpenRgbDirectory + "/OpenRGB.exe"];
        if(invalid == "missing-exe") value.Files.Remove(ServicePackage.OpenRgbDirectory + "/OpenRGB.exe");
        ServiceEnvironment.Write(Path.Combine(package.Root,ServicePackage.ManifestName),value);
        Assert.Throws<IOException>(() => ServicePackage.Open(package.Root));
    }
}
