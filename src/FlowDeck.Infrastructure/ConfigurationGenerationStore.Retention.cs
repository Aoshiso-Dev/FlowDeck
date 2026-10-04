using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed record ConfigurationRetentionResult(int Removed, int Tracked, string? Deferred, string[] Errors);

public sealed partial class ConfigurationGenerationStore
{
    internal const int RetainedGenerations = 10, MaximumRemovalsPerPass = 4;
    private sealed record RetentionEntry(ConfigurationPointer Pointer, long Order, Dictionary<string, string>? Deleting = null);
    private sealed record RetentionState(long Sequence, RetentionEntry[] Entries);
    private sealed record RetentionDocument(int Version, RetentionState State, string Sha256);
    private string RetentionPath => Path.Combine(Root, "generation-retention.json");
    private static bool ValidPointer(ConfigurationPointer? p) => p is { Version: 1 } && Id(p.Id) && Digest(p.ManifestHash);
    private static bool RetentionFile(string name) => name == "manifest.json" || ComponentNames.Contains(name) ||
        name.StartsWith("legacy/", StringComparison.Ordinal) && SettingsTransferStore.Allowed(name[7..]);
    private RetentionState ReadRetention()
    {
        CheckPath(RetentionPath); if (!File.Exists(RetentionPath)) return new(0, []);
        var doc = Parse<RetentionDocument>(Read(RetentionPath));
        if (doc is not { Version: 1, State: { Sequence: >= 0, Entries.Length: <= 4096 } state } ||
            doc.Sha256 != SettingsTransfer.Hash(state) || state.Entries.Any(e => e is null || !ValidPointer(e.Pointer) ||
                e.Order < 1 || e.Order > state.Sequence || e.Deleting is { } files &&
                (files.Count is < 8 or > 264 || files.Any(p => !RetentionFile(p.Key) || !Digest(p.Value)) ||
                 !ComponentNames.All(files.ContainsKey) || files.GetValueOrDefault("manifest.json") != e.Pointer.ManifestHash)) ||
            state.Entries.DistinctBy(e => e.Pointer.Id).Count() != state.Entries.Length)
            throw new IOException("設定世代の保管記録が不正です");
        return state;
    }
    private void WriteRetention(RetentionState state)
    {
        if (state.Entries.Length > 4096) throw new IOException("設定世代の保管記録が上限に達しました");
        var document = new RetentionDocument(1, state, SettingsTransfer.Hash(state));
        if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(document, Contract.Json).Length > MaximumFileBytes)
            throw new IOException("設定世代の保管記録が大きすぎます");
        ServiceEnvironment.Write(RetentionPath, document);
    }
    private ConfigurationPointer? PreviousPointer()
    {
        var path = Current + ".bak"; CheckPath(path); if (!File.Exists(path)) return null;
        var pointer = Parse<ConfigurationPointer>(Read(path, 4096));
        if (!ValidPointer(pointer)) throw new IOException("直前の設定参照が不正です");
        return pointer;
    }
    // Only generations observed as current/previous can enter this history. Unknown directories,
    // staging leftovers, rejected snapshots and deployment backups are never cleanup candidates.
    internal ConfigurationRetentionResult MaintainHistory(ConfigurationPointer expected)
    {
        using var guard = Lock();
        var current = ReadPointer();
        if (current != expected) throw new IOException("設定保管の開始前に採用世代が変更されました");
        _ = ReadGeneration(expected);
        var previous = PreviousPointer(); if (previous is not null) _ = ReadGeneration(previous);
        var history = ReadRetention(); var pinned = new HashSet<string>(StringComparer.Ordinal) { expected.Id };
        string? deferred = null;
        if (previous is not null) pinned.Add(previous.Id);
        var transition = Path.Combine(Root, DeviceProviderTransition.FileName); CheckPath(transition);
        if (File.Exists(transition)) deferred = "取得経路の復旧記録を保持しています";
        var application = Path.Combine(Root, "configuration-application.json"); CheckPath(application);
        if (File.Exists(application))
        {
            var record = Parse<ConfigurationApplication>(Read(application));
            if (record is not { Version: 1 } || !DeviceServiceProtocol.Id(record.RequestId) || !ValidPointer(record.Before) || !ValidPointer(record.After) ||
                record.Phase is not ("accepted" or "applied" or "rolled-back" or "failed" or "interrupted" or "superseded"))
                throw new IOException("設定反映の参照が不正なため世代を整理しません");
            pinned.Add(record.Before.Id); pinned.Add(record.After.Id);
            if (record.Phase == "accepted") deferred = "設定反映の完了を待っています";
        }
        var entries = history.Entries.ToDictionary(e => e.Pointer.Id, StringComparer.Ordinal); var sequence = history.Sequence;
        foreach (var pointer in previous is null ? new[] { expected } : new[] { previous, expected })
        {
            if (entries.TryGetValue(pointer.Id, out var known) && known.Pointer != pointer)
                throw new IOException("保管記録の設定世代が一致しません");
            // A fully valid generation selected for rollback is protected even if an earlier cleanup was interrupted.
            entries[pointer.Id] = new(pointer, checked(++sequence));
        }
        void Save() => WriteRetention(new(sequence, entries.Values.OrderBy(e => e.Order).ToArray()));
        Save();
        if (deferred is not null) return new(0, entries.Count, deferred, []);
        var newest = entries.Values.OrderByDescending(e => e.Order).Take(RetainedGenerations).Select(e => e.Pointer.Id).ToHashSet(StringComparer.Ordinal);
        var candidates = entries.Values.Where(e => !pinned.Contains(e.Pointer.Id) && (e.Deleting is not null || !newest.Contains(e.Pointer.Id)))
            .OrderBy(e => e.Order).Take(MaximumRemovalsPerPass).ToArray();
        var removed = 0; var errors = new List<string>();
        foreach (var entry in candidates)
        {
            try
            {
                var files = entry.Deleting;
                if (files is null)
                {
                    _ = ReadGeneration(entry.Pointer);
                    var manifestPath = Path.Combine(Generations, entry.Pointer.Id, "manifest.json");
                    var bytes = Read(manifestPath); if (Hash(bytes) != entry.Pointer.ManifestHash) throw new IOException("保管対象の一覧が変更されました");
                    var manifest = Parse<ConfigurationManifest>(bytes);
                    files = manifest.Files.Concat(manifest.LegacyFiles.Select(p => new KeyValuePair<string, string>("legacy/" + p.Key, p.Value)))
                        .Append(new("manifest.json", entry.Pointer.ManifestHash)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                    CheckRetirementContents(entry.Pointer, files, allowMissing: false);
                    // Durable intent includes the exact allowed files and hashes. A crash during deletion can resume without guessing missing bytes.
                    entries[entry.Pointer.Id] = entry with { Deleting = files }; Save(); Checkpoint?.Invoke("retention-prepared");
                }
                RemoveRetired(entry.Pointer, files);
                entries.Remove(entry.Pointer.Id); Save(); removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { errors.Add(entry.Pointer.Id + ": " + e.Message); }
        }
        return new(removed, entries.Count, null, errors.ToArray());
    }
    private string RetiredRoot(ConfigurationPointer pointer)
    {
        if (!ValidPointer(pointer)) throw new IOException("整理する設定世代が不正です");
        var root = Path.GetFullPath(Path.Combine(Generations, pointer.Id));
        if (Path.GetDirectoryName(root) != Path.GetFullPath(Generations)) throw new IOException("設定世代の保存範囲外です");
        CheckPath(Path.Combine(root, "manifest.json")); return root;
    }
    private void CheckRetirementContents(ConfigurationPointer pointer, Dictionary<string, string> files, bool allowMissing)
    {
        var root = RetiredRoot(pointer); if (!Directory.Exists(root))
        { if (allowMissing) return; throw new IOException("整理する設定世代がありません"); }
        var actual = new List<string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            CheckPath(path); var name = Path.GetFileName(path);
            if (Directory.Exists(path))
            {
                if (name != "legacy" || !files.Keys.Any(n => n.StartsWith("legacy/", StringComparison.Ordinal)))
                    throw new IOException("設定世代に未確認のフォルダーがあります");
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                {
                    CheckPath(child); if (Directory.Exists(child)) throw new IOException("設定世代に未確認のフォルダーがあります");
                    actual.Add("legacy/" + Path.GetFileName(child));
                }
            }
            else actual.Add(name);
        }
        if (actual.Any(n => !files.ContainsKey(n)) || !allowMissing && files.Keys.Any(n => !actual.Contains(n, StringComparer.Ordinal)))
            throw new IOException("設定世代に未確認のファイルまたは欠損があります");
        foreach (var name in actual)
            if (Hash(Read(Path.Combine(root, name))) != files[name]) throw new IOException("整理する設定の内容が変更されています");
    }
    private void RemoveRetired(ConfigurationPointer pointer, Dictionary<string, string> files)
    {
        CheckRetirementContents(pointer, files, allowMissing: true);
        var root = RetiredRoot(pointer); if (!Directory.Exists(root)) return;
        foreach (var name in files.Keys.OrderBy(n => n == "manifest.json" ? 1 : 0).ThenBy(n => n, StringComparer.Ordinal))
        {
            var path = Path.GetFullPath(Path.Combine(root, name));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("設定の削除対象が範囲外です");
            CheckPath(path);
            if (File.Exists(path))
            {
                if (Hash(Read(path)) != files[name]) throw new IOException("削除直前に設定が変更されています");
                File.Delete(path); Checkpoint?.Invoke("retention-file:" + name);
            }
        }
        var legacy = Path.Combine(root, "legacy"); CheckPath(Path.Combine(legacy, "unused"));
        if (Directory.Exists(legacy)) Directory.Delete(legacy, false);
        CheckPath(Path.Combine(root, "unused")); Directory.Delete(root, false);
        Checkpoint?.Invoke("retention-removed");
    }
}
