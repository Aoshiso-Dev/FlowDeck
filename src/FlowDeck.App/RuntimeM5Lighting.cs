using System.Security.Cryptography;
using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.App;

public sealed partial class RuntimeHost : IM5LightingBackend
{
    private long lightingMutation;
    private long m5LightingChanges;
    public long M5LightingChanges => Interlocked.Read(ref m5LightingChanges);
    private string LightingStamp()
    {
        var s = Coordinator.Snapshot;
        // Animation frames and telemetry are excluded so an open edit remains usable.
        // Board-only writes, failed writes, library edits and connection changes still invalidate it.
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            s.ControllerEpoch, s.LightingRevision, s.DeviceGeneration, mutation = Interlocked.Read(ref lightingMutation),
            SceneLibrary, SceneLibraryError, GroupLibrary, GroupLibraryError, LightingSynchronized,
            Coordinator.Settings.Hardware, Coordinator.Settings.Motherboard.OpenRgbEnabled,
            rgb = Rgb.Devices.Select(d => new { d.ControlIdentity, zones = d.Zones.Select(z => new
                { z.HardwareZone, z.LedCount, setting = LightingSynchronized ? null : z.Configuration }) }), Rgb.Error
        }, Contract.Json)));
    }
    private LightingGroup? M5Group(string id)
    {
        if (!M5LightingCommand.ValidScope(id)) throw new ArgumentException("照明の対象が不正です");
        return ResolveLightingGroup(id == "all" ? null : GroupLibrary.Groups?.SingleOrDefault(g => g.Id == id)
            ?? throw new IOException("照明グループがありません。対象を選び直してください"));
    }
    public M5LightingView Read(string scopeId)
    {
        var locked = lightingSceneGate.Wait(0);
        try
        {
            var busy = !locked || Coordinator.Snapshot.LightingBusy;
            var groups = new[] { new M5LightingItem("all", "すべての照明") }.Concat((GroupLibrary.Groups ?? []).Select(g => new M5LightingItem(g.Id, g.Name))).ToArray();
            var scenes = (SceneLibrary.Scenes ?? []).Select(s => new M5LightingItem(s.Id, s.Name)).ToArray();
            LightingGroup? group = null; string? error = null;
            try
            {
                group = M5Group(scopeId);
                if (!busy) CheckGroupSyncScope(group);
                if (SceneLibraryError is not null) throw new IOException(SceneLibraryError);
            }
            catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException) { error = e.Message; }
            var value = AdjustmentFor(group);
            return new(LightingStamp(), scopeId, scopeId == "all" ? "すべての照明" : group?.Name ?? "未検出のグループ",
                !busy && error is null, busy, error, value.Brightness, value.IsOff, error is null && CanRestoreAdjustment(group),
                LightingSynchronized, groups, scenes);
        }
        finally { if (locked) lightingSceneGate.Release(); }
    }
    public async Task<OperationResult> ExecuteAsync(M5LightingCommand request, CancellationToken cancellationToken)
    {
        await lightingSceneGate.WaitAsync(cancellationToken);
        var dispatched = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!request.Valid) return M5Result(request, Outcome.Rejected, "照明操作が不正です", "INVALID_REQUEST");
            if (request.ExpectedStamp != LightingStamp())
                return M5Result(request, Outcome.Rejected, "照明設定が変更されました。画面を開き直してください", "STALE_LIGHTING");
            var group = M5Group(request.ScopeId); CheckGroupSyncScope(group);
            dispatched = true;
            SceneApplyResult result;
            switch (request.Command)
            {
                case "ApplyLightingScene":
                    if (SceneLibraryError is not null) throw new IOException(SceneLibraryError);
                    var scene = SceneLibrary.Scenes?.SingleOrDefault(s => s.Id == request.SceneId)
                        ?? throw new IOException("シーンがありません。選び直してください");
                    result = await ApplyLightingSceneCoreAsync(LightingGroups.Select(scene, group), invalidateBrightness: true);
                    break;
                case "SetLightingBrightness": result = await AdjustLightingCoreAsync(brightness: request.Brightness, group: group); break;
                case "LightingOff": result = await AdjustLightingCoreAsync(off: true, group: group); break;
                case "RestoreLighting": result = await AdjustLightingCoreAsync(restore: true, group: group); break;
                case "StopLightingSync":
                    if (LightingSynchronized && ActiveLightingSync is { } active)
                        await InvalidateAdjustmentBaselineAsync(new(Guid.NewGuid().ToString("N"), "同期停止", [], [], active));
                    await StopLightingSyncCoreAsync();
                    result = new(true, [new("同期", "停止")]); break;
                default: throw new ArgumentException("未対応の照明操作です");
            }
            Interlocked.Increment(ref lightingMutation);
            Log.Write("m5Lighting", new { request.Id, request.Command, request.ScopeId, result });
            // Hub RGB cannot be read back. Report transmission, never verified optical output.
            return M5Result(request, result.Succeeded ? Outcome.WrittenUnverified : Outcome.Partial,
                result.Succeeded ? "照明設定を送信しました" : result.Error ?? "照明の一部を確認できません", result.Succeeded ? null : "LIGHTING_INCOMPLETE");
        }
        catch (OperationCanceledException) { return M5Result(request, Outcome.Rejected, "接続が変更されました", "SESSION_CHANGED"); }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        { return M5Result(request, dispatched ? Outcome.Partial : Outcome.Rejected, e.Message, dispatched ? "LIGHTING_INCOMPLETE" : "LIGHTING_REJECTED"); }
        finally { if (dispatched) Interlocked.Increment(ref m5LightingChanges); lightingSceneGate.Release(); }
    }
    private OperationResult M5Result(M5LightingCommand request, Outcome outcome, string message, string? error)
    {
        var s = Coordinator.Snapshot;
        return new(request.Id, "M5GO", request.Command, outcome, message, DateTimeOffset.UtcNow,
            s.ControlRevision, s.LightingRevision, s.DeviceGeneration, ErrorCode: error);
    }
}
