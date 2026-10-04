using System.Text.Json;

namespace FlowDeck.Infrastructure;

internal sealed partial class OpenRgbStartupProfile
{
    // Valid only together with the pinned process owner and pre-detection exclusion/journal.
    // These observations alone cannot establish physical resource ownership.
    public void CheckLiveEnvironment(OpenRgbSdkEnvironment environment)
    {
        if (!environment.OwnedHeadless || environment.Startup is not { } startup || environment.Plugins is not null ||
            (environment.ServerFlags & 17) != 17 || (environment.ServerFlags & 65536) != 0)
            throw new IOException("OpenRGBの所有接続と起動条件を確認できません");
        static JsonElement Property(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
                throw new IOException("OpenRGBの必要な起動設定を取得できません");
            return value;
        }
        static void Boolean(JsonElement parent, string name, bool expected)
        {
            var value = Property(parent, name);
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || value.GetBoolean() != expected)
                throw new IOException("OpenRGBの起動設定が変更されています");
        }
        static void Empty(JsonElement parent, string name)
        {
            var value = Property(parent, name);
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 0)
                throw new IOException("OpenRGBの自動接続・動的検出設定が変更されています");
        }
        static void Number(JsonElement parent, string name, int expected)
        {
            var value = Property(parent, name);
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number != expected)
                throw new IOException("OpenRGBの起動設定が変更されています");
        }
        Boolean(environment.Detectors, "hid_safe_mode", true);
        Number(environment.Detectors, "initial_detection_delay_ms", 0);
        var schema = Property(environment.DetectorSchema, "hid_safe_mode");
        if (Property(schema, "type").ValueKind != JsonValueKind.String || Property(schema, "type").GetString() != "bool")
            throw new IOException("OpenRGBの検出設定の定義が一致しません");
        var detectors = Property(environment.Detectors, "detectors");
        if (detectors.ValueKind != JsonValueKind.Object) throw new IOException("OpenRGBの検出一覧が不正です");
        var known = new HashSet<string>(Definition.Names, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in detectors.EnumerateObject())
        {
            if (!known.Contains(entry.Name) || !seen.Add(entry.Name) || entry.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                entry.Value.GetBoolean() != enabled.Contains(entry.Name, StringComparer.Ordinal))
                throw new IOException("OpenRGBの検出対象が固定条件から変更されています");
        }
        // The pinned Windows build prunes registrations excluded by conditional compilation.
        // Every enabled detector must remain. Missing disabled entries never enable new detectors:
        // the complete fixed file was checked before this exact process was resumed.
        if (enabled.Any(name => !seen.Contains(name))) throw new IOException("OpenRGBの必要な検出対象がありません");
        Boolean(environment.Server, "all_controllers", false);
        Boolean(environment.Server, "legacy_workaround", false);
        var host = Property(environment.Server, "default_host");
        if (host.ValueKind != JsonValueKind.String || host.GetString() != "127.0.0.1") throw new IOException("OpenRGBの接続先設定が変更されています");
        Number(environment.Server, "default_port", Port);
        Boolean(environment.Drivers, "shared_smbus_access", true); Number(environment.Drivers, "smbus_sleep_mode", 2);
        Empty(startup.Client, "clients"); Empty(startup.QmkOpenRgb, "devices"); Empty(startup.QmkVial, "devices");
        var orderOverrides = Property(startup.Asrock, "RGSwap");
        if (orderOverrides.ValueKind != JsonValueKind.Object || orderOverrides.EnumerateObject().Count() != 8)
            throw new IOException("ASRockの色順設定が変更されています");
        foreach (var name in AsrockOpenRgbDetectionRecovery.ZoneNames) Boolean(orderOverrides, name, false);
        var handover = Property(startup.Asrock, "FlowDeck");
        Number(handover, "version", 1);
        var paths = Property(handover, "hid_paths");
        if (handover.EnumerateObject().Count() != 2 || paths.ValueKind != JsonValueKind.Array ||
            paths.GetArrayLength() != capturedHidPaths.Length || paths.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String) ||
            !paths.EnumerateArray().Select(p => p.GetString()).SequenceEqual(capturedHidPaths, StringComparer.Ordinal))
            throw new IOException("OpenRGBの保存済み検出対象が変更されています");
        foreach (var name in ProfileNames)
        {
            var profile = Property(startup.Profiles, name); Boolean(profile, "enabled", false);
            var selected = Property(profile, "name");
            if (selected.ValueKind != JsonValueKind.String || selected.GetString() != "") throw new IOException("OpenRGBの自動プロファイル設定が変更されています");
        }
    }
}
