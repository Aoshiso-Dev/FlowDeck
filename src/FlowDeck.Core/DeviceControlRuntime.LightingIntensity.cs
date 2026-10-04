namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private LightingGroup? LightingUpdateGroup(DeviceServiceRequest request, DeviceTargetVersion[] targets,
        IReadOnlyDictionary<EndpointReference, LightingProgram> programs, bool advanced = false, bool keepStatic = false)
    {
        if (targets.Length == 0 || targets.Any(t => !DeviceLighting.FrameProgram(programs[t.Target]))) return null;
        var owners = targets.Select(t => lightingOwners.GetValueOrDefault(t.Target)).Distinct().ToArray();
        if (owners is not [string owner] || !lightingGroups.TryGetValue(owner, out var group) || group.Owner != request.Session ||
            group.Stopping || group.Fault is not null || group.Frames == 0) return null;
        // When the resulting composition is entirely static, its normal stop can settle the other
        // endpoints and the explicit selection can use native mode instead of streaming forever.
        if (!keepStatic && !group.Programs.Any(p => DeviceLighting.Animated(programs.GetValueOrDefault(p.Key) ?? p.Value))) return null;
        foreach (var target in targets)
        {
            var participant = group.Participants.SingleOrDefault(p => p.Targets.Any(t => t.Target == target.Target));
            var before = participant?.Targets.Single(t => t.Target == target.Target);
            if (participant is null || !participant.Limits.EndpointColors || before!.Generation != target.Generation ||
                before.ExpectedPolicyRevision + (advanced ? 1 : 0) != target.ExpectedPolicyRevision ||
                participant.Driver is not IDeviceLightingCompositionDriver && !DeviceLighting.IntensityCompatible(group.Programs[target.Target], programs[target.Target])) return null;
        }
        return group;
    }
    private string? CheckLightingUpdate(DeviceServiceRequest request, LightingGroup group,
        IReadOnlyDictionary<EndpointReference, LightingProgram> programs)
    {
        if (group.Participants.Any(p => !CurrentLighting(group, p))) return "実行中の照明の対象・所有が変更されました";
        var selected = request.Targets!.Select(t => t.Target).ToHashSet();
        if (group.Programs.Keys.Any(t => !selected.Contains(t) && lightingSelectionsPending.ContainsKey(t))) return "同じ照明構成の前の操作が完了するまでお待ちください";
        if (CheckBatch(FrameRequest(request, request.Targets!, programs), checkLightingStream: false) is { } invalid) return invalid;
        return CheckLightingComposition(request, group.Participants.SelectMany(p => p.Targets).ToArray(),
            group.Programs.ToDictionary(p => p.Key, p => programs.GetValueOrDefault(p.Key) ?? p.Value));
    }
    // Existing endpoints stay in the same stream and recovery ownership. Intensity edits keep their
    // clocks; a new color/effect/speed starts only the selected endpoint clocks after prior work ends.
    private Task<DeviceTargetResult[]>? QueueLightingIntensityUpdateCore(DeviceServiceRequest request, DeviceTargetVersion[] targets,
        IReadOnlyDictionary<EndpointReference, LightingProgram>? programs = null, Func<string?>? precondition = null, bool preserveClocks = false)
    {
        if (request.Operation != DeviceOperation.SetLighting) return null;
        programs ??= targets.ToDictionary(t => t.Target, _ => request.Lighting!);
        var group = LightingUpdateGroup(request, targets, programs, advanced: true, keepStatic: preserveClocks);
        if (group is null) return null;
        var selected = targets.ToDictionary(t => t.Target);
        var previous = group.FrameWork;
        foreach (var participant in group.Participants)
            for (var i = 0; i < participant.Targets.Length; i++)
                if (selected.TryGetValue(participant.Targets[i].Target, out var updated)) participant.Targets[i] = updated;
        foreach (var target in selected.Keys) group.Programs[target] = Contract.Copy(programs[target]);
        var revision = ++group.CompositionRevision;
        var work = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            lock (gate)
            {
                RefreshCore();
                if (group.CompositionRevision != revision || group.Stopping || group.Fault is not null || configurationChanging ||
                    stopping || releasing || session != request.Session || precondition?.Invoke() is not null || request.TopologyRevision != registry.Snapshot.TopologyRevision ||
                    targets.Any(t => !states.TryGetValue(t.Target, out var s) || s.Generation != t.Generation || s.PolicyRevision != t.ExpectedPolicyRevision) ||
                    group.Participants.Any(p => !CurrentLighting(group, p)))
                {
                    // Roll back only our unsent program, never a later edit or its monotonic policy revision.
                    if (group.CompositionRevision == revision && !group.Stopping)
                    {
                        foreach (var target in selected.Keys)
                        { group.Programs[target] = group.AppliedPrograms[target]; group.Origins[target] = group.AppliedOrigins[target]; }
                        group.CompositionRevision++;
                        foreach (var t in targets)
                            if (states.TryGetValue(t.Target, out var s) && s.Generation == t.Generation && s.PolicyRevision == t.ExpectedPolicyRevision)
                                states[t.Target] = s with { RequestedLighting = group.AppliedPrograms[t.Target], Outcome = Outcome.Rejected, Message = "照明の変更前に対象・所有・設定が変更されました" };
                    }
                    return targets.Select(t => new DeviceTargetResult(t.Target, t.Generation, t.ExpectedPolicyRevision, Outcome.Rejected,
                        "照明の変更前に対象・所有・設定が変更されました")).ToArray();
                }
                var now = clock.GetTimestamp();
                var restart = !preserveClocks && (selected.Keys.Any(t => !DeviceLighting.SameProgram(
                    group.AppliedPrograms[t] with { Brightness = group.Programs[t].Brightness }, group.Programs[t])) ||
                    selected.Keys.Select(t => group.AppliedOrigins[t]).Distinct().Count() > 1);
                foreach (var target in selected.Keys)
                    group.Origins[target] = restart ? now : group.AppliedOrigins[target];
            }
            return await WriteLightingRoundAsync(group).ConfigureAwait(false);
        });
        group.FrameWork = work;
        group.Monitor = ObserveLightingRoundAsync(group, work);
        return CompleteLightingIntensityAsync(group, work, targets, revision);
    }
    private async Task<DeviceTargetResult[]> CompleteLightingIntensityAsync(LightingGroup group, Task<DeviceTargetResult[]> work,
        DeviceTargetVersion[] selected, long revision)
    {
        var results = await work.ConfigureAwait(false);
        var failed = results.Any(r => r.Outcome is not (Outcome.Confirmed or Outcome.WrittenUnverified));
        bool current;
        lock (gate) current = group.CompositionRevision == revision;
        // A later edit owns its queued frame. An old rejected request must not stop it. Otherwise a partly
        // applied edit follows the same whole-group restoration rule as a failed synchronization start.
        if (failed && current) await StopLightingGroupAsync(group, DeviceLightingStopReason.Failure).ConfigureAwait(false);
        return selected.Select(t =>
        {
            var result = results.SingleOrDefault(r => r.Target == t.Target) ?? new(t.Target, t.Generation, t.ExpectedPolicyRevision, Outcome.Unknown, "明るさの端子別結果がありません");
            return failed && current && result.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified ? result with {
                Outcome = Outcome.Unknown, Message = "同期対象の一部が失敗したため明るさ変更を終了しました。復元結果を確認してください" } : result;
        }).ToArray();
    }
}
