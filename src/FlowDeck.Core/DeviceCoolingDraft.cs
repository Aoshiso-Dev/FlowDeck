using System.Text.Json;

namespace FlowDeck.Core;

public sealed record DeviceCoolingEditVersion(string Epoch, long Topology, string ConfigurationGeneration, DeviceTargetVersion Target);
public sealed record DeviceCoolingEdit(DeviceCoolingEditVersion Version, DeviceOperation Operation, int? Duty = null, CoolingTemperatureProfile? Profile = null);

/// <summary>A persistent row's draft. Polling preserves unsent choices and never advances their target revision.</summary>
public sealed class DeviceCoolingDraft(EndpointReference target)
{
    public EndpointReference Target { get; } = target;
    public int Duty { get; private set; } = 50;
    public string? ProfileId { get; private set; }
    public bool Automatic { get; private set; }
    public bool DeviceManaged { get; private set; } = true;
    public bool Dirty { get; private set; }
    public long EditRevision { get; private set; }
    public bool ModeChanged => DeviceManaged != baselineManaged || !DeviceManaged && Automatic != baselineAutomatic;
    public bool DutyChanged => !DeviceManaged && !Automatic && Duty != baselineDuty;
    public bool ProfileChanged => !DeviceManaged && Automatic && ProfileId != baselineProfileId;
    public bool Conflict => conflicted || Dirty && editedAt != current;
    public bool Available { get; private set; }
    public bool NeedsSetup { get; private set; }
    public bool CanApply => Dirty && !Conflict && current is not null && (DeviceManaged ? CanRelease : Available && configuration is not null &&
        (Automatic ? automaticUsable && SelectedProfile is { } p && DeviceConfigurationRules.ProfileReadiness(p, configuration.Safety) is null : fixedUsable && Duty >= Minimum));
    public bool CanRelease { get; private set; }
    public bool SupportsRelease { get; private set; }
    public bool CanResume { get; private set; }
    public int Minimum => configuration?.Safety.Stop is not null ? 0 : configuration?.Safety.Minimum ?? 1;
    public CoolingTemperatureProfile[] Profiles => Contract.Copy(configuration?.Profiles ?? []);
    public string? Problem { get; private set; }
    private DeviceCoolingEditVersion? current, editedAt;
    private CoolingEndpointConfiguration? configuration;
    private DeviceExecutionState? execution;
    private double? reported;
    private bool fixedUsable, automaticUsable;
    private bool conflicted;
    private bool baselineManaged = true, baselineAutomatic;
    private int baselineDuty = 50;
    private string? baselineProfileId;
    private string? configurationKey, bindingsKey;
    private CoolingTemperatureProfile? SelectedProfile => configuration?.Profiles.SingleOrDefault(p => p.Id == ProfileId);

