namespace FlowDeck.Core;

public enum LightingAdjustmentScope { All, Group, Targets }
public enum LightingAdjustmentKind { Brightness, Off, Restore }
public sealed record LightingLevel(int Brightness = 100, bool Off = false);
// One group owns the group layer on an endpoint. The id/revision describes the applied selection,
// so editing or deleting a library group must not silently change output already selected by the user.
public sealed record LightingTargetAdjustment(EndpointReference Target, LightingProgram Baseline,
    LightingLevel All, LightingLevel Group, LightingLevel Individual, string? GroupId = null, long GroupRevision = 0);
public sealed record LightingAdjustmentRequest(LightingAdjustmentKind Kind, LightingAdjustmentScope Scope, string ExpectedGeneration,
    int? Brightness = null, string? GroupId = null, long GroupRevision = 0);
public sealed record LightingAdjustmentEntry(EndpointReference Target, LightingProgram Before,
    LightingTargetAdjustment? Previous, LightingTargetAdjustment Next);
public sealed record LightingAdjustmentPlan(LightingAdjustmentRequest Selection, long LibraryRevision,
    LightingAdjustmentEntry[] Entries, SynchronizedLighting[] Parts, EndpointReference[][] Synchronization);
public interface IDeviceLightingAdjustments
{
    LightingAdjustmentPlan ResolveLightingAdjustment(LightingAdjustmentRequest request, EndpointReference[] targets);
    string? CheckLightingAdjustment(LightingAdjustmentPlan plan);
}
public static class DeviceLightingAdjustments
{
    private static bool Level(LightingLevel? value) => value is { Brightness: >= 0 and <= 100 };
    public static bool Valid(LightingTargetAdjustment? value) => value is not null && DeviceConfigurationRules.ValidReference(value.Target) &&
        DeviceConfigurationRules.ValidProgram(value.Baseline) && Level(value.All) && Level(value.Group) && Level(value.Individual) &&
        (value.GroupId is null ? value.GroupRevision == 0 && value.Group == new LightingLevel() : DeviceConfigurationRules.ProfileId(value.GroupId) && value.GroupRevision >= 1);
    public static bool Valid(LightingAdjustmentRequest? r) => r is not null && Enum.IsDefined(r.Kind) && Enum.IsDefined(r.Scope) &&
        DeviceServiceProtocol.Id(r.ExpectedGeneration) && (r.Kind == LightingAdjustmentKind.Brightness ? r.Brightness is >= 0 and <= 100 : r.Brightness is null) &&
        (r.Scope == LightingAdjustmentScope.Group ? DeviceConfigurationRules.ProfileId(r.GroupId) && r.GroupRevision >= 1 : r.GroupId is null && r.GroupRevision == 0);
    public static bool Same(LightingTargetAdjustment? a, LightingTargetAdjustment? b) => a is null ? b is null : b is not null &&
        a.Target == b.Target && DeviceLighting.SameProgram(a.Baseline, b.Baseline) && a.All == b.All && a.Group == b.Group &&
        a.Individual == b.Individual && a.GroupId == b.GroupId && a.GroupRevision == b.GroupRevision;
    public static LightingProgram Effective(LightingTargetAdjustment value)
    {
        if (!Valid(value)) throw new ArgumentException("照明の明るさ設定が不正です");
        var brightness = value.All.Off || value.Group.Off || value.Individual.Off ? 0 :
            (int)Math.Round(value.Baseline.Brightness * (value.All.Brightness / 100d) * (value.Group.Brightness / 100d) * (value.Individual.Brightness / 100d));
        // Keep software effects alive at zero so restoring intensity preserves their clock. Native devices
        // may not expose brightness for an internal effect, but their explicit off operation still works.
        return brightness == 0 && !DeviceLighting.Animated(value.Baseline) ? new("off", []) : value.Baseline with { Brightness = brightness };
    }
    public static LightingTargetAdjustment Edit(LightingTargetAdjustment value, LightingAdjustmentRequest request)
    {
        if (!Valid(value) || !Valid(request)) throw new ArgumentException("照明の明るさ操作が不正です");
        LightingLevel Change(LightingLevel level) => request.Kind switch {
            LightingAdjustmentKind.Brightness => level with { Brightness = request.Brightness!.Value },
            LightingAdjustmentKind.Off => level with { Off = true },
            _ => level with { Off = false }
        };
        return request.Scope switch {
            LightingAdjustmentScope.All => value with { All = Change(value.All) },
            LightingAdjustmentScope.Targets => value with { Individual = Change(value.Individual) },
            _ => value with { GroupId = request.GroupId, GroupRevision = request.GroupRevision,
                Group = Change(value.GroupId == request.GroupId && value.GroupRevision == request.GroupRevision ? value.Group : new()) }
        };
    }
    public static LightingAdjustmentPlan Resolve(LightingConfiguration lighting, EndpointReference[] registered,
        LightingAdjustmentRequest request, EndpointReference[] targets)
    {
        if (!Valid(request) || targets is not { Length: > 0 and <= 256 } || targets.Any(t => !registered.Contains(t)) || targets.Distinct().Count() != targets.Length)
            throw new IOException("明るさの操作対象が不正です");
        var selected = request.Scope switch {
            LightingAdjustmentScope.All => registered,
            LightingAdjustmentScope.Group => lighting.Groups.SingleOrDefault(g => g.Id == request.GroupId && g.Revision == request.GroupRevision)?.Targets
                ?? throw new IOException("グループが変更されました。再取得してください"),
            _ => targets
        };
        if (!selected.ToHashSet().SetEquals(targets)) throw new IOException("表示した照明対象と明るさ操作の対象が一致しません");
        var entries = targets.Select(target =>
        {
            var before = lighting.Preferences?.SingleOrDefault(p => p.Target == target)?.Program ?? throw new IOException("明るさを変える前に、対象の色・効果を設定してください");
            var previous = lighting.TargetAdjustments?.SingleOrDefault(p => p.Target == target);
            var next = Edit(previous ?? new(target, before, new(), new(), new()), request);
            return new LightingAdjustmentEntry(target, before, previous, next);
        }).ToArray();
        var animated = entries.Where(e => DeviceLighting.Animated(Effective(e.Next))).Select(e => e.Target).ToHashSet();
        var framed = entries.Where(e => DeviceLighting.FrameProgram(Effective(e.Next))).Select(e => e.Target).ToHashSet();
        var synchronization = (lighting.SavedFrameGroups ?? (lighting.SavedSynchronization ?? []).Select(s => s.Targets).ToArray())
            .Select(g => g.Where(framed.Contains).ToArray()).Where(g => g.Length > 0).ToList();
        var grouped = synchronization.SelectMany(s => s).ToHashSet();
        synchronization.AddRange(animated.Where(t => !grouped.Contains(t)).Select(t => new[] { t }));
        return Contract.Copy(new LightingAdjustmentPlan(request, lighting.Revision, entries,
            entries.Select(e => new SynchronizedLighting([e.Target], Effective(e.Next))).ToArray(), synchronization.ToArray()));
    }
    public static bool Matches(LightingConfiguration lighting, LightingAdjustmentEntry entry) =>
        lighting.Preferences?.SingleOrDefault(p => p.Target == entry.Target)?.Program is { } current && DeviceLighting.SameProgram(current, entry.Before) &&
        Same(lighting.TargetAdjustments?.SingleOrDefault(p => p.Target == entry.Target), entry.Previous);
    public static bool ValidPlan(LightingAdjustmentPlan? plan) => plan is not null && Valid(plan.Selection) && plan.LibraryRevision >= 0 &&
        plan.Entries is { Length: > 0 and <= 256 } && DeviceLightingLibrary.ValidParts(plan.Parts) &&
        plan.Entries.All(e => e is not null && DeviceConfigurationRules.ValidProgram(e.Before) && Valid(e.Next) && e.Target == e.Next.Target &&
            (e.Previous is null || Valid(e.Previous) && e.Previous.Target == e.Target && DeviceLighting.SameProgram(Effective(e.Previous), e.Before)) &&
            Same(e.Next, Edit(e.Previous ?? new(e.Target, e.Before, new(), new(), new()), plan.Selection))) &&
        plan.Entries.DistinctBy(e => e.Target).Count() == plan.Entries.Length &&
        plan.Entries.Length == plan.Parts.Sum(p => p.Targets.Length) && plan.Parts.All(p => p.Targets.All(t =>
            plan.Entries.Any(e => e.Target == t && DeviceLighting.SameProgram(p.Program, Effective(e.Next))))) &&
        plan.Synchronization is { Length: <= 256 } && plan.Synchronization.All(s => s is { Length: > 0 and <= 256 }) &&
        plan.Synchronization.SelectMany(s => s).Distinct().Count() == plan.Synchronization.Sum(s => s.Length) &&
        plan.Entries.Where(e => DeviceLighting.Animated(Effective(e.Next))).All(e => plan.Synchronization.Any(g => g.Contains(e.Target))) &&
        plan.Synchronization.SelectMany(s => s).All(t => plan.Entries.Any(e => e.Target == t && DeviceLighting.FrameProgram(Effective(e.Next))));
}
