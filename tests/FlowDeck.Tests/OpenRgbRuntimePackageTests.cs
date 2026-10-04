using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class OpenRgbRuntimePackageTests
{
    private static OpenRgbRuntimeManifest Embedded(bool helper = false)
    {
        using var stream = typeof(OpenRgbRuntimePackage).Assembly.GetManifestResourceStream(helper
            ? "FlowDeck.DeviceDefinitions.OpenRgbHelperRuntime.json" : "FlowDeck.DeviceDefinitions.OpenRgbRuntime.json")!;
        return JsonSerializer.Deserialize<OpenRgbRuntimeManifest>(stream, Contract.Json)!;
    }
    internal sealed class Fixture : IDisposable
    {
        private readonly ServiceTestDirectory temporary = new();
        public string Root => temporary.Path;
        public string Zip => Path.Combine(Root, "official-fixture.zip");
        public string Target => Path.Combine(Root, "runtime");
        public OpenRgbRuntimeManifest Definition { get; }
        public OpenRgbRuntimePackage Package { get; }
        public Fixture(string? wrongContent = null, string? extraEntry = null, bool directoryEntries = false, bool helper = false)
        {
            var basis = Embedded(helper);
            var files = basis.Files.Select(f => {
                var bytes = Encoding.UTF8.GetBytes("inert-fixture:" + basis.Id + ":" + f.Path);
                return f with { Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
            }).ToArray();
            using (var zip = ZipFile.Open(Zip, ZipArchiveMode.Create))
            {
                if (directoryEntries)
                {
                    zip.CreateEntry(basis.ArchivePrefix);
                    foreach (var folder in new[] { "imageformats/", "platforms/", "styles/" }) zip.CreateEntry(basis.ArchivePrefix + folder);
                }
                foreach (var file in files)
                {
                    var bytes = Encoding.UTF8.GetBytes("inert-fixture:" + basis.Id + ":" + file.Path);
                    if (file.Path == wrongContent) bytes[0] ^= 1;
                    using var stream = zip.CreateEntry(basis.ArchivePrefix + file.Path).Open(); stream.Write(bytes);
                }
                if (extraEntry is not null)
                { using var stream = zip.CreateEntry(basis.ArchivePrefix + extraEntry).Open(); stream.WriteByte(1); }
            }
            using var source = File.OpenRead(Zip);
            Definition = basis with { Files = files, ArchiveSha256 = Convert.ToHexString(SHA256.HashData(source)) };
            Package = new(Definition);
        }
        public void Dispose() => temporary.Dispose();
    }

    [Fact] public void AdoptedReleaseAndKernelModuleExclusionArePinned()
    {
        var definition = Embedded(); _ = OpenRgbRuntimePackage.Pinned;
        Assert.Equal("81bbe18a84c2e507006f19dd252e397e40a56bfe", definition.SourceRevision);
        Assert.Equal("182A52A3C97C4C4AE52C80286B4260C9666C51C3447DBC416EE8945F66192E90", definition.ArchiveSha256);
        Assert.Equal("86A88B99F60A086E13E6F6ECFB8260A94DBCD598DDEABFFDA7070D78C97D7E95", definition.Files.Single(f => f.Path == "OpenRGB.exe").Sha256);
        Assert.Equal(16, definition.Files.Count(f => f.Include));
        Assert.Equal(new[] { "LedsValve.bin", "LpcIO.bin", "SmbusI801.bin", "SmbusIntelSkylakeIMC.bin", "SmbusNCT6793.bin", "SmbusPIIX4.bin" },
            definition.Files.Where(f => !f.Include).Select(f => f.Path).Order(StringComparer.Ordinal));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void PreparesOnlyPinnedFilesAndLeavesSourceArchiveIntact(bool directoryEntries)
    {
        using var f = new Fixture(directoryEntries: directoryEntries);
        var original = File.ReadAllBytes(f.Zip); var receipt = f.Package.Prepare(f.Zip, f.Target);
        Assert.Equal(original, File.ReadAllBytes(f.Zip)); Assert.Equal(f.Target, receipt.Directory);
        Assert.Equal(16, receipt.IncludedFiles); Assert.Equal(6, receipt.ExcludedModules.Length);
        Assert.Equal(16, Directory.EnumerateFiles(f.Target, "*", SearchOption.AllDirectories).Count());
        Assert.All(receipt.ExcludedModules, m => Assert.False(File.Exists(Path.Combine(f.Target, m))));
        Assert.Equal(receipt.ArchiveSha256, f.Package.Inspect(f.Target).ArchiveSha256);
        Assert.Empty(Directory.EnumerateDirectories(f.Root, ".flowdeck-openrgb-*"));
    }

    [Fact] public void AnExistingRuntimeIsNeverOverwritten()
    {
        using var f = new Fixture(); Directory.CreateDirectory(f.Target);
        var path = Path.Combine(f.Target, "user-config.json"); File.WriteAllText(path, "keep");
        Assert.Throws<IOException>(() => f.Package.Prepare(f.Zip, f.Target)); Assert.Equal("keep", File.ReadAllText(path));
        Assert.Single(Directory.EnumerateFiles(f.Target));
    }

    [Fact] public void UnpinnedArchiveFailsBeforeCreatingAnyDestination()
    {
        using var f = new Fixture(); using (var output = new FileStream(f.Zip, FileMode.Append)) output.WriteByte(9);
        Assert.Throws<IOException>(() => f.Package.Prepare(f.Zip, f.Target));
        Assert.False(Directory.Exists(f.Target)); Assert.Empty(Directory.EnumerateDirectories(f.Root));
    }

    [Theory] [InlineData("OpenRGB.exe")] [InlineData("SmbusPIIX4.bin")]
    public void InconsistentIncludedOrExcludedBytesAbortAndCleanOnlyTheirStagingFiles(string wrong)
    {
        using var f = new Fixture(wrongContent: wrong); var marker = Path.Combine(f.Root, "keep.txt"); File.WriteAllText(marker, "keep");
        Assert.Throws<IOException>(() => f.Package.Prepare(f.Zip, f.Target));
        Assert.Equal("keep", File.ReadAllText(marker)); Assert.False(Directory.Exists(f.Target));
        Assert.Empty(Directory.EnumerateDirectories(f.Root));
    }

    [Theory] [InlineData("../escape.dll")] [InlineData("plugins/extra.dll")] [InlineData("OPENRGB.EXE")]
    public void UnexpectedArchivePathsCannotBeExtracted(string extra)
    {
        using var f = new Fixture(extraEntry: extra);
        Assert.Throws<IOException>(() => f.Package.Prepare(f.Zip, f.Target));
        Assert.False(Directory.Exists(f.Target)); Assert.Empty(Directory.EnumerateDirectories(f.Root));
    }

    [Theory]
    [InlineData("LedsValve.bin")] [InlineData("LpcIO.bin")] [InlineData("SmbusI801.bin")]
    [InlineData("SmbusIntelSkylakeIMC.bin")] [InlineData("SmbusNCT6793.bin")] [InlineData("SmbusPIIX4.bin")]
    public void ReintroducingAnyKernelModuleInvalidatesTheRuntime(string module)
    {
        using var f = new Fixture(); f.Package.Prepare(f.Zip, f.Target);
        File.WriteAllText(Path.Combine(f.Target, module), "unexpected");
        Assert.Throws<IOException>(() => f.Package.Inspect(f.Target));
    }

    [Fact] public void PluginDirectoryInvalidatesTheRuntimeEvenBeforeAnyPluginIsLoaded()
    {
        using var f = new Fixture(); f.Package.Prepare(f.Zip, f.Target);
        Directory.CreateDirectory(Path.Combine(f.Target, "plugins"));
        Assert.Throws<IOException>(() => f.Package.Inspect(f.Target));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void TamperedOrMissingDependencyIsRejectedAndPreviouslyOpenedFilesAreReleased(bool missing)
    {
        using var f = new Fixture(); f.Package.Prepare(f.Zip, f.Target);
        var path = Path.Combine(f.Target, "Qt6Widgets.dll");
        if (missing) File.Delete(path); else File.WriteAllText(path, "wrong");
        Assert.Throws<IOException>(() => f.Package.Inspect(f.Target));
        foreach (var file in Directory.EnumerateFiles(f.Target, "*", SearchOption.AllDirectories))
        { using var write = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
    }

    [Fact] public void RuntimeLeasePreventsReplacementAndWritesUntilReleased()
    {
        using var f = new Fixture(); f.Package.Prepare(f.Zip, f.Target);
        var path = Path.Combine(f.Target, "OpenRGB.exe");
        using (var lease = f.Package.Acquire(f.Target, false))
        {
            Assert.Equal(f.Target, lease.Directory);
            Assert.Throws<IOException>(() => { using var write = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
            Assert.Throws<IOException>(() => File.Move(path, path + ".old"));
            using var read = File.OpenRead(path); Assert.True(read.Length > 0);
        }
        using var editable = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact] public void CallerCannotMutateTheCapturedManifest()
    {
        using var f = new Fixture(); f.Definition.Files[0] = f.Definition.Files[0] with { Sha256 = new('f', 64) };
        f.Package.Prepare(f.Zip, f.Target); Assert.Equal(16, f.Package.Inspect(f.Target).IncludedFiles);
    }

    [Fact] public void CancellationDoesNotCreateAnIncompleteRuntime()
    {
        using var f = new Fixture(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => f.Package.Prepare(f.Zip, f.Target, cancel.Token));
        Assert.False(Directory.Exists(f.Target)); Assert.Empty(Directory.EnumerateDirectories(f.Root));
    }
}