    public void Update(DeviceServiceState state, string configurationGeneration, CoolingEndpointConfiguration? setup, double elapsedSeconds, bool connected,
        DeviceBindings? bindings = null)
    {
        var previousConflict = Conflict;
        var nextConfigurationKey = JsonSerializer.Serialize(setup is null ? null : setup with { WiringConfirmed = false }, Contract.Json);
        var nextBindingsKey = bindings is null ? null : JsonSerializer.Serialize(bindings, Contract.Json);
        configuration = setup is null ? null : Contract.Copy(setup);
        var instance = state.Inventory.Devices.SingleOrDefault(d => d.Device.Id == Target.DeviceId);
        var endpoint = instance?.Device.Endpoints.SingleOrDefault(e => e.Id == Target.EndpointId && e.Kind == EndpointKind.Cooling);
        execution = state.Endpoints.SingleOrDefault(e => e.Target == Target && e.Generation == instance?.Generation);
        current = execution is null ? null : new(state.Inventory.Epoch, state.Inventory.TopologyRevision, configurationGeneration,
            new(Target, execution.Generation, execution.PolicyRevision));
        if (Dirty || conflicted)
        {
            // Saving another fan or a display preference advances the shared configuration generation.
            // Only adopt that generation with proof that this endpoint and all physical bindings are unchanged.
            // A real conflict stays blocked until the user explicitly reloads the current settings.
            conflicted |= previousConflict || bindingsKey != nextBindingsKey ||
                bindingsKey is not null && configurationKey != nextConfigurationKey;
            if (!conflicted && bindingsKey is not null && nextBindingsKey is not null &&
                editedAt is { } before && current is { } next &&
                (before with { ConfigurationGeneration = next.ConfigurationGeneration }) == next)
                editedAt = next;
            conflicted |= editedAt != current;
        }
        configurationKey = nextConfigurationKey;
        bindingsKey = nextBindingsKey;
        var live = connected && double.IsFinite(elapsedSeconds) && elapsedSeconds is >= 0 and < 3 && instance?.Connected == true &&
            execution is not null && !state.Releasing && !state.ConfigurationChanging;
        bool Usable(DeviceFeature feature) => endpoint?.Capabilities.Any(c => c.Feature == feature && c.Usable) == true;
        fixedUsable = Usable(DeviceFeature.FixedDuty); automaticUsable = Usable(DeviceFeature.AutomaticDuty);
        SupportsRelease = endpoint?.Capabilities.Any(c => c.Feature == DeviceFeature.ReleaseCooling && c.Support == ImplementationSupport.Supported) == true;
        Available = live && (fixedUsable || automaticUsable) && state.LifecycleError is null && setup is not null &&
            DeviceConfigurationRules.SafetyReadiness(setup.Safety) is null && execution?.Cooling?.Faulted != true;
        CanRelease = live && Usable(DeviceFeature.ReleaseCooling);
        CanResume = live && state.LifecycleError is null && execution?.Cooling?.CanResume == true && fixedUsable;
        var duty = DeviceDashboard.Sample(state, Target, DeviceFeature.DutyRead);
        reported = live && duty is not null && DeviceDashboard.Fresh(duty, elapsedSeconds) ? duty.Value : null;
        var needsSetup = endpoint?.Capabilities.Any(c => (c.Feature is DeviceFeature.FixedDuty or DeviceFeature.AutomaticDuty) &&
            c.Support == ImplementationSupport.Supported && c.Availability == DeviceAvailability.NeedsSetup) == true;
        NeedsSetup = live && state.LifecycleError is null && execution?.Cooling?.Faulted != true &&
            (needsSetup || (fixedUsable || automaticUsable) &&
                (setup is null || DeviceConfigurationRules.SafetyReadiness(setup.Safety) is not null));
        Problem = !connected ? "サービス未接続" : !live ? "状態を更新してください" : state.LifecycleError ?? execution?.Cooling?.Fault ??
            (!fixedUsable && !automaticUsable ? needsSetup ? "設定待ち" : "操作未対応" : setup is null ? "出力範囲を設定してください" : DeviceConfigurationRules.SafetyReadiness(setup.Safety));
        if (!Dirty && !conflicted) Follow();
    }
    private void Begin() { if (!Dirty && !conflicted) editedAt = current; }
    private void Changed()
    {
        EditRevision++;
        // Only the active policy's inputs matter. Returning to the original policy is a local cancel,
        // but an observed conflict still requires an explicit reset before another edit may apply.
        conflicted |= Conflict;
        Dirty = ModeChanged || DutyChanged || ProfileChanged;
        if (!Dirty && !conflicted) { editedAt = null; Follow(); }
    }
    public void SetDuty(int value) { value = Math.Clamp(value, 0, 100); if (value == Duty) return; Begin(); DeviceManaged = false; Automatic = false; Duty = value; Changed(); }
    public void SetAutomatic(bool value) { if (value == Automatic && !DeviceManaged) return; Begin(); DeviceManaged = false; Automatic = value; Changed(); }
    public void SelectDeviceManagement() { if (DeviceManaged) return; Begin(); DeviceManaged = true; Automatic = false; Changed(); }
    public void SelectProfile(string? id) { if (id == ProfileId) return; Begin(); ProfileId = id; Changed(); }
    public void Reset() { EditRevision++; Dirty = false; editedAt = null; conflicted = false; Follow(); }
    public bool Complete(DeviceCoolingEdit edit, long editRevision, DeviceOperationResult result)
    {
        // A completion belongs to exactly the edit that was sent. Partial output or a failed
        // policy save remains pending even when the overall hardware result was confirmed.
        if (EditRevision != editRevision || edit.Version.Target.Target != Target || result.Outcome != Outcome.Confirmed ||
            result.Targets is not [var targetResult] || targetResult.Target != Target ||
            targetResult.Generation != edit.Version.Target.Generation || targetResult.Outcome != Outcome.Confirmed ||
            current is null || current.Epoch != edit.Version.Epoch || current.Topology != edit.Version.Topology ||
            current.Target.Generation != targetResult.Generation || current.Target.ExpectedPolicyRevision != targetResult.PolicyRevision ||
            targetResult.Persistence is { State: not DevicePolicySaveState.Saved }) return false;
        Reset(); return true;
    }
    private void Follow()
    {
        DeviceManaged = execution?.Owned != true;
        Automatic = execution?.Owned == true && execution.Cooling?.Mode == FanMode.Auto;
        ProfileId = execution?.Cooling?.ProfileId ?? configuration?.Profiles.FirstOrDefault()?.Id;
        // After release, use a newly read hardware duty. Old requested or confirmed outputs are not current readings.
        var value = execution?.Owned == true ? execution.RequestedDuty ?? execution.Cooling?.TargetDuty : reported;
        if (value is { } v && double.IsFinite(v)) Duty = (int)Math.Clamp(Math.Round(v), 0, 100);
        baselineManaged = DeviceManaged; baselineAutomatic = Automatic;
        baselineDuty = Duty; baselineProfileId = ProfileId;
    }
    public DeviceCoolingEdit Capture(DeviceOperation? operation = null)
    {
        var kind = operation ?? (DeviceManaged ? DeviceOperation.ReleaseCooling : Automatic ? DeviceOperation.SetAutomatic : DeviceOperation.SetDuty);
        var allowed = kind switch { DeviceOperation.ReleaseCooling => operation is null ? CanApply : CanRelease, DeviceOperation.ResumeCooling => CanResume,
            DeviceOperation.SetDuty or DeviceOperation.SetAutomatic => !DeviceManaged && CanApply, _ => false };
        if (!allowed || current is null) throw new InvalidOperationException(Conflict ? "別の操作で変更されました。現在の設定を読み込んでください" : Problem ?? "この操作を利用できません");
        var version = operation is DeviceOperation.ReleaseCooling or DeviceOperation.ResumeCooling ? current : editedAt ?? current;
        return new(version, kind, kind == DeviceOperation.SetDuty ? Duty : null,
            kind == DeviceOperation.SetAutomatic ? Contract.Copy(SelectedProfile!) : null);
    }
}
