using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceLightingLibraryEdit(string Epoch, string Session, LightingLibraryRequest Request);
public sealed record DeviceLightMember(EndpointReference Target, string Name);
public sealed record DeviceScenePart(string Id, EndpointReference[] Targets, LightingProgram Program, bool Synchronized = false);

/// <summary>An edit of saved definitions. No operation in this draft applies lighting to a device.</summary>
public sealed class DeviceLightingLibraryDraft
{
    private DeviceWorkspaceSnapshot original;
    private DeviceWorkspaceSnapshot latest;
    private readonly EndpointGroup? savedGroup;
    private readonly DeviceLightingScene? savedScene;
    private readonly List<DeviceScenePart> parts = [];
    private readonly DeviceScenePart[] originalParts;
    private EndpointReference[] targets = [];
    public LightingPresetKind Kind { get; }
    public string Id { get; }
    private string name = "";
    public string Name { get => name; set => name = value ?? ""; }
    public DeviceLightMember[] Candidates { get; }
    public EndpointReference[] Targets => targets.ToArray();
    public DeviceScenePart[] Parts => Contract.Copy(parts.ToArray());
    public string[] PartIds => parts.Select(p => p.Id).ToArray();
    public DeviceScenePart? ReadPart(string? id) => Contract.Copy(parts.SingleOrDefault(p => p.Id == id));
    public DeviceNativeLightingEffect[] NativeEffects(string? partId) => parts.SingleOrDefault(p => p.Id == partId) is { } part ?
        DeviceNativeLighting.Common(part.Targets.Select(t => latest.State.Inventory.Devices.SingleOrDefault(d => d.Device.Id == t.DeviceId)?.Device.Endpoints.SingleOrDefault(e => e.Id == t.EndpointId))) : [];
    public string? CapabilityIssue(string? partId) => parts.SingleOrDefault(p => p.Id == partId) is { } part && !DeviceLighting.FrameProgram(part.Program) &&
        !NativeEffects(partId).Any(e => DeviceNativeLighting.Accepts(e, part.Program)) ? "このパートの内蔵効果は現在の対象で利用できません" : null;
    public void ChangeKnownProgram(string id, LightingProgram program)
    {
        var part = parts.SingleOrDefault(p => p.Id == id) ?? throw new ArgumentException("パートを選択してください");
        if (!DeviceLighting.FrameProgram(program) && !NativeEffects(id).Any(e => DeviceNativeLighting.Accepts(e, program)))
            throw new ArgumentException("このパートの対象で使える効果・設定を選んでください");
        ChangePart(id, part.Targets, program);
    }
    public bool CanAddPart => Kind == LightingPresetKind.Scene && parts.Count < 256 && Candidates.Any(c => parts.All(p => !p.Targets.Contains(c.Target)));
    public bool Existing => savedGroup is not null || savedScene is not null;
    public bool Conflict { get; private set; }
    private long Revision => savedGroup?.Revision ?? savedScene?.Revision ?? 0;
    public DeviceLightingLibraryDraft(DeviceWorkspaceSnapshot snapshot, LightingPresetKind kind, string? id = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentException("保存する照明の種類が不正です");
        original = Contract.Copy(snapshot); latest = original; Kind = kind;
        if (id is not null)
        {
            if (kind == LightingPresetKind.Group) savedGroup = original.Configuration.Configuration.Lighting.Groups.SingleOrDefault(g => g.Id == id)
                ?? throw new IOException("保存したグループは削除されています");
            else savedScene = original.Configuration.Configuration.Lighting.Scenes.SingleOrDefault(s => s.Id == id)
                ?? throw new IOException("保存したシーンは削除されています");
        }
        Id = id ?? Guid.NewGuid().ToString("N"); Name = savedGroup?.Name ?? savedScene?.Name ?? "";
        targets = savedGroup?.Targets.ToArray() ?? [];
        Candidates = original.Configuration.Configuration.Devices.Devices.SelectMany(d => d.Endpoints.Where(e => e.Kind == EndpointKind.Lighting)
            .Select(e => new DeviceLightMember(new(d.Id, e.Id), d.Name + " / " +
                (original.Configuration.Configuration.Lighting.Names.SingleOrDefault(n => n.Target == new EndpointReference(d.Id, e.Id))?.Name ?? e.Name)))).ToArray();
        if (savedScene is not null)
        {
            parts.AddRange(savedScene.Individual.Select(p => new DeviceScenePart(Guid.NewGuid().ToString("N"), [p.Target], Contract.Copy(p.Program))));
            parts.AddRange(savedScene.Synchronized.Select(p => new DeviceScenePart(Guid.NewGuid().ToString("N"), p.Targets.ToArray(), Contract.Copy(p.Program), true)));
        }
        // Edits replace immutable part records, so this baseline never needs to be serialized during UI polling.
        originalParts = parts.ToArray();
    }
    public void Update(DeviceWorkspaceSnapshot snapshot)
    {
        latest = snapshot;
        Conflict |= snapshot.State.Inventory.Epoch != original.State.Inventory.Epoch || snapshot.Session != original.Session ||
            snapshot.Configuration.Generation != original.Configuration.Generation;
    }
    public void AcceptMetadataSave(DeviceWorkspaceSnapshot before, DeviceWorkspaceSnapshot after)
    {
        if (!Conflict && original.Configuration.Generation == before.Configuration.Generation && DeviceLightingPersonalization.MetadataOnly(before, after))
        { original = original with { Configuration = after.Configuration }; latest = after; }
    }
    private EndpointReference[] CheckTargets(IEnumerable<EndpointReference> values)
    {
        var next = values.ToArray();
        if (next.Length > 256 || next.Distinct().Count() != next.Length || next.Any(t => !Candidates.Any(c => c.Target == t)))
            throw new ArgumentException("登録済みの照明端子を選択してください");
        return next;
    }
    public void SetTargets(IEnumerable<EndpointReference> values)
    {
        if (Kind != LightingPresetKind.Group) throw new InvalidOperationException("グループを選択してください");
        targets = CheckTargets(values);
    }
    public bool Available(string partId, EndpointReference target) => Candidates.Any(c => c.Target == target) &&
        !parts.Any(p => p.Id != partId && p.Targets.Contains(target));
    public string AddPart()
    {
        if (Kind != LightingPresetKind.Scene || parts.Count >= 256) throw new InvalidOperationException("シーンのパートを追加できません");
        var first = Candidates.FirstOrDefault(c => parts.All(p => !p.Targets.Contains(c.Target))) ?? throw new InvalidOperationException("未選択の照明がありません");
        var id = Guid.NewGuid().ToString("N"); parts.Add(new(id, [first.Target], new("fixed", [new(52, 137, 255)]))); return id;
    }
    public void RemovePart(string id) => parts.RemoveAll(p => p.Id == id);
    public void ChangePart(string id, EndpointReference[] selected, LightingProgram program)
    {
        var index = parts.FindIndex(p => p.Id == id); if (index < 0) throw new ArgumentException("パートを選択してください");
        selected = CheckTargets(selected);
        if (!DeviceConfigurationRules.ValidProgram(program) || selected.Any(t => !Available(id, t))) throw new ArgumentException("照明が別のパートと重複しているか、効果が不正です");
        parts[index] = parts[index] with { Targets = selected, Program = Contract.Copy(program) };
    }
    public void CaptureCurrent(EndpointReference[] selected)
    {
        if (Kind != LightingPresetKind.Scene) throw new InvalidOperationException("シーンを選択してください");
        selected = CheckTargets(selected);
        var value = DeviceLightingSceneCapture.Create(Id, "編集中", latest.Configuration.Configuration.Lighting, selected);
        parts.Clear();
        parts.AddRange(value.Individual.Select(p => new DeviceScenePart(Guid.NewGuid().ToString("N"), [p.Target], p.Program)));
        parts.AddRange(value.Synchronized.Select(p => new DeviceScenePart(Guid.NewGuid().ToString("N"), p.Targets, p.Program, true)));
    }
    private DeviceLightingScene Scene() => DeviceLightingSceneRequirements.Stamp(new(Id, Name.Trim(),
        parts.Where(p => !p.Synchronized && p.Targets.Length == 1).Select(p => new EndpointLighting(p.Targets[0], p.Program)).ToArray(),
        parts.Where(p => p.Synchronized || p.Targets.Length != 1).Select(p => new SynchronizedLighting(p.Targets, p.Program)).ToArray(), Math.Max(1, Revision)));
    private EndpointGroup Group() => new(Id, Name.Trim(), EndpointKind.Lighting, targets, Math.Max(1, Revision));
    public bool Dirty => !Existing || Name.Trim() != (savedGroup?.Name ?? savedScene?.Name) ||
        (Kind == LightingPresetKind.Group ? !targets.SequenceEqual(savedGroup!.Targets) : parts.Count != originalParts.Length ||
        parts.Where((p, i) => p.Synchronized != originalParts[i].Synchronized || !p.Targets.SequenceEqual(originalParts[i].Targets) ||
            !DeviceLighting.SameProgram(p.Program, originalParts[i].Program)).Any());
    private string? ConnectionProblem => Conflict ? "設定が変わりました。再取得してください" :
        !latest.Connected || latest.ExitAttempted || !double.IsFinite(latest.ElapsedSeconds) || latest.ElapsedSeconds is < 0 or >= 3 ? "サービスの状態を更新してください" :
        latest.State.Releasing || latest.State.ConfigurationChanging ? "処理が終わるまでお待ちください" : null;
    public string? Problem => ConnectionProblem ?? (!DeviceConfigurationRules.Text(Name, 80) ? "名前を入力してください" :
        !Existing && (Kind == LightingPresetKind.Group ? latest.Configuration.Configuration.Lighting.Groups.Length >= DeviceLightingLibrary.MaximumGroups : latest.Configuration.Configuration.Lighting.Scenes.Length >= DeviceLightingLibrary.MaximumScenes) ? "保存数の上限に達しています" :
        Kind == LightingPresetKind.Group ? DeviceLightingLibrary.ValidGroup(Group()) ? null : "照明を1〜256個選択してください" :
        DeviceLightingLibrary.ValidScene(Scene()) ? null : "各パートの照明を選択してください（共通パートは32個まで）");
    public bool CanDelete => Existing && ConnectionProblem is null;
    public DeviceLightingLibraryEdit Capture(bool delete = false)
    {
        if (delete ? !CanDelete : Problem is not null) throw new InvalidOperationException(Problem ?? "削除できません");
        var op = Kind == LightingPresetKind.Group ? delete ? LightingLibraryOperation.DeleteGroup : LightingLibraryOperation.SaveGroup :
            delete ? LightingLibraryOperation.DeleteScene : LightingLibraryOperation.SaveScene;
        return new(original.State.Inventory.Epoch, original.Session!, new(op, original.Configuration.Generation, Id, Revision,
            !delete && Kind == LightingPresetKind.Group ? Contract.Copy(Group()) : null, !delete && Kind == LightingPresetKind.Scene ? Contract.Copy(Scene()) : null));
    }
}
