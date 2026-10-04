using System.Text.Json;

namespace FlowDeck.Infrastructure;

internal sealed record OpenRgbDetectorRequirement(string Name, bool Enabled);
internal sealed record OpenRgbEnvironmentIssue(string Code, string Setting, string Subject, string Message);
internal sealed record OpenRgbEnvironmentAssessment(string RuleId, string SourceRevision, OpenRgbEnvironmentStamp Stamp,
    OpenRgbEnvironmentIssue[] Issues, bool Truncated)
{
    // Settings matching is NOT permission to control hardware. A compiled route policy must also
    // establish local exclusions, actual bus/module coexistence, process identity and inventory.
    public bool MatchesSettings => Issues.Length == 0 && !Truncated;
}

// Frozen requirements supplied by compiled provider policy, never by IPC/configuration or by SDK data.
// No production profile is enrolled until its physical-resource coexistence audit is complete.
internal sealed class OpenRgbEnvironmentRequirements
{
    private readonly Dictionary<string, bool> detectors;
    private readonly bool hidSafeMode;
    private readonly bool? sharedSmbus;
    private readonly int? sleepMode;
    public string RuleId { get; }
    public string SourceRevision { get; }
    public OpenRgbEnvironmentRequirements(string ruleId, string sourceRevision, IEnumerable<OpenRgbDetectorRequirement> detectors,
        bool hidSafeMode, bool? sharedSmbus, int? sleepMode)
    {
        if (ruleId is not { Length: > 0 and <= 80 } || ruleId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.') ||
            sourceRevision is not { Length: 40 } || sourceRevision.Any(c => !Uri.IsHexDigit(c)) || sleepMode is < 0 or > 2)
            throw new ArgumentException("OpenRGBの固定判定条件が不正です");
        ArgumentNullException.ThrowIfNull(detectors);
        var entries = detectors.Take(8193).ToArray();
        if (entries.Length is < 1 or > 8192 || entries.Any(d => d is null || d.Name is not { Length: > 0 and <= 256 } ||
            d.Name != d.Name.Trim() || d.Name.Any(char.IsControl)) || entries.DistinctBy(d => d.Name, StringComparer.Ordinal).Count() != entries.Length)
            throw new ArgumentException("OpenRGBの固定検出対象が不正です");
        this.detectors = entries.ToDictionary(d => d.Name, d => d.Enabled, StringComparer.Ordinal);
        RuleId = ruleId; SourceRevision = sourceRevision.ToLowerInvariant();
        this.hidSafeMode = hidSafeMode; this.sharedSmbus = sharedSmbus; this.sleepMode = sleepMode;
    }

    public OpenRgbEnvironmentAssessment Assess(OpenRgbSdkEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var issues = new List<OpenRgbEnvironmentIssue>(); var truncated = false;
        void Add(string code, string setting, string subject, string message)
        {
            if (issues.Count == 64) { truncated = true; return; }
            issues.Add(new(code, setting, OpenRgbLightingProfile.Label(subject, "設定項目", 160), message));
        }
        static bool Property(JsonElement value, string key, out JsonElement result)
        {
            result = default;
            return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out result);
        }
        void Boolean(JsonElement parent, string key, bool expected, string setting, string subject)
        {
            if (!Property(parent, key, out var value)) Add("missing-setting", setting, subject, "必要な設定値を取得できません");
            else if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Add("invalid-setting", setting, subject, "設定値の形式が一致しません");
            else if (value.GetBoolean() != expected) Add("setting-mismatch", setting, subject, "固定された併用条件と設定値が一致しません");
        }

        if ((environment.ServerFlags & 17) != 17 || (environment.ServerFlags & 65536) != 0)
            Add("sdk-flags", "SDK", "SDK機能", "必要な取得機能または権限状態が一致しません");
        Boolean(environment.Server, "all_controllers", false, "Server.all_controllers", "外部コントローラーの公開");
        if (environment.Plugins is null || (environment.ServerFlags & 8) == 0)
            Add("plugins-unavailable", "Plugins", "プラグイン", "プラグイン一覧を確認できません");
        else if (environment.Plugins.Length > 0)
            Add("plugins-present", "Plugins", "プラグイン", "プラグインを含む構成はこの判定条件の対象外です");

        Boolean(environment.Detectors, "hid_safe_mode", hidSafeMode, "Detectors.hid_safe_mode", "HID検出方式");
        if (!Property(environment.DetectorSchema, "hid_safe_mode", out var schema) || !Property(schema, "type", out var type) ||
            type.ValueKind != JsonValueKind.String || type.GetString() != "bool")
            Add("schema-mismatch", "Detectors.hid_safe_mode", "HID検出方式", "対応する設定定義を確認できません");
        if (!Property(environment.Detectors, "detectors", out var detected) || detected.ValueKind != JsonValueKind.Object)
            Add("detectors-unavailable", "Detectors.detectors", "検出対象", "検出対象の設定一覧を取得できません");
        else
        {
            // Missing entries are not disabled: the server can use an enabled default. Extra disabled
            // entries are also a profile change and never silently expand the audited detector set.
            foreach (var expected in detectors.OrderBy(p => p.Key, StringComparer.Ordinal))
                Boolean(detected, expected.Key, expected.Value, "Detectors.detectors", expected.Key);
            foreach (var extra in detected.EnumerateObject().Where(p => !detectors.ContainsKey(p.Name)).OrderBy(p => p.Name, StringComparer.Ordinal))
                Add("unknown-detector", "Detectors.detectors", extra.Name, "固定された検出対象に含まれない項目があります");
        }
        if (sharedSmbus is { } shared) Boolean(environment.Drivers, "shared_smbus_access", shared, "Drivers.shared_smbus_access", "共有SMBus設定");
        if (sleepMode is { } sleep)
        {
            if (!Property(environment.Drivers, "smbus_sleep_mode", out var value))
                Add("missing-setting", "Drivers.smbus_sleep_mode", "SMBus待機方式", "必要な設定値を取得できません");
            else if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var actual) || actual is < 0 or > 2)
                Add("invalid-setting", "Drivers.smbus_sleep_mode", "SMBus待機方式", "設定値の形式または範囲が一致しません");
            else if (actual != sleep) Add("setting-mismatch", "Drivers.smbus_sleep_mode", "SMBus待機方式", "固定された併用条件と設定値が一致しません");
        }
        return new(RuleId, SourceRevision, environment.Stamp, issues.ToArray(), truncated);
    }

    public void Check(OpenRgbSdkEnvironment environment)
    {
        var assessment = Assess(environment);
        if (!assessment.MatchesSettings)
            throw new IOException("OpenRGBの接続条件を確認してください: " + string.Join(" / ", assessment.Issues.Take(3).Select(i => i.Subject + " — " + i.Message)));
    }
}
