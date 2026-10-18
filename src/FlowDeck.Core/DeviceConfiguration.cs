namespace FlowDeck.Core;

public sealed record EndpointReference(string DeviceId, string EndpointId);
public sealed record EndpointSetup(string Accessory = "", int? LedCount = null, bool LedCountConfirmed = false)
{
    [System.Text.Json.Serialization.JsonPropertyName("showOnM5"), System.Text.Json.Serialization.JsonIgnore]
    public bool LegacyPanelVisibility { get; init; }
}
public sealed record RegisteredEndpoint(string Id, string Name, EndpointKind Kind, string Role = "", EndpointSetup? Setup = null);
public sealed record RegisteredDevice(string Id, string Name, string Provider, DeviceKind Kind, RegisteredEndpoint[] Endpoints);
public sealed record DeviceRegistrations(int Version, RegisteredDevice[] Devices);
public sealed record PhysicalDeviceBinding(string DeviceId, string ProviderIdentity, string DefinitionId, string Fingerprint);
public sealed record DeviceBindings(int Version, string? InstallationId, PhysicalDeviceBinding[] Devices);

public enum CoolingExitAction { FixedDuty, ReleaseToDevice }
public sealed record TemperatureRule(EndpointReference Sensor, double Overheat, double ResumeBelow);
public sealed record StopTemperatureRule(EndpointReference Sensor, double Below);
public sealed record CoolingStopRule(StopTemperatureRule[] Temperatures, int RestartDuty, double RestartSeconds);
public sealed record CoolingSafety(int? Minimum, int? Fallback, int? Exit, CoolingExitAction ExitAction,
    TemperatureRule[] ManualProtection, CoolingStopRule? Stop = null);
public sealed record TemperatureDemandCurve(EndpointReference Sensor, CurvePoint[] Points, double Overheat, double ResumeBelow);
public sealed record CoolingTemperatureProfile(string Id, string Name, long Revision, TemperatureDemandCurve[] Curves,
    double? DescentDelaySeconds, double? DescentPerSecond);
public sealed record CoolingSavedPolicy(FanMode Mode, int? Duty = null, CoolingTemperatureProfile? Profile = null,
    CoolingSafety? SafetySnapshot = null);
public sealed record CoolingEndpointConfiguration(EndpointReference Target, bool WiringConfirmed, CoolingSafety Safety,
    CoolingTemperatureProfile[] Profiles, CoolingSavedPolicy? SavedPolicy = null, bool ResumeOnStart = false);
public sealed record EndpointGroup(string Id, string Name, EndpointKind Kind, EndpointReference[] Targets, long Revision = 1);
public sealed record CoolingConfiguration(int Version, CoolingEndpointConfiguration[] Endpoints, EndpointGroup[] Groups,
    CoolingTemperatureProfile[]? ProfileTemplates = null);

// Native effect IDs are namespaced by a compiled provider. They are never executable paths or register addresses.
public sealed record LightingProgram(string EffectId, RgbColor[] Colors, int Speed = 2, bool Reverse = false,
    int Brightness = 100, RgbColor[]? LedColors = null);
public sealed record EndpointLighting(EndpointReference Target, LightingProgram Program);
public sealed record SynchronizedLighting(EndpointReference[] Targets, LightingProgram Program);
public sealed record DeviceLightingScene(string Id, string Name, EndpointLighting[] Individual, SynchronizedLighting[] Synchronized, long Revision = 1,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeviceLightingRequirement[]? Requirements = null)
{
    [System.Text.Json.Serialization.JsonPropertyName("showOnM5"), System.Text.Json.Serialization.JsonIgnore]
    public bool LegacyPanelVisibility { get; init; }
}
public sealed record EndpointAlias(EndpointReference Target, string Name);
public sealed record DeviceBrightness(string GroupId, int Brightness, bool Off, DeviceLightingScene? Baseline);
public sealed record LightingConfiguration(int Version, DeviceLightingScene[] Scenes, EndpointGroup[] Groups,
    EndpointAlias[] Names, EndpointReference[] SyncTargets, DeviceBrightness[] Adjustments, EndpointLighting[]? Preferences = null,
    LightingProgram? DefaultProgram = null, LightingEndpointConfiguration[]? Endpoints = null, long Revision = 0,
    SynchronizedLighting[]? SavedSynchronization = null, LightingTargetAdjustment[]? TargetAdjustments = null,
    EndpointReference[][]? SavedFrameGroups = null);
