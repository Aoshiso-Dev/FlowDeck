namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private async Task ReleaseLightingSetupAsync(DeviceEndpointSetupScope scope)
    {
        LightingGroup[] groups;
        lock (gate) groups = lightingGroups.Values.Where(g => g.Programs.Keys.Any(scope.Contains)).ToArray();
        // Validation excludes groups spanning preserved endpoints. End with the saved exit policy.
        var stopped = (await Task.WhenAll(groups.Select(g => StopLightingGroupAsync(g, DeviceLightingStopReason.ConfigurationChanged)))
            .ConfigureAwait(false)).SelectMany(r => r).ToDictionary();
        var failures = new List<string>();
        foreach (var endpoint in scope.LightingEndpoints!)
        {
            var target = new EndpointReference(scope.Target.DeviceId, endpoint);
            IDeviceControlDriver? driver; string[] domains; long generation;
            lock (gate)
            {
                driver = drivers.GetValueOrDefault(target.DeviceId);
                if (driver is null) continue;
                if (!stopped.ContainsKey(target) && states.GetValueOrDefault(target)?.Owned != true &&
                    !lightingRecoveryRequired.Contains(target) && !recoveryRequired.Contains(target)) continue;
                generation = states.GetValueOrDefault(target)?.Generation ?? 0;
                domains = registry.Snapshot.Devices.SingleOrDefault(d => d.Device.Id == target.DeviceId)?.Device.ResourceDomains ?? ["unopened:" + target.DeviceId];
            }
            DeviceDriverResult result;
            try
            {
                result = stopped.TryGetValue(target, out var ended) ? ended :
                    await scheduler.RunPreparedAsync(domains, HardwarePriority.Recovery, driver, DevicePreparationKind.Release,
                        token => driver.ApplyAsync(new(endpoint, DeviceOperation.StopLighting), token)).ConfigureAwait(false);
                if (!ValidDriverResult(result)) throw new IOException("照明の解除結果が不正です");
            }
            catch (Exception e) { result = new(Outcome.Unknown, e.Message); }
            lock (gate)
            {
                RefreshCore();
                var ok = result.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified;
                if (states.TryGetValue(target, out var state))
                    states[target] = state with { PolicyRevision = checked(state.PolicyRevision + 1),
                        Outcome = ok && state.Generation == generation ? result.Outcome : Outcome.Unknown,
                        RequestedLighting = ok ? null : state.RequestedLighting,
                        ReportedLighting = state.Generation == generation ? result.ReportedLighting : null,
                        Owned = !ok && state.Owned, Lighting = ok ? null : state.Lighting, Message = result.Message };
                if (ok) { recoveryRequired.Remove(target); lightingRecoveryRequired.Remove(target); }
                else { recoveryRequired.Add(target); lightingRecoveryRequired.Add(target); failures.Add(endpoint + ": " + result.Message); }
            }
            log.Write("lightingSetupRelease", new { target, result });
        }
        if (failures.Count > 0) throw new IOException("変更する端子の照明終了を確認できません: " + string.Join(" / ", failures));
    }
}
