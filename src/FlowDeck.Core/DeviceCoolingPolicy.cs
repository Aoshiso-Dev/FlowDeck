namespace FlowDeck.Core;

public sealed record DeviceCoolingStatus(FanMode Mode, int? TargetDuty, bool Faulted, string? Fault, bool CanResume,
    bool Restarting, string? ProfileId = null, long? ProfileRevision = null);
public sealed record CoolingDecision(int Duty, bool Write, bool Fallback, bool Restarting);
public sealed record CoolingAuthorization(string? Error = null, TimeSpan? ValidFor = null, DeviceDriverFailure Failure = DeviceDriverFailure.None);

// Only the service policy engine can construct an actuation. This object is never accepted from IPC.
public sealed class CoolingActuation
{
    private readonly CoolingSafety safety;
    private readonly Func<CoolingAuthorization> authorize;
    public string EndpointId { get; }
    public int Duty { get; }
    public bool Fallback { get; }
    public CoolingSafety Safety => Contract.Copy(safety);
    internal CoolingActuation(string endpointId, int duty, bool fallback, CoolingSafety safety, Func<CoolingAuthorization> authorize)
    { EndpointId = endpointId; Duty = duty; Fallback = fallback; this.safety = Contract.Copy(safety); this.authorize = authorize; }
    public CoolingAuthorization Authorize() => authorize();
}
public interface IDeviceCoolingDriver : IDeviceControlDriver
{
    CoolingEndpointConfiguration? CoolingConfiguration(string endpointId);
    string? ValidateCoolingSetup(string endpointId);
    Task<DeviceDriverResult> ApplyCoolingAsync(CoolingActuation actuation, CancellationToken ct);
}