public sealed record DeviceDashboardItem(EndpointReference Target, DeviceFeature Measurement, string Name, string Role, bool Visible = true,
    string? Indicator = null, double? DisplayMaximum = null);
public sealed record DeviceDashboardConfiguration(int Version, DeviceDashboardItem[] Items);

/// <summary>Validation of complete stored configurations. Readiness for output is checked separately against live capabilities.</summary>
public static class DeviceConfigurationRules
{
    public static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
    public static bool Key(string? value) => Text(value, 160);
    public static bool ProfileId(string? value) => Text(value, 96);
    public static bool ValidReference(EndpointReference? value) => value is not null && Key(value.DeviceId) && Key(value.EndpointId);
    public static bool ValidProgram(LightingProgram? value) => value is not null && Text(value.EffectId, 128) && value.Speed is >= 0 and <= 255 &&
        value.Brightness is >= 0 and <= 100 && value.Colors is { Length: <= 32 } && value.Colors.All(c => c?.Valid == true) &&
        (value.LedColors is null || value.LedColors.Length <= 4096 && value.LedColors.All(c => c?.Valid == true));
    public static void Validate(DeviceRegistrations registrations, DeviceBindings bindings, CoolingConfiguration cooling,
        LightingConfiguration lighting, DeviceDashboardConfiguration dashboard)
    {
        Require(registrations is { Version: 2, Devices.Length: <= DeviceRegistry.MaximumDevices }, "機器登録の形式が不正です");
        Require(registrations.Devices.All(d => d is not null && Key(d.Id) && Text(d.Name, 160) && Text(d.Provider, 64) && Enum.IsDefined(d.Kind) &&
            d.Endpoints is { Length: <= DeviceRegistry.MaximumEndpoints } && d.Endpoints.All(e => e is not null && Key(e.Id) && Text(e.Name, 160) && Enum.IsDefined(e.Kind) &&
                e.Role is not null && e.Role.Length <= 32 && !e.Role.Any(char.IsControl) && ValidSetup(e)) && d.Endpoints.DistinctBy(e => e.Id).Count() == d.Endpoints.Length) &&
            registrations.Devices.DistinctBy(d => d.Id).Count() == registrations.Devices.Length, "機器・端子の登録が不正または重複しています");
        var endpoints = registrations.Devices.SelectMany(d => d.Endpoints.Select(e => (Target: new EndpointReference(d.Id, e.Id), Endpoint: e)))
            .ToDictionary(p => p.Target, p => p.Endpoint);
        bool Ref(EndpointReference? target, EndpointKind? kind = null) => ValidReference(target) && endpoints.TryGetValue(target!, out var e) && (kind is null || e.Kind == kind);
        bool SafetyRefs(CoolingSafety safety) => ValidSafety(safety) && safety.ManualProtection.All(r => Ref(r.Sensor, EndpointKind.Sensor)) &&
            (safety.Stop is null || safety.Stop.Temperatures.All(r => Ref(r.Sensor, EndpointKind.Sensor)));
        Require(bindings is { Version: 2, Devices: not null } && bindings.Devices.Length <= registrations.Devices.Length &&
            (bindings.InstallationId is null ? bindings.Devices.Length == 0 : Guid.TryParseExact(bindings.InstallationId, "N", out _)) &&
            bindings.Devices.All(d => d is not null && registrations.Devices.Any(r => r.Id == d.DeviceId) && Text(d.ProviderIdentity, 2048) && Key(d.DefinitionId) &&
                d.Fingerprint?.Length == 64 && d.Fingerprint.All(Uri.IsHexDigit)) && bindings.Devices.DistinctBy(d => d.DeviceId).Count() == bindings.Devices.Length,
            "実機の関連付けが不正です");
        Require(cooling is { Version: 2, Endpoints.Length: <= 256, Groups.Length: <= 32 } && cooling.Endpoints.All(c => c is not null && Ref(c.Target, EndpointKind.Cooling)) &&
            cooling.Endpoints.DistinctBy(c => c.Target).Count() == cooling.Endpoints.Length, "冷却設定の対象が不正です");
        Require(cooling.ProfileTemplates is null || cooling.ProfileTemplates.Length <= 32 && cooling.ProfileTemplates.All(p => ValidProfileDraft(p) && p.Curves.All(c => Ref(c.Sensor, EndpointKind.Sensor))) &&
            cooling.ProfileTemplates.DistinctBy(p => p.Id).Count() == cooling.ProfileTemplates.Length, "カーブのテンプレートが不正です");
        foreach (var c in cooling.Endpoints)
        {
            Require(SafetyRefs(c.Safety) && c.Profiles is { Length: <= 32 } && c.Profiles.All(p => ValidProfileDraft(p) && p.Curves.All(curve => Ref(curve.Sensor, EndpointKind.Sensor))) &&
                c.Profiles.DistinctBy(p => p.Id).Count() == c.Profiles.Length, "冷却設定または温度の参照が不正です");
            Require(c.SavedPolicy is null || c.SavedPolicy.Mode switch
            {
                FanMode.Manual => c.SavedPolicy.Duty is >= 0 and <= 100 && c.SavedPolicy.Profile is null,
                FanMode.Auto => c.SavedPolicy.Duty is null && ValidProfileDraft(c.SavedPolicy.Profile) && c.SavedPolicy.Profile!.Curves.All(curve => Ref(curve.Sensor, EndpointKind.Sensor)),
                _ => false
            }, "保存した制御方針が不正です");
            Require(c.SavedPolicy?.SafetySnapshot is not { } savedSafety || SafetyRefs(savedSafety), "保存した安全条件または温度の参照が不正です");
            Require(!c.ResumeOnStart || bindings.Devices.Any(b => b.DeviceId == c.Target.DeviceId) &&
                SavedPolicyReadiness(c) is null, "起動時制御の条件が揃っていません");
        }
        void Groups(EndpointGroup[] groups, EndpointKind kind)
        {
            Require(groups.All(g => g is not null && g.Revision >= 1 && ProfileId(g.Id) && Text(g.Name, 80) && g.Kind == kind && g.Targets is { Length: > 0 and <= 256 } &&
                g.Targets.All(t => Ref(t, kind)) && g.Targets.Distinct().Count() == g.Targets.Length) && groups.DistinctBy(g => g.Id).Count() == groups.Length,
                "グループの対象が不正または重複しています");
        }
        Groups(cooling.Groups, EndpointKind.Cooling);
        Require(lighting is { Version: 2, Revision: >= 0, Scenes.Length: <= DeviceLightingLibrary.MaximumScenes, Groups.Length: <= DeviceLightingLibrary.MaximumGroups, Names.Length: <= 256, SyncTargets.Length: <= 256, Adjustments.Length: <= 33 }, "照明設定の形式が不正です");
        Groups(lighting.Groups, EndpointKind.Lighting);
        Require(lighting.Endpoints is null || lighting.Endpoints.Length <= 256 &&
            lighting.Endpoints.All(e => DeviceLightingConfiguration.ValidEndpoint(e) && Ref(e.Target, EndpointKind.Lighting)) &&
            lighting.Endpoints.DistinctBy(e => e.Target).Count() == lighting.Endpoints.Length, "照明の配線・終了設定が不正です");
        if (DeviceControlCapacity.Problem(DeviceControlCapacity.Count(registrations, cooling, lighting)) is { } capacityProblem)
            throw new DeviceControlCapacityException(capacityProblem);
        Require(lighting.DefaultProgram is null || ValidProgram(lighting.DefaultProgram), "照明の既定設定が不正です");
        Require(lighting.Preferences is null || lighting.Preferences.Length <= 256 && lighting.Preferences.All(p => p is not null && Ref(p.Target, EndpointKind.Lighting) && ValidProgram(p.Program)) &&
            lighting.Preferences.DistinctBy(p => p.Target).Count() == lighting.Preferences.Length, "照明の選択設定が不正です");
        Require(lighting.SavedSynchronization is null || lighting.SavedSynchronization.Length <= 256 &&
            lighting.SavedSynchronization.All(g => g is not null && g.Targets is { Length: > 0 and <= 256 } && DeviceLighting.ValidAnimation(g.Program) &&
                g.Targets.All(t => Ref(t, EndpointKind.Lighting) && (lighting.Preferences ?? []).Any(p => p.Target == t &&
                    DeviceLighting.SameProgram(p.Program, g.Program)))) &&
            lighting.SavedSynchronization.SelectMany(g => g.Targets).Distinct().Count() == lighting.SavedSynchronization.Sum(g => g.Targets.Length),
            "保存した同期対象・基準色が照明の選択設定と一致しません");
        Require(lighting.SavedFrameGroups is null || lighting.SavedFrameGroups.Length <= 256 &&
            lighting.SavedFrameGroups.All(g => g is { Length: > 0 and <= 256 } && g.All(t => Ref(t, EndpointKind.Lighting) &&
                (lighting.Preferences ?? []).Any(p => p.Target == t && DeviceLighting.FrameProgram(p.Program)))) &&
            lighting.SavedFrameGroups.Sum(g => g.Length) <= 256 && lighting.SavedFrameGroups.SelectMany(g => g).Distinct().Count() == lighting.SavedFrameGroups.Sum(g => g.Length),
            "保存したフレーム構成と照明の選択設定が一致しません");
        Require(lighting.TargetAdjustments is null || lighting.TargetAdjustments.Length <= 256 &&
            lighting.TargetAdjustments.All(a => DeviceLightingAdjustments.Valid(a) && Ref(a.Target, EndpointKind.Lighting) &&
                (lighting.Preferences ?? []).Any(p => p.Target == a.Target && DeviceLighting.SameProgram(p.Program, DeviceLightingAdjustments.Effective(a)))) &&
            lighting.TargetAdjustments.DistinctBy(a => a.Target).Count() == lighting.TargetAdjustments.Length,
            "照明の基準・明るさの各段階が選択設定と一致しません");
        bool Scene(DeviceLightingScene? scene)
        {
            if (scene is null || scene.Revision < 1 || !ProfileId(scene.Id) || !Text(scene.Name, 80) || scene.Individual is not { Length: <= 256 } || scene.Synchronized is not { Length: <= 32 }) return false;
            if (!scene.Individual.All(i => i is not null && Ref(i.Target, EndpointKind.Lighting) && ValidProgram(i.Program)) ||
                !scene.Synchronized.All(s => s is not null && s.Targets is { Length: > 0 and <= 256 } && s.Targets.All(t => Ref(t, EndpointKind.Lighting)) && ValidProgram(s.Program))) return false;
            var targets = scene.Individual.Select(i => i.Target).Concat(scene.Synchronized.SelectMany(s => s.Targets)).ToArray();
            return targets.Length is > 0 and <= 256 && targets.Distinct().Count() == targets.Length && DeviceLightingSceneRequirements.Matches(scene);
        }
        Require(lighting.Scenes.All(Scene) && lighting.Scenes.DistinctBy(s => s.Id).Count() == lighting.Scenes.Length &&
            lighting.Names.All(n => n is not null && Ref(n.Target, EndpointKind.Lighting) && Text(n.Name, 80)) && lighting.Names.DistinctBy(n => n.Target).Count() == lighting.Names.Length &&
            lighting.SyncTargets.All(t => Ref(t, EndpointKind.Lighting)) && lighting.SyncTargets.Distinct().Count() == lighting.SyncTargets.Length &&
            lighting.Adjustments.All(a => a is not null && (a.GroupId == "all" || lighting.Groups.Any(g => g.Id == a.GroupId)) && a.Brightness is >= 0 and <= 100 &&
                (a.Baseline is null ? !a.Off : Scene(a.Baseline) && (a.GroupId == "all" ||
                    lighting.Groups.Single(g => g.Id == a.GroupId).Targets.ToHashSet().SetEquals(a.Baseline.Individual.Select(i => i.Target)
                        .Concat(a.Baseline.Synchronized.SelectMany(s => s.Targets)))))) && lighting.Adjustments.DistinctBy(a => a.GroupId).Count() == lighting.Adjustments.Length,
            "照明のシーン・名前・明るさの参照が不正です");
        Require(dashboard is { Version: 2, Items.Length: <= DeviceDashboard.MaximumSavedItems } && dashboard.Items.All(i => i is not null && Ref(i.Target) &&
            i.Measurement is DeviceFeature.Temperature or DeviceFeature.Rpm && Text(i.Name, 80) && i.Role is "temperature" or "fan" or "pump" && DeviceDashboardDraft.ValidPresentation(i) &&
            (i.Measurement == DeviceFeature.Temperature ? Ref(i.Target, EndpointKind.Sensor) && i.Role == "temperature" :
                (Ref(i.Target, EndpointKind.Sensor) || Ref(i.Target, EndpointKind.Cooling)) && i.Role is "fan" or "pump")) &&
            dashboard.Items.DistinctBy(i => (i.Target, i.Measurement)).Count() == dashboard.Items.Length, "表示項目の参照が不正です");
    }
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid, string error)
    { if (!valid) throw new ArgumentException(error); }
    private static bool ValidSetup(RegisteredEndpoint e) => e.Setup is not { } s ||
        s.Accessory is not null && s.Accessory.Length <= 256 && !s.Accessory.Any(char.IsControl) && s.LedCount is null or >= 0 and <= 4096 &&
        (!s.LedCountConfirmed || s.LedCount is not null) &&
        (e.Kind == EndpointKind.Lighting || s.Accessory.Length == 0 && s.LedCount is null && !s.LedCountConfirmed);
    public static bool ValidSafety(CoolingSafety? s) => s is not null && s.Minimum is null or >= 1 and <= 100 && s.Fallback is null or >= 1 and <= 100 &&
        s.Exit is null or >= 1 and <= 100 && Enum.IsDefined(s.ExitAction) && s.ManualProtection is { Length: <= 32 } &&
        s.ManualProtection.All(r => r is not null && ValidReference(r.Sensor) && FanPolicyEngine.ThresholdsValid(r.Overheat, r.ResumeBelow)) &&
        s.ManualProtection.DistinctBy(r => r.Sensor).Count() == s.ManualProtection.Length &&
        (s.Stop is null || s.Stop.Temperatures is { Length: > 0 and <= 32 } && s.Stop.Temperatures.All(r => r is not null && ValidReference(r.Sensor) && double.IsFinite(r.Below) && r.Below is > 0 and < 150) &&
            s.Stop.Temperatures.DistinctBy(r => r.Sensor).Count() == s.Stop.Temperatures.Length && s.Stop.RestartDuty is >= 1 and <= 100 && double.IsFinite(s.Stop.RestartSeconds) && s.Stop.RestartSeconds > 0);
    public static bool ValidProfileDraft(CoolingTemperatureProfile? p) => p is not null && ProfileId(p.Id) && Text(p.Name, 80) && p.Revision >= 0 &&
        p.DescentDelaySeconds is null or >= 0 && (p.DescentDelaySeconds is null || double.IsFinite(p.DescentDelaySeconds.Value)) &&
        p.DescentPerSecond is null or > 0 && (p.DescentPerSecond is null || double.IsFinite(p.DescentPerSecond.Value)) &&
        p.Curves is { Length: <= 32 } && p.Curves.All(c => c is not null && ValidReference(c.Sensor) && c.Points is { Length: >= 2 and <= 64 } &&
            FanPolicyEngine.ThresholdsValid(c.Overheat, c.ResumeBelow) && c.Points.All(point => point is not null && double.IsFinite(point.Temperature) && point.Duty is >= 0 and <= 100) &&
            c.Points.Zip(c.Points.Skip(1)).All(pair => pair.First.Temperature < pair.Second.Temperature && pair.First.Duty <= pair.Second.Duty)) &&
        p.Curves.DistinctBy(c => c.Sensor).Count() == p.Curves.Length;
    public static string? ProfileReadiness(CoolingTemperatureProfile p, CoolingSafety safety)
    {
        if (!ValidProfileDraft(p) || !ValidSafety(safety)) return "冷却設定が不正です";
        if (SafetyReadiness(safety) is { } error) return error;
        if (p.Curves.Length == 0 || p.DescentDelaySeconds is null || p.DescentPerSecond is null) return "温度カーブと下降設定が必要です";
        // Curves describe requested output. Each endpoint applies its own minimum and stop permission after interpolation.
        return null;
    }
    public static string? SafetyReadiness(CoolingSafety safety)
    {
        if (!ValidSafety(safety)) return "冷却設定が不正です";
        if (safety.Minimum is null || safety.Fallback is null || safety.Fallback < safety.Minimum ||
            safety.ExitAction == CoolingExitAction.FixedDuty && (safety.Exit is null || safety.Exit < safety.Minimum)) return "最低・異常時・終了時の設定が必要です";
        if (safety.Stop is { } stop && stop.RestartDuty < safety.Minimum) return "再始動出力が最低出力未満です";
        return null;
    }
    public static bool SameSafety(CoolingSafety a, CoolingSafety b) => ValidSafety(a) && ValidSafety(b) &&
        a.Minimum == b.Minimum && a.Fallback == b.Fallback && a.Exit == b.Exit && a.ExitAction == b.ExitAction &&
        a.ManualProtection.SequenceEqual(b.ManualProtection) && (a.Stop is null ? b.Stop is null : b.Stop is { } stop &&
            a.Stop.RestartDuty == stop.RestartDuty && a.Stop.RestartSeconds == stop.RestartSeconds && a.Stop.Temperatures.SequenceEqual(stop.Temperatures));
    public static string? SavedPolicyReadiness(CoolingEndpointConfiguration config)
    {
        if (config.SavedPolicy is not { SafetySnapshot: { } snapshot } policy) return "最後に適用した安全条件がありません";
        if (!SameSafety(snapshot, config.Safety)) return "保存した安全条件と現在の設定が異なります";
        if (DeviceCoolingPolicy.Validate(policy, snapshot) is { } error) return error;
        // Saved manual values are requests, just like live/group commands. The engine applies the endpoint minimum and stop permission on restore.
        return null;
    }
    public static int Calculate(CoolingTemperatureProfile profile, CoolingSafety safety, IReadOnlyDictionary<EndpointReference, double> temperatures)
    {
        if (ProfileReadiness(profile, safety) is { } error) throw new ArgumentException(error);
        foreach (var curve in profile.Curves)
            if (!temperatures.TryGetValue(curve.Sensor, out var temperature) || !double.IsFinite(temperature)) throw new ArgumentException("必須の温度がありません");
        var requested = (int)Math.Ceiling(profile.Curves.Max(c => FanPolicyEngine.Interpolate(c.Points, temperatures[c.Sensor])));
        return requested == 0 && safety.Stop is not null ? 0 : Math.Clamp(requested, safety.Minimum!.Value, 100);
    }
    public static int EffectiveGroupDuty(int requested, CoolingSafety safety)
    {
        if (requested is < 0 or > 100 || SafetyReadiness(safety) is not null) throw new ArgumentException("端子の出力条件が不正です");
        return requested == 0 && safety.Stop is not null ? 0 : Math.Max(requested, safety.Minimum!.Value);
    }
}
