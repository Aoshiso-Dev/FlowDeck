namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private readonly HashSet<EndpointReference> lightingTransitionTargets = [];
    private bool LightingTransitionBusy(DeviceServiceRequest request) =>
        request.Operation is DeviceOperation.SetLighting or DeviceOperation.StopLighting or DeviceOperation.ApplyLightingPreset or DeviceOperation.AdjustLighting && request.Targets!.Any(t =>
            lightingTransitionTargets.Contains(t.Target) || lightingGroups.Values.Any(g => g.Reconfiguring && g.Participants.Any(p => p.DeviceId == t.Target.DeviceId)));
    private LightingGroup[] LightingJoinSources(DeviceTargetVersion[] targets, IReadOnlyDictionary<EndpointReference, LightingProgram> programs)
    {
        if (targets.Any(t => !DeviceLighting.FrameProgram(programs[t.Target]))) return [];
        var devices = targets.Select(t => t.Target.DeviceId).ToHashSet();
        var groups = lightingGroups.Values.Where(g => g.Participants.Any(p => devices.Contains(p.DeviceId))).ToArray();
        if (groups.Length == 0 || groups.Length == 1 && targets.All(t => lightingOwners.GetValueOrDefault(t.Target) == groups[0].Id)) return [];
        var selected = targets.Select(t => t.Target).ToHashSet();
        var needsReplacement = targets.Any(t => !lightingOwners.ContainsKey(t.Target) &&
            groups.Any(g => g.Participants.Any(p => p.DeviceId == t.Target.DeviceId)) &&
            drivers[t.Target.DeviceId] is not IDeviceLightingDriver { LightingLimits.ConcurrentEndpointStreams: true } and not IDeviceLightingExpansionDriver);
        // A controller-wide restart is permitted only when every affected endpoint was selected.
        // Otherwise the join must preserve the unselected streams and reject unsupported expansion.
        if (needsReplacement && groups.SelectMany(g => g.Programs.Keys).All(selected.Contains)) return [];
        return groups.SelectMany(g => g.Programs).Select(p => programs.GetValueOrDefault(p.Key) ?? p.Value).Concat(programs.Values).Any(DeviceLighting.Animated) ? groups : [];
    }
    private string? CheckLightingJoin(DeviceServiceRequest request, IReadOnlyDictionary<EndpointReference, LightingProgram> programs, LightingGroup[] sources)
    {
        if (sources.Any(g => g.Owner != request.Session || g.Stopping || g.Reconfiguring || g.Frames == 0 || g.Fault is not null ||
                g.Participants.Any(p => !CurrentLighting(g, p) || !p.Limits.EndpointColors))) return "照明の構成変更前に実行中の状態を再取得してください";
        if (sources.SelectMany(g => g.Programs.Keys).Concat(request.Targets!.Select(t => t.Target)).Any(lightingSelectionsPending.ContainsKey))
            return "前の照明操作が完了するまでお待ちください";
        if (CheckBatch(FrameRequest(request, request.Targets!, programs), checkLightingStream: false) is { } invalid) return invalid;
        var selected = request.Targets!.ToDictionary(t => t.Target);
        var versions = sources.SelectMany(g => g.Participants).SelectMany(p => p.Targets).Concat(request.Targets!).DistinctBy(t => t.Target)
            .Select(t => selected.GetValueOrDefault(t.Target) ?? t).ToArray();
        if (versions.Length > 256) return "照明構成の端子数が上限を超えます";
        var all = sources.SelectMany(g => g.Programs).ToDictionary(p => p.Key, p => p.Value);
        foreach (var (target, program) in programs) all[target] = program;
        foreach (var target in request.Targets!)
        {
            var previous = sources.SingleOrDefault(g => g.Programs.ContainsKey(target.Target));
            if (previous is not null && drivers[target.Target.DeviceId] is not IDeviceLightingCompositionDriver &&
                !DeviceLighting.IntensityCompatible(previous.Programs[target.Target], programs[target.Target])) return "この機器の実行中の効果変更は未対応です";
            if (previous is null && sources.Any(g => g.Participants.Any(p => p.DeviceId == target.Target.DeviceId)) &&
                drivers[target.Target.DeviceId] is not IDeviceLightingDriver { LightingLimits.ConcurrentEndpointStreams: true } and not IDeviceLightingExpansionDriver)
                return "この機器への実行中の端子追加は未対応です";
        }
        foreach (var p in sources.SelectMany(g => g.Participants).Where(p => p.Driver is IDeviceLightingExpansionDriver))
        {
            var addition = request.Targets!.Where(t => t.Target.DeviceId == p.DeviceId && !p.Targets.Any(old => old.Target == t.Target)).ToArray();
            if (addition.Length == 0) continue;
            var expanded = p.Targets.Concat(addition).Select(t => new EndpointLightingProgram(t.Target.EndpointId, all[t.Target])).ToArray();
            if (((IDeviceLightingExpansionDriver)p.Driver).ValidateLightingExpansion(p.Stream!, expanded) is { } error) return error;
        }
        return CheckLightingComposition(request, versions, all);
    }
    // The gate is held and requested revisions have already advanced. Old streams are retained;
    // only independently openable new endpoint sets acquire new streams and recovery records.
    private Task<DeviceTargetResult[]>? QueueLightingJoin(DeviceServiceRequest request, DeviceTargetVersion[] targets,
        IReadOnlyDictionary<EndpointReference, LightingProgram> programs, IReadOnlyDictionary<EndpointReference, LightingProgram?> previousLighting,
        CancellationToken ct, Func<string?>? guard = null)
    {
        var sources = LightingJoinSources(targets, programs); if (sources.Length == 0) return null;
        var selected = targets.ToDictionary(t => t.Target);
        var reserved = sources.SelectMany(g => g.Programs.Keys).Concat(selected.Keys).Distinct().ToArray();
        var previous = sources.Select(g => g.FrameWork).ToArray();
        var expansions = sources.SelectMany(g => g.Participants).Where(p => p.Driver is IDeviceLightingExpansionDriver &&
            targets.Any(t => t.Target.DeviceId == p.DeviceId && !lightingOwners.ContainsKey(t.Target))).ToArray();
        var newcomers = targets.Where(t => !lightingOwners.ContainsKey(t.Target) && !expansions.Any(p => p.DeviceId == t.Target.DeviceId)).GroupBy(t => t.Target.DeviceId).Select(g => new LightingParticipant(g.Key, g.ToArray(),
            registry.Snapshot.Devices.Single(d => d.Device.Id == g.Key).Device.ResourceDomains, (IDeviceLightingDriver)drivers[g.Key],
            ((IDeviceLightingDriver)drivers[g.Key]).LightingLimits!, g.Where(t => states[t.Target].Owned).Select(t => t.Target.EndpointId).ToArray())).ToArray();
        foreach (var source in sources)
        {
            source.Reconfiguring = true; source.CompositionRevision++;
            foreach (var participant in source.Participants)
                for (var i = 0; i < participant.Targets.Length; i++)
                    if (selected.TryGetValue(participant.Targets[i].Target, out var changed)) participant.Targets[i] = changed;
        }
        foreach (var target in reserved) lightingTransitionTargets.Add(target);
        return Task.Run(async () =>
        {
            var transferred = false; LightingGroup? joined = null; var attempted = new HashSet<LightingParticipant>();
            try
            {
                await Task.WhenAll(previous).ConfigureAwait(false);
                lock (gate) Check();
                await Task.WhenAll(newcomers.Select(p => scheduler.RunPreparedAsync(p.Domains, HardwarePriority.Manual, p.Driver, DevicePreparationKind.LightingStart, async token =>
                {
                    lock (gate) { Check(); attempted.Add(p); }
                    p.Stream = p.Driver is IDeviceLightingCompositionDriver composition
                        ? await composition.OpenLightingCompositionAsync(p.Targets.Select(t => new EndpointLightingProgram(t.Target.EndpointId, programs[t.Target])).ToArray(), token).ConfigureAwait(false)
                        : await p.Driver.OpenLightingStreamAsync(p.Targets.Select(t => t.Target.EndpointId).ToArray(), programs[p.Targets[0].Target], token).ConfigureAwait(false);
                    if (p.Stream is null) throw new IOException("追加する照明の操作権を取得できません");
                    return true;
                }, ct))).ConfigureAwait(false);
                Task<DeviceTargetResult[]> first;
                lock (gate)
                {
                    Check();
                    var participants = sources.SelectMany(g => g.Participants).Select(p =>
                    {
                        if (!expansions.Contains(p)) return p;
                        var full = p.Targets.Concat(targets.Where(t => t.Target.DeviceId == p.DeviceId && !lightingOwners.ContainsKey(t.Target))).ToArray();
                        var all = full.Select(t => new EndpointLightingProgram(t.Target.EndpointId, programs.GetValueOrDefault(t.Target) ??
                            sources.Single(g => g.AppliedPrograms.ContainsKey(t.Target)).AppliedPrograms[t.Target])).ToArray();
                        return p with { Targets = full, Stream = ((IDeviceLightingExpansionDriver)p.Driver).CreateLightingExpansion(p.Stream!, all) };
                    }).Concat(newcomers).ToArray();
                    joined = new(request.Id, request.Session, request.Lighting!, participants, ct, guard) {
                        Origin = clock.GetTimestamp(), Sequence = sources.Max(g => g.Sequence) };
                    foreach (var source in sources)
                        foreach (var (target, program) in source.AppliedPrograms)
                        {
                            joined.Programs[target] = program; joined.Origins[target] = source.AppliedOrigins[target];
                            joined.AppliedPrograms[target] = program; joined.AppliedOrigins[target] = source.AppliedOrigins[target];
                        }
                    foreach (var (target, program) in programs) { joined.Programs[target] = Contract.Copy(program); joined.Origins[target] = joined.Origin; }
                    foreach (var source in sources)
                    {
                        lightingGroups.Remove(source.Id); source.Stopping = true;
                        source.Ending = Task.FromResult(new Dictionary<EndpointReference, DeviceDriverResult>());
                        // Do not cancel the old open token when handing its stream to the same session.
                        source.Stop.Dispose();
                    }
                    lightingGroups.Add(joined.Id, joined);
                    foreach (var participant in participants)
                        foreach (var target in participant.Targets)
                        {
                            lightingOwners[target.Target] = joined.Id;
                            states[target.Target] = states[target.Target] with { Owned = true, Lighting = LightingStatus(joined) };
                        }
                    transferred = true; joined.FrameWork = first = Task.Run(() => WriteLightingRoundAsync(joined));
                }
                var results = await first.ConfigureAwait(false);
                var failed = results.Any(r => r.Outcome is not (Outcome.Confirmed or Outcome.WrittenUnverified));
                if (failed) await StopLightingGroupAsync(joined, DeviceLightingStopReason.Failure).ConfigureAwait(false);
                return targets.Select(t =>
                {
                    var result = results.Single(r => r.Target == t.Target);
                    return failed && result.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified ? result with {
                        Outcome = Outcome.Unknown, Message = "照明構成の一部で失敗したため復元しました。対象ごとの状態を確認してください" } : result;
                }).ToArray();
            }
            catch (Exception e)
            {
                if (!transferred) await CleanupNewLightingParticipantsAsync(attempted.ToArray()).ConfigureAwait(false);
                else await StopLightingGroupAsync(joined!, DeviceLightingStopReason.Failure).ConfigureAwait(false);
                lock (gate)
                    foreach (var target in targets)
                        if (request.Session == session && states.TryGetValue(target.Target, out var state) && state.Generation == target.Generation && state.PolicyRevision == target.ExpectedPolicyRevision)
                        {
                            var original = sources.SingleOrDefault(g => g.AppliedPrograms.ContainsKey(target.Target));
                            states[target.Target] = state with { RequestedLighting = original?.AppliedPrograms[target.Target] ?? previousLighting.GetValueOrDefault(target.Target),
                                Outcome = Outcome.Rejected, Message = "照明構成を変更できませんでした: " + e.Message };
                        }
                return targets.Select(t => new DeviceTargetResult(t.Target, t.Generation, t.ExpectedPolicyRevision, Outcome.Rejected,
                    "照明構成を変更できませんでした: " + e.Message)).ToArray();
            }
            finally
            {
                lock (gate)
                {
                    foreach (var target in reserved) lightingTransitionTargets.Remove(target);
                    foreach (var source in sources) source.Reconfiguring = false;
                }
            }
            void Check()
            {
                RefreshCore();
                if (ct.IsCancellationRequested || stopping || releasing || configurationChanging || session != request.Session || guard?.Invoke() is not null ||
                    request.TopologyRevision != registry.Snapshot.TopologyRevision || sources.Any(g => !lightingGroups.TryGetValue(g.Id, out var current) || current != g ||
                        g.Stopping || g.Fault is not null || g.Participants.Any(p => !CurrentLighting(g, p))) ||
                    targets.Any(t => !states.TryGetValue(t.Target, out var s) || s.Generation != t.Generation || s.PolicyRevision != t.ExpectedPolicyRevision ||
                        CheckTarget(FrameRequest(request, [t], programs), t) is not null)) throw new IOException("照明の対象・所有・設定が変更されました");
            }
        });
    }
    private async Task CleanupNewLightingParticipantsAsync(LightingParticipant[] participants)
    {
        foreach (var p in participants)
        {
            DeviceDriverReleaseResult[] results;
            try
            {
                results = await scheduler.RunPreparedAsync(p.Domains, HardwarePriority.Recovery, p.Driver, DevicePreparationKind.Release, async token =>
                {
                    if (p.Stream is not null) return LightingResults(p, await p.Stream.EndAsync(DeviceLightingStopReason.Failure, token).ConfigureAwait(false));
                    var rows = new List<DeviceDriverReleaseResult>();
                    foreach (var t in p.Targets) rows.Add(new(t.Target.EndpointId, await p.Driver.ApplyAsync(new(t.Target.EndpointId, DeviceOperation.StopLighting), token).ConfigureAwait(false)));
                    return LightingResults(p, rows.ToArray());
                }).ConfigureAwait(false);
            }
            catch (Exception e) { results = p.Targets.Select(t => new DeviceDriverReleaseResult(t.Target.EndpointId, new(Outcome.Unknown, "追加端子の終了を確認できません: " + e.Message))).ToArray(); }
            lock (gate)
                foreach (var target in p.Targets)
                {
                    var result = results.Single(r => r.EndpointId == target.Target.EndpointId).Result; var ok = result.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified;
                    if (ok) lightingRecoveryRequired.Remove(target.Target); else lightingRecoveryRequired.Add(target.Target);
                    if (states.TryGetValue(target.Target, out var state) && state.Generation == target.Generation && state.PolicyRevision == target.ExpectedPolicyRevision)
                        states[target.Target] = state with { Owned = !ok, Outcome = result.Outcome, ReportedLighting = result.ReportedLighting, Message = result.Message };
                }
        }
    }
}
