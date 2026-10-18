namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private sealed record LightingParticipant(string DeviceId, DeviceTargetVersion[] Targets, string[] Domains,
        IDeviceLightingDriver Driver, DeviceLightingLimits Limits, string[] PreviouslyOwned)
    { public IDeviceLightingStream? Stream; }
    private sealed class LightingGroup(string id, string owner, LightingProgram program, LightingParticipant[] participants, CancellationToken token, Func<string?>? startGuard = null)
    {
        public readonly string Id = id, Owner = owner;
        public readonly LightingProgram Program = Contract.Copy(program);
        public readonly Dictionary<EndpointReference, LightingProgram> Programs = participants.SelectMany(p => p.Targets)
            .ToDictionary(t => t.Target, _ => Contract.Copy(program));
        public readonly Dictionary<EndpointReference, long> Origins = participants.SelectMany(p => p.Targets).ToDictionary(t => t.Target, _ => 0L);
        public readonly Dictionary<EndpointReference, LightingProgram> AppliedPrograms = [];
        public readonly Dictionary<EndpointReference, long> AppliedOrigins = [];
        public LightingParticipant[] Participants = participants;
        public readonly Func<string?>? StartGuard = startGuard;
        public readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        public Task<DeviceTargetResult[]> FrameWork = Task.FromResult(Array.Empty<DeviceTargetResult>());
        public Task Monitor = Task.CompletedTask;
        public Task<Dictionary<EndpointReference, DeviceDriverResult>>? Ending;
        public long Origin, SampledAt, Frames, Dropped, Sequence, CompositionRevision;
        public bool Stopping, Reconfiguring;
        public RgbColor? Last;
        public double MaximumWriteMilliseconds;
        public string? Fault;
    }
    private readonly Dictionary<string, LightingGroup> lightingGroups = [];
    private readonly Dictionary<EndpointReference, string> lightingOwners = [];
    private readonly HashSet<EndpointReference> lightingRecoveryRequired = [];
    private Task lightingWatchdog = Task.CompletedTask;
    private string? CheckLightingBatch(DeviceServiceRequest request)
    {
        if (request.Operation != DeviceOperation.SetLighting || !DeviceLighting.Animated(request.Lighting)) return null;
        if (!DeviceLighting.ValidAnimation(request.Lighting)) return "共通同期は単色・速度0〜4に対応しています";
        foreach (var group in request.Targets!.GroupBy(t => t.Target.DeviceId))
        {
            if (drivers[group.Key] is not IDeviceLightingDriver driver || !DeviceLighting.ValidLimits(driver.LightingLimits))
                return "この機器は期限付きの連続照明フレームに対応していません";
            if (driver.ValidateLightingStream(group.Select(t => t.Target.EndpointId).ToArray(), request.Lighting!) is { } error) return error;
        }
        return null;
    }
    // Recovery can make new frames temporarily unavailable. Ending targets must retain release
    // capability and the same identity/ownership; every continuing target still requires frames.
    private bool CurrentLighting(LightingGroup group, LightingParticipant participant, IReadOnlySet<EndpointReference>? ending = null) =>
        !group.Stopping && !stopping && !releasing && !ConfigurationInvalidatesLighting(participant) && session == group.Owner && (group.Frames > 0 || group.StartGuard?.Invoke() is null) &&
        participant.Targets.All(t => lightingOwners.GetValueOrDefault(t.Target) == group.Id && states.TryGetValue(t.Target, out var s) &&
            s.Generation == t.Generation && s.PolicyRevision == t.ExpectedPolicyRevision) &&
        participant.Targets.All(t => PhysicalBindingError(t.Target,
            ending?.Contains(t.Target) == true ? DeviceFeature.RestoreLighting : DeviceFeature.ColorFrames) is null &&
            registry.Check(new(Epoch, t.Target.DeviceId, t.Target.EndpointId, t.Generation,
                ending?.Contains(t.Target) == true ? DeviceFeature.RestoreLighting : DeviceFeature.ColorFrames)) is null);
    private DeviceLightingStatus LightingStatus(LightingGroup group) => new(group.Id, !group.Stopping, group.Frames, group.Dropped, group.Last,
        group.MaximumWriteMilliseconds, group.Fault);
    private void PublishLightingCore(LightingGroup group, DeviceTargetResult[] results, IReadOnlySet<EndpointReference>? skipped = null)
    {
        foreach (var result in results)
            if (lightingOwners.GetValueOrDefault(result.Target) == group.Id && states.TryGetValue(result.Target, out var old) &&
                old.Generation == result.Generation && old.PolicyRevision == result.PolicyRevision)
                // An unsent frame does not replace the last applied output. Keep
                // its outcome until a later frame is actually applied.
                states[result.Target] = old with { Outcome = skipped?.Contains(result.Target) == true && group.AppliedPrograms.ContainsKey(result.Target)
                        ? old.Outcome : result.Outcome, Message = result.Message,
                    ReportedLighting = result.ReportedLighting ?? old.ReportedLighting,
                    Lighting = LightingStatus(group) with { LastColor = result.ReportedLighting?.Colors.FirstOrDefault() ?? old.Lighting?.LastColor }, Owned = true };
    }
    private async Task<DeviceTargetResult[]> StartLightingGroupAsync(DeviceServiceRequest request, DeviceTargetVersion[] targets, CancellationToken ct,
        Func<string?>? precondition = null, IReadOnlyDictionary<EndpointReference, LightingProgram>? programs = null)
    {
        LightingGroup group; Task<DeviceTargetResult[]> started;
        lock (gate)
        {
            RefreshCore();
            if (configurationChanging || stopping || releasing || session != request.Session || precondition?.Invoke() is not null || request.TopologyRevision != registry.Snapshot.TopologyRevision ||
                targets.Any(t => !states.TryGetValue(t.Target, out var s) || s.PolicyRevision != t.ExpectedPolicyRevision ||
                    CheckTarget(programs is null ? request : FrameRequest(request, [t], programs), t) is not null) ||
                (programs is null ? CheckLightingBatch(request) : CheckLightingComposition(request, targets, programs)) is not null)
                return targets.Select(t => new DeviceTargetResult(t.Target, t.Generation, t.ExpectedPolicyRevision, Outcome.Rejected, "同期開始前に対象・設定が変更されました")).ToArray();
            var participants = targets.GroupBy(t => t.Target.DeviceId).Select(g => new LightingParticipant(g.Key, g.ToArray(),
                registry.Snapshot.Devices.Single(d => d.Device.Id == g.Key).Device.ResourceDomains,
                (IDeviceLightingDriver)drivers[g.Key], ((IDeviceLightingDriver)drivers[g.Key]).LightingLimits!,
                g.Where(t => states[t.Target].Owned).Select(t => t.Target.EndpointId).ToArray())).ToArray();
            group = new(request.Id, request.Session, request.Lighting!, participants, ct, precondition) { Origin = clock.GetTimestamp() };
            if (programs is not null) foreach (var target in targets) group.Programs[target.Target] = Contract.Copy(programs[target.Target]);
            lightingGroups.Add(group.Id, group);
            foreach (var t in targets) { lightingOwners[t.Target] = group.Id; states[t.Target] = states[t.Target] with { Owned = true, Lighting = LightingStatus(group) }; }
            group.FrameWork = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(participants.Select(p => scheduler.RunPreparedAsync(p.Domains, HardwarePriority.Manual, p.Driver, DevicePreparationKind.LightingStart, async token =>
                    {
                        lock (gate) { RefreshCore(); if (!CurrentLighting(group, p)) throw new IOException("同期対象が変更されました"); }
                        p.Stream = programs is not null && p.Driver is IDeviceLightingCompositionDriver composition
                            ? await composition.OpenLightingCompositionAsync(p.Targets.Select(t => new EndpointLightingProgram(t.Target.EndpointId, group.Programs[t.Target])).ToArray(), token).ConfigureAwait(false)
                            : await p.Driver.OpenLightingStreamAsync(p.Targets.Select(t => t.Target.EndpointId).ToArray(), group.Programs[p.Targets[0].Target], token).ConfigureAwait(false);
                        if (p.Stream is null) throw new IOException("照明の操作権を取得できません");
                        return true;
                    }, group.Stop.Token))).ConfigureAwait(false);
                    lock (gate)
                    {
                        group.Origin = clock.GetTimestamp();
                        foreach (var target in group.Programs.Keys) group.Origins[target] = group.Origin;
                    }
                    return await WriteLightingRoundAsync(group).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    lock (gate) if (!group.Stopping) group.Fault = e.Message;
                    return targets.Select(t => new DeviceTargetResult(t.Target, t.Generation, t.ExpectedPolicyRevision, Outcome.Unknown, "同期を開始できません: " + e.Message)).ToArray();
                }
            });
            started = group.FrameWork;
        }
        var results = await started.ConfigureAwait(false);
        if (results.Any(r => r.Outcome is not (Outcome.Confirmed or Outcome.WrittenUnverified)))
            await StopLightingGroupAsync(group, DeviceLightingStopReason.Failure).ConfigureAwait(false);
        return results;
    }
    private DeviceDriverReleaseResult[] LightingResults(LightingParticipant participant, DeviceDriverReleaseResult[]? results)
    {
        if (results is null || results.Length != participant.Targets.Length || results.Any(r => r is null || !ValidDriverResult(r.Result)) ||
            !results.Select(r => r.EndpointId).Order().SequenceEqual(participant.Targets.Select(t => t.Target.EndpointId).Order()))
            return participant.Targets.Select(t => new DeviceDriverReleaseResult(t.Target.EndpointId, new(Outcome.Unknown, "照明の端子別結果が欠落または不正です"))).ToArray();
        return results;
    }
    private async Task<DeviceTargetResult[]> WriteLightingRoundAsync(LightingGroup group)
    {
        long sampled, frameNumber, composition;
        LightingParticipant[] participants; Dictionary<EndpointReference, LightingProgram> programs; Dictionary<EndpointReference, long> origins;
        lock (gate)
        {
            RefreshCore(); sampled = group.SampledAt = clock.GetTimestamp(); frameNumber = ++group.Sequence;
            composition = group.CompositionRevision;
            participants = group.Participants.Select(p => p with { Targets = p.Targets.ToArray() }).ToArray();
            programs = group.Programs.ToDictionary(p => p.Key, p => p.Value);
            origins = group.Origins.ToDictionary(p => p.Key, p => p.Value);
        }
        RgbColor Color(EndpointReference target) => DeviceLighting.Frame(programs[target], clock.GetElapsedTime(origins[target], sampled).TotalSeconds);
        var expired = 0; var failed = 0;
        var skipped = new System.Collections.Concurrent.ConcurrentDictionary<EndpointReference, byte>();
        var values = await Task.WhenAll(participants.Select(async participant =>
        {
            DeviceDriverReleaseResult[] results;
            var color = Color(participant.Targets[0].Target);
            var frame = new DeviceLightingFrame(frameNumber, color, () =>
            {
                lock (gate)
                {
                    RefreshCore();
                    if (composition != group.CompositionRevision) return new("照明の明るさが変更されたため以前のフレームを破棄しました", Expired: true);
                    if (!CurrentLighting(group, participant) || group.Stop.IsCancellationRequested) return new("照明の操作権・対象が変更されました");
                    var remaining = TimeSpan.FromMilliseconds(participant.Limits.MaximumFrameAgeMilliseconds) - clock.GetElapsedTime(sampled);
                    return remaining <= TimeSpan.Zero ? new("照明フレームの有効期限を超えました", Expired: true) : new(ValidFor: remaining);
                }
            }, participant.Limits.EndpointColors ? participant.Targets.Select(t => new EndpointLightingFrame(t.Target.EndpointId,
                Color(t.Target), DeviceLighting.StopColor(programs[t.Target]))).ToArray() : null);
            try
            {
                Task<DeviceDriverReleaseResult[]> Transmit(CancellationToken ct) => scheduler.RunPreparedAsync(participant.Domains, HardwarePriority.LightingFrame, participant.Driver, DevicePreparationKind.LightingFrame, async token =>
                {
                    var authorization = frame.Authorize();
                    if (authorization.Error is { } error) return participant.Targets.Select(t => new DeviceDriverReleaseResult(t.Target.EndpointId,
                        new(Outcome.Rejected, error, Failure: authorization.Expired ? DeviceDriverFailure.LightingFrameExpired : DeviceDriverFailure.None))).ToArray();
                    var began = clock.GetTimestamp();
                    try { return LightingResults(participant, await participant.Stream!.WriteAsync(frame, token).ConfigureAwait(false)); }
                    finally { lock (gate) group.MaximumWriteMilliseconds = Math.Max(group.MaximumWriteMilliseconds, clock.GetElapsedTime(began).TotalMilliseconds); }
                }, ct);
                results = LightingResults(participant, participant.Stream is IPreparedDeviceLightingStream prepared
                    ? await prepared.PrepareFrameAsync(frame, Transmit, group.Stop.Token).ConfigureAwait(false)
                    : await Transmit(group.Stop.Token).ConfigureAwait(false));
            }
            catch (Exception e) { results = participant.Targets.Select(t => new DeviceDriverReleaseResult(t.Target.EndpointId, new(Outcome.Unknown, "照明結果は未確認です: " + e.Message))).ToArray(); }
            if (results.Any(r => r.Result.Failure == DeviceDriverFailure.LightingFrameExpired)) Interlocked.Exchange(ref expired, 1);
            foreach (var r in results.Where(r => r.Result.Failure == DeviceDriverFailure.LightingFrameExpired))
                skipped.TryAdd(new(participant.DeviceId, r.EndpointId), 0);
            if (results.Any(r => r.Result.Outcome is not (Outcome.Confirmed or Outcome.WrittenUnverified) && r.Result.Failure != DeviceDriverFailure.LightingFrameExpired))
                Interlocked.Exchange(ref failed, 1);
            return participant.Targets.Select(t => { var r = results.Single(r => r.EndpointId == t.Target.EndpointId).Result;
                return new DeviceTargetResult(t.Target, t.Generation, t.ExpectedPolicyRevision, r.Outcome, r.Message, ReportedLighting: r.ReportedLighting); }).ToArray();
        })).ConfigureAwait(false);
        var all = values.SelectMany(v => v).ToArray();
        lock (gate)
        {
            if (expired != 0) group.Dropped++;
            // Expired frames are skipped, but a simultaneous real failure must still stop the group.
            if (failed != 0 && !group.Stopping) group.Fault ??= "同期対象の一部で送信に失敗しました";
            if (failed == 0 && expired == 0)
            {
                group.Frames++; group.Last = Color(participants[0].Targets[0].Target);
                foreach (var (target, program) in programs) { group.AppliedPrograms[target] = program; group.AppliedOrigins[target] = origins[target]; }
            }
            if (composition == group.CompositionRevision) PublishLightingCore(group, all, skipped.Keys.ToHashSet());
        }
        return all;
    }
    private Task<Dictionary<EndpointReference, DeviceDriverResult>> StopLightingGroupAsync(LightingGroup group, DeviceLightingStopReason reason)
    {
        lock (gate)
        {
            if (group.Ending is not null) return group.Ending;
            group.Stopping = true; group.Stop.Cancel(); var frameWork = group.FrameWork;
            group.Ending = Task.Run(async () =>
            {
                try { await frameWork.ConfigureAwait(false); } catch { }
                var restored = await Task.WhenAll(group.Participants.Select(async p =>
                {
                    DeviceDriverReleaseResult[] result;
                    try
                    {
                        result = p.Stream is null && p.PreviouslyOwned.Length == 0 ? p.Targets.Select(t => new DeviceDriverReleaseResult(t.Target.EndpointId, new(Outcome.Confirmed, "照明の出力は開始していません"))).ToArray() :
                            await scheduler.RunPreparedAsync(p.Domains, HardwarePriority.Recovery, p.Driver, DevicePreparationKind.Release, async token => LightingResults(p,
                                p.Stream is null ? await ReleaseUnopenedLightingAsync(p).ConfigureAwait(false) :
                                await p.Stream.EndAsync(reason, token).ConfigureAwait(false))).ConfigureAwait(false);
                    }
                    catch (Exception e) { result = p.Targets.Select(t => new DeviceDriverReleaseResult(t.Target.EndpointId, new(Outcome.Unknown, "照明の復元を確認できません: " + e.Message))).ToArray(); }
                    return result.Select(r => new KeyValuePair<EndpointReference, DeviceDriverResult>(new(p.DeviceId, r.EndpointId), r.Result)).ToArray();
                })).ConfigureAwait(false);
                var results = restored.SelectMany(r => r).ToDictionary();
                lock (gate)
                {
                    foreach (var p in group.Participants)
                    foreach (var t in p.Targets)
                    {
                        if (lightingOwners.GetValueOrDefault(t.Target) != group.Id) continue;
                        lightingOwners.Remove(t.Target);
                        var r = results[t.Target]; var ok = r.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified;
                        if (ok) lightingRecoveryRequired.Remove(t.Target); else lightingRecoveryRequired.Add(t.Target);
                        if (states.TryGetValue(t.Target, out var old)) states[t.Target] = old with { Owned = !ok, Outcome = r.Outcome,
                            PolicyRevision = old.PolicyRevision == t.ExpectedPolicyRevision ? checked(old.PolicyRevision + 1) : old.PolicyRevision,
                            ReportedLighting = old.Generation == t.Generation ? r.ReportedLighting : null, Message = r.Message,
                            Lighting = LightingStatus(group) with { LastColor = r.ReportedLighting?.Colors.FirstOrDefault() ?? old.Lighting?.LastColor } };
                    }
                    lightingGroups.Remove(group.Id);
                }
                group.Stop.Dispose(); return results;
            });
            return group.Ending;
        }
    }
    private static async Task<DeviceDriverReleaseResult[]> ReleaseUnopenedLightingAsync(LightingParticipant participant)
    {
        var results = new List<DeviceDriverReleaseResult>();
        foreach (var target in participant.Targets)
        {
            DeviceDriverResult result;
            if (!participant.PreviouslyOwned.Contains(target.Target.EndpointId)) result = new(Outcome.Confirmed, "照明の出力は開始していません");
            else
            {
                try { result = await participant.Driver.ApplyAsync(new(target.Target.EndpointId, DeviceOperation.StopLighting), CancellationToken.None).ConfigureAwait(false); }
                catch (Exception e) { result = new(Outcome.Unknown, "同期開始前の照明を終了できません: " + e.Message); }
            }
            results.Add(new(target.Target.EndpointId, result));
        }
        return results.ToArray();
    }
    private async Task<Dictionary<EndpointReference, DeviceDriverResult>> StopOverlappingLightingAsync(DeviceServiceRequest request, DeviceTargetVersion[] versions)
    {
        if (request.Operation is not (DeviceOperation.SetLighting or DeviceOperation.StopLighting)) return [];
        LightingGroup[] affected;
        lock (gate)
        {
            if (request.Session != session || stopping || releasing || versions.Any(t => !states.TryGetValue(t.Target, out var s) || s.Generation != t.Generation || s.PolicyRevision != t.ExpectedPolicyRevision))
                throw new IOException("以前の照明要求は新しい操作を停止できません");
            affected = versions.Select(t => lightingOwners.GetValueOrDefault(t.Target)).Where(id => id is not null).Distinct()
                .Select(id => lightingGroups[id!]).ToArray();
        }
        var results = await Task.WhenAll(affected.Select(g => StopLightingGroupAsync(g, DeviceLightingStopReason.Normal))).ConfigureAwait(false);
        return results.SelectMany(r => r).ToDictionary();
    }
    private async Task StopAllLightingAsync(DeviceReleaseReason reason, IDeviceControlDriver[]? selected = null)
    {
        LightingGroup[] groups;
        lock (gate) groups = lightingGroups.Values.Where(g => selected is null ||
            g.Participants.Any(p => selected.Any(d => d.DeviceId == p.DeviceId))).ToArray();
        var stopReason = reason switch { DeviceReleaseReason.NormalExit => DeviceLightingStopReason.SessionEnded,
            DeviceReleaseReason.ConfigurationChanged => DeviceLightingStopReason.ConfigurationChanged,
            DeviceReleaseReason.Suspended => DeviceLightingStopReason.Suspended,
            DeviceReleaseReason.ServiceStopping => DeviceLightingStopReason.ServiceStopping, _ => DeviceLightingStopReason.OwnerLost };
        await Task.WhenAll(groups.Select(g => StopLightingGroupAsync(g, stopReason))).ConfigureAwait(false);
    }
    public Task TickLightingAsync()
    {
        lock (gate)
        {
            if (lightingGroups.Count == 0) return Task.CompletedTask;
            ExpireCore(); RefreshCore();
            if (session is null || stopping || releasing || configurationChanging && providerIsolation is null && endpointSetupScope is null) return Task.CompletedTask;
            foreach (var group in lightingGroups.Values.ToArray())
            {
                if (group.Participants.Any(p => ConfigurationBlocksLighting(p.DeviceId))) continue;
                if (group.Stopping || group.Reconfiguring || !group.FrameWork.IsCompleted || !group.Monitor.IsCompleted) continue;
                if (group.Fault is not null) { group.Monitor = StopLightingGroupAsync(group, DeviceLightingStopReason.Failure); continue; }
                if (group.Participants.Any(p => !CurrentLighting(group, p)))
                { group.Fault ??= "同期対象の接続・構成が変更されました"; group.Monitor = StopLightingGroupAsync(group, DeviceLightingStopReason.DeviceChanged); continue; }
                var interval = group.Participants.Max(p => p.Limits.MinimumIntervalMilliseconds);
                if (clock.GetElapsedTime(group.SampledAt).TotalMilliseconds < interval) continue;
                group.FrameWork = Task.Run(() => WriteLightingRoundAsync(group));
                group.Monitor = ObserveLightingRoundAsync(group, group.FrameWork);
            }
            return Task.WhenAll(lightingGroups.Values.Select(g => g.Monitor));
        }
    }
    private async Task ObserveLightingRoundAsync(LightingGroup group, Task<DeviceTargetResult[]> round)
    {
        try { await round.ConfigureAwait(false); }
        catch (Exception e) { lock (gate) group.Fault = e.Message; }
        if (group.Fault is not null) await StopLightingGroupAsync(group, DeviceLightingStopReason.Failure).ConfigureAwait(false);
    }
}
