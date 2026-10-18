using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceLightingLevelEdit(string Session, DeviceLightingEdit Lighting);

// A slider edits one saved intensity layer, never the effective (already multiplied) color.
public sealed class DeviceLightingLevelDraft
{
    private DeviceWorkspaceSnapshot? snapshot;
    private EndpointReference[] selected = [], targets = [];
    private DeviceLightingEditVersion? editedAt;
    private string? editedSession;
    private int baselineBrightness = 100;
    private bool baselineMixed;
    private DeviceLightingLevelEdit? captured;
    public LightingAdjustmentScope Scope { get; private set; } = LightingAdjustmentScope.All;
    public string? GroupId { get; private set; }
    public EndpointReference[] Targets => targets.ToArray();
    public int Brightness { get; private set; } = 100;
    public bool Dirty { get; private set; }
    public bool Conflict { get; private set; }
    public bool Mixed { get; private set; }
    public string Current { get; private set; } = "照明を選択してください";
    private LightingConfiguration? Library => snapshot?.Configuration.Configuration.Lighting;
    private EndpointGroup? Group => Library?.Groups.SingleOrDefault(g => g.Id == GroupId);
    private EndpointReference[] Registered => snapshot?.Configuration.Configuration.Devices.Devices
        .SelectMany(d => d.Endpoints.Where(e => e.Kind == EndpointKind.Lighting).Select(e => new EndpointReference(d.Id, e.Id))).ToArray() ?? [];
    public void Select(LightingAdjustmentScope scope, string? groupId = null)
    {
        if (!Enum.IsDefined(scope) || (scope != LightingAdjustmentScope.Group && groupId is not null)) throw new ArgumentException("明るさの対象が不正です");
        if (scope == Scope && groupId == GroupId) return;
        if (Dirty) throw new InvalidOperationException("明るさの編集を適用または取り消してください");
        Scope = scope; GroupId = groupId; captured = null; Refresh();
    }
    public void Update(DeviceWorkspaceSnapshot value, IEnumerable<EndpointReference> selection)
    {
        var next = selection.Distinct().ToArray();
        var selectionChanged = selected.Length != next.Length || !selected.All(next.Contains);
        snapshot = value; selected = next;
        // Only the selected-target layer belongs to these checkboxes. All/group drafts
        // retain their own scope, and service membership changes still report conflicts.
        if (Scope == LightingAdjustmentScope.Targets && selectionChanged) Reset();
        else Refresh();
    }
    private void Refresh()
    {
        targets = Scope switch { LightingAdjustmentScope.All => Registered, LightingAdjustmentScope.Group => Group?.Targets.ToArray() ?? [], _ => selected.ToArray() };
        if (Dirty && (!SameVersion(editedAt, Version()) || editedSession != snapshot?.Session)) Conflict = true;
        var levels = targets.Select(t =>
        {
            var a = Library?.TargetAdjustments?.SingleOrDefault(a => a.Target == t);
            return Scope switch { LightingAdjustmentScope.All => a?.All ?? new(), LightingAdjustmentScope.Targets => a?.Individual ?? new(),
                _ => a?.GroupId == GroupId && a?.GroupRevision == Group?.Revision ? a!.Group : new LightingLevel() };
        }).ToArray();
        Mixed = levels.Select(l => l.Brightness).Distinct().Count() > 1;
        var off = levels.Length == 0 ? "" : levels.All(l => l.Off) ? " · 消灯中" : levels.Any(l => l.Off) ? " · 一部消灯中" : "";
        Current = levels.Length == 0 ? "照明を選択してください" : (Mixed ? "明るさが混在" : $"現在 {levels[0].Brightness}%") + off;
        if (targets.Any(t => Library?.Preferences?.Any(p => p.Target == t) != true)) Current = "色・効果が未設定";
        if (!Dirty)
        {
            Brightness = Mixed || levels.Length == 0 ? 100 : levels[0].Brightness;
            baselineBrightness = Brightness; baselineMixed = Mixed;
        }
    }
    public void SetBrightness(int value)
    {
        if (value is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(value));
        if (!Mixed && value == Brightness) return;
        if (!Dirty) { editedAt = Version(); editedSession = snapshot?.Session; }
        captured = null; Brightness = value;
        Dirty = Conflict || baselineMixed || value != baselineBrightness;
        if (!Dirty) Reset();
    }
    public void Reset() { Dirty = false; Conflict = false; editedAt = null; editedSession = null; captured = null; Refresh(); }
    public bool AcceptApplied(DeviceLightingLevelEdit edit, DeviceOperationResult result)
    {
        if (!ReferenceEquals(captured, edit) || edit.Session != snapshot?.Session ||
            targets.Length != edit.Lighting.Version.Targets.Length || !targets.All(t => edit.Lighting.Version.Targets.Any(v => v.Target == t)) ||
            !DeviceLightingDraft.AppliedSuccessfully(edit.Lighting, result, snapshot?.State)) return false;
        Reset(); return true;
    }
    public string? Problem
    {
        get
        {
            if (snapshot is not { Connected: true, Session: not null, ExitAttempted: false } s || !double.IsFinite(s.ElapsedSeconds) || s.ElapsedSeconds is < 0 or >= 3 || s.State.Releasing || s.State.ConfigurationChanging)
                return "サービスの状態を更新してください";
            if (Conflict) return "別の操作で変更あり・現在の設定を読み込んでください";
            if (targets.Length == 0) return "照明を選択してください";
            var registered = Registered.ToHashSet();
            if (targets.Any(t => !registered.Contains(t))) return "登録済みの照明を選択してください";
            var guard = new DeviceLightingDraft(); guard.Select(targets);
            guard.Update(s.State, s.Configuration.Generation, s.Configuration.Configuration.Lighting, s.ElapsedSeconds, s.Connected);
            return guard.CanAdjust ? null : "対象の接続・設定を確認し、色・効果を適用してください";
        }
    }
    private DeviceLightingEditVersion? Version()
    {
        if (snapshot is not { } s) return null;
        var entries = targets.Select(t => s.State.Endpoints.SingleOrDefault(e => e.Target == t)).ToArray();
        return entries.Any(e => e is null) ? null : new(s.State.Inventory.Epoch, s.State.Inventory.TopologyRevision, s.Configuration.Generation,
            entries.Select(e => new DeviceTargetVersion(e!.Target, e.Generation, e.PolicyRevision)).ToArray());
    }
    private static bool SameVersion(DeviceLightingEditVersion? a, DeviceLightingEditVersion? b) => a is not null && b is not null &&
        a.Epoch == b.Epoch && a.Topology == b.Topology && a.ConfigurationGeneration == b.ConfigurationGeneration && a.Targets.Length == b.Targets.Length && a.Targets.All(b.Targets.Contains);
    public DeviceLightingLevelEdit Capture(LightingAdjustmentKind kind)
    {
        if (Problem is { } problem) throw new InvalidOperationException(problem);
        if (kind != LightingAdjustmentKind.Brightness && Dirty) throw new InvalidOperationException("明るさの編集を適用または取り消してください");
        if (kind == LightingAdjustmentKind.Brightness && !Dirty && Mixed) throw new InvalidOperationException("適用する明るさを選んでください");
        var request = new LightingAdjustmentRequest(kind, Scope, snapshot!.Configuration.Generation,
            kind == LightingAdjustmentKind.Brightness ? Brightness : null, Scope == LightingAdjustmentScope.Group ? GroupId : null,
            Scope == LightingAdjustmentScope.Group ? Group!.Revision : 0);
        DeviceLightingAdjustments.Resolve(Library!, Registered, request, targets);
        return captured = new(snapshot.Session!, new(Contract.Copy(Dirty ? editedAt! : Version()!), DeviceOperation.AdjustLighting, Adjustment: request));
    }
}
