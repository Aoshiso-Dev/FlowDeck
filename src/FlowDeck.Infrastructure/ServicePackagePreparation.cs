using System.Security.Cryptography;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>Composes a new reviewed package. No service, executable, driver or hardware is opened.</summary>
public static class ServicePackagePreparation
{
    public static ServicePackage WithOpenRgb(string source, string expectedManifestHash, string archive, string destination, CancellationToken ct = default) =>
        WithOpenRgb(source, expectedManifestHash, archive, destination, OpenRgbRuntimePackage.FlowDeckPinned, ct);

    internal static ServicePackage WithOpenRgb(string source, string expectedManifestHash, string archive, string destination,
        OpenRgbRuntimePackage runtime, CancellationToken ct = default, Action<string>? checkpoint = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!DeviceConfigurationProtocol.Digest(expectedManifestHash)) throw new IOException("確認済みの配布マニフェストハッシュが必要です");
        static string Full(string path)
        {
            if (!Path.IsPathFullyQualified(path)) throw new IOException("配布物の準備には絶対パスが必要です");
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); ServiceEnvironment.NoLinks(full); return full;
        }
        source = Full(source); archive = Full(archive); destination = Full(destination);
        static bool Within(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (Within(destination, source) || Within(source, destination) || Path.Exists(destination))
            throw new IOException("元の配布物と独立した新しい準備先が必要です");
        var parent = Path.GetDirectoryName(destination) ?? throw new IOException("配布物の準備先が不正です");
        if (!Directory.Exists(parent)) throw new IOException("準備先の親フォルダーがありません");
        var basis = ServicePackage.Open(source, expectedManifestHash);
        if (!basis.SupportsOptionalOpenRgb || runtime.AuthorizationVersion != 1) throw new IOException("任意ランタイムに対応した汎用サービスと専用OpenRGBの組み合わせが必要です");
        var stage = Path.Combine(parent, ".flowdeck-service-" + Guid.NewGuid().ToString("N"));
        var files = new List<string>(); var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Folder(string path)
        {
            ServiceEnvironment.NoLinks(path);
            if (!Within(path, stage)) throw new IOException("一時配置の範囲外です");
            if (Directory.Exists(path)) return;
            if (path != stage) Folder(Path.GetDirectoryName(path)!);
            Directory.CreateDirectory(path); folders.Add(path);
        }
        void TrackRuntime(string root)
        {
            folders.Add(root);
            foreach (var entry in runtime.IncludedFiles)
            {
                var path = Path.Combine(root, entry.Path); files.Add(path);
                var directory = Path.GetDirectoryName(path)!;
                while (Within(directory, root)) { folders.Add(directory); if (directory == root) break; directory = Path.GetDirectoryName(directory)!; }
            }
        }
        try
        {
            Folder(stage); var payload = Path.Combine(stage, "payload"); Folder(payload);
            foreach (var (name, entry) in basis.Manifest.Files.Where(p => !ServicePackage.IsOpenRgbFile(p.Key)))
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.Combine(payload, name); Folder(Path.GetDirectoryName(target)!);
                ServiceEnvironment.NoLinks(Path.Combine(basis.PayloadDirectory, name));
                using var input = new FileStream(Path.Combine(basis.PayloadDirectory, name), FileMode.Open, FileAccess.Read, FileShare.Read);
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { files.Add(target); input.CopyTo(output); output.Flush(true); }
                using var copy = File.OpenRead(target);
                if (copy.Length != entry.Length || Convert.ToHexStringLower(SHA256.HashData(copy)) != entry.Sha256)
                    throw new IOException("準備中に元の配布ファイルが変わりました: " + name);
                checkpoint?.Invoke("copied:" + name);
            }
            var helper = Path.GetFullPath(Path.Combine(payload, ServicePackage.OpenRgbDirectory)); Folder(Path.GetDirectoryName(helper)!);
            var receipt = runtime.Prepare(archive, helper, ct); TrackRuntime(helper);
            checkpoint?.Invoke("runtime-prepared"); ct.ThrowIfCancellationRequested();
            var descriptor = new ServiceOptionalOpenRgb(receipt.DefinitionId, runtime.DefinitionDigest);
            var manifest = Path.Combine(stage, ServicePackage.ManifestName); files.Add(manifest);
            ServiceEnvironment.Write(manifest, ServicePackage.CreateManifest(payload, basis.Manifest.Release, basis.Manifest.Runtimes, descriptor));
            var prepared = ServicePackage.Open(stage); _ = runtime.Inspect(helper);
            checkpoint?.Invoke("verified"); ct.ThrowIfCancellationRequested();
            Directory.Move(stage, destination);
            return ServicePackage.Open(destination, prepared.ManifestHash);
        }
        catch (Exception error)
        {
            // Delete only files created by this preparation; retain unexpected content for inspection.
            var failures = new List<Exception> { error };
            foreach (var path in files.AsEnumerable().Reverse())
                try { ServiceEnvironment.NoLinks(path); if (File.Exists(path)) File.Delete(path); } catch (Exception e) { failures.Add(e); }
            foreach (var path in folders.OrderByDescending(p => p.Length))
                try { ServiceEnvironment.NoLinks(path); if (Directory.Exists(path)) Directory.Delete(path, false); } catch (Exception e) { failures.Add(e); }
            if (failures.Count > 1) throw new AggregateException("配布物の準備と一時配置の片付けに失敗しました", failures);
            throw;
        }
    }
}
