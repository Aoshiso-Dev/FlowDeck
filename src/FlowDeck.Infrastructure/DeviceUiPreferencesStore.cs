using System.Text.Json;
using System.Text.Json.Serialization;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceWorkspaceUiState(int SelectedPage = 0, EndpointReference[]? LightingTargets = null)
{
    public static DeviceWorkspaceUiState Normalize(DeviceWorkspaceUiState? value) => new(
        value?.SelectedPage is >= 0 and <= 3 ? value.SelectedPage : 0,
        value?.LightingTargets is { Length: <= DeviceServiceProtocol.MaximumTargets } targets
            ? targets.Where(DeviceConfigurationRules.ValidReference).Distinct().ToArray() : []);

    public EndpointReference[] AvailableLightingTargets(IEnumerable<EndpointReference> available)
    {
        var current = available.ToHashSet();
        return Normalize(this).LightingTargets!.Where(current.Contains).ToArray();
    }
}

public sealed record DeviceUiPreferences(int Version, UiSettings Window, DeviceWorkspaceUiState? Workspace = null,
    [property: JsonConverter(typeof(DeviceAccentColorJsonConverter))] string AccentColor = DeviceAccentColor.Default);

/// <summary>Per-user appearance and navigation only. Never reads or changes the service's authoritative device configuration.</summary>
public sealed class DeviceUiPreferencesStore(string path)
{
    private readonly SemaphoreSlim access = new(1);
    private DeviceWorkspaceUiState workspace = DeviceWorkspaceUiState.Normalize(null);
    public DeviceWorkspaceUiState Workspace => DeviceWorkspaceUiState.Normalize(workspace);
    public string AccentColor { get; private set; } = DeviceAccentColor.Default;
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
        workspace = DeviceWorkspaceUiState.Normalize(null);
        AccentColor = DeviceAccentColor.Default;
        var fallback = Valid(seed) ? seed! : new UiSettings();
        if (!File.Exists(PathName)) return fallback;
        try
        {
            var saved = ServiceEnvironment.Read<DeviceUiPreferences>(PathName);
            if (saved is not { Version: 1 } || !Valid(saved.Window)) throw new IOException("表示設定の形式が不正です");
            workspace = DeviceWorkspaceUiState.Normalize(saved.Workspace);
            AccentColor = DeviceAccentColor.Normalize(saved.AccentColor);
            return saved.Window;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            LoadWarning = "表示設定を読み込めませんでした。変更を保存するまで元のファイルを保持します。";
            return fallback;
        }
    }
    public async Task SaveAsync(UiSettings value, DeviceWorkspaceUiState? state = null, string? accentColor = null)
    {
        if (!Valid(value)) throw new ArgumentException("表示設定が不正です");
        if (accentColor is not null && !DeviceAccentColor.Valid(accentColor))
            throw new ArgumentException("アクセントカラーは #RRGGBB 形式で指定してください", nameof(accentColor));
        var next = state is null ? null : DeviceWorkspaceUiState.Normalize(state);
        await access.WaitAsync().ConfigureAwait(false);
        try
        {
            next ??= workspace;
            var nextAccent = accentColor is null ? AccentColor : DeviceAccentColor.Normalize(accentColor);
            await Task.Run(() => ServiceEnvironment.Write(PathName, new DeviceUiPreferences(1, value, next, nextAccent))).ConfigureAwait(false);
            workspace = next;
            AccentColor = nextAccent;
        }
        finally { access.Release(); }
    }
}
