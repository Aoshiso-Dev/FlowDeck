using System.Security.Cryptography;
using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>Firmware 1.3 lighting projection. Only the common authenticated client performs writes.</summary>
public sealed partial class DeviceM5Lighting(DeviceWorkspaceClient client) : IM5LightingBackend, IM5BodyLedBackend
{
    private int pending;
    public M5LightingView Read(string scopeId) => Read(client.Snapshot, scopeId, Volatile.Read(ref pending) > 0);
    public async Task<OperationResult> ExecuteAsync(M5LightingCommand request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref pending);
        try { return await client.ApplyM5LightingAsync(request, cancellationToken).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref pending); }
    }

    // The wire accepts 32 hex characters, while v2 library ids also permit names. Namespace both
    // kinds, and resolve by the complete original id; never use a list position or display name.
    public static string WireId(LightingPresetKind kind, string id)
    {
        if (!Enum.IsDefined(kind) || !DeviceConfigurationRules.ProfileId(id)) throw new ArgumentException("照明IDが不正です");
        return Hash(new[] { "FlowDeck.M5.v2", kind.ToString(), id })[..32];
    }
    private static string Hash(object value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Contract.Json)));
    private static EndpointReference[] Registered(DeviceWorkspaceSnapshot snapshot) => snapshot.Configuration.Configuration.Devices.Devices
        .SelectMany(d => d.Endpoints.Where(e => e.Kind == EndpointKind.Lighting).Select(e => new EndpointReference(d.Id, e.Id))).ToArray();
    private static EndpointReference[] SceneTargets(DeviceLightingScene scene) => scene.Individual.Select(p => p.Target)
        .Concat(scene.Synchronized.SelectMany(p => p.Targets)).ToArray();
    private static bool Live(DeviceWorkspaceSnapshot s) => s.Connected && DeviceServiceProtocol.Id(s.Session) &&
        double.IsFinite(s.ElapsedSeconds) && s.ElapsedSeconds is >= 0 and < 3 && !s.State.Releasing && !s.State.ConfigurationChanging;
    private static string Stamp(DeviceWorkspaceSnapshot? s, string scopeId)
    {
        if (s is null) return Hash(new { scopeId, connected = false });
        var registered = Registered(s).ToHashSet();
        // Neither sensor samples nor animation frames invalidate an edit. Connection, ownership,
        // configuration, capability and policy changes do, including errors that left no saved policy.
        return Hash(new { scopeId, s.Session, live = Live(s), s.State.Inventory.Epoch, s.State.Inventory.TopologyRevision,
            s.Configuration.Generation, s.Configuration.Configuration.Lighting, s.State.LifecycleError,
            devices = s.State.Inventory.Devices.Where(d => registered.Any(t => t.DeviceId == d.Device.Id)).OrderBy(d => d.Device.Id)
                .Select(d => new { d.Device.Id, d.Generation, d.Connected, endpoints = d.Device.Endpoints.Where(e => registered.Contains(new(d.Device.Id, e.Id))).OrderBy(e => e.Id) }),
            states = s.State.Endpoints.Where(e => registered.Contains(e.Target)).OrderBy(e => e.Target.DeviceId).ThenBy(e => e.Target.EndpointId)
                .Select(e => new { e.Target, e.Generation, e.PolicyRevision, e.Owned, e.Outcome, e.Persistence,
                    group = e.Lighting?.GroupId, running = e.Lighting?.Running, fault = e.Lighting?.Fault }) });
    }
    private static EndpointGroup? Group(DeviceWorkspaceSnapshot s, string scopeId)
    {
        if (!M5LightingCommand.ValidScope(scopeId)) throw new ArgumentException("照明の対象が不正です");
        return scopeId == "all" ? null : s.Configuration.Configuration.Lighting.Groups.SingleOrDefault(g => WireId(LightingPresetKind.Group, g.Id) == scopeId)
            ?? throw new InvalidOperationException("照明グループがありません。対象を選び直してください");
    }
    private static void CatalogCheck(LightingConfiguration library)
    {
        if (library.Groups.Length > DeviceLightingLibrary.MaximumGroups || library.Scenes.Length > DeviceLightingLibrary.MaximumScenes ||
            library.Scenes.Count(s => s.ShowOnM5) > DeviceLightingLibrary.MaximumM5Scenes ||
            library.Groups.Select(g => WireId(LightingPresetKind.Group, g.Id)).Distinct().Count() != library.Groups.Length ||
            library.Scenes.Select(s => WireId(LightingPresetKind.Scene, s.Id)).Distinct().Count() != library.Scenes.Length)
            throw new InvalidOperationException("M5GOで扱える照明一覧の範囲を超えています");
    }
    private static EndpointReference[] Targets(DeviceWorkspaceSnapshot s, EndpointGroup? group) => group?.Targets ?? Registered(s);
    private static string? Problem(DeviceWorkspaceSnapshot s, EndpointReference[] targets)
    {
        if (!Live(s)) return "サービスの状態を更新してください";
        if (s.State.LifecycleError is not null) return s.State.LifecycleError;
        if (targets.Length is < 1 or > DeviceServiceProtocol.MaximumTargets || targets.Distinct().Count() != targets.Length)
            return "登録された照明を選択してください";
        var registered = Registered(s).ToHashSet();
        foreach (var t in targets)
        {
            if (!registered.Contains(t)) return "照明の登録が変更されました";
            var device = s.State.Inventory.Devices.SingleOrDefault(d => d.Device.Id == t.DeviceId);
            var capability = device?.Device.Endpoints.SingleOrDefault(e => e.Id == t.EndpointId && e.Kind == EndpointKind.Lighting)?
                .Capabilities.SingleOrDefault(c => c.Feature == DeviceFeature.FixedColor);
            var current = s.State.Endpoints.SingleOrDefault(e => e.Target == t);
            if (device?.Connected != true) return "選択した照明が切断されています";
            if (capability?.Usable != true) return capability?.Reason ?? "選択した照明の設定を確認してください";
            if (current?.Generation != device.Generation) return "照明の構成を更新してください";
            if (current.Outcome is Outcome.Accepted or Outcome.Pending) return "照明の処理中です";
            if (current.Lighting?.Fault is { } fault) return fault;
        }
        return null;
    }
    private static LightingLevel[] Levels(LightingConfiguration library, EndpointReference[] targets, EndpointGroup? group) => targets.Select(t =>
    {
        var a = library.TargetAdjustments?.SingleOrDefault(a => a.Target == t);
        return group is null ? a?.All ?? new() : a?.GroupId == group.Id && a.GroupRevision == group.Revision ? a.Group : new();
    }).Distinct().ToArray();
    public static M5LightingView Read(DeviceWorkspaceSnapshot? snapshot, string scopeId, bool busy = false)
    {
        var stamp = Stamp(snapshot, scopeId);
        M5LightingItem[] groups = [new("all", "すべての照明")], scenes = [];
        var name = scopeId == "all" ? "すべての照明" : "未検出のグループ";
        var brightness = 100; var off = false; var restore = false; var synchronized = false;
        string? error = null;
        try
        {
            if (snapshot is not { } s) throw new InvalidOperationException("サービスへ接続してください");
            var library = s.Configuration.Configuration.Lighting; CatalogCheck(library);
            groups = [.. groups, .. library.Groups.Select(g => new M5LightingItem(WireId(LightingPresetKind.Group, g.Id), g.Name))];
            var group = Group(s, scopeId); name = group?.Name ?? name; var targets = Targets(s, group); var selected = targets.ToHashSet();
            // Firmware has no partial-scene editor. Only expose whole scenes contained in this scope.
            scenes = library.Scenes.Where(scene => scene.ShowOnM5 && SceneTargets(scene).All(selected.Contains))
                .Select(scene => new M5LightingItem(WireId(LightingPresetKind.Scene, scene.Id), scene.Name)).ToArray();
            error = Problem(s, targets);
            var levels = Levels(library, targets, group);
            if (levels.Length == 1) { brightness = Math.Clamp(levels[0].Brightness, 1, 100); off = levels[0].Off; restore = off; }
            // 1.3 cannot display mixed levels or 0% as a brightness. Do not invent a representative value.
            if (levels.Length > 1 || levels is [{ Brightness: 0 }]) error ??= "明るさが混在または0%です。PCで設定してください";
            synchronized = s.State.Endpoints.Any(e => selected.Contains(e.Target) && e.Owned && e.Lighting?.Running == true);
            busy |= s.State.Endpoints.Any(e => selected.Contains(e.Target) && e.Outcome is Outcome.Accepted or Outcome.Pending);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { error = e.Message; }
        return new(stamp, scopeId, name, !busy && error is null, busy, error, brightness, off, restore && error is null,
            synchronized, groups, scenes);
    }
    internal static void WholeActiveGroups(DeviceWorkspaceSnapshot s, EndpointReference[] targets)
    {
        var selected = targets.ToHashSet();
        var active = s.State.Endpoints.Where(e => selected.Contains(e.Target) && e.Owned && e.Lighting?.Running == true)
            .Select(e => e.Lighting!.GroupId).ToHashSet();
        if (s.State.Endpoints.Any(e => !selected.Contains(e.Target) && e.Owned && e.Lighting is { Running: true } light && active.Contains(light.GroupId)))
            throw new InvalidOperationException("対象外の照明と同期中です。同期全体を含むグループを選んでください");
    }
    public static DeviceLightingEdit Capture(DeviceWorkspaceSnapshot s, M5LightingCommand request)
    {
        if (!request.Valid) throw new ArgumentException("照明操作が不正です");
        var view = Read(s, request.ScopeId);
        if (view.Stamp != request.ExpectedStamp) throw new InvalidOperationException("照明設定・接続が変更されました。画面を開き直してください");
        if (!view.Ready) throw new InvalidOperationException(view.Error ?? "照明の処理中です");
        var group = Group(s, request.ScopeId); var targets = Targets(s, group); var library = s.Configuration.Configuration.Lighting;
        DeviceLightingEditVersion Version(EndpointReference[] references) => new(s.State.Inventory.Epoch, s.State.Inventory.TopologyRevision,
            s.Configuration.Generation, references.Select(t => s.State.Endpoints.Single(e => e.Target == t))
                .Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray());
        if (request.Command == "ApplyLightingScene")
        {
            if (!view.Scenes.Any(scene => scene.Id == request.SceneId)) throw new InvalidOperationException("この対象で呼び出せるシーンではありません");
            var scene = library.Scenes.Single(scene => WireId(LightingPresetKind.Scene, scene.Id) == request.SceneId);
            WholeActiveGroups(s, SceneTargets(scene));
            return DeviceWorkspaceClient.Preset(s, LightingPresetKind.Scene, scene.Id);
        }
        if (request.Command == "StopLightingSync")
        {
            targets = s.State.Endpoints.Where(e => targets.Contains(e.Target) && e.Owned && e.Lighting?.Running == true).Select(e => e.Target).ToArray();
            if (targets.Length == 0) throw new InvalidOperationException("選択した対象の同期は動作していません");
            WholeActiveGroups(s, targets);
            var programs = targets.Select(t => library.Preferences?.SingleOrDefault(p => p.Target == t)?.Program).ToArray();
            if (programs.Any(p => p is null || !DeviceLighting.FrameProgram(p)) ||
                programs.Select(p => DeviceLighting.StopColor(p!)).Distinct().Count() != 1)
                throw new InvalidOperationException("異なる基準色の同期停止はPCで行ってください");
            // Persist the fixed normal-stop color so later brightness changes cannot restart animation.
            return new(Version(targets), DeviceOperation.SetLighting, new("fixed", [DeviceLighting.StopColor(programs[0]!)]));
        }
        var kind = request.Command switch { "SetLightingBrightness" => LightingAdjustmentKind.Brightness,
            "LightingOff" => LightingAdjustmentKind.Off, "RestoreLighting" => LightingAdjustmentKind.Restore,
            _ => throw new ArgumentException("未対応の照明操作です") };
        var adjustment = new LightingAdjustmentRequest(kind, group is null ? LightingAdjustmentScope.All : LightingAdjustmentScope.Group,
            s.Configuration.Generation, request.Brightness, group?.Id, group?.Revision ?? 0);
        // Validate the stored baseline without changing it. The service resolves and validates it again.
        DeviceLightingAdjustments.Resolve(library, Registered(s), adjustment, targets);
        return new(Version(targets), DeviceOperation.AdjustLighting, Adjustment: adjustment);
    }
}
