using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class OpenRgbRuntimePackageTests
{
    [Fact] public void HelperPinIncludesItsBootstrapBuildSourceAndCompilerRuntimeAndExcludesKernelModules()
    {
        var definition = Embedded(helper: true);
        Assert.Equal(1, OpenRgbRuntimePackage.FlowDeckPinned.AuthorizationVersion);
        Assert.Equal(0, OpenRgbRuntimePackage.Pinned.AuthorizationVersion);
        Assert.Equal(8, definition.SourcePatch); Assert.Equal(1, definition.AuthorizationVersion);
        Assert.Equal("7A2AA04EE266652C5585F034427B92B5DA5782FEC6B0213E9F80449862A7F17A", definition.ArchiveSha256);
        Assert.Equal("3B89F75F024674A3112A0B03D383BF7D246E362376D260D1D8D977555D7EEB31", definition.Files.Single(f => f.Path == "OpenRGB.exe").Sha256);
        Assert.Equal(35, definition.Files.Length); Assert.All(definition.Files, f => Assert.True(f.Include));
        Assert.DoesNotContain(definition.Files, f => f.Path.EndsWith(".bin") || f.Path.EndsWith(".sys"));
        foreach (var file in new[] { "source/OpenRGB-patch8.zip", "metadata/runtime-inputs.json", "metadata/build-result.json",
            "Qt6Core.dll", "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "LICENSE-OpenRGB.txt",
            "source/PawnIOLib-aef64eb8.zip", "metadata/pawniolib-build.json", "metadata/msvc-runtime.json" })
            Assert.Contains(definition.Files, f => f.Path == file);
    }

    [Fact] public void PreparesAndInspectsTheHelperAsADistinctImmutablePackage()
    {
        using var f = new Fixture(helper: true); var original = File.ReadAllBytes(f.Zip);
        var receipt = f.Package.Prepare(f.Zip, f.Target);
        Assert.Equal("flowdeck-openrgb-1.0-patch8-windows-x64-usb", receipt.DefinitionId);
        Assert.Equal(35, receipt.IncludedFiles); Assert.Empty(receipt.ExcludedModules);
        Assert.Equal(original, File.ReadAllBytes(f.Zip)); Assert.Equal(35, Directory.GetFiles(f.Target, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(receipt.ArchiveSha256, f.Package.Inspect(f.Target).ArchiveSha256);
        using var official = new Fixture();
        Assert.Throws<IOException>(() => official.Package.Inspect(f.Target));
    }

    [Theory]
    [InlineData("OpenRGB.exe")] [InlineData("Qt6Core.dll")] [InlineData("vcruntime140.dll")]
    [InlineData("source/OpenRGB-patch8.zip")] [InlineData("metadata/runtime-inputs.json")]
    public void ChangedHelperCodeOrSourceEvidenceCannotBeAdopted(string target)
    {
        using var f = new Fixture(helper: true); f.Package.Prepare(f.Zip, f.Target);
        var path = Path.Combine(f.Target, target); var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
        Assert.Throws<IOException>(() => f.Package.Inspect(f.Target));
        using var editable = new FileStream(Path.Combine(f.Target, "OpenRGB.exe"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("SmbusPIIX4.bin")] [InlineData("LpcIO.bin")] [InlineData("unexpected.dll")]
    [InlineData("plugins/extra.dll")] [InlineData("platforms/other.dll")]
    public void HelperCannotGainAdditionalHardwareModulesOrPlugins(string extra)
    {
        using var f = new Fixture(helper: true); f.Package.Prepare(f.Zip, f.Target);
        var path = Path.Combine(f.Target, extra); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "unexpected");
        Assert.Throws<IOException>(() => f.Package.Inspect(f.Target));
    }

    [Fact] public void HelperLeaseKeepsAllDependenciesAndItsEvidenceUnmodifiable()
    {
        using var f = new Fixture(helper: true); f.Package.Prepare(f.Zip, f.Target);
        using (var lease = f.Package.Acquire(f.Target, false))
            foreach (var path in Directory.GetFiles(f.Target, "*", SearchOption.AllDirectories))
            {
                Assert.Throws<IOException>(() => { using var write = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
                Assert.Throws<IOException>(() => File.Move(path, path + ".changed"));
            }
        foreach (var path in Directory.GetFiles(f.Target, "*", SearchOption.AllDirectories))
        { using var write = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None); }
    }

    [Theory] [InlineData(0, 8)] [InlineData(1, 0)] [InlineData(2, 8)] [InlineData(1, 5)] [InlineData(1, 6)] [InlineData(1, 7)] [InlineData(-1, 0)]
    public void AManifestCannotClaimAnUnimplementedBootstrapOrPatch(int authorization, int patch)
    {
        var definition = Embedded(helper: true) with { AuthorizationVersion = authorization, SourcePatch = patch };
        Assert.Throws<IOException>(() => new OpenRgbRuntimePackage(definition));
    }

    [Fact] public void OfficialAndHelperArchivesAreNotInterchangeable()
    {
        using var official = new Fixture(); using var helper = new Fixture(helper: true);
        Assert.Throws<IOException>(() => official.Package.Prepare(helper.Zip, official.Target));
        Assert.Throws<IOException>(() => helper.Package.Prepare(official.Zip, helper.Target));
        Assert.False(Directory.Exists(official.Target)); Assert.False(Directory.Exists(helper.Target));
    }
}
