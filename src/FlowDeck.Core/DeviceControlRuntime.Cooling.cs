namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private sealed record CoolingControl(EndpointReference Target, long Generation, long Revision, string Owner,
        DeviceCoolingPolicy Policy, Dictionary<EndpointReference, long> Sensors);
    private sealed record CoolingInputs(Dictionary<EndpointReference, double> Temperatures, int? Duty, double? Rpm, bool RpmFresh,
        TimeSpan? ValidFor);
    private readonly Dictionary<EndpointReference, CoolingControl> coolingPolicies = [];
    private readonly Dictionary<string, Task> coolingWork = [];
    private sealed record CoolingRestorePermit(string Owner,DeviceTargetVersion Target,string Policy);
    private readonly Dictionary<(string Request,EndpointReference Target),CoolingRestorePermit> coolingRestorePermits=[];
    private static string CoolingPolicyKey(CoolingSavedPolicy policy)=>System.Text.Json.JsonSerializer.Serialize(policy,Contract.Json);
    private bool HasCoolingRestorePermit(DeviceServiceRequest request,DeviceTargetVersion target,CoolingEndpointConfiguration config)=>
        coolingRestorePermits.TryGetValue((request.Id,target.Target),out var permit)&&permit.Owner==request.Session&&permit.Target==target&&
        config.SavedPolicy is { } policy&&permit.Policy==CoolingPolicyKey(policy);
    private static bool IsCoolingPolicy(DeviceOperation operation) => operation is DeviceOperation.SetDuty or DeviceOperation.SetAutomatic or DeviceOperation.ResumeCooling or DeviceOperation.RestoreSavedCooling;
    private CoolingEndpointConfiguration? CoolingConfiguration(IDeviceCoolingDriver driver, string endpoint)
    {
        var configured = driver.CoolingConfiguration(endpoint);
        if (configured is null || coolingPersistence is null) return configured;
        var saved = coolingPersistence.SavedCoolingConfiguration(new(driver.DeviceId, endpoint));
        return configured with { SavedPolicy = saved?.SavedPolicy, ResumeOnStart = saved?.ResumeOnStart == true &&
            DeviceConfigurationRules.SameSafety(configured.Safety, saved.Safety) };
    }
    private CoolingPolicyChange? PrepareCoolingChangeCore(DeviceServiceRequest request, DeviceTargetVersion target)
    {
        if (coolingPersistence is null || request.Operation is not (DeviceOperation.SetDuty or DeviceOperation.SetAutomatic or DeviceOperation.ResumeCooling or DeviceOperation.ReleaseCooling or DeviceOperation.RestoreSavedCooling) ||
            drivers[target.Target.DeviceId] is not IDeviceCoolingDriver driver) return null;
        if (configurationChanging || request.Session != session || !states.TryGetValue(target.Target, out var state) ||
            state.Generation != target.Generation || state.PolicyRevision != target.ExpectedPolicyRevision || CheckTarget(request, target) is not null)
            throw new IOException("保存前に操作対象・設定が変更されました");
        var config = CoolingConfiguration(driver, target.Target.EndpointId);
        if(request.Operation==DeviceOperation.ReleaseCooling&&(config is null||recoveryRequired.Contains(target.Target))) return null;
        if(config is null) throw new IOException("保存する冷却設定がありません");
        var policy = request.Operation switch {
            DeviceOperation.SetDuty => new CoolingSavedPolicy(FanMode.Manual, request.Duty, SafetySnapshot: config.Safety),
            DeviceOperation.SetAutomatic => new CoolingSavedPolicy(FanMode.Auto, Profile: request.Profile, SafetySnapshot: config.Safety),
            DeviceOperation.ResumeCooling => coolingPolicies[target.Target].Policy.Desired,
            DeviceOperation.RestoreSavedCooling => config.SavedPolicy!,
            _ => null
        };
        var ids = (policy is null ? [] : DeviceCoolingPolicy.Required(policy, config.Safety).Select(s => s.DeviceId))
            .Append(target.Target.DeviceId).Distinct().ToArray();
        var bindings = ids.Select(id => physicalBindings?.GetValueOrDefault(id) ?? throw new IOException("保存する実機関連付けがありません")).ToArray();
        return new(target.Target, checked(++coolingSaveOrder), config.Safety, policy, bindings);
    }
    private string? PhysicalBindingError(string deviceId) => ConnectionLightingBinding(deviceId) is not null || physicalBindings is null ? null :
        drivers.GetValueOrDefault(deviceId) is IPhysicalDeviceDriver physical && physical.BindingCandidate is { } actual &&
        physicalBindings.GetValueOrDefault(deviceId) == actual ? null : "機器または温度源の物理関連付けが一致しません";
    private Dictionary<EndpointReference, long> BindCoolingSensors(CoolingSavedPolicy policy, CoolingSafety safety)
    {
        var inventory = registry.Snapshot;
        return DeviceCoolingPolicy.Required(policy, safety).ToDictionary(s => s, s =>
        {
            if (PhysicalBindingError(s.DeviceId) is { } physicalError) throw new IOException(physicalError);
            var device = inventory.Devices.SingleOrDefault(d => d.Device.Id == s.DeviceId);
            if (device is null || registry.Check(new(Epoch, s.DeviceId, s.EndpointId, device.Generation, DeviceFeature.Temperature)) is not null)
                throw new IOException("指定した温度センサーを利用できません");
            return device.Generation;
        });
    }
    private CoolingInputs ReadCoolingInputs(EndpointReference target, Dictionary<EndpointReference, long> bindings)
    {
        var inventory = registry.Snapshot; var telemetry = TelemetryCore(inventory); var temperatures = new Dictionary<EndpointReference, double>();
        double remainingSeconds = 3;
        foreach (var (sensor, generation) in bindings)
        {
            if (PhysicalBindingError(sensor.DeviceId) is not null) continue;
            if (registry.Check(new(Epoch, sensor.DeviceId, sensor.EndpointId, generation, DeviceFeature.Temperature)) is not null) continue;
            var value = telemetry.SingleOrDefault(t => t.Target == sensor && t.Feature == DeviceFeature.Temperature);
            if (value is { Stale: false, Value: { } celsius, AgeSeconds: { } age })
            { temperatures[sensor] = celsius; remainingSeconds = Math.Min(remainingSeconds, 3 - age); }
        }
        var duty = telemetry.SingleOrDefault(t => t.Target == target && t.Feature == DeviceFeature.DutyRead);
        var rpm = telemetry.SingleOrDefault(t => t.Target == target && t.Feature == DeviceFeature.Rpm);
        // Controllers can report fractional percentages (for example Nuvoton's 8-bit PWM). Policies operate in whole percent.
        return new(temperatures, duty is { Stale: false, Value: { } dutyValue } ? (int)Math.Round(dutyValue) : null,
            rpm?.Value, rpm is { Stale: false }, bindings.Count == 0 ? null :
                TimeSpan.FromSeconds(temperatures.Count == bindings.Count ? remainingSeconds : 0));
    }
    private string? CheckCoolingRequest(DeviceServiceRequest request, DeviceTargetVersion target, IDeviceCoolingDriver driver)
    {
        if (driver.ValidateCoolingSetup(target.Target.EndpointId) is { } setupError) return setupError;
        var config = CoolingConfiguration(driver, target.Target.EndpointId);
        if (config is null) return "端子の冷却設定がありません";
        var previous = coolingPolicies.GetValueOrDefault(target.Target);
        if (request.Operation == DeviceOperation.RestoreSavedCooling && (!(config.ResumeOnStart || HasCoolingRestorePermit(request,target,config)) || previous is not null ||
            DeviceConfigurationRules.SavedPolicyReadiness(config) is not null)) return "再開可能な保存済み方針がないか、現在の操作が優先されています";
        if (request.Operation == DeviceOperation.ResumeCooling)
        {
            if (previous is null || previous.Generation != target.Generation || !DeviceConfigurationRules.SameSafety(previous.Policy.Safety, config.Safety)) return "再開する制御方針と安全条件を確認してください";
            var inputs = ReadCoolingInputs(target.Target, previous.Sensors);
            return previous.Policy.CanResume(inputs.Temperatures, inputs.Duty, inputs.Rpm, inputs.RpmFresh) ? null : "制御の復帰条件がまだ成立していません";
        }
        if (previous?.Policy.Faulted == true) return "異常時の制御は、復帰条件を確認して再開してください";
        var desired = request.Operation == DeviceOperation.RestoreSavedCooling ? config.SavedPolicy! :
            request.Operation == DeviceOperation.SetDuty ? new CoolingSavedPolicy(FanMode.Manual, request.Duty) : new(FanMode.Auto, Profile: request.Profile);
        if (desired.Mode == FanMode.Auto && registry.Check(new(Epoch, target.Target.DeviceId, target.Target.EndpointId, target.Generation, DeviceFeature.AutomaticDuty)) is not null)
            return "この端子は保存した自動制御に対応していません";
        if (DeviceCoolingPolicy.Validate(desired, config.Safety) is { } error) return error;
        var sensors = BindCoolingSensors(desired, config.Safety); var current = ReadCoolingInputs(target.Target, sensors);
        if (current.Duty is null) return "端子の最新の報告出力が必要です";
        var policy = new DeviceCoolingPolicy(desired, config.Safety, current.Duty.Value, clock);
        return policy.InputError(current.Temperatures);
    }
    private async Task<DeviceDriverResult> ApplyCoolingRequestAsync(DeviceServiceRequest request, DeviceTargetVersion target, IDeviceCoolingDriver driver, CancellationToken ct, Func<string?>? precondition = null)
    {
        CoolingControl control;
        lock (gate)
        {
            if (CheckCoolingRequest(request, target, driver) is { } error) return new(Outcome.Rejected, error);
            var config = CoolingConfiguration(driver, target.Target.EndpointId)!;
            var previous = coolingPolicies.GetValueOrDefault(target.Target);
            var desired = request.Operation == DeviceOperation.ResumeCooling ? previous!.Policy.Desired :
                request.Operation == DeviceOperation.RestoreSavedCooling ? config.SavedPolicy! :
                request.Operation == DeviceOperation.SetDuty ? new CoolingSavedPolicy(FanMode.Manual, request.Duty) : new(FanMode.Auto, Profile: request.Profile);
            var sensors = request.Operation == DeviceOperation.ResumeCooling ? previous!.Sensors : BindCoolingSensors(desired, config.Safety);
            var inputs = ReadCoolingInputs(target.Target, sensors);
            control = new(target.Target, target.Generation, target.ExpectedPolicyRevision, request.Session,
                new(desired, config.Safety, inputs.Duty!.Value, clock), new(sensors));
            coolingPolicies[target.Target] = control;
            states[target.Target] = states[target.Target] with { RequestedDuty = desired.Duty };
        }
        // A first equal-value write still acquires a durable exit obligation in the driver.
        return await StepCoolingCoreAsync(control, driver, forceWrite: true, ct, precondition).ConfigureAwait(false);
    }
    private bool CurrentCooling(CoolingControl control) => !stopping && !releasing && !ConfigurationBlocksDevice(control.Target.DeviceId) && session == control.Owner &&
        coolingPolicies.GetValueOrDefault(control.Target) == control && states.TryGetValue(control.Target, out var state) &&
        state.Generation == control.Generation && state.PolicyRevision == control.Revision && !recoveryRequired.Contains(control.Target);
    private void PublishCoolingCore(CoolingControl control, DeviceDriverResult? result = null)
    {
        if (!CurrentCooling(control)) return;
        var inputs = ReadCoolingInputs(control.Target, control.Sensors);
        var state = states[control.Target];
        states[control.Target] = state with { Cooling = control.Policy.Snapshot(inputs.Temperatures, inputs.Duty, inputs.Rpm, inputs.RpmFresh),
            Outcome = result?.Outcome ?? state.Outcome, ReportedDuty = result?.ReportedDuty ?? state.ReportedDuty, Message = result?.Message ?? state.Message };
    }
    private async Task<DeviceDriverResult> StepCoolingCoreAsync(CoolingControl control, IDeviceCoolingDriver driver, bool forceWrite, CancellationToken ct, Func<string?>? precondition = null)
    {
        CoolingDecision decision; CoolingActuation actuation;
        lock (gate)
        {
            RefreshCore();
            if (!CurrentCooling(control)) return new(Outcome.Rejected, "冷却方針・対象・操作権が変更されています");
            if (PhysicalBindingError(control.Target.DeviceId) is { } physicalError)
            {
                control.Policy.Fail(physicalError, prohibitWrites: true);
                PublishCoolingCore(control, new(Outcome.Unknown, physicalError));
                return new(Outcome.Rejected, physicalError);
            }
            var calculatedAt = clock.GetTimestamp();
            var inputs = ReadCoolingInputs(control.Target, control.Sensors);
            decision = control.Policy.Decide(inputs.Temperatures, inputs.Duty, inputs.Rpm, inputs.RpmFresh);
            if (control.Policy.OutputBlocked)
            {
                PublishCoolingCore(control, new(Outcome.Unknown, "端子の外部変更を確認してください", inputs.Duty));
                return new(Outcome.Rejected, "端子の外部変更を確認してください");
            }
            if (!decision.Write && !forceWrite) { PublishCoolingCore(control); return new(Outcome.Confirmed, "現在の出力を維持します", inputs.Duty); }
            control.Policy.BeginWrite(decision);
            actuation = new(control.Target.EndpointId, decision.Duty, decision.Fallback, control.Policy.Safety, () =>
            {
                lock (gate)
                {
                    RefreshCore();
                    if (ct.IsCancellationRequested || !CurrentCooling(control)) return new("冷却方針・対象・操作権が変更されています");
                    if (precondition?.Invoke() is { } changedGroup) return new(changedGroup);
                    if (PhysicalBindingError(control.Target.DeviceId) is { } changedBinding) return new(changedBinding);
                    var latest = ReadCoolingInputs(control.Target, control.Sensors);
                    if (decision.Fallback) return new();
                    if (control.Policy.InputError(latest.Temperatures, stopping: decision.Duty == 0) is { } inputError)
                        return new(inputError, Failure: DeviceDriverFailure.CoolingInputInvalid);
                    // A newer sample cannot extend the validity of the values used to calculate this duty.
                    var remaining = inputs.ValidFor - clock.GetElapsedTime(calculatedAt);
                    if (remaining is { } duration && latest.ValidFor is { } latestDuration)
                        remaining = duration < latestDuration ? duration : latestDuration;
                    return remaining <= TimeSpan.Zero ? new("計算に使った温度が古くなっています", Failure: DeviceDriverFailure.CoolingInputExpired) : new(ValidFor: remaining);
                }
            });
        }
        DeviceDriverResult result;
        try { result = await driver.ApplyCoolingAsync(actuation, ct).ConfigureAwait(false); }
        catch (Exception e) { result = new(Outcome.Unknown, "冷却出力が未確認です: " + e.Message); }
        if (!ValidDriverResult(result)) result = new(Outcome.Unknown, "冷却ドライバーの結果が不正です");
        lock (gate)
        {
            RefreshCore();
            if (coolingPolicies.GetValueOrDefault(control.Target) == control)
            {
                // Retain a completed old output for the same controller, but never publish over a newer requested revision.
                if (CurrentCooling(control) && result.Failure is DeviceDriverFailure.CoolingInputExpired or DeviceDriverFailure.CoolingInputInvalid)
                    control.Policy.Fail(result.Message);
                else if (CurrentCooling(control) && result.Outcome == Outcome.Rejected &&
                    control.Policy.InputError(ReadCoolingInputs(control.Target, control.Sensors).Temperatures, stopping: decision.Duty == 0) is { } inputError)
                    control.Policy.Fail(inputError);
                else if (CurrentCooling(control) || result.Outcome == Outcome.Confirmed) control.Policy.CompleteWrite(decision, result);
                PublishCoolingCore(control, result);
            }
        }
        return result;
    }
    private void CoolingRequestFinishedCore(DeviceServiceRequest request, DeviceTargetVersion target, DeviceDriverResult result)
    {
        if (!coolingPolicies.TryGetValue(target.Target, out var control)) return;
        if (request.Operation == DeviceOperation.ReleaseCooling)
        {
            if (result.Outcome == Outcome.Confirmed)
            { coolingPolicies.Remove(target.Target); states[target.Target] = states[target.Target] with { Cooling = null }; return; }
            control.Policy.Fail("制御終了を確認できません", prohibitWrites: true);
        }
        control = control with { Revision = target.ExpectedPolicyRevision }; coolingPolicies[target.Target] = control; PublishCoolingCore(control);
    }
    public Task TickCoolingAsync()
    {
        lock (gate)
        {
            ExpireCore();
            if (session is null || stopping || releasing || configurationChanging && providerIsolation is null && endpointSetupScope is null) return Task.CompletedTask;
            foreach (var group in coolingPolicies.Values.GroupBy(c => c.Target.DeviceId))
            {
                if (ConfigurationBlocksDevice(group.Key)) continue;
                if (coolingWork.TryGetValue(group.Key, out var previous) && !previous.IsCompleted) continue;
                var controls = group.ToArray(); var driver = (IDeviceCoolingDriver)drivers[group.Key]; var ct = sessionLifetime!.Token;
                coolingWork[group.Key] = Task.Run(async () =>
                {
                    foreach (var control in controls)
                    {
                        try
                        {
                            string[] domains; HardwarePriority priority;
                            lock (gate)
                            {
                                if (!CurrentCooling(control)) continue;
                                var inputs = ReadCoolingInputs(control.Target, control.Sensors);
                                priority = control.Policy.Faulted || control.Policy.InputError(inputs.Temperatures) is not null ? HardwarePriority.Recovery : HardwarePriority.Automatic;
                                domains = registry.Snapshot.Devices.Single(d => d.Device.Id == control.Target.DeviceId).Device.ResourceDomains;
                            }
                            // Submit one endpoint at a time per device, releasing its resource between writes so recovery/manual work can take priority.
                            await scheduler.RunPreparedAsync(domains, priority, driver, DevicePreparationKind.Command, token => StepCoolingCoreAsync(control, driver, false, token), ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                        catch (Exception e)
                        {
                            lock (gate) if (CurrentCooling(control)) { control.Policy.Fail(e.Message); PublishCoolingCore(control); }
                        }
                    }
                });
            }
            return Task.WhenAll(coolingWork.Values);
        }
    }
}
