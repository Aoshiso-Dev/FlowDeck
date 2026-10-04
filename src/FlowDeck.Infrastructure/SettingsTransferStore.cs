using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record SettingsRestoreRequest(int Version, string Id, string Baseline, SettingsArchive Archive);
public sealed record SettingsRestoreJournal(int Version, string Id, string State, Dictionary<string, byte[]> Before, string Sha256);
public sealed record SettingsRestoreResult(bool Attempted = false, bool Restored = false, string? Message = null, string? BackupPath = null);

/// <summary>Queues a reviewed restore. Applies before any runtime is created, with a durable rollback journal.</summary>
public sealed class SettingsTransferStore(string directory)
{
    private string Root => Path.GetFullPath(directory);
    private string Work => Path.Combine(Root, "settings-transfer");
    private string Pending => Path.Combine(Work, "pending.json");
    private string Journal => Path.Combine(Work, "journal.json");
    public bool HasPending => File.Exists(Pending);
    // Fault injection only for deterministic transaction tests. Production leaves this null.
    internal Action<int>? AfterFileWritten { get; set; }
    private static readonly string[] FixedFiles = ["settings.json", "lighting-scenes.json", "lighting-groups.json", "lighting-names.json", "lighting-adjustment.json", "notifications.json", "color-palette.json"];
    internal static bool Allowed(string name) => FixedFiles.Contains(name) || name.StartsWith("lighting-adjustment-", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal) &&
        Guid.TryParseExact(name[20..^5], "N", out var id) && name == SettingsTransfer.AdjustmentFile(id.ToString("N"));
    private FileStream Lock()
    {
        CheckDirectory(Root); CheckDirectory(Work); Directory.CreateDirectory(Work);
        CheckFile(Path.Combine(Work, "operation.lock"));
        return new FileStream(Path.Combine(Work, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private static void CheckDirectory(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.LinkTarget is not null) throw new IOException("設定の復元先にはシンボリックリンクやジャンクションを使用できません");
    }
    private static void CheckFile(string path)
    { if (new FileInfo(path).LinkTarget is not null) throw new IOException("設定ファイルにはリンクを使用できません"); }
    private static byte[] ReadFile(string path, int maximum = SettingsTransfer.MaximumBytes)
    {
        CheckFile(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > maximum) throw new IOException("設定ファイルが大きすぎます");
        // Enforce the bound even if another process extends the file while reading.
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = file.Read(buffer)) != 0) { if (output.Length + count > maximum) throw new IOException("設定ファイルが大きすぎます"); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
    private Dictionary<string, byte[]> CaptureFiles()
    {
        CheckDirectory(Root);
        var names = Directory.Exists(Root) ? Directory.EnumerateFiles(Root, "*.json").Select(Path.GetFileName).OfType<string>().Where(Allowed).Order(StringComparer.Ordinal).ToArray() : [];
        if (names.Length > 256) throw new IOException("設定ファイル数が上限を超えています");
        var files = names.ToDictionary(n => n, n => ReadFile(Path.Combine(Root, n), 1024 * 1024));
        if (files.Values.Sum(v => (long)v.Length) > SettingsTransfer.MaximumBytes) throw new IOException("設定一式が8MBを超えています");
        return files;
    }
    private Dictionary<string, byte[]> StableFiles()
    {
        var first = CaptureFiles(); var second = CaptureFiles();
        if (!EqualFiles(first, second)) throw new IOException("設定の保存中です。完了後にやり直してください");
        return second;
    }
    private static bool EqualFiles(Dictionary<string, byte[]> a, Dictionary<string, byte[]> b) => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var value) && value.AsSpan().SequenceEqual(p.Value));
    public TransferContent Capture(TransferDevices? devices = null)
    { using var guard = Lock(); return SettingsTransfer.FromFiles(StableFiles(), devices); }
    public SettingsArchive Export(string destination, TransferDevices? devices = null)
    {
        using var guard = Lock(); var archive = SettingsTransfer.Create(SettingsTransfer.FromFiles(StableFiles(), devices));
        var full = Path.GetFullPath(destination);
        if (full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("書き出し先は設定フォルダーの外を選んでください");
        Atomic(full, JsonSerializer.SerializeToUtf8Bytes(archive, SettingsTransfer.Indented)); return archive;
    }
    public SettingsRestorePreview Preview(string source, TransferDevices? devices = null) => Preview(SettingsTransfer.Read(ReadFile(source)), devices);
    public SettingsRestorePreview Preview(SettingsArchive archive, TransferDevices? devices = null)
    {
        using var guard = Lock(); archive = SettingsTransfer.Read(JsonSerializer.SerializeToUtf8Bytes(archive, Contract.Json));
        var local = SettingsTransfer.FromFiles(StableFiles(), devices);
        return new(archive, SettingsTransfer.Fingerprint(local), SettingsTransfer.Differences(local, archive.Content), SettingsTransfer.CheckDevices(archive.Content, local));
    }
    public void Schedule(SettingsRestorePreview preview)
    {
        using var guard = Lock();
        if (HasPending || File.Exists(Journal)) throw new IOException("復元予約または復旧処理が残っています");
        var archive = SettingsTransfer.Read(JsonSerializer.SerializeToUtf8Bytes(preview.Archive, Contract.Json));
        var current = SettingsTransfer.FromFiles(StableFiles());
        if (preview.Baseline != SettingsTransfer.Fingerprint(current)) throw new IOException("確認後に設定が変わりました。差分を再確認してください");
        var request = new SettingsRestoreRequest(1, Guid.NewGuid().ToString("N"), preview.Baseline, archive);
        Atomic(Pending, JsonSerializer.SerializeToUtf8Bytes(request, SettingsTransfer.Indented));
    }
    public void Cancel()
    {
        using var guard = Lock(); if (File.Exists(Journal)) throw new IOException("復旧処理が残っています。再起動してください");
        if (File.Exists(Pending)) File.Delete(Pending);
    }
    private static T ReadObject<T>(string path, int maximum = 24 * 1024 * 1024)
    {
        var bytes = ReadFile(path, maximum); using var document = JsonDocument.Parse(bytes); SettingsTransfer.NoDuplicates(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Contract.Json) ?? throw new IOException("復旧情報が空です");
    }
    public SettingsRestoreResult ApplyAtStartup(bool service = false)
    {
        using var guard = Lock();
        if (File.Exists(Journal)) return Recover(); // Recovery failure stops startup before any device is opened.
        if (!HasPending) return new();
        SettingsRestoreRequest request;
        try
        {
            request = ReadObject<SettingsRestoreRequest>(Pending);
            if (request.Version != 1 || !Id(request.Id)) throw new IOException("復元予約の形式が不正です");
            _ = SettingsTransfer.Read(JsonSerializer.SerializeToUtf8Bytes(request.Archive, Contract.Json));
        }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException or NullReferenceException)
        { RetirePending("invalid"); return new(true, Message: "復元予約を拒否しました: " + e.Message); }
        var before = StableFiles(); var local = SettingsTransfer.FromFiles(before);
        if (SettingsTransfer.Fingerprint(local) != request.Baseline)
        { RetirePending("changed"); return new(true, Message: "復元予約後に設定が変わったため、復元しませんでした。差分を再確認してください"); }
        var settings = before.TryGetValue("settings.json", out var original) ? JsonSerializer.Deserialize<AppSettings>(original, Contract.Json)! : new();
        var after = SettingsTransfer.ToFiles(request.Archive.Content, settings, service);
        var backupDirectory = Path.Combine(Work, request.Id); Directory.CreateDirectory(backupDirectory); CheckDirectory(backupDirectory);
        var backup = Path.Combine(backupDirectory, "before.flowdeck.json");
        Atomic(backup, JsonSerializer.SerializeToUtf8Bytes(SettingsTransfer.Create(local), SettingsTransfer.Indented));
        var journal = new SettingsRestoreJournal(1, request.Id, "applying", before, SettingsTransfer.Hash(before));
        Atomic(Journal, JsonSerializer.SerializeToUtf8Bytes(journal, Contract.Json));
        try
        {
            WriteFiles(after, before.Keys.Union(after.Keys).ToArray(), true);
            // Read back every byte before committing. No hardware or runtime exists yet.
            if (!EqualFiles(after, CaptureFiles())) throw new IOException("復元後の保存内容が一致しません");
            Atomic(Journal, JsonSerializer.SerializeToUtf8Bytes(journal with { State = "committed" }, Contract.Json));
            RetirePending("applied"); ArchiveJournal(request.Id);
            return new(true, true, "設定を復元しました。ファンは配線確認後に制御を開始してください", backup);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var recovered = Recover(); return recovered.Restored ? recovered : recovered with { Message = "復元に失敗したため元の設定に戻しました: " + e.Message };
        }
    }
    private SettingsRestoreResult Recover()
    {
        var journal = ReadObject<SettingsRestoreJournal>(Journal);
        if (journal.Version != 1 || !Id(journal.Id) || journal.State is not ("applying" or "committed" or "rolledback") || journal.Before is null ||
            journal.Before.Count > 256 || journal.Before.Any(p => !Allowed(p.Key) || p.Value is null || p.Value.Length > 1024 * 1024) ||
            journal.Before.Values.Sum(v => (long)v.Length) > SettingsTransfer.MaximumBytes || journal.Sha256 != SettingsTransfer.Hash(journal.Before))
            throw new IOException("復旧情報を検証できません。機器を開かず起動を停止しました");
        if (journal.State == "applying")
        {
            WriteFiles(journal.Before, CaptureFiles().Keys.Union(journal.Before.Keys).ToArray(), false);
            if (!EqualFiles(journal.Before, CaptureFiles())) throw new IOException("元の設定を復旧できません。機器を開かず起動を停止しました");
            Atomic(Journal, JsonSerializer.SerializeToUtf8Bytes(journal with { State = "rolledback" }, Contract.Json));
        }
        RetirePending("recovered"); ArchiveJournal(journal.Id);
        return new(true, journal.State == "committed", journal.State == "committed" ? "設定の復元を完了しました" : "中断した復元を検出し、元の設定に戻しました", Path.Combine(Work, journal.Id, "before.flowdeck.json"));
    }
    private static bool Id(string? value) => Guid.TryParseExact(value, "N", out var id) && id.ToString("N") == value;
    private void WriteFiles(Dictionary<string, byte[]> values, string[] names, bool inject)
    {
        var count = 0;
        foreach (var name in names.Order(StringComparer.Ordinal))
        {
            if (!Allowed(name)) throw new IOException("復元対象外のファイル名です");
            var target = Path.Combine(Root, name); CheckFile(target);
            if (values.TryGetValue(name, out var bytes)) Atomic(target, bytes); else if (File.Exists(target)) File.Delete(target);
            if (inject) AfterFileWritten?.Invoke(++count);
        }
    }
    private void RetirePending(string reason)
    { if (File.Exists(Pending)) File.Move(Pending, Path.Combine(Work, "request-" + reason + "-" + Guid.NewGuid().ToString("N") + ".json")); }
    private void ArchiveJournal(string id)
    {
        var target = Path.Combine(Work, id); Directory.CreateDirectory(target); CheckDirectory(target);
        File.Move(Journal, Path.Combine(target, "transaction.json"), true);
    }
    private static void Atomic(string path, byte[] data)
    {
        CheckFile(path); var folder = Path.GetDirectoryName(Path.GetFullPath(path))!; CheckDirectory(folder); Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, ".flowdeck-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(data); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
