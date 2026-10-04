using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class RuntimeHost
{
    private readonly SemaphoreSlim lightingSceneGate = new(1);
    private LightingAnimator? lightingAnimator;
    private Task lightingCleanup = Task.CompletedTask;
    public bool LightingSynchronized => lightingAnimator?.Running == true;
    private bool dismissSyncError;
    private string? lightingRecoveryError;
    public string? LightingSyncError => dismissSyncError ? null : lightingRecoveryError ?? lightingAnimator?.Error;
    public string LightingSyncMode { get; private set; } = "";
    public SceneSyncLight? ActiveLightingSync { get; private set; }
    public void DismissLightingSyncError() => dismissSyncError = true;
    public int LightingFrames => lightingAnimator?.Frames ?? 0;
    private async Task<IDisposable> LockRgbEditAsync(Action? beforeStop = null)
    {
        await lightingSceneGate.WaitAsync();
        try { beforeStop?.Invoke(); await StopLightingSyncCoreAsync(); await rgbGate.WaitAsync(); return new RgbEditLease(rgbGate, lightingSceneGate); }
        catch { lightingSceneGate.Release(); throw; }
    }
    private sealed class RgbEditLease(SemaphoreSlim rgb, SemaphoreSlim scene) : IDisposable
    {
        public void Dispose() { rgb.Release(); scene.Release(); }
    }
    public async Task StartLightingSyncAsync(string[] channels, string mode, RgbColor color, int speed, BoardLightAddress[]? boardTargets = null, LightingGroup? group = null)
    {
        await lightingSceneGate.WaitAsync();
        try
        {
            group = ResolveLightingGroup(group); CheckGroupSyncScope(group);
            boardTargets ??= Rgb.Devices.Where(d => d.ControlIdentity is not null && (d.IsAsrock || d.IsMemory))
                .SelectMany(d => d.Zones.Where(z => z.HardwareZone is not null && z.LedCount > 0).Select(z => new BoardLightAddress(d.ControlIdentity!,z.HardwareZone!.Value))).ToArray();
            var memory = boardTargets.Where(b => MemoryLighting.IsIdentity(b.Identity)).Select(b => b.Identity).ToArray();
            var native = boardTargets.Where(b => !MemoryLighting.IsIdentity(b.Identity)).ToArray();
            var device = Rgb.Devices.SingleOrDefault(d => d.IsAsrock && native.Any(t => t.Identity == d.ControlIdentity));
            if (boardTargets.Length == 0 || native.Any(b => b.Identity != device?.ControlIdentity) || boardTargets.Distinct().Count() != boardTargets.Length ||
                boardTargets.Any(b => MemoryLighting.IsIdentity(b.Identity) && b.Zone != 0)) throw new IOException("同期する照明を選択してください");
            var identity = channels.Length == 0 ? "" : Coordinator.Settings.Hardware.Hub?.Id ?? throw new IOException("NZXTが未設定です");
            var sync = new SceneSyncLight(identity, channels.ToArray(), device?.ControlIdentity ?? "", native.Select(b => b.Zone).ToArray(), mode, color, speed, memory.Length == 0 ? null : memory);
            var scene = new LightingScene(Guid.NewGuid().ToString("N"), "同期", [], [], sync);
            if (LightingScenes.Validate(scene) is { } invalid) throw new ArgumentException(invalid);
            CheckSceneTargets(scene); CheckSceneSyncScope(scene);
            if (group is not null && !LightingGroups.Contains(group, sync)) throw new IOException("同期対象がグループに含まれていません");
            await SaveSyncTargetsCoreAsync(boardTargets); await InvalidateAdjustmentBaselineAsync(scene);
            await StartLightingSyncCoreAsync(channels, mode, color, speed, sync.BoardIdentity, sync.Zones, memoryIdentities: sync.Memory);
        }
        finally { lightingSceneGate.Release(); }
    }
    private async Task StartLightingSyncCoreAsync(string[] channels, string mode, RgbColor color, int speed, string? boardIdentity = null, byte[]? boardZones = null, long? expectedRevision = null, long? expectedGeneration = null, string[]? memoryIdentities = null)
    {
            await StopLightingSyncCoreAsync();
            if (Coordinator.Settings.Motherboard.OpenRgbEnabled && !Demo && boardZones is not { Length: 0 }) throw new InvalidOperationException("マザー照明をUSB接続に切り替えてください");
            await rgbGate.WaitAsync();
            AsrockRgbStream? stream = null;
            MemorySyncConnection? memory = null;
            var handedOff = false;
            try
            {
                if (!Demo)
                {
                    if (await AsrockRgbRecovery.RecoverAsync(Store.DirectoryPath) is { } recovered) Log.Write("boardLightingRecovery", new { message = recovered });
                    Rgb = await ReadNativeRgbAsync();
                }
                var device = boardZones is { Length: 0 } ? null : Rgb.Devices.Single(d => d.ControlIdentity is not null && d.IsAsrock && (boardIdentity is null || d.ControlIdentity == boardIdentity));
                var zones = device?.Zones.Where(z => z.HardwareZone is not null && z.LedCount > 0 && (boardZones is null || boardZones.Contains(z.HardwareZone.Value))).ToArray() ?? [];
                if (zones.Length + (memoryIdentities?.Length ?? 0) == 0) throw new InvalidOperationException("同期できる照明がありません");
                if (boardZones is not null && zones.Length != boardZones.Length) throw new InvalidOperationException("同期するマザー照明が見つかりません");
                if (!Demo && memoryIdentities is { Length: > 0 }) memory = await OpenMemorySyncAsync(memoryIdentities);
                if (!Demo && device is not null) stream = await AsrockRgbStream.OpenAsync(device.ControlIdentity!, zones, mode is "breathing" or "color-cycle", Store.DirectoryPath);
                var writer = stream;
                var memoryWriter = memory;
                async Task Write(RgbColor value)
                {
                    if (!Demo) { await Task.WhenAll(writer?.WriteAsync(value) ?? Task.CompletedTask,memoryWriter?.Write(value) ?? Task.CompletedTask); return; }
            Rgb = Rgb with { Devices = Rgb.Devices.Select(d => d.ControlIdentity == device?.ControlIdentity || memoryIdentities?.Contains(d.ControlIdentity!) == true ? d with { Zones = d.Zones.Select(z => d.IsMemory || zones.Any(selected => selected.HardwareZone == z.HardwareZone) ?
                        z with { Configuration = new(1, value.R, value.G, value.B), ReportedMode = "Static" } : z).ToArray() } : d).ToArray(), UpdatedAt = DateTimeOffset.UtcNow };
                }
                var current = Coordinator.Snapshot;
                if (channels.Length > 0 && ((expectedRevision is not null && current.LightingRevision != expectedRevision) || (expectedGeneration is not null && current.DeviceGeneration != expectedGeneration)))
                    throw new IOException("別の照明操作または接続変更により同期の開始を停止しました");
                var owner = Coordinator; var ownerEpoch = owner.Epoch;
                var animator = channels.Length > 0 ? new LightingAnimator(owner, channels, mode, color, speed, Write) :
                    new LightingAnimator(new LightingFrameTarget(Write, () => ReferenceEquals(owner, Coordinator) && owner.Epoch == ownerEpoch &&
                        (owner is not RemoteControlCoordinator remote || remote.Connected)), mode, color, speed);
                lightingAnimator = animator; LightingSyncMode = mode; dismissSyncError = false; lightingRecoveryError = null;
                ActiveLightingSync = new(channels.Length == 0 ? "" : Coordinator.Settings.Hardware.Hub!.Id, channels.ToArray(), device?.ControlIdentity ?? "", zones.Select(z => z.HardwareZone!.Value).ToArray(), mode, color, speed, memoryIdentities);
                lightingCleanup = CleanupLightingAsync(animator, stream, memory);
                // Cleanup now owns the stream and gate, including startup failures.
                handedOff = true;
                await animator.Started;
                Log.Write("lightingSyncStarted", new { mode, color, speed, channels, zones = zones.Select(z => z.HardwareZone), memoryIdentities, demo = Demo });
            }
            catch
            {
                if (!handedOff) { try { if (stream is not null) await stream.DisposeAsync(); } finally { try { if (memory is not null) await memory.End(false); } finally { rgbGate.Release(); } } }
                else await lightingCleanup;
                throw;
            }
    }
    private async Task CleanupLightingAsync(LightingAnimator animator, AsrockRgbStream? stream, MemorySyncConnection? memory)
    {
        try
        {
            await animator.Completion;
            if (stream is not null)
            {
                await stream.FinishAsync(animator.Error is null);
                lightingRecoveryError = stream.RecoveryError;
            }
            if (memory is not null)
                try { await memory.End(animator.Error is null); }
                catch (Exception e) { lightingRecoveryError = string.Join(" / ",new[] { lightingRecoveryError,e.Message }.Where(s => s is not null)); }
            if (!Demo)
            {
                try { Rgb = lightingRecoveryError is null ? await ReadNativeRgbAsync() : new([], Error: lightingRecoveryError); }
                catch (Exception e) { Rgb = new([], Error: e.Message); }
            }
            Log.Write("lightingSyncStopped", new { animator.Frames, animator.Skipped, animator.MaximumWriteMilliseconds, animator.Error, recoveryError = lightingRecoveryError });
        }
        finally { rgbGate.Release(); }
    }
    private async Task StopLightingSyncCoreAsync()
    {
        if (lightingAnimator is not null) await lightingAnimator.StopAsync();
        await lightingCleanup;
    }
    public async Task StopLightingSyncAsync()
    {
        await lightingSceneGate.WaitAsync();
        try
        {
            if (LightingSynchronized && ActiveLightingSync is { } active)
                await InvalidateAdjustmentBaselineAsync(new(Guid.NewGuid().ToString("N"), "同期停止", [], [], active));
            await StopLightingSyncCoreAsync();
        }
        finally { lightingSceneGate.Release(); }
    }
}
