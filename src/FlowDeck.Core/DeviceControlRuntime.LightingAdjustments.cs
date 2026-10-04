namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private readonly Dictionary<EndpointReference, string> lightingSelectionsPending = [];
    private static DeviceServiceRequest AdjustmentPart(DeviceServiceRequest r, DeviceTargetVersion[] targets, LightingProgram program) =>
        r with { Operation = DeviceOperation.SetLighting, Targets = targets, Lighting = program, LightingAdjustment = null };
    private static DeviceServiceRequest FrameRequest(DeviceServiceRequest r, DeviceTargetVersion[] targets,
        IReadOnlyDictionary<EndpointReference, LightingProgram> programs) => r with {
            Operation = DeviceOperation.SetLighting, Targets = targets, LightingAdjustment = null, LightingPreset = null,
            Lighting = DeviceLightingComposition.TransportProgram(targets.Select(t => programs[t.Target])) };
    private LightingAdjustmentPlan ResolveLightingAdjustment(DeviceServiceRequest request)
    {
        if (lightingAdjustments is null || lightingPersistence is null) throw new IOException("明るさ設定の保存窓口がありません");
        var plan = lightingAdjustments.ResolveLightingAdjustment(request.LightingAdjustment!, request.Targets!.Select(t => t.Target).ToArray());
        if (!DeviceLightingAdjustments.ValidPlan(plan) || !plan.Entries.Select(e => e.Target).ToHashSet().SetEquals(request.Targets!.Select(t => t.Target)))
            throw new IOException("明るさの適用内容が不正です");
        return Contract.Copy(plan);
    }
    private EndpointReference[][] ColdAdjustmentGroups(LightingAdjustmentPlan plan)
    {
        var programs = plan.Entries.ToDictionary(e => e.Target, e => DeviceLightingAdjustments.Effective(e.Next));
        var groups = plan.Synchronization.Concat(plan.Entries.Where(e => !DeviceLighting.Animated(programs[e.Target])).Select(e => new[] { e.Target }));
        return DeviceLightingComposition.Components(groups.Select(g => g.Where(t => !lightingOwners.ContainsKey(t)).ToArray()))
            .Where(g => g.Any(t => DeviceLighting.Animated(programs[t]))).ToArray();
    }
    private string? CheckLightingComposition(DeviceServiceRequest request, DeviceTargetVersion[] targets,
        IReadOnlyDictionary<EndpointReference, LightingProgram> programs)
    {
        foreach (var device in targets.GroupBy(t => t.Target.DeviceId))
        {
            if (drivers[device.Key] is not IDeviceLightingDriver driver || !DeviceLighting.ValidLimits(driver.LightingLimits)) return "連続照明フレームに対応していません";
            var first = programs[device.First().Target];
            if (device.Any(t => !DeviceLighting.FrameProgram(programs[t.Target]))) return "この機器固有効果は他の効果と合成できません";
            if (!driver.LightingLimits!.EndpointColors && device.Any(t => !DeviceLighting.SameProgram(first, programs[t.Target]))) return "この機器の端子別明るさは未対応です";
            if (driver is IDeviceLightingCompositionDriver composition)
            {
                if (composition.ValidateLightingComposition(device.Select(t => new EndpointLightingProgram(t.Target.EndpointId, programs[t.Target])).ToArray()) is { } error) return error;
            }
            else
            {
                if (!DeviceLighting.ValidAnimation(first) || device.Any(t => !DeviceLighting.SameProgram(first with {
                    Brightness = programs[t.Target].Brightness }, programs[t.Target]))) return "この機器の複数効果合成は未対応です";
                if (driver.ValidateLightingStream(device.Select(t => t.Target.EndpointId).ToArray(), first) is { } error) return error;
            }
        }
        return null;
    }
    private string? CheckLightingAdjustment(DeviceServiceRequest request, LightingAdjustmentPlan plan)
    {
        if (lightingAdjustments!.CheckLightingAdjustment(plan) is { } changed) return changed;
        var programs = plan.Entries.ToDictionary(e => e.Target, e => DeviceLightingAdjustments.Effective(e.Next));
        var cold = ColdAdjustmentGroups(plan); var frameTargets = cold.SelectMany(g => g).Concat(lightingOwners.Keys).ToHashSet();
        foreach (var target in request.Targets!)
        {
            if (lightingSelectionsPending.ContainsKey(target.Target)) return "前の照明操作の完了後に、明るさの基準を再取得してください";
            var state = states.GetValueOrDefault(target.Target);
            if (state?.Persistence?.State == DevicePolicySaveState.Failed) return "照明設定が保存されていません。色・効果を再適用してください";
            if (state is { Owned: true, RequestedLighting: { } requested } &&
                !DeviceLighting.SameProgram(requested, plan.Entries.Single(e => e.Target == target.Target).Before))
                return "実行中の照明と保存した選択が一致しません。色・効果を再適用してください";
            if (frameTargets.Contains(target.Target) && CheckBatch(FrameRequest(request, [target], programs), checkLightingStream: false) is { } invalid) return invalid;
            if (lightingOwners.TryGetValue(target.Target, out var owner))
            {
                var group = lightingGroups[owner]; var participant = group.Participants.Single(p => p.Targets.Any(t => t.Target == target.Target));
                if (group.Stopping || group.Fault is not null || group.Frames == 0 || group.Owner != request.Session || !participant.Limits.EndpointColors ||
                    !CurrentLighting(group, participant) || !DeviceLighting.IntensityCompatible(group.Programs[target.Target], programs[target.Target]))
                    return "実行中の照明と保存した基準が一致しません。色・効果を再適用してください";
            }
        }
        var staticTargets = request.Targets.Where(t => !frameTargets.Contains(t.Target)).ToArray();
        if (staticTargets.Length > 0 && CheckBatch(request with { Operation = DeviceOperation.SetLighting, Targets = staticTargets },
            checkLightingStream: false, staticPrograms: programs) is { } staticError) return staticError;
        foreach (var owner in request.Targets.Select(t => lightingOwners.GetValueOrDefault(t.Target)).Where(o => o is not null).Distinct())
        {
            var group = lightingGroups[owner!];
            if (group.Participants.Any(p => !CurrentLighting(group, p))) return "同期対象の所有・設定が変更されました";
            if (group.Programs.Keys.Any(t => !programs.ContainsKey(t) && lightingSelectionsPending.ContainsKey(t))) return "同じ照明構成の前の操作が完了するまでお待ちください";
            var next = group.Programs.ToDictionary(p => p.Key, p => programs.GetValueOrDefault(p.Key) ?? p.Value);
            if (CheckLightingComposition(request, group.Participants.SelectMany(p => p.Targets).ToArray(), next) is { } invalid) return invalid;
        }
        foreach (var group in cold)
        {
            if (CheckLightingComposition(request, request.Targets.Where(t => group.Contains(t.Target)).ToArray(), programs) is { } error) return error;
        }
        return null;
    }
    private Task<DeviceTargetResult[]> QueueLightingAdjustmentCore(DeviceServiceRequest request, DeviceTargetVersion[] targets, LightingAdjustmentPlan plan, CancellationToken ct)
    {
        string? Current() => lightingAdjustments!.CheckLightingAdjustment(plan);
        var programs = plan.Entries.ToDictionary(e => e.Target, e => DeviceLightingAdjustments.Effective(e.Next));
        var pending = new List<Task<DeviceTargetResult[]>>();
        var cold = ColdAdjustmentGroups(plan); var framed = cold.SelectMany(g => g).Concat(lightingOwners.Keys).ToHashSet();
        foreach (var existing in targets.Where(t => lightingOwners.ContainsKey(t.Target)).GroupBy(t => lightingOwners[t.Target]))
        {
            var selected = existing.ToArray();
            pending.Add(QueueLightingIntensityUpdateCore(AdjustmentPart(request, selected, programs[selected[0].Target]), selected, programs, Current, preserveClocks: true)
                ?? throw new InvalidOperationException("検査済みの明るさ対象が変更されました"));
        }
        foreach (var group in cold)
        {
            var selected = targets.Where(t => group.Contains(t.Target)).ToArray();
            var command = FrameRequest(request, selected, programs) with { Id = Guid.NewGuid().ToString("N") };
            pending.Add(Task.Run(() => StartLightingGroupAsync(command, selected, ct, Current, programs)));
        }
        var staticTargets = targets.Where(t => !framed.Contains(t.Target)).ToArray();
        if (staticTargets.Length > 0) pending.Add(Task.Run(() => ExecuteStaticLightingAsync(request, staticTargets, programs, ct, Current)));
        return Complete();
        async Task<DeviceTargetResult[]> Complete() => (await Task.WhenAll(pending).ConfigureAwait(false)).SelectMany(r => r).ToArray();
    }
}
