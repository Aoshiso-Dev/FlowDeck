namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private async Task<DeviceTargetResult[]> PersistLightingSelectionAsync(DeviceServiceRequest request, DeviceTargetVersion[] targets,
        LightingPresetPlan? plan, DeviceTargetResult[] results, long order, LightingAdjustmentPlan? adjustment = null)
    {
        if (lightingPersistence is null || order == 0) return results;
        var successful = results.Where(r => r.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified).Select(r => r.Target).ToHashSet();
        if (successful.Count == 0) return results;
        var selected = new List<SynchronizedLighting>();
        var saved = successful.ToDictionary(t => t, _ => new DevicePolicySaveResult(DevicePolicySaveState.Superseded,
            "照明の対象・設定が変更されたため、以前の要求を保存しませんでした"));
        try
        {
            PhysicalDeviceBinding[] bindings; EndpointLighting[][] frameGroups;
            lock (gate)
            {
                bool Current(EndpointReference target) => request.Session == session &&
                    states.TryGetValue(target, out var current) && current.Owned &&
                    targets.Any(t => t.Target == target && t.Generation == current.Generation && t.ExpectedPolicyRevision == current.PolicyRevision);
                var parts = adjustment?.Parts ?? plan?.Parts ?? [new SynchronizedLighting(targets.Select(t => t.Target).ToArray(), request.Lighting!)];
                foreach (var part in parts)
                {
                    var accepted = part.Targets.Where(t => successful.Contains(t) && Current(t)).ToArray();
                    if (DeviceLighting.Animated(part.Program))
                    {
                        // A partial first frame is restored as a group. Never save its successful members as a new effect.
                        var owner = lightingOwners.GetValueOrDefault(part.Targets[0]);
                        if (accepted.Length != part.Targets.Length || owner is null || !lightingGroups.TryGetValue(owner, out var group) ||
                            group.Stopping || group.Frames == 0 || part.Targets.Any(t => lightingOwners.GetValueOrDefault(t) != owner)) continue;
                    }
                    if (accepted.Length > 0 && part.Targets.Any(t => drivers.GetValueOrDefault(t.DeviceId) is IDeviceConnectionLightingDriver { UsesConnectionLighting: true }))
                    {
                        // A mixed synchronization is connection-scoped as a whole; never persist a partial group.
                        foreach (var target in accepted) saved[target] = new(DevicePolicySaveState.SessionOnly, "今回の接続中のみ有効です。次の接続へ照明設定を引き継ぎません");
                        continue;
                    }
                    if (accepted.Length > 0)
                    {
                        var existing = DeviceLighting.Animated(part.Program) ? selected.FindIndex(p => DeviceLighting.SameProgram(p.Program, part.Program) &&
                            lightingOwners.GetValueOrDefault(p.Targets[0]) == lightingOwners.GetValueOrDefault(accepted[0])) : -1;
                        if (existing < 0) selected.Add(new(accepted, part.Program));
                        else selected[existing] = selected[existing] with { Targets = [.. selected[existing].Targets, .. accepted] };
                    }
                }
                bindings = selected.SelectMany(p => p.Targets.Select(t => (Target: t, Feature: LightingFeature(p.Program)))).Select(item =>
                {
                    if (PhysicalBindingError(item.Target, item.Feature) is { } error) throw new IOException(error);
                    return OperationBinding(item.Target, item.Feature);
                }).DistinctBy(b => b.DeviceId).ToArray();
                var acceptedTargets = selected.SelectMany(p => p.Targets).ToHashSet();
                frameGroups = lightingGroups.Values.Where(g => !g.Stopping && g.Fault is null && g.Frames > 0 && g.Participants.All(p => CurrentLighting(g, p)) &&
                        g.Programs.Keys.Any(acceptedTargets.Contains))
                    .Where(g => !g.Programs.Keys.Any(t => drivers.GetValueOrDefault(t.DeviceId) is IDeviceConnectionLightingDriver { UsesConnectionLighting: true }))
                    .Select(g => g.Programs.Select(p => new EndpointLighting(p.Key, p.Value)).ToArray()).ToArray();
            }
            if (selected.Count > 0)
            {
                // Storage is outside both the runtime lock and the hardware resource reservation. Only the chosen program
                // crosses this boundary; reported/scaled animation frames never become the saved baseline.
                var answer = await lightingPersistence.SaveLightingPoliciesAsync(new(order, Contract.Copy(selected.ToArray()), Contract.Copy(bindings), adjustment,
                    Contract.Copy(frameGroups))).ConfigureAwait(false);
                var expected = selected.SelectMany(p => p.Targets).ToHashSet();
                if (answer is null || answer.Length != expected.Count || answer.Any(r => r is null || r.Result is null || !Enum.IsDefined(r.Result.State) ||
                        !DeviceConfigurationRules.Text(r.Result.Message, 16384)) || !expected.SetEquals(answer.Select(r => r.Target)))
                    throw new IOException("照明設定の保存結果が欠落または不正です");
                foreach (var result in answer) saved[result.Target] = result.Result;
            }
        }
        catch (Exception e)
        {
            foreach (var target in selected.SelectMany(p => p.Targets))
                saved[target] = new(DevicePolicySaveState.Failed, "送信後に照明設定を保存できませんでした: " + e.Message);
        }
        lock (gate)
            foreach (var result in results)
                if (saved.TryGetValue(result.Target, out var persistence) && request.Session == session &&
                    states.TryGetValue(result.Target, out var current) && current.Generation == result.Generation && current.PolicyRevision == result.PolicyRevision)
                    states[result.Target] = current with { Persistence = persistence };
        return results.Select(r => saved.TryGetValue(r.Target, out var persistence) ? r with { Persistence = persistence } : r).ToArray();
    }
}
