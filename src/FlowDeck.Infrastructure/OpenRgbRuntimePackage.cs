using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// A package receipt proves bytes and layout only. It grants neither process ownership nor device access.
public sealed record OpenRgbRuntimeReceipt(string DefinitionId, string SourceRevision, string ArchiveSha256,
    string Directory, int IncludedFiles, string[] ExcludedModules);

internal sealed record OpenRgbRuntimeFile(string Path, long Length, string Sha256, bool Include);
internal sealed record OpenRgbRuntimeManifest(int Version, string Id, string Release, string SourceRevision,
    string ArchiveUrl, string ArchiveSha256, string ArchivePrefix, OpenRgbRuntimeFile[] Files,
    int AuthorizationVersion = 0, int SourcePatch = 0);

/// <summary>
/// Prepares a separate, pinned optional runtime from an official or locally built fixed archive. Never runs
/// OpenRGB, installs a driver, changes its existing installation, or enables the production route.
/// </summary>
public sealed class OpenRgbRuntimePackage
{
    private const string Resource = "FlowDeck.DeviceDefinitions.OpenRgbRuntime.json";
    private readonly OpenRgbRuntimeManifest manifest;
    private readonly Dictionary<string, OpenRgbRuntimeFile> included;
    private readonly HashSet<string> directories;
    public static OpenRgbRuntimePackage Pinned { get; } = Load();
    public static OpenRgbRuntimePackage FlowDeckPinned { get; } = LoadHelper();
    internal int AuthorizationVersion => manifest.AuthorizationVersion;
    internal string DefinitionDigest => SettingsTransfer.Hash(manifest);
    internal string ExpectedVersion => manifest.Release;
    internal OpenRgbRuntimeFile[] IncludedFiles => included.Values.Select(f => f with { }).ToArray();
    internal OpenRgbRuntimePackage(OpenRgbRuntimeManifest definition)
    {
        if (definition is not { Version: 1, Files.Length: > 0 and <= 128 } || !DeviceConfigurationRules.Key(definition.Id) ||
            definition.SourceRevision is not { Length: 40 } || definition.SourceRevision.Any(c => !Uri.IsHexDigit(c)) ||
            !Digest(definition.ArchiveSha256) || !Relative(definition.ArchivePrefix.TrimEnd('/')) || !definition.ArchivePrefix.EndsWith('/') ||
            definition.Files.Any(f => f is null || !Relative(f.Path) || f.Length is < 1 or > 128 * 1024 * 1024 || !Digest(f.Sha256) ||
                (f.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) == f.Include)) ||
            definition.Files.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Count() != definition.Files.Length ||
            !definition.Files.Any(f => f.Path == "OpenRGB.exe" && f.Include) ||
            (definition.AuthorizationVersion, definition.SourcePatch) is not ((0, 0) or (1, 8)))
            throw new IOException("OpenRGBランタイムの固定定義が不正です");
        manifest = definition with { Files = definition.Files.Select(f => f with { }).ToArray() };
        included = manifest.Files.Where(f => f.Include).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        directories = new(StringComparer.OrdinalIgnoreCase);
        foreach (var file in included.Keys)
        {
            var index = file.LastIndexOf('/');
            while (index >= 0) { directories.Add(file[..index]); index = file.LastIndexOf('/', index - 1); }
        }
        if (directories.Overlaps(included.Keys)) throw new IOException("OpenRGBのファイルとフォルダー定義が重複しています");
    }
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool Relative(string? value) => value is { Length: > 0 and <= 180 } &&
        value.Split('/').All(p => p.Length > 0 && p is not "." and not ".." && p[^1] is not '.' and not ' ' &&
            p.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ' '));
    private static OpenRgbRuntimePackage Load()
    {
        using var stream = typeof(OpenRgbRuntimePackage).Assembly.GetManifestResourceStream(Resource)
            ?? throw new IOException("OpenRGBランタイムの定義がありません");
        using var document = JsonDocument.Parse(stream); SettingsTransfer.NoDuplicates(document.RootElement);
        var definition = document.RootElement.Deserialize<OpenRgbRuntimeManifest>(Contract.Json)
            ?? throw new IOException("OpenRGBランタイムの定義が空です");
        if (definition.Id != "openrgb-1.0-windows-x64-usb" || definition.Release != "1.0" ||
            definition.SourceRevision != "81bbe18a84c2e507006f19dd252e397e40a56bfe" || definition.Files.Length != 22 ||
            definition.Files.Count(f => f.Include) != 16 || !definition.Files.Where(f => !f.Include).Select(f => f.Path).Order(StringComparer.Ordinal)
                .SequenceEqual(new[] { "LedsValve.bin", "LpcIO.bin", "SmbusI801.bin", "SmbusIntelSkylakeIMC.bin", "SmbusNCT6793.bin", "SmbusPIIX4.bin" }))
            throw new IOException("OpenRGBランタイムの採用版が一致しません");
        return new(definition);
    }
    private static OpenRgbRuntimePackage LoadHelper()
    {
        using var stream = typeof(OpenRgbRuntimePackage).Assembly.GetManifestResourceStream("FlowDeck.DeviceDefinitions.OpenRgbHelperRuntime.json")
            ?? throw new IOException("OpenRGB専用版の定義がありません");
        using var document = JsonDocument.Parse(stream); SettingsTransfer.NoDuplicates(document.RootElement);
        var definition = document.RootElement.Deserialize<OpenRgbRuntimeManifest>(Contract.Json)
            ?? throw new IOException("OpenRGB専用版の定義が空です");
        if (definition.Id != "flowdeck-openrgb-1.0-patch8-windows-x64-usb" || definition.Release != "1.0-flowdeck-patch8" ||
            definition.SourceRevision != OpenRgbStartupProfile.SourceRevision || definition.AuthorizationVersion != 1 || definition.SourcePatch != 8 ||
            definition.ArchiveSha256 != "7A2AA04EE266652C5585F034427B92B5DA5782FEC6B0213E9F80449862A7F17A" ||
            definition.Files.Length != 35 || definition.Files.Any(f => !f.Include) ||
            definition.Files.SingleOrDefault(f => f.Path == "OpenRGB.exe")?.Sha256 != "3B89F75F024674A3112A0B03D383BF7D246E362376D260D1D8D977555D7EEB31")
            throw new IOException("OpenRGB専用版の採用条件が一致しません");
        return new(definition);
    }
    private static string Root(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("OpenRGBランタイムには絶対パスが必要です");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        ServiceEnvironment.NoLinks(full); return full;
    }
    private OpenRgbRuntimeReceipt Receipt(string root) => new(manifest.Id, manifest.SourceRevision, manifest.ArchiveSha256,
        root, included.Count, manifest.Files.Where(f => !f.Include).Select(f => f.Path).ToArray());

    public OpenRgbRuntimeReceipt Prepare(string archivePath, string destination, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var target = Root(destination); var archive = Root(archivePath);
        var parent = Path.GetDirectoryName(target) ?? throw new IOException("OpenRGBの準備先が不正です");
        if (!Directory.Exists(parent)) throw new IOException("OpenRGBの準備先の親フォルダーがありません");
        if (Path.Exists(target)) throw new IOException("OpenRGBの準備先は新しいフォルダーにしてください");
        // Lock the source against writes/replacement for the complete hash-check and extraction.
        using var source = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length > 256 * 1024 * 1024 || !Hash(source).Equals(manifest.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("OpenRGBの固定版アーカイブと一致しません");
        ct.ThrowIfCancellationRequested(); source.Position = 0;
        using var zip = new ZipArchive(source, ZipArchiveMode.Read, true);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(manifest.ArchivePrefix, StringComparison.Ordinal)) throw new IOException("OpenRGB ZIPの配置が不正です");
            var relative = entry.FullName[manifest.ArchivePrefix.Length..];
            if (entry.FullName.EndsWith('/'))
            {
                if (entry.Length != 0 || (relative.Length != 0 && !directories.Contains(relative.TrimEnd('/'))))
                    throw new IOException("OpenRGB ZIPに未知のフォルダーがあります");
                continue;
            }
            if (!Relative(relative) || !entries.TryAdd(relative, entry)) throw new IOException("OpenRGB ZIPのファイル名が不正・重複しています");
        }
        if (entries.Count != manifest.Files.Length || manifest.Files.Any(f => !entries.TryGetValue(f.Path, out var e) || e.Length != f.Length))
            throw new IOException("OpenRGB ZIPの内容が固定定義と一致しません");
        var stage = Path.Combine(parent, ".flowdeck-openrgb-" + Guid.NewGuid().ToString("N"));
        var createdFiles = new List<string>(); var createdDirectories = new List<string>();
        try
        {
            Directory.CreateDirectory(stage); createdDirectories.Add(stage);
            foreach (var folder in directories.OrderBy(d => d.Count(c => c == '/')).ThenBy(d => d, StringComparer.Ordinal))
            {
                var path = Path.Combine(stage, folder); Directory.CreateDirectory(path); createdDirectories.Add(path);
            }
            foreach (var file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested(); using var input = entries[file.Path].Open();
                // Excluded modules are verified too, but never written to the destination.
                if (!file.Include)
                {
                    if (!Hash(input).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("OpenRGB ZIPのモジュールが一致しません");
                    continue;
                }
                var path = Path.Combine(stage, file.Path);
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { createdFiles.Add(path); input.CopyTo(output); output.Flush(true); }
            }
            _ = Inspect(stage); ct.ThrowIfCancellationRequested();
            // No overwrite. Another process creating the target makes this operation fail.
            Directory.Move(stage, target); return Receipt(target);
        }
        catch (Exception failure)
        {
            // Remove only paths created here; never recurse through unknown content or an existing installation.
            var errors = new List<Exception> { failure };
            foreach (var path in createdFiles.AsEnumerable().Reverse())
                try { ServiceEnvironment.NoLinks(path); if (File.Exists(path)) File.Delete(path); } catch (Exception e) { errors.Add(e); }
            foreach (var path in createdDirectories.AsEnumerable().Reverse())
                try { ServiceEnvironment.NoLinks(path); if (Directory.Exists(path)) Directory.Delete(path, false); } catch (Exception e) { errors.Add(e); }
            if (errors.Count > 1) throw new AggregateException("OpenRGBの準備と一時ファイルの片付けに失敗しました", errors);
            throw;
        }
    }
    public OpenRgbRuntimeReceipt Inspect(string directory)
    {
        using var lease = Acquire(directory, false); return Receipt(lease.Directory);
    }
    internal OpenRgbRuntimeFileLease AcquireProtected(string directory) => Acquire(directory, true);
    // Kept internal for the ordinary-user byte/locking tests; this is not a hardware authorization API.
    internal OpenRgbRuntimeFileLease Acquire(string directory, bool requireProtected)
    {
        var root = Root(directory);
        if (requireProtected)
        {
            ServiceEnvironment.ValidateTree(root);
            var parent = new DirectoryInfo(root).Parent ?? throw new IOException("OpenRGB配置の親がありません");
            ServiceEnvironment.ValidateSecurity(parent.GetAccessControl());
        }
        CheckLayout(root); var handles = new List<FileStream>();
        try
        {
            foreach (var file in included.Values)
            {
                var stream = new FileStream(Path.Combine(root, file.Path), FileMode.Open, FileAccess.Read, FileShare.Read);
                handles.Add(stream);
                if (stream.Length != file.Length || !Hash(stream).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("OpenRGBランタイムのファイルが固定版と一致しません: " + file.Path);
            }
            CheckLayout(root);
            return new(root, handles.ToArray());
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }
    private void CheckLayout(string root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var folder))
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("OpenRGB配置にリンクは使用できません");
                var relative = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
                if (entry is DirectoryInfo)
                {
                    if (!directories.Contains(relative)) throw new IOException("OpenRGB配置に未知のフォルダーがあります: " + relative);
                    pending.Push(entry.FullName);
                }
                else if (!included.ContainsKey(relative) || !seen.Add(relative)) throw new IOException("OpenRGB配置に許可されていないファイルがあります: " + relative);
            }
        }
        if (seen.Count != included.Count) throw new IOException("OpenRGBランタイムに不足したファイルがあります");
    }
    private static string Hash(Stream stream) => Convert.ToHexString(SHA256.HashData(stream));
}

// This lease retains validated executable/DLL handles without write or delete sharing. The process
// owner must retain it until exit; directory ACLs prevent normal users from adding missing modules.
internal sealed class OpenRgbRuntimeFileLease(string directory, FileStream[] files) : IDisposable
{
    public string Directory { get; } = directory;
    public void Dispose() { foreach (var file in files) file.Dispose(); }
}
