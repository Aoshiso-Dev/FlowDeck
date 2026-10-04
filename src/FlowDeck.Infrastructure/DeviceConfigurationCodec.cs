using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DevicePreferences(int Version, UiSettings Ui, M5Settings M5, StartupSettings Startup,
    DeviceNotificationSettings Notifications, TransferPalette Palette, DeviceProviderPreferences? Providers = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeviceM5Selection? M5Selection = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool M5ConnectOnStart = false);
public sealed record MigratedReference(string Source, string LegacyDeviceId, string LegacyEndpointId, EndpointReference Target);
public sealed record ConfigurationMigration(int Version, string SourceFormat, string SourceHash, MigratedReference[] References, string[] Notices);
public sealed record DeviceConfigurationPackage(DeviceRegistrations Devices, DeviceBindings Bindings, CoolingConfiguration Cooling,
    LightingConfiguration Lighting, DeviceDashboardConfiguration Dashboard, DevicePreferences Preferences, ConfigurationMigration Migration);

/// <summary>Seven typed, cross-validated components. Neither execution paths nor credentials are part of the new configuration.</summary>
public static class DeviceConfigurationCodec
{
    public static byte[] SerializePackage(DeviceConfigurationPackage value)
    {
        Validate(value); var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Contract.Json);
        if (bytes.Length > DeviceConfigurationProtocol.MaximumBytes) throw new IOException("設定は8MB以内にしてください");
        return bytes;
    }
    public static DeviceConfigurationPackage DeserializePackage(byte[] bytes)
    {
        if (bytes.Length is < 2 or > DeviceConfigurationProtocol.MaximumBytes) throw new IOException("設定の長さが不正です");
        try
        {
            using var json = JsonDocument.Parse(bytes); Unique(json.RootElement);
            var value = json.RootElement.Deserialize<DeviceConfigurationPackage>(Contract.Json) ?? throw new IOException("設定が空です");
            Validate(value); return value;
        }
        catch (JsonException e) { throw new IOException("設定の形式が不正です", e); }
    }
    public static void Validate(DeviceConfigurationPackage value)
    {
        try
        {
            DeviceConfigurationRules.Validate(value.Devices, value.Bindings, value.Cooling, value.Lighting, value.Dashboard);
            var p = value.Preferences;
            if (p is not { Version: 2, Ui: not null, M5: not null, Startup: not null, Notifications: not null, Palette: not null } ||
                p.Ui.Theme is not ("system" or "light" or "dark") || !double.IsFinite(p.Ui.Width) || !double.IsFinite(p.Ui.Height) ||
                p.Ui.Width is < 1 or > 16384 || p.Ui.Height is < 1 or > 16384 || p.Ui.Left is { } x && !double.IsFinite(x) || p.Ui.Top is { } y && !double.IsFinite(y) ||
                p.M5.Layout is not ("A" or "B") || p.M5.Transport is not ("usb" or "wifi" or "auto") || p.M5.WifiPort is < 1024 or > 65535 ||
                !OptionalText(p.M5.Port, 128) || !OptionalText(p.M5.DeviceId, 64) || !OptionalText(p.M5.WifiAddress, 128) || !OptionalText(p.M5.WifiInterfaceId, 128) ||
                p.Palette.Favorites is not { Length: <= 32 } || p.Palette.Recent is not { Length: <= 32 } ||
                p.Palette.Favorites.Concat(p.Palette.Recent).Any(c => c is null || c.Length != 7 || c[0] != '#' || !c[1..].All(Uri.IsHexDigit)))
                throw new IOException("表示・接続の設定形式が不正です");
            if (p.Providers is { } providers && !providers.Valid()) throw new IOException("OpenRGBのポート・選択機器が不正です");
            DeviceM5Configuration.ValidateStored(value);
            if (DeviceNotifications.Validate(p.Notifications) is { } notificationError) throw new IOException(notificationError);
            foreach (var rule in p.Notifications.Rules ?? [])
            {
                var endpoint = value.Devices.Devices.SingleOrDefault(d => d.Id == rule.Target.DeviceId)?.Endpoints.SingleOrDefault(e => e.Id == rule.Target.EndpointId);
                if (endpoint is null || (rule.Measurement == DeviceFeature.Temperature ? endpoint.Kind != EndpointKind.Sensor :
                    endpoint.Kind is not (EndpointKind.Sensor or EndpointKind.Cooling))) throw new IOException("通知の対象に温度・回転数がありません");
            }
            var migration = value.Migration;
            if (migration is not { Version: 2, References.Length: <= 8192, Notices.Length: <= 256 } ||
                migration.SourceFormat is not ("legacy-v1" or "native-v2" or "portable-v2") || migration.SourceHash?.Length != 64 || !migration.SourceHash.All(Uri.IsHexDigit) ||
                migration.References.Any(r => r is null || !DeviceConfigurationRules.Text(r.Source, 64) || !DeviceConfigurationRules.Text(r.LegacyDeviceId, 2048) ||
                    !DeviceConfigurationRules.Text(r.LegacyEndpointId, 512) || !value.Devices.Devices.Any(d => d.Id == r.Target?.DeviceId && d.Endpoints.Any(e => e.Id == r.Target.EndpointId))) ||
                migration.References.DistinctBy(r => (r.Source, r.LegacyDeviceId, r.LegacyEndpointId)).Count() != migration.References.Length ||
                migration.Notices.Any(n => !DeviceConfigurationRules.Text(n, 512))) throw new IOException("移行記録の形式が不正です");
        }
        catch (DeviceControlCapacityException e) { throw new IOException(e.Message, e); }
        catch (Exception e) when (e is ArgumentException or NullReferenceException) { throw new IOException("汎用設定の形式が不正です", e); }
    }
    private static bool OptionalText(string? value, int max) => value is null || DeviceConfigurationRules.Text(value, max);
    public static Dictionary<string, byte[]> Write(DeviceConfigurationPackage value)
    {
        Validate(value);
        return new(StringComparer.Ordinal)
        {
            ["devices.json"] = JsonSerializer.SerializeToUtf8Bytes(value.Devices, Contract.Json),
            ["bindings.json"] = JsonSerializer.SerializeToUtf8Bytes(value.Bindings, Contract.Json),
            ["cooling.json"] = JsonSerializer.SerializeToUtf8Bytes(value.Cooling, Contract.Json),
            ["lighting.json"] = JsonSerializer.SerializeToUtf8Bytes(value.Lighting, Contract.Json),
            ["dashboard.json"] = JsonSerializer.SerializeToUtf8Bytes(value.Dashboard, Contract.Json),
            ["preferences.json"] = JsonSerializer.SerializeToUtf8Bytes(value.Preferences, Contract.Json),
            ["migration.json"] = JsonSerializer.SerializeToUtf8Bytes(value.Migration, Contract.Json)
        };
    }
    public static DeviceConfigurationPackage Read(IReadOnlyDictionary<string, byte[]> files)
    {
        if (files.Count != ConfigurationGenerationStore.ComponentNames.Count || files.Keys.Any(n => !ConfigurationGenerationStore.ComponentNames.Contains(n)) ||
            files.Values.Any(b => b is null || b.Length > 1024 * 1024) || files.Values.Sum(b => (long)b.Length) > SettingsTransfer.MaximumBytes)
            throw new IOException("汎用設定のファイル一式が不正です");
        T Read<T>(string name)
        {
            try
            {
                using var json = JsonDocument.Parse(files[name]); Unique(json.RootElement);
                return json.RootElement.Deserialize<T>(Contract.Json) ?? throw new IOException(name + " が空です");
            }
            catch (JsonException e) { throw new IOException(name + " の形式が不正です", e); }
        }
        var value = new DeviceConfigurationPackage(Read<DeviceRegistrations>("devices.json"), Read<DeviceBindings>("bindings.json"),
            Read<CoolingConfiguration>("cooling.json"), Read<LightingConfiguration>("lighting.json"), Read<DeviceDashboardConfiguration>("dashboard.json"),
            Read<DevicePreferences>("preferences.json"), Read<ConfigurationMigration>("migration.json"));
        Validate(value); return value;
    }
    internal static void Unique(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in e.EnumerateObject()) { if (!names.Add(p.Name)) throw new IOException("設定項目が重複しています"); Unique(p.Value); }
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var item in e.EnumerateArray()) Unique(item);
    }
    public static DeviceConfigurationPackage Portable(DeviceConfigurationPackage value)
    {
        Validate(value);
        var result = value with
        {
            Devices = value.Devices with { Devices = value.Devices.Devices.Select(d => d with { Endpoints = d.Endpoints.Select(e =>
                e.Setup is { } setup ? e with { Setup = setup with { LedCountConfirmed = false } } : e).ToArray() }).ToArray() },
            Bindings = new(2, null, []),
            Cooling = value.Cooling with { Endpoints = value.Cooling.Endpoints.Select(c => c with { WiringConfirmed = false, SavedPolicy = null, ResumeOnStart = false }).ToArray() },
            Lighting = value.Lighting with { Endpoints = value.Lighting.Endpoints?.Select(e => e with { WiringConfirmed = false }).ToArray() },
            Preferences = value.Preferences with { Startup = new(), M5 = new(Layout: value.Preferences.M5.Layout), M5Selection = value.Preferences.M5Selection?.WithoutControl(),
                M5ConnectOnStart = false, Ui = value.Preferences.Ui with { Left = null, Top = null }, Providers = new() },
            Migration = new(2, "portable-v2", SettingsTransfer.Hash(value.Devices), [], [])
        };
        Validate(result); return Contract.Copy(result);
    }
    public static ConfigurationGenerationStore Store(string directory) => new(directory, files => _ = Read(files));
}
