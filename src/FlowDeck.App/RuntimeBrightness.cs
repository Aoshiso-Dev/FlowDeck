using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class RuntimeHost
{
    public LightingAdjustmentState LightingAdjustment { get; private set; } = new();
    public string? LightingAdjustmentError { get; private set; }
    public bool LightingAdjustmentBusy { get; private set; }
    private LightingAdjustmentStore AdjustmentStore => new(Store.DirectoryPath);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, LightingAdjustmentState> groupAdjustments = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> groupAdjustmentErrors = new();
    public LightingAdjustmentState AdjustmentFor(LightingGroup? group) => group is null ? LightingAdjustment : groupAdjustments.GetValueOrDefault(group.Id) ?? new();
    public string? AdjustmentErrorFor(LightingGroup? group) => group is null ? LightingAdjustmentError : groupAdjustmentErrors.GetValueOrDefault(group.Id);
    public bool CanRestoreAdjustment(LightingGroup? group) => AdjustmentFor(group).BaseScene is { } scene && (group is null || LightingGroups.SameTargets(group, scene));
    private LightingAdjustmentStore AdjustmentStoreFor(string? id) => new(Store.DirectoryPath, id);
    private async Task LoadGroupAdjustmentAsync(string id)
    {
        try { groupAdjustments[id] = await AdjustmentStoreFor(id).LoadAsync(); groupAdjustmentErrors.TryRemove(id, out _); }
        catch (Exception e) { groupAdjustments[id] = new(); groupAdjustmentErrors[id] = "明るさ設定を読み込めません: " + e.Message; }
    }
    private async Task StoreAdjustmentAsync(string? id, LightingAdjustmentState value)
    {
        await AdjustmentStoreFor(id).SaveAsync(value);
        if (id is null) LightingAdjustment = value; else groupAdjustments[id] = value;
    }
    private async Task InvalidateAdjustmentBaselineAsync(LightingScene? changed = null, string? exceptKey = null)
    {
        if (exceptKey != "all" && LightingAdjustment is { Applied: true, BaseScene: { } all } && (changed is null || LightingGroups.Overlaps(changed, all)))
            await StoreAdjustmentAsync(null, LightingAdjustment with { Applied = false });
        foreach (var (id, value) in groupAdjustments.ToArray())
            if (id != exceptKey && value is { Applied: true, BaseScene: { } previous } && (changed is null || LightingGroups.Overlaps(changed, previous)))
                await StoreAdjustmentAsync(id, value with { Applied = false });
    }

    private async Task LoadLightingAdjustmentAsync()
    {
        try { LightingAdjustment = await AdjustmentStore.LoadAsync(); LightingAdjustmentError = null; }
        catch (Exception e) { LightingAdjustment = new(); LightingAdjustmentError = "明るさ設定を読み込めません: " + e.Message; }
        // Only restoration data is loaded. Startup never changes lighting.
    }
    public async Task<SceneApplyResult> AdjustLightingAsync(int? brightness = null, bool off = false, bool restore = false, LightingGroup? group = null)
    {
        await lightingSceneGate.WaitAsync();
        try { return await AdjustLightingCoreAsync(brightness, off, restore, group); }
        finally { lightingSceneGate.Release(); }
    }
    private async Task<SceneApplyResult> AdjustLightingCoreAsync(int? brightness = null, bool off = false, bool restore = false, LightingGroup? group = null)
    {
        LightingAdjustmentBusy = true;
        try
        {
            group = ResolveLightingGroup(group); CheckGroupSyncScope(group);
            if (AdjustmentErrorFor(group) is { } error) throw new IOException(error);
            if (brightness is < 1 or > 100 || (restore && (off || brightness is not null))) throw new ArgumentException("照明操作が不正です");
            var previous = AdjustmentFor(group);
            var capturedAt = Coordinator.Snapshot;
            LightingScene original; int level;
            if (restore)
            {
                original = previous.BaseScene ?? throw new InvalidOperationException("復帰する照明設定がありません"); level = previous.Brightness;
                if (group is not null && !LightingGroups.SameTargets(group, original)) throw new IOException("グループの対象が変わっています。現在の照明を適用してから操作してください");
            }
            else
            {
                var settings = Coordinator.Settings;
                var includeHub = settings.Hardware.Hub is not null && settings.Hardware.Leds is { Length: > 0 };
                var includeBoard = Rgb.Devices.Any(d => d.ControlIdentity is not null) || settings.Motherboard.RgbZones?.Any(z => z.LastLighting is not null) == true;
                var current = await CaptureLightingSceneCoreAsync(Guid.NewGuid().ToString("N"), "明るさ調整前", includeHub, includeBoard, group);
                var same = previous.Applied && previous.BaseScene is not null &&
                    LightingBrightness.SameOutput(current, LightingBrightness.Build(previous.BaseScene, previous.Brightness, previous.IsOff));
                original = same ? previous.BaseScene! : current;
                level = brightness ?? (same ? previous.Brightness : 100);
            }
            var target = LightingBrightness.Build(original, level, off);
            CheckSceneTargets(target); CheckSceneSyncScope(target);
            // Persist original colors before touching hardware, including when turning automatic rainbow effects off.
            var pending = new LightingAdjustmentState(BaseScene: original, Brightness: level, IsOff: off);
            await StoreAdjustmentAsync(group?.Id, pending);
            await InvalidateAdjustmentBaselineAsync(target, group?.Id ?? "all");
            if (Coordinator.Snapshot.LightingRevision != capturedAt.LightingRevision || Coordinator.Snapshot.DeviceGeneration != capturedAt.DeviceGeneration)
                throw new IOException("別の照明操作または接続変更により適用を停止しました");
            var result = await ApplyLightingSceneCoreAsync(target);
            var completed = pending with { Applied = result.Succeeded };
            await StoreAdjustmentAsync(group?.Id, completed);
            return result;
        }
        finally { LightingAdjustmentBusy = false; }
    }
}
