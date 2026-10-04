using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed record DeviceM5ControlPlan(string Epoch, string Session, string Generation, long Topology,
    DeviceTargetVersion[] Targets, DeviceOperation Operation, int? Duty = null, CoolingTemperatureProfile? Profile = null, LightingProgram? Lighting = null);

/// <summary>Explicit 1.3 Hub and sensor projection. Selection never follows discovery order or a same-named endpoint.</summary>
public sealed class DeviceM5Control : IM5ControlBackend
{
    private readonly DeviceWorkspaceClient client;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private DeviceM5Selection selection;
    private readonly string disconnectedEpoch = Guid.NewGuid().ToString("N");
    private string? fanStamp, lightStamp;
    private long fanRevision, lightRevision, sequence;
    private int pending;
    private OperationResult? lastOperation;
    public DeviceM5Control(DeviceWorkspaceClient client, DeviceM5Selection? selection = null, TimeProvider? time = null)
    {
        this.client = client; clock = time ?? TimeProvider.System;
        DeviceM5Selection.Validate(selection ?? new()); this.selection = Contract.Copy(selection ?? new DeviceM5Selection());
    }
    public DeviceM5Selection Selection { get { lock (gate) return Contract.Copy(selection); } }
    public void Select(DeviceM5Selection next)
    {
        DeviceM5Selection.Validate(next);
        lock (gate) { if (pending > 0) throw new InvalidOperationException("M5GOの操作完了後に対象を変更してください"); selection = Contract.Copy(next); }
    }
    public string Epoch => client.Snapshot?.State.Inventory.Epoch ?? disconnectedEpoch;
    public AppSettings Settings => ReadM5State().Settings;
    public ControlSnapshot Snapshot => ReadM5State().Snapshot;
    public M5ControlView ReadM5State() { lock (gate) return Build(client.Snapshot); }
    internal M5ControlView Project(DeviceWorkspaceSnapshot? snapshot) { lock (gate) return Build(snapshot); }
    public string? ProfileReadiness(string id) => ReadM5State().ReadyProfiles.Contains(id) ? null : "M5GOのプリセットを設定してください";
    public Task<OperationResult> RequestAsync(ControlRequest request) => RequestFromM5Async(request, CancellationToken.None);
    public async Task<OperationResult> RequestFromM5Async(ControlRequest request, CancellationToken ct)
    {
        lock (gate) pending++;
        try { return await client.ApplyM5ControlAsync(this, request, ct).ConfigureAwait(false); }
        finally { lock (gate) pending--; }
    }
    private static bool Live(DeviceWorkspaceSnapshot? s) => s is not null && s.Connected && DeviceServiceProtocol.Id(s.Session) &&
        double.IsFinite(s.ElapsedSeconds) && s.ElapsedSeconds is >= 0 and < 3 && !s.State.Releasing && !s.State.ConfigurationChanging;
    private static string Hash(object value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Contract.Json)));
    private static int? Common(IEnumerable<int?> values)
    { var all = values.ToArray(); return all.Length > 0 && all[0] is not null && all.All(v => v == all[0]) ? all[0] : null; }
    private static string Short(string text, int bytes)
    {
        var result = new StringBuilder(); var count = 0;
        foreach (var rune in text.EnumerateRunes()) { if (count + rune.Utf8SequenceLength > bytes) break; result.Append(rune); count += rune.Utf8SequenceLength; }
        return result.ToString();
    }
    private EndpointReference[] FanTargets() => selection.Hub is null ? [] : selection.Fans.Select(f => new EndpointReference(selection.Hub.DeviceId, f)).ToArray();
    private EndpointReference[] LedTargets() => selection.Hub is null ? [] : selection.Leds.Select(f => new EndpointReference(selection.Hub.DeviceId, f)).ToArray();
    private static RegisteredDevice? Registered(DeviceWorkspaceSnapshot s, EndpointReference t) => s.Configuration.Configuration.Devices.Devices.SingleOrDefault(d => d.Id == t.DeviceId);
    private static double? Reading(DeviceWorkspaceSnapshot? s, EndpointReference? target, DeviceFeature feature, DeviceKind? kind = null)
    {
        if (!Live(s) || target is null) return null;
        var registered = Registered(s!, target);
        var instance = s!.State.Inventory.Devices.SingleOrDefault(d => d.Device.Id == target.DeviceId);
        if (registered?.Endpoints.Any(e => e.Id == target.EndpointId) != true || kind is not null && (registered.Kind != kind || instance?.Device.Kind != kind) ||
            instance?.Connected != true || instance.Device.Endpoints.SingleOrDefault(e => e.Id == target.EndpointId)?.Capabilities.Any(c => c.Feature == feature && c.Usable) != true)
            return null;
        var sample = DeviceDashboard.Sample(s.State, target, feature);
        return sample is not null && DeviceDashboard.Fresh(sample, s.ElapsedSeconds) ? sample.Value : null;
    }
    private long Timestamp(double age) => clock.GetTimestamp() - (long)(Math.Clamp(age, 0, 999) * clock.TimestampFrequency);
    private SensorReading Sensor(DeviceWorkspaceSnapshot? s, EndpointReference? target, string id, DeviceKind kind)
    {
        var value = Reading(s, target, DeviceFeature.Temperature, kind);
        var sample = s is null || target is null ? null : DeviceDashboard.Sample(s.State, target, DeviceFeature.Temperature);
        return new(id, id, "temperature", value, Timestamp(value is null ? 999 : sample!.AgeSeconds!.Value + s!.ElapsedSeconds));
    }
    private string? HubProblem(DeviceWorkspaceSnapshot? s)
    {
        if (!Live(s)) return "サービスの状態を更新してください";
        if (selection.Hub is not { } h) return "M5GOの操作先Hubを選択してください";
        var device = s!.State.Inventory.Devices.SingleOrDefault(d => d.Device.Id == h.DeviceId);
        if (device?.Connected != true) return "選択したHubが切断されています";
        var registered = s.Configuration.Configuration.Devices.Devices.SingleOrDefault(d => d.Id == h.DeviceId);
        if (device.Device.Provider != HubDeviceDriver.Provider || device.Device.DefinitionId != HubDeviceDriver.Definition ||
            registered?.Provider != HubDeviceDriver.Provider || registered.Kind != DeviceKind.Controller ||
            selection.HasHubControl() && s.Configuration.Configuration.Bindings.Devices.SingleOrDefault(b => b.DeviceId == h.DeviceId) != h ||
            s.Configuration.Candidates.SingleOrDefault(b => b.DeviceId == h.DeviceId) != h)
            return "Hubの個体・配線が変わりました。M5GOの操作先を選び直してください";
        return null;
    }
    private string? FanProblem(DeviceWorkspaceSnapshot? s, bool ignoreFault = false)
    {
        if (HubProblem(s) is { } problem) return problem;
        if (s!.State.LifecycleError is { } lifecycle) return lifecycle;
        var targets = FanTargets(); if (targets.Length == 0) return "M5GOで操作するHub端子を選択してください";
        foreach (var t in targets)
        {
            var setup = s.Configuration.Configuration.Cooling.Endpoints.SingleOrDefault(c => c.Target == t);
            var device = s.State.Inventory.Devices.Single(d => d.Device.Id == t.DeviceId);
            var endpoint = device.Device.Endpoints.SingleOrDefault(e => e.Id == t.EndpointId && e.Kind == EndpointKind.Cooling);
            var execution = s.State.Endpoints.SingleOrDefault(e => e.Target == t);
            if (setup?.WiringConfirmed != true || DeviceConfigurationRules.SafetyReadiness(setup.Safety) is { } ||
                Registered(s, t)?.Endpoints.Any(e => e.Id == t.EndpointId && e.Kind == EndpointKind.Cooling) != true)
                return "選択したファンの設定を確認してください";
            if (endpoint?.Capabilities.Any(c => c.Feature == DeviceFeature.FixedDuty && c.Usable) != true || execution?.Generation != device.Generation)
                return "選択したファンを制御できません";
            if (execution.Outcome is Outcome.Pending or Outcome.Accepted) return "ファンの処理中です";
            if (!ignoreFault && execution.Cooling?.Faulted == true) return execution.Cooling.Fault ?? "ファンの異常からの再開が必要です";
            if (Reading(s, t, DeviceFeature.Rpm) is null || Reading(s, t, DeviceFeature.DutyRead) is null) return "ファンの現在値を更新してください";
        }
        return null;
    }
    private CoolingTemperatureProfile? Profile(DeviceWorkspaceSnapshot? s, string slot)
    {
        if (s is null || selection.Profiles.SingleOrDefault(p => p.Slot == slot) is not { } mapping || FanTargets() is not { Length: > 0 } targets) return null;
        var profiles = targets.Select(t => s.Configuration.Configuration.Cooling.Endpoints.SingleOrDefault(c => c.Target == t)?.Profiles.SingleOrDefault(p => p.Id == mapping.ProfileId)).ToArray();
        return profiles[0] is { } first && profiles.All(p => p is not null && Hash(p) == Hash(first)) ? first : null;
    }
    private bool ProfileReady(DeviceWorkspaceSnapshot? s, CoolingTemperatureProfile? p)
    {
        if (p is null || FanProblem(s) is not null) return false;
        return FanTargets().All(t => {
            var config = s!.Configuration.Configuration.Cooling.Endpoints.Single(c => c.Target == t);
            var endpoint = s.State.Inventory.Devices.Single(d => d.Device.Id == t.DeviceId).Device.Endpoints.Single(e => e.Id == t.EndpointId);
            return endpoint.Capabilities.Any(c => c.Feature == DeviceFeature.AutomaticDuty && c.Usable) && DeviceConfigurationRules.ProfileReadiness(p, config.Safety) is null;
        }) && p.Curves.All(c => Reading(s, c.Sensor, DeviceFeature.Temperature) is not null);
    }
    private M5ControlView Build(DeviceWorkspaceSnapshot? s)
    {
        var targets = FanTargets(); var lights = LedTargets(); var live = Live(s); var hubError = HubProblem(s);
        var instance = s?.State.Inventory.Devices.SingleOrDefault(d => d.Device.Id == selection.Hub?.DeviceId);
        var executions = targets.Select(t => s?.State.Endpoints.SingleOrDefault(e => e.Target == t)).ToArray();
        var currentFanStamp = Hash(new { selection, live, s?.Session, epoch = s?.State.Inventory.Epoch, topology = s?.State.Inventory.TopologyRevision,
            generation = s?.Configuration.Generation, instance, hubError, s?.State.LifecycleError,
            targets = executions.Select(e => new { e?.Target, e?.Generation, e?.PolicyRevision, e?.Owned, e?.Outcome,
                mode = e?.Cooling?.Mode, fault = e?.Cooling?.Fault, resume = e?.Cooling?.CanResume }),
            fresh = targets.Select(t => new { t, rpm = Reading(s, t, DeviceFeature.Rpm) is not null, duty = Reading(s, t, DeviceFeature.DutyRead) is not null }) });
        var currentLightStamp = Hash(new { selection.Hub, selection.Leds, hubError, lighting = DeviceM5Lighting.Read(s, "all").Stamp });
        if (currentFanStamp != fanStamp) { fanStamp = currentFanStamp; fanRevision++; }
        if (currentLightStamp != lightStamp) { lightStamp = currentLightStamp; lightRevision++; }
        var setups = targets.Select(t => s?.Configuration.Configuration.Cooling.Endpoints.SingleOrDefault(c => c.Target == t)).ToArray();
        var minimum = setups.Length > 0 && setups.All(c => c?.Safety.Minimum is not null) ? setups.Max(c => c!.Safety.Minimum) : null;
        var profiles = DeviceM5Selection.Slots.Select(slot => (Slot: slot, Value: Profile(s, slot))).ToArray();
        var settings = new AppSettings { CpuSensorId = "m5/cpu", GpuSensorId = "m5/gpu",
            Profiles = profiles.Select(p => new AutoProfile(p.Slot, Short(p.Value?.Name ?? (p.Slot switch { "quiet" => "静音", "balanced" => "標準", _ => "冷却優先" }), 48), p.Value?.Revision ?? 0)).ToArray(),
            Limits = new(Minimum: minimum, AllowStop: setups.Length > 0 && setups.All(c => c?.Safety.Stop is not null)),
            Hardware = new(Fans: selection.Fans.ToArray(), Leds: []) };
        if (hubError is null && lights.Length > 0)
        {
            var draft = new DeviceLightingDraft(); draft.Update(s!.State, s.Configuration.Generation, s.Configuration.Configuration.Lighting, s.ElapsedSeconds, true);
            draft.Select(lights); draft.SetProgram(new("fixed", [new(52, 137, 255)]));
            if (draft.CanApply && lights.All(t => Registered(s, t)?.Endpoints.Any(e => e.Id == t.EndpointId && e.Kind == EndpointKind.Lighting) == true))
                settings = settings with { Hardware = settings.Hardware with { Leds = selection.Leds.ToArray() } };
        }
        var channels = Enumerable.Range(1, 5).Select(i => {
            var id = "fan" + i; var t = selection.Hub is null ? null : new EndpointReference(selection.Hub.DeviceId, id);
            var rpm = hubError is null ? Reading(s, t, DeviceFeature.Rpm) : null;
            var duty = hubError is null ? Reading(s, t, DeviceFeature.DutyRead) : null;
            return new FanChannel(id, rpm is null ? Presence.Unknown : Presence.Present,
                rpm is >= 0 and <= int.MaxValue ? (int)Math.Round(rpm.Value) : null,
                duty is >= 0 and <= 100 ? (int)Math.Round(duty.Value) : null, null);
        }).ToArray();
        var owned = executions.Length > 0 && executions.All(e => e?.Owned == true);
        var faulted = executions.Any(e => e?.Cooling?.Faulted == true);
        var mode = faulted ? FanMode.Fallback : executions.All(e => e?.Owned != true) ? FanMode.Monitor :
            owned && executions.Select(e => e!.Cooling?.Mode ?? FanMode.Unknown).Distinct().Count() == 1 ? executions[0]!.Cooling?.Mode ?? FanMode.Unknown : FanMode.Unknown;
        var selectedProfile = profiles.FirstOrDefault(p => p.Value is not null && owned && executions.All(e => e?.Cooling?.ProfileId == p.Value.Id && e.Cooling.ProfileRevision == p.Value.Revision));
        var dutyTarget = owned ? Common(executions.Select(e => e?.Cooling?.TargetDuty ?? e?.RequestedDuty)) : null;
        var dutyReported = hubError is null ? Common(selection.Fans.Select(f => channels.Single(c => c.Id == f).Duty)) : null;
        var last = mode == FanMode.Manual ? new FanPolicy(mode, Common(executions.Select(e => e?.RequestedDuty))) :
            mode == FanMode.Auto && selectedProfile.Value is not null ? new FanPolicy(mode, Profile: new(selectedProfile.Slot, selectedProfile.Value.Name, selectedProfile.Value.Revision)) : null;
        var fanError = FanProblem(s); var resume = faulted && FanProblem(s, true) is null && executions.Where(e => e?.Cooling?.Faulted == true).All(e => e!.Cooling!.CanResume);
        var status = new ControlSnapshot(s?.State.Inventory.Epoch ?? disconnectedEpoch, ++sequence, fanRevision, lightRevision, instance?.Generation ?? 0,
            hubError is null, executions.Any(e => e?.Owned == true), mode, last, last, dutyTarget, dutyReported,
            dutyReported is null ? Outcome.Unknown : Outcome.Confirmed, channels,
            [Sensor(s, selection.Cpu, "m5/cpu", DeviceKind.Processor), Sensor(s, selection.Gpu, "m5/gpu", DeviceKind.Graphics)],
            live ? Timestamp(s!.ElapsedSeconds) : null, new(Generation: instance?.Generation ?? 0),
            pending > 0, pending > 0, faulted, fanError, resume, fanError ?? "制御可能", lastOperation is null ? null : Contract.Copy(lastOperation), client.IsDemo);
        return new(settings, status, profiles.Where(p => ProfileReady(s, p.Value)).Select(p => p.Slot).ToArray());
    }
    public BoardPanel ReadMotherboard() => ProjectMotherboard(client.Snapshot);
    internal BoardPanel ProjectMotherboard(DeviceWorkspaceSnapshot? s)
    {
        lock (gate)
        {
            var live = Live(s); var ages = new List<double>();
            var rows = selection.Motherboard.Select(i => {
                var value = Reading(s, i.Target, i.Measurement, DeviceKind.Motherboard);
                if (value is not null) ages.Add(DeviceDashboard.Sample(s!.State, i.Target, i.Measurement)!.AgeSeconds!.Value + s.ElapsedSeconds);
                return new BoardPanelRow(Short(i.Name, 72), i.Measurement == DeviceFeature.Temperature ? "temperature" : "fan",
                    i.Measurement == DeviceFeature.Temperature ? "°C" : "RPM", value);
            }).ToArray();
            return new("マザーボード", live, !live ? "更新停止" : rows.Length == 0 ? "表示項目を選択してください" : rows.Any(r => r.Value is null) ? "未取得の項目があります" : "監視中",
                rows, live ? Math.Max(s!.ElapsedSeconds, ages.DefaultIfEmpty(0).Max()) * 1000 : null);
        }
    }
    internal DeviceM5ControlPlan Capture(DeviceWorkspaceSnapshot s, ControlRequest request)
    {
        lock (gate)
        {
            var view = Build(s); var snapshot = view.Snapshot;
            if (request.Source != "M5GO" || request.ControllerEpoch != snapshot.ControllerEpoch || request.LightingEffect is not null || request.LightingChannels is not null ||
                request.ExpectedRevision != (request.Command == "SetLighting" ? snapshot.LightingRevision : snapshot.ControlRevision))
                throw new InvalidOperationException("接続・操作先・設定が変更されました。画面を開き直してください");
            var targets = FanTargets(); CoolingTemperatureProfile? profile = null; LightingProgram? light = null; DeviceOperation operation;
            switch (request.Command)
            {
                case "SetLighting":
                    if (request.Duty is not null || request.ProfileId is not null || request.ProfileRevision is not null || request.Lighting is not ("blue" or "white" or "purple" or "off") ||
                        !snapshot.HubReady || view.Settings.Hardware.Leds is not { Length: > 0 }) throw new InvalidOperationException("選択したHub照明を操作できません");
                    targets = LedTargets(); DeviceM5Lighting.WholeActiveGroups(s, targets);
                    var preset = HubLighting.Preset(request.Lighting); light = new(preset.Mode, preset.Colors); operation = DeviceOperation.SetLighting; break;
                case "SetManualDuty":
                    if (request.Duty is not (>= 0 and <= 100) || request.ProfileId is not null || request.ProfileRevision is not null || request.Lighting is not null ||
                        snapshot.Readiness != "制御可能" || request.Duty < view.Settings.Limits.Minimum && !(request.Duty == 0 && view.Settings.Limits.AllowStop))
                        throw new InvalidOperationException(snapshot.Readiness == "制御可能" ? "出力の範囲が不正です" : snapshot.Readiness);
                    operation = DeviceOperation.SetDuty; break;
                case "SelectAutoProfile":
                    profile = Profile(s, request.ProfileId ?? "");
                    if (request.Duty is not null || request.Lighting is not null || !view.ReadyProfiles.Contains(request.ProfileId) || profile?.Revision != request.ProfileRevision)
                        throw new InvalidOperationException("プリセットが変更されたか、制御条件が揃っていません");
                    operation = DeviceOperation.SetAutomatic; break;
                case "ResumeControl":
                    if (request.Duty is not null || request.ProfileId is not null || request.ProfileRevision is not null || request.Lighting is not null || !snapshot.CanResume)
                        throw new InvalidOperationException("現在は制御を再開できません");
                    targets = targets.Where(t => s.State.Endpoints.Single(e => e.Target == t).Cooling?.Faulted == true).ToArray(); operation = DeviceOperation.ResumeCooling; break;
                default: throw new ArgumentException("未対応のM5GO操作です");
            }
            return new(snapshot.ControllerEpoch, s.Session!, s.Configuration.Generation, s.State.Inventory.TopologyRevision,
                targets.Select(t => s.State.Endpoints.Single(e => e.Target == t)).Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray(),
                operation, request.Duty, profile, light);
        }
    }
    internal OperationResult Result(ControlRequest request, Outcome outcome, string message, DeviceTargetResult[]? targets = null, string? error = null)
    {
        lock (gate)
        {
            var state = Build(client.Snapshot).Snapshot;
            return lastOperation = new(request.Id, "M5GO", request.Command, outcome, message, clock.GetUtcNow(), state.ControlRevision,
                state.LightingRevision, state.DeviceGeneration, targets?.Select(t => new ChannelResult(t.Target.EndpointId,
                    t.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified ? "completed" : "incomplete", t.ReportedDuty, JsonNamingPolicy.CamelCase.ConvertName(t.Outcome.ToString()),
                    t.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified ? null : t.Message)).ToArray(), error);
        }
    }
}
