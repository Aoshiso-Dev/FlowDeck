using System.Diagnostics;
using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// Choosing a profile is a compiled route decision. It is never deserialized from user settings.
internal enum OpenRgbStartupKind { NoDetection, AsrockPolychromeUsb }

internal sealed partial class OpenRgbStartupProfile
{
    private sealed record Catalog(int Version, string SourceRevision, string SourceArchiveSha256, string Platform, string[] Names);
    private static readonly Catalog Definition = Load();
    private readonly string[] enabled;
    private readonly byte[] settings;
    private readonly string[] capturedHidPaths;
    public const string SourceRevision = "81bbe18a84c2e507006f19dd252e397e40a56bfe";
    public OpenRgbStartupKind Kind { get; }
    public int Port { get; }
    public string SettingsDigest { get; }
    public IReadOnlyList<string> EnabledDetectors => Array.AsReadOnly(enabled);
    public static IReadOnlyList<string> DetectorNames => Array.AsReadOnly(Definition.Names);
    public byte[] Settings => settings.ToArray();
    public IReadOnlyList<string> CapturedHidPaths => Array.AsReadOnly(capturedHidPaths);
    private static readonly string[] ProfileNames = ["open_profile", "exit_profile", "resume_profile", "suspend_profile",
        "service_startup_profile", "service_shutdown_profile"];

    public OpenRgbStartupProfile(OpenRgbStartupKind kind, int port) : this(kind, port, []) { }

    private OpenRgbStartupProfile(OpenRgbStartupKind kind, int port, string[] paths)
    {
        if (port is < 1024 or > 65535 || !Enum.IsDefined(kind)) throw new ArgumentException("OpenRGBの起動条件が不正です");
        Kind = kind; Port = port;
        capturedHidPaths = paths.ToArray();
        enabled = kind == OpenRgbStartupKind.NoDetection ? [] : ["ASRock Polychrome USB"];
        var values = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Detectors"] = new { hid_safe_mode = true, initial_detection_delay_ms = 0,
                detectors = Definition.Names.ToDictionary(n => n, n => enabled.Contains(n, StringComparer.Ordinal), StringComparer.Ordinal) },
            ["Client"] = new { clients = Array.Empty<object>() },
            ["Server"] = new { default_host = "127.0.0.1", default_port = port, all_controllers = false, legacy_workaround = false },
            ["ProfileManager"] = ProfileNames.ToDictionary(n => n, _ => new { enabled = false, name = "" }, StringComparer.Ordinal),
            ["QMKOpenRGBDevices"] = new { devices = Array.Empty<object>() },
            ["QMKVialRGBDevices"] = new { devices = Array.Empty<object>() },
            ["ASRock Polychrome USB"] = new
            {
                RGSwap = AsrockOpenRgbDetectionRecovery.ZoneNames.ToDictionary(n => n, _ => false, StringComparer.Ordinal),
                FlowDeck = new { version = 1, hid_paths = capturedHidPaths }
            },
            ["Drivers"] = new { shared_smbus_access = true, smbus_sleep_mode = 2 },
            ["General"] = new { low_priority = true }
        };
        settings = JsonSerializer.SerializeToUtf8Bytes(values);
        SettingsDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(settings)).ToLowerInvariant();
    }
    // Called by the prepared detection scope, never from user configuration. The pinned helper
    // consumes this list once, before opening HID. It is not a device identity/continuity proof.
    public OpenRgbStartupProfile WithCapturedAsrockPaths(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var copy = paths.Take(DeviceRegistry.MaximumDevices + 1).ToArray();
        if (Kind != OpenRgbStartupKind.AsrockPolychromeUsb || copy.Length > DeviceRegistry.MaximumDevices ||
            copy.Any(p => p is null || p.Length is < 1 or > 2048 || p.Any(c => c < '!' || c > '~')) ||
            copy.Distinct(StringComparer.OrdinalIgnoreCase).Count() != copy.Length)
            throw new IOException("OpenRGBへ引き渡すHIDパスが不正です");
        return new(Kind, Port, copy.Select(p => p.ToLowerInvariant()).Order(StringComparer.Ordinal).ToArray());
    }
    private static Catalog Load()
    {
        using var stream = typeof(OpenRgbStartupProfile).Assembly.GetManifestResourceStream("FlowDeck.DeviceDefinitions.OpenRgbDetectors.json")
            ?? throw new IOException("OpenRGBの検出定義がありません");
        var value = JsonSerializer.Deserialize<Catalog>(stream, Contract.Json);
        if (value is not { Version: 1, Names.Length: 1911, Platform: "windows-x64-conditional-superset" } || value.SourceRevision != SourceRevision ||
            value.SourceArchiveSha256 != "2F02829016EC955C301C5FE27F9CBBCB086EFA923E81A8F91B96B001BF59EAEE" ||
            value.Names.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 256 || n.Any(char.IsControl)) ||
            value.Names.Distinct(StringComparer.Ordinal).Count() != value.Names.Length || !value.Names.Contains("ASRock Polychrome USB"))
            throw new IOException("OpenRGBの固定検出定義が一致しません");
        return value;
    }
    // No process is created here. The caller must establish protected paths, exclusive route
    // ownership and pre-detection recovery before handing this plan to the owned-process launcher.
    public ProcessStartInfo CreateStartInfo(string runtimeDirectory, string configurationDirectory)
    {
        if (!Path.IsPathFullyQualified(runtimeDirectory) || !Path.IsPathFullyQualified(configurationDirectory))
            throw new IOException("OpenRGBの起動には絶対パスが必要です");
        var runtime = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeDirectory));
        var config = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configurationDirectory));
        if (runtime.Equals(config, StringComparison.OrdinalIgnoreCase) || config.StartsWith(runtime + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            runtime.StartsWith(config + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("OpenRGBの実行ファイルと可変設定は別の領域にしてください");
        var start = new ProcessStartInfo(Path.Combine(runtime, "OpenRGB.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = config };
        foreach (var arg in new[] { "--config", config, "--noautoconnect", "--server", "--server-host", "127.0.0.1",
            "--server-port", Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--loglevel", "warning" }) start.ArgumentList.Add(arg);
        if (Kind == OpenRgbStartupKind.NoDetection) start.ArgumentList.Add("--nodetect");
        // Avoid inherited Qt/plugin, PATH and user configuration overrides in a SYSTEM helper.
        start.Environment.Clear();
        start.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        start.Environment["WINDIR"] = start.Environment["SystemRoot"];
        start.Environment["PATH"] = Environment.SystemDirectory;
        start.Environment["APPDATA"] = config;
        start.Environment["LOCALAPPDATA"] = config;
        start.Environment["TEMP"] = config;
        start.Environment["TMP"] = config;
        return start;
    }
}
