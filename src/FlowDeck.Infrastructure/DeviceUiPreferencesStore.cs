using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceUiPreferences(int Version, UiSettings Window);

/// <summary>Per-user appearance only. Never reads or changes the service's authoritative device configuration.</summary>
public sealed class DeviceUiPreferencesStore(string path)
{
    private readonly SemaphoreSlim access = new(1);
    public string PathName { get; } = Path.GetFullPath(path);
    public string? LoadWarning { get; private set; }
    public static DeviceUiPreferencesStore ForCurrentUser() => new(Path.Combine(
        DeviceStartupOptions.DefaultDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), "ui-v2.json"));
    public static bool Valid(UiSettings? value) => value is not null && value.Theme is "system" or "light" or "dark" &&
        double.IsFinite(value.Width) && double.IsFinite(value.Height) && value.Width is >= 1 and <= 16384 && value.Height is >= 1 and <= 16384 &&
        (value.Left is null || double.IsFinite(value.Left.Value)) && (value.Top is null || double.IsFinite(value.Top.Value));
    public UiSettings Load(UiSettings? seed = null)
    {
        LoadWarning = null;
        var fallback = Valid(seed) ? seed! : new UiSettings();
        if (!File.Exists(PathName)) return fallback;
        try
        {
            var saved = ServiceEnvironment.Read<DeviceUiPreferences>(PathName);
            if (saved is not { Version: 1 } || !Valid(saved.Window)) throw new IOException("表示設定の形式が不正です");
            return saved.Window;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            LoadWarning = "表示設定を読み込めませんでした。変更を保存するまで元のファイルを保持します。";
            return fallback;
        }
    }
    public async Task SaveAsync(UiSettings value)
    {
        if (!Valid(value)) throw new ArgumentException("表示設定が不正です");
        await access.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(() => ServiceEnvironment.Write(PathName, new DeviceUiPreferences(1, value))).ConfigureAwait(false); }
        finally { access.Release(); }
    }
}
