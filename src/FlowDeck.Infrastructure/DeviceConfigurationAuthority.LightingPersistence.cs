using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceConfigurationAuthority
{
    private readonly Dictionary<EndpointReference, long> lightingOrders = [];
    public async Task<LightingPolicySaveResult[]> SaveLightingPoliciesAsync(LightingPolicyChange incoming)
    {
        var change = Contract.Copy(incoming);
        if (change.Order < 1 || change.Parts is not { Length: > 0 and <= 256 } ||
            !DeviceLightingLibrary.ValidParts(change.Parts) || change.Bindings is not { Length: <= DeviceRegistry.MaximumDevices })
            throw new IOException("適用した照明の保存内容が不正です");
        if (change.Adjustment is { } adjustment && (!DeviceLightingAdjustments.ValidPlan(adjustment) || change.Parts.Any(p => p.Targets.Any(t =>
            !adjustment.Entries.Any(e => e.Target == t && DeviceLighting.SameProgram(p.Program, DeviceLightingAdjustments.Effective(e.Next)))))))
            throw new IOException("適用した明るさと保存対象が一致しません");
        if (change.FrameGroups is { } frames && (frames.Length > 256 || frames.Any(g => g is not { Length: > 0 and <= 256 } ||
                g.Any(e => e is null || !DeviceConfigurationRules.ValidReference(e.Target) || !DeviceLighting.FrameProgram(e.Program))) ||
                frames.Sum(g => g.Length) > 256 || frames.SelectMany(g => g).DistinctBy(e => e.Target).Count() != frames.Sum(g => g.Length)))
            throw new IOException("適用した照明のフレーム構成が不正です");
        await access.WaitAsync().ConfigureAwait(false);
        try
        {
            CheckCurrent();
            var targets = change.Parts.SelectMany(p => p.Targets).ToArray();
            var deviceIds = targets.Select(t => t.DeviceId).Distinct().Order().ToArray();
            if (configuration.Bindings.InstallationId != installation || !deviceIds.SequenceEqual(change.Bindings.Select(b => b.DeviceId).Order()) ||
                change.Parts.Any(p => p.Targets.Any(t => change.Bindings.SingleOrDefault(b => b.DeviceId == t.DeviceId) is not { } binding ||
                    !configuration.Bindings.Devices.Contains(binding) && !CurrentBindingMatches(t, SelectedLightingFeature(p.Program), binding))))
                throw new IOException("照明の実機関連付けが変更されました");
            var stale = targets.Where(t => lightingOrders.GetValueOrDefault(t) >= change.Order).ToHashSet();
            var registered = configuration.Devices.Devices.SelectMany(d => d.Endpoints.Where(e => e.Kind == EndpointKind.Lighting)
                .Select(e => new EndpointReference(d.Id, e.Id))).ToHashSet();
            var sessionOnly = change.Parts.Where(p => p.Targets.Any(t => !registered.Contains(t))).SelectMany(p => p.Targets).ToHashSet();
            var applied = new List<SynchronizedLighting>();
            foreach (var part in change.Parts)
            {
                // Selecting a color never registers hardware or imports an old physical association.
                // Keep a mixed unregistered synchronization wholly within the present session.
                if (part.Targets.Any(sessionOnly.Contains)) continue;
                // A saved synchronization selection is one program across its entire accepted target set.
                if (DeviceLighting.Animated(part.Program) && part.Targets.Any(stale.Contains)) { foreach (var t in part.Targets) stale.Add(t); continue; }
                var selected = part.Targets.Where(t => !stale.Contains(t)).ToArray();
                if (selected.Length > 0) applied.Add(new(selected, part.Program));
            }
            var accepted = applied.SelectMany(p => p.Targets).ToHashSet();
            if (accepted.Count > 0)
            {
                var intensityEntries = change.Adjustment?.Entries.Where(e => accepted.Contains(e.Target)).ToArray();
                if (intensityEntries?.Any(e => !DeviceLightingAdjustments.Matches(configuration.Lighting, e)) == true)
                    throw new IOException("明るさの基準設定が保存前に変更されました");
                // These requests already reached hardware. Even a failed disk commit must supersede older,
                // delayed saves; otherwise an old color could be published as the last successful selection.
                foreach (var target in accepted) lightingOrders[target] = change.Order;
                var current = configuration.Lighting;
                var preferences = (current.Preferences ?? []).Where(p => !accepted.Contains(p.Target))
                    .Concat(applied.SelectMany(p => p.Targets.Select(t => new EndpointLighting(t, p.Program)))).ToArray();
                var preserved = (current.SavedSynchronization ?? []).Select(g => g with { Targets = g.Targets.Where(t => !accepted.Contains(t)).ToArray() })
                    .Where(g => g.Targets.Length > 0);
                var synchronization = preserved.Concat(applied.Where(p => DeviceLighting.Animated(p.Program))).ToArray();
                // Explicit applications establish a new baseline; stale off/brightness snapshots must not revive older colors.
                static IEnumerable<EndpointReference> Baseline(DeviceBrightness a) => a.Baseline is null ? [] :
                    a.Baseline.Individual.Select(i => i.Target).Concat(a.Baseline.Synchronized.SelectMany(g => g.Targets));
                var adjustments = current.Adjustments.Where(a => !Baseline(a).Any(accepted.Contains)).ToArray();
                var intensity = (current.TargetAdjustments ?? []).Where(a => !accepted.Contains(a.Target))
                    .Concat(intensityEntries?.Select(e => e.Next) ?? []).ToArray();
                var byTarget = preferences.ToDictionary(p => p.Target, p => p.Program);
                // A phase group is independent of its endpoint colors/brightness. Include unedited members
                // only while their selected programs still match and no later write has superseded this snapshot.
                var incomingFrames = (change.FrameGroups ?? []).Where(g => g.Any(e => accepted.Contains(e.Target)))
                    .Select(g => g.Where(e => lightingOrders.GetValueOrDefault(e.Target) <= change.Order &&
                        byTarget.TryGetValue(e.Target, out var p) && DeviceLighting.SameProgram(p, e.Program)).Select(e => e.Target).ToArray())
                    .Where(g => g.Any(accepted.Contains)).ToArray();
                var replacedFrames = accepted.Concat(incomingFrames.SelectMany(g => g)).ToHashSet();
                var frameGroups = (current.SavedFrameGroups ?? (current.SavedSynchronization ?? []).Select(g => g.Targets).ToArray())
                    .Select(g => g.Where(t => !replacedFrames.Contains(t)).ToArray()).Where(g => g.Length > 0).Concat(incomingFrames).ToArray();
                var next = current with { Preferences = preferences, SavedSynchronization = synchronization, Adjustments = adjustments,
                    TargetAdjustments = current.TargetAdjustments is null && change.Adjustment is null ? null : intensity,
                    SavedFrameGroups = change.FrameGroups is null && current.SavedFrameGroups is null ? null : frameGroups };
                // This is selected output state, not an edit to group/scene membership; don't invalidate other in-flight presets.
                if (!Same(current, next)) PersistLightingLibrary(next);
            }
            return targets.Select(t => new LightingPolicySaveResult(t, new(stale.Contains(t) ? DevicePolicySaveState.Superseded :
                sessionOnly.Contains(t) ? DevicePolicySaveState.SessionOnly : DevicePolicySaveState.Saved,
                stale.Contains(t) ? "新しい照明設定の保存を優先しました" : sessionOnly.Contains(t) ? "今回の接続中のみ有効です" :
                "選択した照明の基準色・効果・対象を保存しました"))).ToArray();
        }
        finally { access.Release(); }
    }
}