/// <summary>One endpoint's policy and safety snapshot. Inputs contain only fresh, validated, generation-bound temperatures.</summary>
public sealed class DeviceCoolingPolicy
{
    private readonly CoolingSavedPolicy desired;
    private readonly CoolingSafety safety;
    private readonly TimeProvider clock;
    private readonly DescentLimiter descent = new();
    private int? confirmed, target;
    private long? restartSince;
    private bool restarting, faulted, blocked, fallbackAttempted, uncertain;
    private string? fault;
    public CoolingSavedPolicy Desired => Contract.Copy(desired);
    public CoolingSafety Safety => Contract.Copy(safety);
    public bool Faulted => faulted;
    public bool OutputBlocked => blocked;
    public EndpointReference[] RequiredSensors => Required(desired, safety);
    public static EndpointReference[] Required(CoolingSavedPolicy desired, CoolingSafety safety) =>
        (desired.Mode == FanMode.Auto ? desired.Profile!.Curves.Select(c => c.Sensor) : safety.ManualProtection.Select(r => r.Sensor))
        .Concat(safety.Stop is not null && (desired.Mode == FanMode.Auto || desired.Duty == 0) ? safety.Stop.Temperatures.Select(r => r.Sensor) : [])
        .Distinct().ToArray();
    public static string? Validate(CoolingSavedPolicy desired, CoolingSafety safety)
    {
        if (desired is null || DeviceConfigurationRules.SafetyReadiness(safety) is not null) return "冷却方針・安全条件が不正です";
        if (desired.SafetySnapshot is { } snapshot && !DeviceConfigurationRules.SameSafety(snapshot, safety)) return "保存した方針の安全条件が変更されています";
        return desired.Mode switch
        {
            FanMode.Manual => desired.Duty is >= 0 and <= 100 && desired.Profile is null ? null : "固定出力が不正です",
            FanMode.Auto => desired.Duty is null && desired.Profile is not null ? DeviceConfigurationRules.ProfileReadiness(desired.Profile, safety) : "温度カーブがありません",
            _ => "冷却方針が不正です"
        };
    }
    public DeviceCoolingPolicy(CoolingSavedPolicy desired, CoolingSafety safety, int currentDuty, TimeProvider? clock = null)
    {
        if (Validate(desired, safety) is { } error) throw new ArgumentException(error);
        if (currentDuty is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(currentDuty));
        this.desired = Contract.Copy(desired with { SafetySnapshot = safety }); this.safety = Contract.Copy(safety); this.clock = clock ?? TimeProvider.System;
        confirmed = currentDuty; descent.Reset(currentDuty, this.clock.GetTimestamp());
    }
    private IEnumerable<TemperatureRule> Protection => desired.Mode == FanMode.Auto
        ? desired.Profile!.Curves.Select(c => new TemperatureRule(c.Sensor, c.Overheat, c.ResumeBelow)) : safety.ManualProtection;
    public string? InputError(IReadOnlyDictionary<EndpointReference, double> temperatures, bool resume = false, bool stopping = false)
    {
        foreach (var sensor in RequiredSensors)
            if (!temperatures.TryGetValue(sensor, out var value) || !double.IsFinite(value) || value is <= 0 or >= 150)
                return "必要な温度が未取得・古化、または接続変更されています";
        foreach (var rule in Protection)
            if (temperatures[rule.Sensor] >= (resume ? rule.ResumeBelow : rule.Overheat))
                return resume ? "温度が復帰条件に達していません" : "過熱温度に達しました";
        return (stopping || desired.Mode == FanMode.Manual && desired.Duty == 0 && safety.Stop is not null) && !StopAllowed(temperatures)
            ? "停止条件の温度を確認できません" : null;
    }
    private bool StopAllowed(IReadOnlyDictionary<EndpointReference, double> temperatures) => safety.Stop is { } stop &&
        stop.Temperatures.All(r => temperatures.TryGetValue(r.Sensor, out var t) && double.IsFinite(t) && t < r.Below);
    public void Fail(string reason, bool prohibitWrites = false)
    {
        if (!faulted) fallbackAttempted = false;
        faulted = true; fault = reason; blocked |= prohibitWrites;
    }
    public CoolingDecision Decide(IReadOnlyDictionary<EndpointReference, double> temperatures, int? reportedDuty, double? rpm, bool rpmFresh)
    {
        if (!faulted && InputError(temperatures) is { } error) Fail(error);
        if (reportedDuty is null) Fail("端子の現在の出力を確認できません");
        if (!uncertain && confirmed is { } last && reportedDuty is { } current && current != last)
            Fail("外部で出力が変更されたため操作を停止しました", prohibitWrites: true);
        var now = clock.GetTimestamp();
        var requested = faulted ? Math.Max(safety.Fallback!.Value, Math.Max(reportedDuty ?? confirmed ?? 100, confirmed ?? 0)) :
            desired.Mode == FanMode.Manual ? DeviceConfigurationRules.EffectiveGroupDuty(desired.Duty!.Value, safety) :
            DeviceConfigurationRules.Calculate(desired.Profile!, safety, temperatures);
        if (!faulted && requested == 0 && !StopAllowed(temperatures)) requested = safety.Minimum!.Value;
        if (!faulted && desired.Mode == FanMode.Auto && !restarting)
        {
            requested = descent.Next(requested, now, desired.Profile!.DescentDelaySeconds!.Value, desired.Profile.DescentPerSecond!.Value, clock);
            if (requested > 0) requested = Math.Max(requested, safety.Minimum!.Value);
        }
        if (requested > 0 && reportedDuty is null or 0 && safety.Stop is not null) restarting = true;
        if (restarting && safety.Stop is { } stop)
        {
            if (restartSince is { } start && clock.GetElapsedTime(start, now).TotalSeconds >= stop.RestartSeconds)
            {
                if (!rpmFresh || rpm is not > 0)
                {
                    Fail("再始動後の回転を確認できません");
                    requested = Math.Max(stop.RestartDuty, Math.Max(requested, safety.Fallback!.Value));
                }
                else { restarting = false; restartSince = null; descent.Reset(requested, now); }
            }
            if (restarting) requested = Math.Max(requested, stop.RestartDuty);
        }
        target = requested;
        return new(requested, !blocked && (faulted ? !fallbackAttempted : confirmed != requested || uncertain), faulted, restarting);
    }
    public void BeginWrite(CoolingDecision decision) { if (decision.Fallback) fallbackAttempted = true; }
    public void CompleteWrite(CoolingDecision decision, DeviceDriverResult result)
    {
        if (result.Outcome == Outcome.Confirmed && result.ReportedDuty is { } duty)
        {
            confirmed = duty; uncertain = false;
            if (decision.Restarting) restartSince ??= clock.GetTimestamp();
            if (duty == 0) { restarting = false; restartSince = null; }
        }
        else
        {
            uncertain = true; Fail(result.Message);
            if (result.Outcome == Outcome.Rejected) blocked = true;
        }
    }
    public bool CanResume(IReadOnlyDictionary<EndpointReference, double> temperatures, int? reportedDuty, double? rpm, bool rpmFresh) =>
        faulted && !blocked && !uncertain && !restarting && reportedDuty == confirmed && reportedDuty is not null && InputError(temperatures, resume: true) is null &&
        (reportedDuty == 0 && desired.Duty == 0 || rpmFresh && rpm is > 0);
    public DeviceCoolingStatus Snapshot(IReadOnlyDictionary<EndpointReference, double> temperatures, int? reportedDuty, double? rpm, bool rpmFresh) =>
        new(blocked || uncertain ? FanMode.Unknown : faulted ? FanMode.Fallback : desired.Mode, target, faulted, fault,
            CanResume(temperatures, reportedDuty, rpm, rpmFresh), restarting, desired.Profile?.Id, desired.Profile?.Revision);
}
