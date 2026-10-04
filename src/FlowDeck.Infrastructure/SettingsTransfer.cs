using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record TransferPalette(string[] Favorites, string[] Recent);
public sealed record TransferDevices(string? BoardName = null, string[]? SensorIds = null, BoardLightAddress[]? BoardLights = null);
public sealed record TransferContent(AppSettings Settings, LightingSceneCollection Scenes, LightingGroupCollection Groups,
    LightingNameCollection Names, NotificationSettings Notifications, TransferPalette Palette,
    Dictionary<string, LightingAdjustmentState> Adjustments, TransferDevices Devices);
public sealed record SettingsArchive(string Format, int Version, DateTimeOffset CreatedAt, string Sha256, TransferContent Content);
public sealed record SettingsDifference(string Item, string Before, string After);
public sealed record SettingsRestorePreview(SettingsArchive Archive, string Baseline, SettingsDifference[] Differences, string[] Devices);

/// <summary>Explicit portable configuration. Never exports credentials, executable paths, or runtime acknowledgements.</summary>
public static class SettingsTransfer
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    public static readonly JsonSerializerOptions Indented = new(Contract.Json) { WriteIndented = true };
    private static readonly JsonSerializerOptions PaletteJson = new(Contract.Json) { PropertyNamingPolicy = null };
    public static AppSettings Portable(AppSettings value) => value with
    {
        PythonPath = null, Startup = new(), M5 = new(Layout: value.M5.Layout), ServiceConfigurationPending = false,
        Ui = value.Ui with { Left = null, Top = null }, LastConfirmedPolicy = null,
        Hardware = value.Hardware with { WiringConfirmed = false },
        CpuFan = value.CpuFan with { WiringConfirmed = false }, ChassisFan = value.ChassisFan with { WiringConfirmed = false },
        BoardFans = value.BoardFans.ToDictionary(p => p.Key, p => p.Value with { WiringConfirmed = false }),
        Lighting = new(Effect: value.Lighting.Effect, Channels: value.Lighting.Channels),
        LightingPorts = value.LightingPorts.ToDictionary(p => p.Key, p => p.Value with { Outcome = Outcome.Unknown, SentAt = null })
    };
    public static AppSettings RestoreLocal(AppSettings imported, AppSettings local) => Portable(imported) with
    {
        PythonPath = local.PythonPath, Startup = local.Startup with { ResumeControl = false },
        M5 = local.M5 with { Layout = imported.M5.Layout }, Ui = imported.Ui with { Left = local.Ui.Left, Top = local.Ui.Top }
    };
    public static string Canonical<T>(T value)
    {
        using var json = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, Contract.Json));
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) Write(json.RootElement, writer);
        return Encoding.UTF8.GetString(stream.ToArray());
        static void Write(JsonElement e, Utf8JsonWriter w)
        {
            if (e.ValueKind == JsonValueKind.Object)
            { w.WriteStartObject(); foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { w.WritePropertyName(p.Name); Write(p.Value, w); } w.WriteEndObject(); }
            else if (e.ValueKind == JsonValueKind.Array) { w.WriteStartArray(); foreach (var c in e.EnumerateArray()) Write(c, w); w.WriteEndArray(); }
            else e.WriteTo(w);
        }
    }
    public static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(value))));
    public static string Fingerprint(TransferContent value) => Hash(value with { Devices = new() });
    public static SettingsArchive Create(TransferContent content)
    { Validate(content); return new("FlowDeck.Settings", 1, DateTimeOffset.UtcNow, Hash(content), Contract.Copy(content)); }
    public static SettingsArchive Read(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new IOException("設定ファイルは8MB以内で指定してください");
        try
        {
            // Reject duplicate properties too: different JSON readers must not see different configurations.
            using var document = JsonDocument.Parse(bytes); NoDuplicates(document.RootElement);
            var archive = JsonSerializer.Deserialize<SettingsArchive>(bytes, Contract.Json);
            if (archive is null || archive.Format != "FlowDeck.Settings" || archive.Version != 1 || archive.Content is null)
                throw new IOException("未対応の設定ファイルです");
            Validate(archive.Content);
            if (archive.Sha256 != Hash(archive.Content)) throw new IOException("設定ファイルのチェックサムが一致しません");
            return archive;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or NullReferenceException or ArgumentException)
        { throw new IOException("設定ファイルの形式が不正です", e); }
    }
    internal static void NoDuplicates(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in e.EnumerateObject()) { if (!seen.Add(p.Name)) throw new IOException("JSONの項目が重複しています"); NoDuplicates(p.Value); }
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var item in e.EnumerateArray()) NoDuplicates(item);
    }
    public static void Validate(TransferContent value)
    {
        try
        {
            ValidateSettings(value.Settings);
            if (Canonical(value.Settings) != Canonical(Portable(value.Settings))) throw new IOException("実行環境・制御確認状態を含む設定は復元できません");
            Require(LightingScenes.Validate(value.Scenes)); Require(LightingGroups.Validate(value.Groups));
            Require(LightingNames.Validate(value.Names)); Require(Notifications.Validate(value.Notifications));
            if (value.Palette is null || !Colors(value.Palette.Favorites, 12) || !Colors(value.Palette.Recent, 8)) throw new IOException("色見本が不正です");
            if (value.Adjustments is null || value.Adjustments.Count > 33) throw new IOException("明るさの設定数が不正です");
            foreach (var (id, state) in value.Adjustments)
            {
                if (id != "all" && !value.Groups.Groups!.Any(g => g.Id == id)) throw new IOException("明るさのグループが見つかりません");
                Require(LightingBrightness.Validate(state));
                if (state.Applied) throw new IOException("明るさの適用済み状態は復元できません");
                if (id != "all" && state.BaseScene is { } scene && !LightingGroups.SameTargets(value.Groups.Groups!.Single(g => g.Id == id), scene))
                    throw new IOException("グループと明るさの対象が一致しません");
            }
            if (value.Devices is null || value.Devices.BoardName is { } name && !Text(name, 256) ||
                value.Devices.SensorIds is { } ids && (ids.Length > 512 || ids.Any(id => !Text(id, 256)) || ids.Distinct().Count() != ids.Length) ||
                value.Devices.BoardLights is { } lights && (lights.Length > 256 || lights.Any(l => l is null || !Text(l.Identity, 2048) || l.Zone > 7) || lights.Distinct().Count() != lights.Length))
                throw new IOException("機器情報が不正です");
        }
        catch (Exception e) when (e is NullReferenceException or ArgumentException or InvalidOperationException)
        { throw new IOException("設定の値が不正です", e); }
    }
    private static void Require(string? error) { if (error is not null) throw new IOException(error); }
    private static bool Text(string? s, int maximum) => !string.IsNullOrWhiteSpace(s) && s.Length <= maximum && !s.Any(char.IsControl);
    private static bool Colors(string[]? values, int max) => values is not null && values.Length <= max && values.Distinct(StringComparer.OrdinalIgnoreCase).Count() == values.Length &&
        values.All(s => s is { Length: 7 } && s[0] == '#' && s.AsSpan(1).ContainsAnyExcept("0123456789ABCDEFabcdef") == false);
    private static void ValidateSettings(AppSettings s)
    {
        if (s.SchemaVersion != 1 || s.Hardware is null || s.M5.Layout is not ("A" or "B") || s.Ui.Theme is not ("system" or "light" or "dark") ||
            !double.IsFinite(s.Ui.Width) || !double.IsFinite(s.Ui.Height) || s.Ui.Width is < 640 or > 16384 || s.Ui.Height is < 480 or > 16384)
            throw new IOException("表示または設定バージョンが不正です");
        var hw = s.Hardware;
        if (hw.Hub is { } hub && (!Text(hub.Id, 2048) || !Text(hub.Description, 256) || !Text(hub.Path, 2048) || hub.Serial is { } serial && !Text(serial, 256)) ||
            !Channels(hw.Fans, "fan") || !Channels(hw.Leds, "led") || hw.WiringSignature is { Length: > 8192 } ||
            hw.LedDetails is { } details && (details.Length > 5 || details.Any(d => d is null || !Channels([d.Id], "led") || d.Accessory is null || d.Accessory.Length > 256 || d.LedCount is < 0 or > 4096) || details.DistinctBy(d => d.Id).Count() != details.Length))
            throw new IOException("Hubの識別情報または配線設定が不正です");
        var limits = s.Limits;
        if (new[] { limits.Minimum, limits.Fallback, limits.Exit }.Any(d => d is not null and not (>= 1 and <= 100)) ||
            limits.Minimum is { } min && (limits.Fallback < min || limits.Exit < min) ||
            new[] { limits.CpuOverheat, limits.CpuResumeBelow, limits.GpuOverheat, limits.GpuResumeBelow }.Any(t => t is { } v && (!double.IsFinite(v) || v is < -40 or > 150)))
            throw new IOException("ファン出力または保護温度が不正です");
        if (limits.Stop is { } stop && (!double.IsFinite(stop.CpuBelow) || !double.IsFinite(stop.GpuBelow) || stop.CpuBelow is < -40 or > 150 || stop.GpuBelow is < -40 or > 150 ||
            stop.RestartDuty is < 1 or > 100 || stop.RestartDuty < limits.Minimum || !double.IsFinite(stop.RestartSeconds) || stop.RestartSeconds is <= 0 or > 3600)) throw new IOException("停止・再始動条件が不正です");
        if (limits.AllowStop && limits.Stop is null || limits.ProtectManualCpu && !FanPolicyEngine.ThresholdsValid(limits.CpuOverheat, limits.CpuResumeBelow) ||
            limits.ProtectManualGpu && !FanPolicyEngine.ThresholdsValid(limits.GpuOverheat, limits.GpuResumeBelow)) throw new IOException("停止または手動保護の条件が不足しています");
        if (s.CpuSensorId is { } cpu && !Text(cpu, 256) || s.GpuSensorId is { } gpu && !Text(gpu, 256) || s.Profiles is not { Length: 3 } ||
            !s.Profiles.Select(p => p.Id).Order().SequenceEqual(new[] { "balanced", "cool", "quiet" })) throw new IOException("センサーまたはプリセットが不正です");
        foreach (var p in s.Profiles) Profile(p, limits, true);
        Require(CpuFanController.Validate(s.CpuFan)); Require(CpuFanController.Validate(s.ChassisFan)); Require(MotherboardFans.ValidateSettings(s.BoardFans));
        foreach (var fan in s.BoardFans.Values.Concat([s.CpuFan, s.ChassisFan])) if (fan.Profile is { } profile) Profile(profile, new(fan.Minimum, 100, 100), false);
        Require(MotherboardDisplay.Validate(s.Motherboard)); Require(DashboardDisplay.Validate(s.Dashboard));
        if (s.Lighting.Effect is { } effect && !HubLighting.Valid(effect) || !Channels(s.Lighting.Channels, "led") || s.LightingPorts.Count > 5 ||
            s.LightingPorts.Any(p => !Channels([p.Key], "led") || !Text(p.Value.HubId, 2048) || p.Value.Effect is { } e && !HubLighting.Valid(e))) throw new IOException("保存された照明設定が不正です");
    }
    private static bool Channels(string[]? ids, string prefix) => ids is null || ids.Length <= 5 && ids.Distinct().Count() == ids.Length && ids.All(id => Enumerable.Range(1, 5).Any(i => id == prefix + i));
    private static void Profile(AutoProfile p, FanLimits limits, bool emptyAllowed)
    {
        if (!Text(p.Name, 64) || !Text(p.Id, 64) || p.Revision < 0 || p.DescentDelaySeconds is { } delay && (!double.IsFinite(delay) || delay is < 0 or > 86400) ||
            p.DescentPerSecond is { } rate && (!double.IsFinite(rate) || rate is <= 0 or > 100)) throw new IOException("温度カーブの名前・下降条件が不正です");
        if (emptyAllowed && p.Cpu is null && p.Gpu is null) return;
        foreach (var curve in new[] { p.Cpu, p.Gpu }.OfType<SensorCurve>())
            if (!Text(curve.SensorId, 256) || curve.Points is not { Length: >= 2 and <= 128 } || curve.Points.Any(p => p is null || p.Temperature is < -40 or > 150) ||
                curve.Overheat is < -40 or > 150 || curve.ResumeBelow is < -40 or > 150) throw new IOException("温度カーブの範囲が不正です");
        Require(FanPolicyEngine.ValidateProfile(p, limits));
    }
    public static TransferContent FromFiles(IReadOnlyDictionary<string, byte[]> files, TransferDevices? devices = null)
    {
        T Read<T>(string name, T fallback, JsonSerializerOptions? options = null)
        {
            if (!files.TryGetValue(name, out var data)) return fallback;
            using var json = JsonDocument.Parse(data); NoDuplicates(json.RootElement);
            return JsonSerializer.Deserialize<T>(data, options ?? Contract.Json) ?? throw new IOException(name + " が空です");
        }
        try
        {
            var groups = Read("lighting-groups.json", new LightingGroupCollection(Groups: []));
            Require(LightingGroups.Validate(groups));
            var adjustments = new Dictionary<string, LightingAdjustmentState>();
            foreach (var key in new[] { "all" }.Concat(groups.Groups!.Select(g => g.Id)))
            {
                var name = AdjustmentFile(key);
                if (files.ContainsKey(name)) adjustments[key] = Read(name, new LightingAdjustmentState()) with { Applied = false };
            }
            var value = new TransferContent(Portable(Read("settings.json", new AppSettings())),
                Read("lighting-scenes.json", new LightingSceneCollection(Scenes: [])), groups,
                Read("lighting-names.json", new LightingNameCollection(Names: [])), Read("notifications.json", new NotificationSettings(Rules: [])),
                Read("color-palette.json", new TransferPalette([], []), PaletteJson), adjustments, devices ?? new());
            Validate(value); return value;
        }
        catch (Exception e) when (e is JsonException or NullReferenceException or ArgumentException or NotSupportedException)
        { throw new IOException("現在の設定を読み込めません。破損した設定を上書きせず停止しました", e); }
    }
    public static Dictionary<string, byte[]> ToFiles(TransferContent content, AppSettings local, bool serviceConfigurationPending = false)
    {
        Validate(content);
        var result = new Dictionary<string, byte[]>();
        void Add<T>(string key, T value, JsonSerializerOptions? options = null) => result[key] = JsonSerializer.SerializeToUtf8Bytes(value, options ?? Indented);
        Add("settings.json", RestoreLocal(content.Settings, local) with { ServiceConfigurationPending = serviceConfigurationPending }); Add("lighting-scenes.json", content.Scenes); Add("lighting-groups.json", content.Groups);
        Add("lighting-names.json", content.Names); Add("notifications.json", content.Notifications); Add("color-palette.json", content.Palette, PaletteJson);
        foreach (var (id, value) in content.Adjustments) Add(AdjustmentFile(id), value with { Applied = false });
        return result;
    }
    internal static string AdjustmentFile(string id) => id == "all" ? "lighting-adjustment.json" : "lighting-adjustment-" + id + ".json";
    public static SettingsDifference[] Differences(TransferContent before, TransferContent after)
    {
        var changes = new List<SettingsDifference>();
        var a = JsonSerializer.SerializeToElement(before with { Devices = new() }, Contract.Json);
        var b = JsonSerializer.SerializeToElement(after with { Devices = new() }, Contract.Json);
        Diff(a, b, ""); return changes.ToArray();
        void Diff(JsonElement a, JsonElement b, string path)
        {
            if (Canonical(a) == Canonical(b)) return;
            if (a.ValueKind is JsonValueKind.Object or JsonValueKind.Null && b.ValueKind is JsonValueKind.Object or JsonValueKind.Null &&
                (a.ValueKind == JsonValueKind.Object || b.ValueKind == JsonValueKind.Object))
            {
                var keysA = a.ValueKind == JsonValueKind.Object ? a.EnumerateObject().Select(p => p.Name) : [];
                var keysB = b.ValueKind == JsonValueKind.Object ? b.EnumerateObject().Select(p => p.Name) : [];
                foreach (var key in keysA.Union(keysB))
                { var aa = a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var av) ? av : JsonSerializer.SerializeToElement<object?>(null); var bb = b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out var bv) ? bv : JsonSerializer.SerializeToElement<object?>(null); Diff(aa, bb, path.Length == 0 ? key : path + "." + key); }
            }
            else if (a.ValueKind is JsonValueKind.Array or JsonValueKind.Null && b.ValueKind is JsonValueKind.Array or JsonValueKind.Null &&
                (a.ValueKind == JsonValueKind.Array || b.ValueKind == JsonValueKind.Array))
            {
                var countA = a.ValueKind == JsonValueKind.Array ? a.GetArrayLength() : 0; var countB = b.ValueKind == JsonValueKind.Array ? b.GetArrayLength() : 0;
                for (var i = 0; i < Math.Max(countA, countB); i++)
                    Diff(i < countA ? a[i] : JsonSerializer.SerializeToElement<object?>(null), i < countB ? b[i] : JsonSerializer.SerializeToElement<object?>(null), path + "[" + (i + 1) + "]");
            }
            else changes.Add(new(Label(path), Display(a, path), Display(b, path)));
        }
    }
    private static string Display(JsonElement e, string path)
    {
        if (path.EndsWith(".theme", StringComparison.Ordinal) && e.ValueKind == JsonValueKind.String)
            return e.GetString() switch { "system" => "システム", "light" => "ライト", "dark" => "ダーク", var text => text! };
        return e.ValueKind == JsonValueKind.Null ? "未設定" : e.ValueKind == JsonValueKind.True ? "有効" : e.ValueKind == JsonValueKind.False ? "無効" : e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText();
    }
    private static string Label(string path)
    {
        var labels = new Dictionary<string, string> { ["settings"]="基本設定", ["hardware"]="接続機器", ["limits"]="出力条件", ["minimum"]="最低出力", ["fallback"]="異常時出力", ["exit"]="終了時出力", ["profiles"]="カーブ", ["cpuFan"]="CPU_FAN1", ["chassisFan"]="CHA_FAN1", ["boardFans"]="マザーファン", ["motherboard"]="マザー", ["dashboard"]="表示項目", ["ui"]="外観", ["theme"]="テーマ", ["scenes"]="シーン", ["groups"]="グループ", ["names"]="照明名", ["notifications"]="通知", ["palette"]="色見本", ["adjustments"]="明るさ", ["brightness"]="明るさ", ["name"]="名前", ["threshold"]="しきい値", ["isOff"]="消灯", ["enabled"]="有効", ["width"]="幅", ["height"]="高さ",
            ["favorites"]="お気に入り", ["recent"]="最近使った色", ["delaySeconds"]="異常の継続秒数", ["recoverySeconds"]="復帰の継続秒数", ["repeatSeconds"]="再通知の秒数", ["desktop"]="Windows通知", ["recovery"]="復帰通知", ["controlFaults"]="制御異常", ["disconnects"]="切断通知", ["operationFailures"]="操作の失敗", ["rules"]="通知条件", ["address"]="対象", ["source"]="種類", ["deviceId"]="機器ID", ["sensorId"]="センサーID", ["kind"]="項目種別", ["hysteresis"]="復帰幅", ["missing"]="未取得を通知", ["ignoreStopped"]="意図した停止を除外",
            ["hub"]="Hub", ["board"]="マザー", ["hubId"]="Hub ID", ["identity"]="機器ID", ["zone"]="ゾーン", ["channel"]="ポート", ["id"]="識別ID", ["description"]="機器名", ["serial"]="シリアル", ["path"]="機器パス", ["fans"]="ファン", ["leds"]="照明ポート", ["ledDetails"]="LED構成", ["ledCount"]="LED数", ["accessory"]="接続機器名", ["wiringSignature"]="配線構成", ["wiringConfirmed"]="配線確認", ["cpuSensorId"]="CPUセンサー", ["gpuSensorId"]="GPUセンサー",
            ["allowStop"]="停止許可", ["stop"]="停止条件", ["cpuBelow"]="CPU停止温度", ["gpuBelow"]="GPU停止温度", ["restartDuty"]="再始動出力", ["restartSeconds"]="再始動秒数", ["protectManualCpu"]="CPU手動保護", ["protectManualGpu"]="GPU手動保護", ["cpuOverheat"]="CPU過熱温度", ["cpuResumeBelow"]="CPU復帰温度", ["gpuOverheat"]="GPU過熱温度", ["gpuResumeBelow"]="GPU復帰温度", ["profile"]="温度カーブ", ["revision"]="更新番号", ["cpu"]="センサー1", ["gpu"]="センサー2", ["points"]="カーブ点", ["temperature"]="温度", ["duty"]="出力", ["overheat"]="過熱温度", ["resumeBelow"]="復帰温度", ["descentDelaySeconds"]="下降待機秒数", ["descentPerSecond"]="毎秒下降率",
            ["lighting"]="保存した照明", ["lightingPorts"]="ポートの照明", ["effect"]="効果", ["mode"]="モード", ["colors"]="色", ["color"]="色", ["speed"]="速度", ["reverse"]="逆方向", ["channels"]="ポート", ["zones"]="ゾーン", ["sync"]="同期", ["syncTargets"]="同期対象", ["boardIdentity"]="マザー機器ID", ["baseScene"]="復帰元", ["all"]="全体", ["r"]="赤", ["g"]="緑", ["b"]="青", ["red"]="赤", ["green"]="緑", ["blue"]="青", ["sensors"]="センサー", ["displayName"]="表示名", ["showOnM5"]="M5GOに表示", ["openRgbEnabled"]="OpenRGB連携", ["openRgbPort"]="OpenRGBポート", ["rgbZones"]="RGB構成", ["confirmedLedCount"]="確認済みLED数", ["lastLighting"]="保存した照明", ["items"]="表示項目", ["visible"]="表示", ["role"]="役割", ["m5"]="M5GO", ["layout"]="レイアウト" };
        return string.Join(" / ", path.Split('.').Select(s => { var i = s.IndexOf('['); var key = i < 0 ? s : s[..i]; return labels.GetValueOrDefault(key, key) + (i < 0 ? "" : s[i..]); }));
    }
    public static string[] CheckDevices(TransferContent source, TransferContent local)
    {
        var result = new List<string>();
        if (source.Settings.Hardware.Hub is { } hub)
            result.Add(hub.Id == local.Settings.Hardware.Hub?.Id ? "Control Hub：保存先の機器IDと一致" : "Control Hub：異なる機器ID・接続先の再選択が必要");
        var savedScenes = source.Scenes.Scenes!.Concat(source.Adjustments.Values.Select(v => v.BaseScene).OfType<LightingScene>()).ToArray();
        var hubTargets = savedScenes.SelectMany(LightingGroups.HubTargets).Concat(source.Groups.Groups!.SelectMany(g => g.Hub))
            .Concat(source.Names.Names!.Select(n => n.Address.Hub).OfType<HubLightAddress>())
            .Concat(source.Settings.LightingPorts.Select(p => new HubLightAddress(p.Value.HubId, p.Key)));
        foreach (var id in hubTargets.Select(h => h.HubId).Distinct())
            if (id != local.Settings.Hardware.Hub?.Id) result.Add("未確認の照明Hub：" + id);
        foreach (var address in savedScenes.SelectMany(LightingGroups.BoardTargets).Concat(source.Groups.Groups!.SelectMany(g => g.Board))
            .Concat(source.Groups.SyncTargets ?? []).Concat(source.Names.Names!.Select(n => n.Address.Board).OfType<BoardLightAddress>()).Distinct())
            if (!(local.Devices.BoardLights ?? []).Contains(address)) result.Add("未確認のマザー照明：" + address.Identity + " / " + address.Zone);
        if (source.Devices.BoardName is { } board)
            result.Add(board == local.Devices.BoardName ? "マザー：機種名は一致（個体の一致は保証しません）" : "マザー：異なる機種または未取得");
        var profiles = source.Settings.Profiles.Concat(source.Settings.BoardFans.Values.Concat([source.Settings.CpuFan, source.Settings.ChassisFan])
            .Select(f => f.Profile).OfType<AutoProfile>());
        var sensorIds = profiles.SelectMany(p => new[] { p.Cpu?.SensorId, p.Gpu?.SensorId }).Concat([source.Settings.CpuSensorId, source.Settings.GpuSensorId])
            .Concat((source.Settings.Motherboard.Sensors ?? []).Select(s => s.Id))
            .Concat((source.Settings.Dashboard.Items ?? []).Where(d => d.Source == "motherboard").Select(d => d.SensorId))
            .Concat((source.Notifications.Rules ?? []).Where(r => r.Address.Source is "sensor" or "motherboard").Select(r => r.Address.SensorId))
            .OfType<string>().Distinct().Where(id => !(local.Devices.SensorIds ?? []).Contains(id)).ToArray();
        foreach (var id in sensorIds) result.Add("未取得のセンサー：" + id);
        result.Add("全ファン：配線確認を解除・制御は監視状態から開始");
        result.Add("照明・通知の対象ID：保持（未検出の機器へ自動再割り当てしません）");
        return result.ToArray();
    }
}
