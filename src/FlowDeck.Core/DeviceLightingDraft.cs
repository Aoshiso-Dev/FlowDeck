namespace FlowDeck.Core;

public sealed record DeviceLightingEditVersion(string Epoch, long Topology, string ConfigurationGeneration, DeviceTargetVersion[] Targets);
public sealed record DeviceLightingEdit(DeviceLightingEditVersion Version, DeviceOperation Operation,
    LightingProgram? Program = null, LightingAdjustmentRequest? Adjustment = null, LightingPresetRequest? Preset = null);

/// <summary>Common lighting editor. Target identities and unsent values survive every telemetry refresh.</summary>
public sealed class DeviceLightingDraft
{
    private DeviceServiceState? state;
    private LightingConfiguration? lighting;
    private string generation = "";
    private EndpointReference[] selected = [];
    private LightingProgram program = new("fixed", [new(52, 137, 255)]);
    private DeviceLightingEditVersion? editedAt;
    private DeviceLightingEdit? captured;
    private LightingProgram?[] baseline = [];
    private bool live;
    public EndpointReference[] Targets => selected.ToArray();
    public LightingProgram Program => Contract.Copy(program);
    public bool Dirty { get; private set; }
    public bool EffectChanged => Changed((a, b) => a.EffectId == b.EffectId);
    public bool ColorChanged => Changed((a, b) => a.Colors.SequenceEqual(b.Colors) &&
        (a.LedColors is null ? b.LedColors is null : b.LedColors is not null && a.LedColors.SequenceEqual(b.LedColors)));
    public bool BrightnessChanged => Changed((a, b) => a.Brightness == b.Brightness);
    public bool SpeedChanged => Changed((a, b) => a.Speed == b.Speed);
    public bool DirectionChanged => Changed((a, b) => a.Reverse == b.Reverse);
    public bool PerLedChanged => Changed((a, b) => (a.LedColors is null) == (b.LedColors is null));
    private bool Changed(Func<LightingProgram, LightingProgram, bool> same) => Dirty && baseline.Any(value => value is null || !same(value, program));
    public bool Mixed { get; private set; }
    public bool Conflict => Dirty && !SameVersion(editedAt, CurrentVersion());
    public string? Problem => selected.Length == 0 ? "照明を選択してください" : !live ? "状態を更新してください" :
        Conflict ? "別の操作で変更あり・現在の設定を読み込んでください" : state!.LifecycleError ?? Check(Feature(program)) ??
        (!DeviceLighting.FrameProgram(program) && !NativeEffects.Any(e => DeviceNativeLighting.Accepts(e, program)) ? "選択した照明で使える効果・設定を選んでください" : null);
    public bool CanApply => !Mixed && Problem is null;
    public bool CanAdjust => !Dirty && live && selected.Length > 0 && state!.LifecycleError is null &&
        selected.All(t => lighting?.Preferences?.Any(p => p.Target == t) == true &&
            state!.Endpoints.SingleOrDefault(e => e.Target == t)?.Persistence?.State != DevicePolicySaveState.SessionOnly) && Check(DeviceFeature.FixedColor) is null;
    public bool CanStop => live && selected.Length > 0 && Check(DeviceFeature.RestoreLighting) is null;
    public void Update(DeviceServiceState current, string configurationGeneration, LightingConfiguration configuration,
        double elapsedSeconds, bool connected)
    {
        // A location-bound replacement must not inherit the previous connection's displayed selection.
        if (!Dirty && selected.Any(t => current.Inventory.Devices.Any(d => d.Device.Id == t.DeviceId && d.Device.Provider == "ene-memory" &&
                d.Device.IdentityConfidence == IdentityConfidence.ConnectionBound) && state is not null &&
                (state.Inventory.Epoch != current.Inventory.Epoch || state.Endpoints.SingleOrDefault(e => e.Target == t)?.Generation !=
                    current.Endpoints.SingleOrDefault(e => e.Target == t)?.Generation ||
                 state.Endpoints.SingleOrDefault(e => e.Target == t) is { Owned: true, Persistence.State: DevicePolicySaveState.SessionOnly } &&
                    current.Endpoints.SingleOrDefault(e => e.Target == t)?.Owned != true)))
            program = new("fixed", [new(52, 137, 255)]);
        state = current; generation = configurationGeneration; lighting = configuration;
        live = connected && double.IsFinite(elapsedSeconds) && elapsedSeconds is >= 0 and < 3 && !current.Releasing && !current.ConfigurationChanging;
        if (!Dirty) Adopt();
    }
    public void Select(IEnumerable<EndpointReference> references)
    {
        var next = references.ToArray();
        if (next.Length > DeviceServiceProtocol.MaximumTargets || next.Any(t => !DeviceConfigurationRules.ValidReference(t)) || next.Distinct().Count() != next.Length)
            throw new ArgumentException("照明対象の指定が不正です");
        if (selected.Length == next.Length && selected.All(next.Contains)) return;
        selected = next;
        // A draft belongs to exactly one target set. Selection itself never applies it.
        Reset();
    }
    public void SetProgram(LightingProgram value)
    {
        if (!DeviceLighting.FrameProgram(value) && !NativeEffects.Any(e => DeviceNativeLighting.Accepts(e, value))) throw new ArgumentException("選択した照明で使える効果・設定を選んでください");
        if (!Mixed && DeviceLighting.SameProgram(program, value)) return;
        var conflicted = Conflict;
        if (!Dirty) editedAt = CurrentVersion();
        captured = null;
        program = Contract.Copy(value); Mixed = false;
        Dirty = conflicted || baseline.Any(before => before is null || !DeviceLighting.SameProgram(before, program));
        if (!Dirty) { editedAt = null; Adopt(); }
    }
    public void Reset() { Dirty = false; editedAt = null; captured = null; Adopt(); }
    public static bool AppliedSuccessfully(DeviceLightingEdit edit, DeviceOperationResult result, DeviceServiceState? current) =>
        result.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified &&
        current is not null && current.Inventory.Epoch == edit.Version.Epoch &&
        edit.Version.Targets.Length > 0 && result.Targets.Length == edit.Version.Targets.Length &&
        result.Targets.Select(t => t.Target).Distinct().Count() == result.Targets.Length &&
        edit.Version.Targets.All(t => result.Targets.Any(r => r.Target == t.Target && r.Generation == t.Generation &&
            r.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified &&
            r.Persistence?.State is not (DevicePolicySaveState.Failed or DevicePolicySaveState.Superseded) &&
            current.Endpoints.SingleOrDefault(e => e.Target == r.Target) is { } latest &&
            latest.Generation == r.Generation && latest.PolicyRevision == r.PolicyRevision));
    public bool AcceptApplied(DeviceLightingEdit edit, DeviceOperationResult result)
    {
        // Late completion of a cancelled or replaced draft must not clear the new editor.
        if (!ReferenceEquals(captured, edit) || edit.Operation != DeviceOperation.SetLighting || !Dirty ||
            !SameVersion(editedAt, edit.Version) || edit.Program is null || !DeviceLighting.SameProgram(program, edit.Program) ||
            !AppliedSuccessfully(edit, result, state)) return false;
        Reset(); return true;
    }
    // The service adapter calls this only after proving that a metadata save left all operating settings unchanged.
    public void AcceptMetadataSave(DeviceLightingEditVersion before, DeviceLightingEditVersion after)
    {
        if (Dirty && SameVersion(editedAt, before) && SameVersion(before with { ConfigurationGeneration = after.ConfigurationGeneration }, after))
            editedAt = Contract.Copy(after);
    }
    private void Adopt()
    {
        var values = selected.Select(t => state?.Endpoints.SingleOrDefault(e => e.Target == t) is { Owned: true, Persistence.State: DevicePolicySaveState.SessionOnly } current
            ? current.RequestedLighting : lighting?.Preferences?.SingleOrDefault(p => p.Target == t)?.Program).ToArray();
        Mixed = values.Length > 0 && values.Any(v => v is not null) &&
            (values.Any(v => v is null) || values.Skip(1).Any(v => !DeviceLighting.SameProgram(values[0]!, v!)));
        program = !Mixed && values.FirstOrDefault() is { } value ? Contract.Copy(value) : new("fixed", [new(52, 137, 255)]);
        baseline = values.Any(v => v is not null) ? values.Select(v => v is null ? null : Contract.Copy(v)).ToArray() : [Contract.Copy(program)];
    }
    public DeviceNativeLightingEffect[] NativeEffects => DeviceNativeLighting.Common(selected.Select(t => state?.Inventory.Devices.SingleOrDefault(d => d.Device.Id == t.DeviceId)?.Device.Endpoints.SingleOrDefault(e => e.Id == t.EndpointId)));
    public bool Supports(string effect) => selected.Length > 0 && Check(Feature(new(effect, []))) is null &&
        (effect is "fixed" or "off" or "breathing" or "color-cycle" || NativeEffects.Any(e => e.Id == effect));
    private static DeviceFeature Feature(LightingProgram value) => DeviceLighting.Animated(value) ? DeviceFeature.ColorFrames : value.EffectId is "fixed" or "off" ? DeviceFeature.FixedColor : DeviceFeature.NativeEffect;
    private string? Check(DeviceFeature feature)
    {
        foreach (var target in selected)
        {
            if (state?.Endpoints.Any(e => e.Target.DeviceId == target.DeviceId && DeviceLightingIdentification.Running(e.Identification)) == true)
                return "この機器の識別が終了するまでお待ちください";
            var instance = state?.Inventory.Devices.SingleOrDefault(d => d.Device.Id == target.DeviceId);
            var endpoint = instance?.Device.Endpoints.SingleOrDefault(e => e.Id == target.EndpointId && e.Kind == EndpointKind.Lighting);
            var capability = endpoint?.Capabilities.SingleOrDefault(c => c.Feature == feature);
            if (instance?.Connected != true) return "選択した照明が切断されています";
            if (capability?.Support != ImplementationSupport.Supported) return "選択した照明に未対応の効果です";
            if (!capability.Usable) return capability.Reason ?? "選択した照明の設定を確認してください";
            if (state!.Endpoints.SingleOrDefault(e => e.Target == target)?.Generation != instance.Generation) return "照明の構成を更新してください";
        }
        return null;
    }
    private DeviceLightingEditVersion? CurrentVersion()
    {
        if (state is null) return null;
        var versions = selected.Select(t => state.Endpoints.SingleOrDefault(e => e.Target == t)).ToArray();
        return versions.Any(v => v is null) ? null : new(state.Inventory.Epoch, state.Inventory.TopologyRevision, generation,
            versions.Select(v => new DeviceTargetVersion(v!.Target, v.Generation, v.PolicyRevision)).ToArray());
    }
    private static bool SameVersion(DeviceLightingEditVersion? a, DeviceLightingEditVersion? b) => a is not null && b is not null &&
        a.Epoch == b.Epoch && a.Topology == b.Topology && a.ConfigurationGeneration == b.ConfigurationGeneration &&
        a.Targets.Length == b.Targets.Length && a.Targets.All(t => b.Targets.Contains(t));
    public DeviceLightingEdit Capture()
    {
        if (!CanApply) throw new InvalidOperationException(Mixed ? "選択した照明の設定が混在しています。色・効果を選んでください" : Problem);
        return captured = new(Contract.Copy(Dirty ? editedAt! : CurrentVersion()!), DeviceOperation.SetLighting, Program);
    }
    public DeviceLightingEdit CaptureAdjustment(LightingAdjustmentKind kind, int? brightness = null)
    {
        if (!CanAdjust) throw new InvalidOperationException("適用済みの照明を選び、未適用の編集を確定または取り消してください");
        var request = new LightingAdjustmentRequest(kind, LightingAdjustmentScope.Targets, generation, brightness);
        if (!DeviceLightingAdjustments.Valid(request)) throw new ArgumentException("明るさ操作が不正です");
        return new(CurrentVersion()!, DeviceOperation.AdjustLighting, Adjustment: request);
    }
    public DeviceLightingEdit CaptureStop()
    {
        if (!CanStop) throw new InvalidOperationException("選択した照明の終了を現在実行できません");
        return new(CurrentVersion()!, DeviceOperation.StopLighting);
    }
}
