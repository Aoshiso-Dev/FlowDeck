namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private readonly HashSet<Task> lightingLibraryWork = [];
    private async Task ExecuteLightingLibraryAsync(DeviceServiceRequest request, TaskCompletionSource<DeviceServiceReply> completion, CancellationToken ct)
    {
        try
        {
            var result = await lightingLibrary!.SaveLightingLibraryAsync(request.LightingLibrary!, ct).ConfigureAwait(false);
            if (!DeviceLightingLibrary.Valid(result)) throw new IOException("照明の保存結果が不正です");
            var success = result.Outcome == Outcome.Confirmed;
            var operation = new DeviceOperationResult(request.Id, request.Source, result.Outcome, clock.GetUtcNow(), [], result.Message);
            log.Write("lightingLibrary", operation);
            completion.TrySetResult(new(2, Epoch, request.Session, request.Id, success, success ? null : result.Message, operation, LightingLibrary: result));
        }
        catch (Exception e) { completion.TrySetResult(Reject(request, e.Message)); }
        finally { lock (gate) { pendingCount--; TrimRequests(); } }
    }
    private LightingPresetPlan ResolveLightingPlan(DeviceServiceRequest request)
    {
        if (lightingLibrary is null) throw new IOException("照明グループ・シーンの保存窓口がありません");
        var plan = lightingLibrary.ResolveLightingPreset(request.LightingPreset!);
        if (plan is null || plan.LibraryRevision < 0 || plan.Parts is not { Length: > 0 and <= 256 } ||
            !DeviceLightingLibrary.ValidParts(plan.Parts)) throw new IOException("照明の呼出し内容が不正です");
        var expected = plan.Parts.SelectMany(p => p.Targets).ToHashSet();
        if (!expected.SetEquals(request.Targets!.Select(t => t.Target))) throw new IOException("表示した対象と保存したグループ・シーンが一致しません");
        return Contract.Copy(plan);
    }
    private static DeviceServiceRequest PresetPart(DeviceServiceRequest original, SynchronizedLighting part, DeviceTargetVersion[] targets, string? id = null) =>
        original with { Operation = DeviceOperation.SetLighting, Lighting = part.Program, LightingPreset = null, Targets = targets, Id = id ?? original.Id };
    // Existing physical streams can connect multiple scene parts. Keep that connected component
    // together, without combining independent scene components into one failure domain.
    private EndpointReference[][] LightingPlanComponents(LightingPresetPlan plan)
    {
        var selected = plan.Parts.SelectMany(p => p.Targets).ToHashSet();
        var devices = selected.Select(t => t.DeviceId).ToHashSet();
        var live = lightingGroups.Values.Where(g => g.Participants.Any(p => devices.Contains(p.DeviceId)))
            .Select(g => g.Programs.Keys.ToArray());
        return DeviceLightingComposition.Components(plan.Parts.Select(p => p.Targets).Concat(live))
            .Select(g => g.Where(selected.Contains).ToArray()).Where(g => g.Length > 0).ToArray();
    }
    private Task<DeviceTargetResult[]> QueueLightingPresetUpdate(DeviceServiceRequest request, DeviceTargetVersion[] targets, LightingPresetPlan plan,
        IReadOnlyDictionary<EndpointReference, LightingProgram?> previousLighting)
    {
        var all = plan.Parts.SelectMany(p => p.Targets.Select(t => (Target: t, p.Program))).ToDictionary(p => p.Target, p => p.Program);
        var components = LightingPlanComponents(plan); var pending = new List<Task<DeviceTargetResult[]>>();
        var ct = sessionLifetime!.Token;
        string? Current() => lightingLibrary!.CheckLightingPreset(plan);
        foreach (var component in components)
        {
            var selected = targets.Where(t => component.Contains(t.Target)).ToArray();
            var programs = component.ToDictionary(t => t, t => all[t]);
            var command = FrameRequest(request, selected, programs) with { Id = Guid.NewGuid().ToString("N") };
            pending.Add(QueueLightingIntensityUpdateCore(command, selected, programs, Current) ??
                QueueLightingJoin(command, selected, programs, previousLighting, ct, Current) ??
                Task.Run(() => ExecuteLightingColdComponentAsync(command, selected, programs, Current, ct)));
        }
        return Complete();
        async Task<DeviceTargetResult[]> Complete() => (await Task.WhenAll(pending).ConfigureAwait(false)).SelectMany(r => r).ToArray();
    }
    private string? CheckLightingPlan(DeviceServiceRequest request, LightingPresetPlan plan)
    {
        if (lightingLibrary!.CheckLightingPreset(plan) is { } invalid) return invalid;
        var programs = plan.Parts.SelectMany(p => p.Targets.Select(t => (Target: t, p.Program))).ToDictionary(p => p.Target, p => p.Program);
        foreach (var component in LightingPlanComponents(plan))
        {
            var targets = request.Targets!.Where(t => component.Contains(t.Target)).ToArray();
            if (CheckLightingPlanComponent(request with { Targets = targets }, component.ToDictionary(t => t, t => programs[t])) is { } error) return error;
        }
        return null;
    }
    private string? CheckLightingPlanComponent(DeviceServiceRequest request, IReadOnlyDictionary<EndpointReference, LightingProgram> programs)
    {
        if (LightingUpdateGroup(request, request.Targets!, programs) is { } active) return CheckLightingUpdate(request, active, programs);
        var sources = LightingJoinSources(request.Targets!, programs);
        if (sources.Length > 0) return CheckLightingJoin(request, programs, sources);
        return CheckLightingColdComponent(request, programs);
    }
    private string? CheckLightingColdComponent(DeviceServiceRequest request, IReadOnlyDictionary<EndpointReference, LightingProgram> programs)
    {
        var selected = request.Targets!.Select(t => t.Target).ToHashSet();
        if (lightingGroups.Values.Any(g => g.Programs.Keys.Any(selected.Contains) &&
            g.Programs.Any(p => !selected.Contains(p.Key) && DeviceLighting.Animated(p.Value))))
            return "対象外の同期を保持できません。同期を終了してから適用してください";
        if (programs.Values.Any(DeviceLighting.Animated))
            return CheckBatch(FrameRequest(request, request.Targets!, programs), checkLightingStream: false) ?? CheckLightingComposition(request, request.Targets!, programs);
        return CheckBatch(request with { Operation = DeviceOperation.SetLighting }, checkLightingStream: false, staticPrograms: programs);
    }
    private async Task<DeviceTargetResult[]> ExecuteLightingColdComponentAsync(DeviceServiceRequest request, DeviceTargetVersion[] targets,
        IReadOnlyDictionary<EndpointReference, LightingProgram> programs, Func<string?> current, CancellationToken ct)
    {
        DeviceTargetResult[] Rejected(string message)
        {
            lock (gate)
                foreach (var t in targets)
                    if (request.Session == session && states.TryGetValue(t.Target, out var current) && current.Generation == t.Generation && current.PolicyRevision == t.ExpectedPolicyRevision)
                        states[t.Target] = current with { Outcome = Outcome.Rejected, Message = message };
            return targets.Select(t => new DeviceTargetResult(t.Target, t.Generation, t.ExpectedPolicyRevision, Outcome.Rejected, message)).ToArray();
        }
        lock (gate)
        {
            RefreshCore();
            if (request.Session != session || ct.IsCancellationRequested || current() is not null || CheckLightingColdComponent(request, programs) is not null)
                return Rejected("適用前に照明の対象・設定が変更されました");
        }
        // This path has no retained streams; only this component may stop before replacement.
        var stopped = await StopOverlappingLightingAsync(request with { Operation = DeviceOperation.SetLighting }, targets).ConfigureAwait(false);
        if (stopped.Values.Any(r => r.Outcome is not (Outcome.Confirmed or Outcome.WrittenUnverified))) return Rejected("以前の照明の停止を確認できません");
        return programs.Values.Any(DeviceLighting.Animated)
            ? await StartLightingGroupAsync(request, targets, ct, current, programs).ConfigureAwait(false)
            : await ExecuteStaticLightingAsync(request, targets, programs, ct, current).ConfigureAwait(false);
    }
}
