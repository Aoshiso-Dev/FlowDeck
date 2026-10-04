using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed class JsonSettingsStore(string directory) : ISettingsStore
{
    private readonly SemaphoreSlim gate = new(1);
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string? LoadWarning { get; private set; }
    public async Task<AppSettings> LoadAsync()
    {
        var path = Path.Combine(DirectoryPath, "settings.json");
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(path), Contract.Json);
            if (settings is null || settings.SchemaVersion != 1 || settings.Profiles is null || settings.Hardware is null ||
                settings.Limits is null || settings.Ui is null || settings.M5 is null || settings.Startup is null || MotherboardFans.ValidateSettings(settings.BoardFans) is not null ||
                settings.LightingPorts is null || settings.LightingPorts.Count > 5 || settings.LightingPorts.Any(p => p.Key is not ("led1" or "led2" or "led3" or "led4" or "led5") || p.Value is null || string.IsNullOrWhiteSpace(p.Value.HubId) || p.Value.Outcome == Outcome.WrittenUnverified && !HubLighting.Valid(p.Value.Effect)) ||
                settings.Lighting is null || settings.Lighting.Effect is not null && !HubLighting.Valid(settings.Lighting.Effect) || MotherboardDisplay.Validate(settings.Motherboard) is not null || DashboardDisplay.Validate(settings.Dashboard) is not null || CpuFanController.Validate(settings.CpuFan) is not null || CpuFanController.Validate(settings.ChassisFan, "CHA_FAN1") is not null || settings.M5.Transport is not ("usb" or "wifi" or "auto") || settings.M5.WifiPort != 47631 ||
                settings.Profiles.Select(p => p.Id).Distinct().Count() != 3 ||
                !new[] { "quiet", "balanced", "cool" }.All(id => settings.Profiles.Any(p => p.Id == id)))
                throw new JsonException("未対応または不正な設定形式");
            return settings;
        }
        catch (Exception e) when (e is IOException or JsonException or NotSupportedException or ArgumentException or NullReferenceException)
        {
            LoadWarning = "設定を読み込めないため監視のみで起動します。バックアップは自動適用しません。";
            return new();
        }
    }
    public async Task SaveAsync(AppSettings settings)
    {
        await gate.WaitAsync();
        var temporary = Path.Combine(DirectoryPath, "settings.tmp");
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var data = JsonSerializer.SerializeToUtf8Bytes(settings, new JsonSerializerOptions(Contract.Json) { WriteIndented = true });
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await stream.WriteAsync(data); stream.Flush(true); }
            var target = Path.Combine(DirectoryPath, "settings.json");
            if (File.Exists(target)) File.Replace(temporary, target, Path.Combine(DirectoryPath, "settings.backup.json"));
            else File.Move(temporary, target);
        }
        finally { gate.Release(); }
    }
}
public sealed class RollingEventLog(string directory) : IEventLog
{
    private readonly object sync = new();
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string? LastError { get; private set; }
    public void Write(string category, object data)
    {
        lock (sync)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "control.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length >= 10 * 1024 * 1024)
                {
                    for (var i = 4; i >= 1; i--)
                    {
                        var from = i == 1 ? path : path + "." + (i - 1);
                        if (File.Exists(from)) File.Move(from, path + "." + i, true);
                    }
                }
                File.AppendAllText(path, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, category, data }, Contract.Json) + "\n");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LastError = e.Message; }
        }
    }
    public void Export(string destination, object diagnostic)
    {
        lock (sync)
        {
            using var archive = System.IO.Compression.ZipFile.Open(destination, System.IO.Compression.ZipArchiveMode.Create);
            if (Directory.Exists(DirectoryPath)) foreach (var path in Directory.GetFiles(DirectoryPath, "control.jsonl*"))
                System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(archive, path, Path.GetFileName(path));
            using var writer = new StreamWriter(archive.CreateEntry("diagnostic.json").Open());
            writer.Write(JsonSerializer.Serialize(diagnostic, new JsonSerializerOptions(Contract.Json) { WriteIndented = true }));
        }
    }
}
