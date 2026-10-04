using System.Security.Cryptography;
using System.Text.Json;

namespace FlowDeck.Infrastructure;

public sealed record ServicePackageFile(long Length, string Sha256);
public sealed record ServicePackageManifest(int Version, string Release, Dictionary<string, ServicePackageFile> Files,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string[]? Runtimes = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ServiceOptionalOpenRgb? OpenRgb = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? OptionalRuntimeFormat = null);

/// <summary>Verifies a reviewed local package without loading its assemblies or running its Python.</summary>
public sealed partial class ServicePackage
{
    public const string ManifestName = "service-package.json";
    public static readonly string[] RequiredFiles = ["FlowDeck.Service.exe", "FlowDeck.Service.dll", "FlowDeck.Service.deps.json", "FlowDeck.Service.runtimeconfig.json",
        "FlowDeck.Infrastructure.dll", "FlowDeck.Core.dll", "coreclr.dll", "hostfxr.dll", "runtime/python/python.exe", "runtime/python/python313.dll", "worker/hub_worker.py", "worker/requirements.lock"];
    public string Root { get; }
    public string PayloadDirectory { get; }
    public string ManifestHash { get; }
    public ServicePackageManifest Manifest { get; }
    public bool SupportsDevices => Manifest.Runtimes?.Contains("devices-v2") == true;
    public bool SupportsOptionalOpenRgb => SupportsDevices && Manifest.OptionalRuntimeFormat == 1;
    private ServicePackage(string root, string payload, byte[] bytes, ServicePackageManifest manifest)
    { Root = root; PayloadDirectory = payload; ManifestHash = Convert.ToHexStringLower(SHA256.HashData(bytes)); Manifest = manifest; }
    private static bool RelativePath(string path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 512 && !path.Contains('\\') &&
        !path.Contains(':') && !Path.IsPathRooted(path) && path.Split('/').All(part => part.Length > 0 && part is not ("." or "..") &&
            !part.EndsWith('.') && !part.EndsWith(' ') && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
            !new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(part.Split('.')[0], StringComparer.OrdinalIgnoreCase));
    private static bool Hash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static FileInfo[] Files(string root)
    {
        var result = new List<FileInfo>();
        void Visit(string path)
        {
            ServiceEnvironment.NoLinks(path);
            foreach (var item in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                if (item.LinkTarget is not null) throw new IOException("サービス配布物にリンクを使用できません");
                if (item is DirectoryInfo directory) Visit(directory.FullName);
                else if (item is FileInfo file) { result.Add(file); if (result.Count > 10000) throw new IOException("配布ファイル数が上限を超えています"); }
            }
        }
        Visit(root); return result.ToArray();
    }
    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');
    public static ServicePackage Open(string root, string? expectedHash = null)
    {
        root = Path.GetFullPath(root); ServiceEnvironment.NoLinks(root);
        return Read(Path.Combine(root, ManifestName), Path.Combine(root, "payload"), expectedHash);
    }
    public static ServicePackage Read(string manifestPath, string payloadDirectory, string? expectedHash = null)
    {
        manifestPath = Path.GetFullPath(manifestPath); payloadDirectory = Path.GetFullPath(payloadDirectory);
        ServiceEnvironment.NoLinks(manifestPath); ServiceEnvironment.NoLinks(payloadDirectory);
        using var file = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 2 * 1024 * 1024) throw new IOException("配布マニフェストが大きすぎます");
        using var memory = new MemoryStream(); file.CopyTo(memory); var bytes = memory.ToArray();
        using var document = JsonDocument.Parse(bytes); SettingsTransfer.NoDuplicates(document.RootElement);
        var manifest = document.RootElement.Deserialize<ServicePackageManifest>(FlowDeck.Core.Contract.Json) ?? throw new IOException("配布マニフェストが空です");
        if (manifest.Version != 1 || !Version.TryParse(manifest.Release, out var release) || release.ToString() != manifest.Release ||
            manifest.OptionalRuntimeFormat is not (null or 1) || manifest.OptionalRuntimeFormat is not null && manifest.Runtimes?.Contains("devices-v2") != true ||
            manifest.Runtimes is { } runtimes && (runtimes.Length is < 1 or > 2 || runtimes.Distinct().Count() != runtimes.Length ||
                runtimes.Any(r => r is not ("legacy-v1" or "devices-v2"))) ||
            manifest.Files is not { Count: > 0 and <= 10000 } || manifest.Files.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count ||
            manifest.Files.Any(p => !RelativePath(p.Key) || p.Value is null || p.Value.Length is < 0 or > 512L * 1024 * 1024 || p.Value.Sha256 is null || !Hash(p.Value.Sha256)) ||
            manifest.Files.Values.Sum(f => f.Length) > 2L * 1024 * 1024 * 1024 || RequiredFiles.Any(path => !manifest.Files.ContainsKey(path)))
            throw new IOException("サービス配布マニフェストの形式・必須ファイルが不正です");
        var package = new ServicePackage(Path.GetDirectoryName(manifestPath)!, payloadDirectory, bytes, manifest);
        if (expectedHash is not null && package.ManifestHash != expectedHash) throw new IOException("事前確認した配布物とマニフェストが変わっています");
        package.VerifyPayload(); return package;
    }
    public void VerifyPayload()
    {
        ValidateOptionalRuntime();
        var files = Files(PayloadDirectory);
        if (files.Length != Manifest.Files.Count || files.Any(f => !Manifest.Files.ContainsKey(Relative(PayloadDirectory, f.FullName))))
            throw new IOException("マニフェストと配布ファイルの一覧が一致しません");
        foreach (var file in files)
        {
            var entry = Manifest.Files[Relative(PayloadDirectory, file.FullName)];
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != entry.Length || Convert.ToHexStringLower(SHA256.HashData(stream)) != entry.Sha256)
                throw new IOException("配布ファイルの内容が一致しません: " + Relative(PayloadDirectory, file.FullName));
        }
    }
    public static ServicePackageManifest CreateManifest(string payloadDirectory, string release, string[]? runtimes = null, ServiceOptionalOpenRgb? openRgb = null)
    {
        payloadDirectory = Path.GetFullPath(payloadDirectory);
        var values = Files(payloadDirectory).OrderBy(f => Relative(payloadDirectory, f.FullName), StringComparer.Ordinal).ToDictionary(f => Relative(payloadDirectory, f.FullName), f =>
        {
            using var stream = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new ServicePackageFile(stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream)));
        });
        return new(1, release, values, runtimes?.ToArray(), openRgb, runtimes?.Contains("devices-v2") == true ? 1 : null);
    }
}
