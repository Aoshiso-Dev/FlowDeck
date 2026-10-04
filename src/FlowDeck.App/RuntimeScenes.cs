using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public record SceneApplyRow(string Target, string Result);
public record SceneApplyResult(bool Succeeded, SceneApplyRow[] Rows, string? Error = null);

public sealed partial class RuntimeHost
{
    public LightingSceneCollection SceneLibrary { get; private set; } = new(Scenes: []);
    public string? SceneLibraryError { get; private set; }
    public bool SceneApplying { get; private set; }
    private LightingSceneStore SceneStore => new(Store.DirectoryPath);

    public async Task LoadScenesAsync()
    {
        await lightingSceneGate.WaitAsync();
        try
        {
            try { SceneLibrary = await SceneStore.LoadAsync(); SceneLibraryError = null; }
            catch (Exception e) { SceneLibrary = new(Scenes: []); SceneLibraryError = "シーンを読み込めません: " + e.Message; }
        }
        finally { lightingSceneGate.Release(); }
    }
    public async Task SaveSceneAsync(LightingScene scene)
    {
        await lightingSceneGate.WaitAsync();
        try
        {
            if (SceneLibraryError is not null) throw new IOException(SceneLibraryError);
            var scenes = (SceneLibrary.Scenes ?? []).ToList(); var index = scenes.FindIndex(s => s.Id == scene.Id);
            if (index >= 0) scenes[index] = scene; else scenes.Add(scene);
            var next = new LightingSceneCollection(Scenes: scenes.ToArray());
            await SceneStore.SaveAsync(next); SceneLibrary = Contract.Copy(next);
        }
        finally { lightingSceneGate.Release(); }
    }
    public async Task DeleteSceneAsync(string id)
    {
        await lightingSceneGate.WaitAsync();
        try
        {
            if (SceneLibraryError is not null) throw new IOException(SceneLibraryError);
            var next = SceneLibrary with { Scenes = (SceneLibrary.Scenes ?? []).Where(s => s.Id != id).ToArray() };
            await SceneStore.SaveAsync(next); SceneLibrary = next;
        }
        finally { lightingSceneGate.Release(); }
    }
    public async Task<LightingScene> CaptureLightingSceneAsync(string id, string name, bool includeHub, bool includeBoard, LightingGroup? group = null)
    {
        await lightingSceneGate.WaitAsync();
        try { return await CaptureLightingSceneCoreAsync(id, name, includeHub, includeBoard, ResolveLightingGroup(group)); }
        finally { lightingSceneGate.Release(); }
    }
    private async Task<LightingScene> CaptureLightingSceneCoreAsync(string id, string name, bool includeHub, bool includeBoard, LightingGroup? group = null)
    {
            CheckGroupSyncScope(group);
            if (group is not null) { includeHub &= group.Hub.Length > 0; includeBoard &= group.Board.Length > 0; }
            var revision = Coordinator.Snapshot.LightingRevision;
            var active = LightingSynchronized ? ActiveLightingSync : null;
            if (active is not null && (active.Channels.Length > 0 && !includeHub || !includeBoard)) throw new InvalidOperationException("同期中の照明を一緒に保存してください");
            if (includeBoard && active is null)
            {
                await lightingCleanup; await rgbGate.WaitAsync();
                try
                {
                    if (!Demo && Coordinator.Settings.Motherboard.OpenRgbEnabled && (group is null || group.Board.Any(b => !MemoryLighting.IsIdentity(b.Identity)))) throw new IOException("マザー照明をUSB接続に切り替えてください");
                    if (!Demo) Rgb = await ReadNativeRgbAsync();
                }
                finally { rgbGate.Release(); }
            }
            var settings = Coordinator.Settings; var hub = new List<SceneHubLight>(); var board = new List<SceneBoardLight>();
            if (includeHub)
            {
                var identity = settings.Hardware.Hub?.Id ?? throw new IOException("NZXTが未設定です");
                var channels = group is null ? settings.Hardware.Leds ?? [] : group.Hub.Where(h => h.HubId == identity).Select(h => h.Channel).ToArray();
                if (Coordinator.LightingTargetError(identity, channels) is { } error) throw new IOException(error);
                foreach (var channel in channels)
                {
                    if (active?.Channels.Contains(channel) == true) continue;
                    if (!settings.LightingPorts.TryGetValue(channel, out var value) || value.HubId != identity || value.Outcome != Outcome.WrittenUnverified || !HubLighting.Valid(value.Effect))
                        throw new IOException(channel.ToUpperInvariant() + " の最終送信設定が不明です。一度照明を適用してから保存してください");
                    hub.Add(new(identity, channel, value.Effect!));
                }
            }
            if (includeBoard)
            {
                if (Rgb.Error is not null || Rgb.Devices.All(d => d.ControlIdentity is null)) throw new IOException(Rgb.Error ?? "マザー照明が未接続です");
                foreach (var device in Rgb.Devices.Where(d => d.ControlIdentity is not null))
                foreach (var zone in device.Zones.Where(z => z.HardwareZone is not null && z.LedCount > 0))
                {
                    if (group is not null && !group.Board.Contains(new(device.ControlIdentity!, zone.HardwareZone!.Value))) continue;
                    if (active is { } sync && LightingGroups.SyncTargets(sync).Contains(new(device.ControlIdentity!,zone.HardwareZone!.Value))) continue;
                    if (!BoardLighting.IsValid(zone.Configuration)) throw new IOException("保存できないマザー照明設定です");
                    board.Add(new(device.ControlIdentity!, zone.HardwareZone!.Value, zone.Configuration!));
                }
            }
            if (includeHub && Coordinator.Snapshot.LightingRevision != revision) throw new IOException("保存中に照明が変更されました。もう一度保存してください");
            var scene = new LightingScene(id, name.Trim(), hub.ToArray(), board.ToArray(), active);
            if (LightingScenes.Validate(scene) is { } invalid) throw new IOException(invalid);
            return Contract.Copy(scene);
    }
    private void CheckSceneTargets(LightingScene scene)
    {
        if (scene.Board.Any(b => MemoryLighting.IsIdentity(b.Identity) && !MemoryLighting.Valid(b.Effect)))
            throw new IOException("シーンに未対応のメモリ照明効果があります");
        if (scene.Sync is { } unsupportedSync && MemoryLighting.IsIdentity(unsupportedSync.BoardIdentity))
            throw new IOException("メモリの連続同期は未対応です");
        var hub = scene.Hub.Select(h => (h.HubId, h.Channel)).Concat(scene.Sync is { } s ? s.Channels.Select(c => (s.HubId, c)) : []);
        foreach (var group in hub.GroupBy(h => h.HubId))
            if (Coordinator.LightingTargetError(group.Key, group.Select(h => h.Item2).ToArray()) is { } error) throw new IOException(error);
        var board = scene.Board.Select(b => (b.Identity, b.Zone)).Concat(scene.Sync is { } sync ? LightingGroups.SyncTargets(sync).Select(z => (z.Identity, z.Zone)) : []);
        foreach (var (identity, zoneId) in board)
        {
            if (!Demo && !MemoryLighting.IsIdentity(identity) && Coordinator.Settings.Motherboard.OpenRgbEnabled) throw new IOException("シーンのマザー照明にはUSB接続が必要です");
            var device = Rgb.Devices.SingleOrDefault(d => d.ControlIdentity == identity);
            var zone = device?.Zones.SingleOrDefault(z => z.HardwareZone == zoneId && z.LedCount > 0);
            if (zone?.Configuration is null) throw new IOException("シーンのマザー照明が見つかりません。対象を確認してください");
        }
    }
    public async Task<OperationResult> ApplyHubLightingAsync(ControlRequest request, LightingGroup? group = null)
    {
        await lightingSceneGate.WaitAsync();
        try
        {
            group = ResolveLightingGroup(group); CheckGroupSyncScope(group);
            var identity = Coordinator.Settings.Hardware.Hub?.Id ?? "";
            var channels = request.LightingChannels ?? Coordinator.Settings.Hardware.Leds ?? [];
            if (group is not null && channels.Any(c => !group.Hub.Contains(new(identity, c)))) throw new IOException("グループ外の照明は変更できません");
            await InvalidateAdjustmentBaselineAsync(new(Guid.NewGuid().ToString("N"), "NZXT照明",
                channels.Select(c => new SceneHubLight(identity, c, new("fixed", [new(0, 0, 0)]))).ToArray(), []));
            return await Coordinator.RequestAsync(request);
        }
        finally { lightingSceneGate.Release(); }
    }
    public async Task<SceneApplyResult> ApplyLightingSceneAsync(LightingScene source, LightingGroup? group = null)
    {
        await lightingSceneGate.WaitAsync();
        try
        {
            group = ResolveLightingGroup(group); CheckGroupSyncScope(group);
            return await ApplyLightingSceneCoreAsync(LightingGroups.Select(source, group), invalidateBrightness: true);
        }
        finally { lightingSceneGate.Release(); }
    }
    private async Task<SceneApplyResult> ApplyLightingSceneCoreAsync(LightingScene source, bool invalidateBrightness = false)
    {
        var scene = Contract.Copy(source);
        if (LightingScenes.Validate(scene) is { } invalid) throw new ArgumentException(invalid);
        SceneApplying = true;
        var rows = new List<SceneApplyRow>(); var planned = scene.Hub.Select(h => "NZXT / " + h.Channel.ToUpperInvariant())
            .Concat(scene.Board.Select(b => NativeLightLabel(b.Identity, b.Zone))).Concat(scene.Sync is null ? [] : new[] { "同期" }).ToArray();
        try
        {
            CheckSceneTargets(scene); // Missing identities fail before stopping an existing animation or writing any device.
            CheckSceneSyncScope(scene);
            if (invalidateBrightness) await InvalidateAdjustmentBaselineAsync(scene);
            await StopLightingSyncCoreAsync();
            var expected = Coordinator.Snapshot.LightingRevision;
            var generation = Coordinator.Snapshot.DeviceGeneration;
            var usesHub = scene.Hub.Length > 0 || scene.Sync is { Channels.Length: > 0 };
            void CheckRevision()
            {
                var state = Coordinator.Snapshot;
                if (usesHub && (state.LightingRevision != expected || state.DeviceGeneration != generation)) throw new IOException("別の照明操作または接続変更によりシーンの適用を停止しました");
            }
            await rgbGate.WaitAsync();
            try
            {
                if (!Demo && (scene.Board.Length > 0 || scene.Sync is not null)) Rgb = await ReadNativeRgbAsync();
                CheckSceneTargets(scene); CheckRevision();
                foreach (var light in scene.Hub)
                {
                    CheckRevision();
                    var result = await Coordinator.RequestAsync(new(Guid.NewGuid().ToString("N"), "照明シーン", Coordinator.Epoch, expected, "SetLighting",
                        LightingEffect: light.Effect, LightingChannels: [light.Channel]));
                    expected = result.LightingRevision;
                    rows.Add(new("NZXT / " + light.Channel.ToUpperInvariant(), result.Outcome == Outcome.WrittenUnverified ? "送信済み" : result.Message));
                    if (result.Outcome != Outcome.WrittenUnverified) throw new IOException(result.Message);
                }
                foreach (var group in scene.Board.GroupBy(b => b.Identity))
                {
                    CheckRevision();
                    var device = Rgb.Devices.Single(d => d.ControlIdentity == group.Key);
                    var changes = group.Select(b => new BoardLightChange(b.Zone, device.Zones.Single(z => z.HardwareZone == b.Zone).Configuration!, b.Effect)).ToArray();
                    var result = await ApplyBoardLightingCoreAsync(group.Key, changes);
                    rows.AddRange(result.Zones.Select(z => new SceneApplyRow(NativeLightLabel(group.Key, z.Zone), z.Status == "verified" ? "設定一致" : z.Status)));
                    if (!result.Verified) throw new IOException("マザー照明の一部を確認できません。対象別の結果を確認してください");
                    CheckRevision();
                }
            }
            finally { rgbGate.Release(); }
            CheckRevision();
            if (scene.Sync is { } sync)
            {
                await StartLightingSyncCoreAsync(sync.Channels, sync.Mode, sync.Color, sync.Speed, sync.BoardIdentity, sync.Zones, expected, generation, sync.Memory);
                rows.Add(new("同期", "送信開始"));
            }
            DismissLightingSyncError();
            var success = new SceneApplyResult(true, rows.ToArray()); Log.Write("lightingScene", new { scene.Id, scene.Name, result = success }); return success;
        }
        catch (Exception e)
        {
            rows.AddRange(planned.Where(p => rows.All(r => r.Target != p)).Select(p => new SceneApplyRow(p, "未完了")));
            var failure = new SceneApplyResult(false, rows.ToArray(), e.Message); Log.Write("lightingScene", new { scene.Id, scene.Name, result = failure }); return failure;
        }
        finally { SceneApplying = false; }
    }
}

