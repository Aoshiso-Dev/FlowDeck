namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private DeviceEndpointSetupScope? endpointSetupScope;
    private DeviceEndpointSetupScope? failedEndpointSetupScope;
    private string? endpointSetupFailure;
    public void CheckEndpointSetupScope(DeviceEndpointSetupScope scope)
    {
        lock (gate)
        {
            if (!DeviceConfigurationRules.ValidReference(scope.Target) || scope.ReboundDevices.Length > DeviceRegistry.MaximumDevices ||
                scope.ReboundDevices.Any(id => !DeviceConfigurationRules.Key(id)) || scope.ReboundDevices.Distinct().Count() != scope.ReboundDevices.Length)
                throw new IOException("端子の変更範囲が不正です");
            if (scope.LightingEndpoints is { } lights && (lights.Length > DeviceRegistry.MaximumEndpoints ||
                !lights.Contains(scope.Target.EndpointId) || lights.Any(id => !DeviceConfigurationRules.Key(id)) || lights.Distinct().Count() != lights.Length))
                throw new IOException("照明の変更範囲が不正です");
            if (lightingIdentifications.Count != 0 || calibrationTrials.Count != 0)
                throw new IOException("識別・端子テストが終了してから設定を保存してください");
            if (!scope.ReboundDevices.Contains(scope.Target.DeviceId) && drivers.TryGetValue(scope.Target.DeviceId, out var driver) &&
                (scope.IsLighting ? driver is not IIndividualLightingConfiguration : driver is not IIndividualCoolingConfiguration)) throw new IOException("このドライバーは端子単位の設定更新に対応していません");
            if (coolingPolicies.Values.Any(c => !scope.Contains(c.Target) && c.Sensors.Keys.Any(s => scope.ReboundDevices.Contains(s.DeviceId))))
                throw new IOException("変更する温度源を使用中のファンがあります。該当する制御を終了してから保存してください");
            if (lightingGroups.Values.Any(g => g.Programs.Keys.Any(scope.Contains) && g.Programs.Keys.Any(t => !scope.Contains(t))))
                throw new IOException("変更する端子がほかの端子と照明同期中です。照明画面で対象端子だけを選び「照明の制御を終了」してから保存してください");
        }
    }
    private bool ConfigurationBlocksLighting(string id) => configurationChanging && (endpointSetupScope is { } scope
        ? scope.ReboundDevices.Contains(id) || scope.IsLighting && scope.Target.DeviceId == id : ConfigurationBlocksDevice(id));

    // Port setup drains submitted frames before releasing/configuring the selected port.
    // Keep their existing authority while draining; new rounds are paused for this device.
    private bool ConfigurationInvalidatesLighting(LightingParticipant participant) => configurationChanging && (endpointSetupScope is { } scope
        ? scope.ReboundDevices.Contains(participant.DeviceId)
        : ConfigurationBlocksDevice(participant.DeviceId));

    private async Task ReleaseEndpointSetupAsync(DeviceEndpointSetupScope scope)
    {
        IDeviceControlDriver[] rebound;
        lock (gate) rebound = drivers.Values.Where(d => scope.ReboundDevices.Contains(d.DeviceId)).ToArray();
        if (rebound.Length > 0) await ReleaseDriversAsync(DeviceReleaseReason.ConfigurationChanged, rebound).ConfigureAwait(false);
        if (scope.ReboundDevices.Contains(scope.Target.DeviceId)) return;
        if (scope.IsLighting) { await ReleaseLightingSetupAsync(scope).ConfigureAwait(false); return; }
        IDeviceControlDriver? driver; string[] domains; long expected;
        lock (gate)
        {
            driver = drivers.GetValueOrDefault(scope.Target.DeviceId);
            if (driver is null) return;
            if (states.GetValueOrDefault(scope.Target)?.Owned != true && !coolingPolicies.ContainsKey(scope.Target) && !recoveryRequired.Contains(scope.Target)) return;
            expected = states.GetValueOrDefault(scope.Target)?.Generation ?? 0;
            domains = registry.Snapshot.Devices.SingleOrDefault(d => d.Device.Id == driver.DeviceId)?.Device.ResourceDomains ?? ["unopened:" + driver.DeviceId];
        }
        DeviceDriverResult result;
        try
        {
            result = await scheduler.RunPreparedAsync(domains, HardwarePriority.Recovery, driver, DevicePreparationKind.Release,
                token => driver.ApplyAsync(new(scope.Target.EndpointId, DeviceOperation.ReleaseCooling), token)).ConfigureAwait(false);
            if (!ValidDriverResult(result)) throw new IOException("端子の解除結果が不正です");
        }
        catch (Exception e) { result = new(Outcome.Unknown, e.Message); }
        lock (gate)
        {
            RefreshCore();
            var ok = result.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified;
            if (states.TryGetValue(scope.Target, out var state))
                states[scope.Target] = state with { PolicyRevision = checked(state.PolicyRevision + 1),
                    Outcome = ok && state.Generation == expected ? result.Outcome : Outcome.Unknown,
                    RequestedDuty = ok ? null : state.RequestedDuty,
                    ReportedDuty = state.Generation == expected ? result.ReportedDuty : null,
                    Owned = !ok && state.Owned, Cooling = ok ? null : state.Cooling, Message = result.Message };
            if (ok) { recoveryRequired.Remove(scope.Target); coolingPolicies.Remove(scope.Target); }
            else
            {
                recoveryRequired.Add(scope.Target); lifecycleError = "変更する冷却端子の終了を確認できません: " + result.Message;
                throw new IOException(lifecycleError);
            }
        }
        log.Write("coolingSetupRelease", new { scope.Target, result });
    }
}
