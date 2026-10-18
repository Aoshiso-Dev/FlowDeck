using System.Diagnostics;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

internal static class LightingSyncCheck
{
    // Explicit developer check; never starts cooling, edits settings, or requests elevation.
    public static async Task RunAsync(string python, string worker, string settingsFile, string output)
    {
        foreach (var name in new[] { "FlowDeck", "NzxtControl" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length != 0) throw new IOException("通常終了したアプリのUSB解放を待ってください"); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        var settings = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(settingsFile), Contract.Json)
            ?? throw new IOException("設定を読み取れません");
        var identity = settings.Hardware.Hub ?? throw new IOException("確認済みHubがありません");
        var channels = settings.Hardware.Leds ?? throw new IOException("確認済み照明配線がありません");
        if (!settings.Hardware.WiringConfirmed || channels.Length == 0) throw new IOException("確認済み配線が必要です");
        var restoreLight = settings.Lighting.Effect ?? (settings.Lighting.LastSent is "blue" or "white" or "purple" or "off" ?
            HubLighting.Preset(settings.Lighting.LastSent) : throw new IOException("復元する最終送信色が不明です"));
        if (!HubLighting.Valid(restoreLight)) throw new IOException("復元するNZXT効果が不正です");
        // One saved last-write can only restore a whole-Hub state; refuse ambiguous older per-port edits.
        if (settings.Lighting.Channels is { Length: > 0 } saved && !saved.Order().SequenceEqual(channels.Order()))
            throw new IOException("個別ポートの以前の色が不明なため試験しません");
        var monitor = new AsrockRgbMonitor();
        var before = await monitor.ReadAsync(); var board = before.Devices.Single();
        var zones = board.Zones.Where(z => z.HardwareZone is not null && z.LedCount > 0).ToArray();
        if (zones.Length == 0 || zones.Any(z => z.Configuration is null || !BoardLighting.IsValid(z.Configuration)))
            throw new IOException("復元可能なマザー照明設定が必要です");
        var path = Path.GetFullPath(output);
        await File.WriteAllTextAsync(path + ".before.json", JsonSerializer.Serialize(new { before, settings.Lighting, channels }, Contract.Json));
        var log = new CheckLog();
        var hub = new LightingOnlyHub(new HubWorkerClient(Path.GetFullPath(python), Path.GetFullPath(worker), log));
        await using var core = new ControlCoordinator(hub, new NoTemperatures(), settings, log);
        if (!(await core.ProbeAsync()).Any(d => d.Id == identity.Id)) throw new IOException("確認済みHubが見つかりません");
        await core.OpenAsync(identity);
        if (core.Snapshot.Faulted) throw new IOException(core.Snapshot.Fault);
        var fansBefore = core.LastStatus.Channels;
        var checks = new List<object>();
        string? error = null; HubResult? hubRestored = null; BoardLightApplyResult? boardRestored = null;
        RgbInventory? after = null; FanChannel[]? fansAfter = null;
        try
        {
            core.Start(); // monitoring only; ResumeControl/SetFanGroup is never called.
            foreach (var value in new[] { new HubLightSetting("breathing", [new(52, 137, 255)], 3),
                new HubLightSetting("super-rainbow", [], 2, true) })
            {
                var result = await core.RequestAsync(new(Guid.NewGuid().ToString("N"), "lighting-check", core.Epoch,
                    core.Snapshot.LightingRevision, "SetLighting", LightingEffect: value, LightingChannels: channels));
                checks.Add(new { native = value.Mode, result });
                if (result.Outcome != Outcome.WrittenUnverified) throw new IOException(result.Message);
                await Task.Delay(500);
            }
            foreach (var mode in new[] { "breathing", "color-cycle" })
            {
                var current = (await monitor.ReadAsync()).Devices.Single();
                await using var stream = await AsrockRgbStream.OpenAsync(board.ControlIdentity!, current.Zones.Where(z => z.HardwareZone is not null && z.LedCount > 0).ToArray(), true, Path.GetDirectoryName(path)!);
                await using var animator = new LightingAnimator(core, channels, mode, new(52, 137, 255), 4, stream.WriteAsync);
                await animator.Started;
                await Task.Delay(3500);
                await animator.StopAsync();
                await stream.FinishAsync(animator.Error is null);
                if (stream.RecoveryError is { } recoveryError) throw new IOException(recoveryError);
                checks.Add(new { synchronized = mode, animator.Frames, animator.Skipped, animator.MaximumWriteMilliseconds, animator.Error });
                if (animator.Error is not null || animator.Frames < 15) throw new IOException(animator.Error ?? "同期送信が遅すぎます");
            }
        }
        catch (Exception e) { error = e.ToString(); }
        finally
        {
            // Restore both independently, even if one device or the animation failed.
            try
            {
                var result = await core.RequestAsync(new(Guid.NewGuid().ToString("N"), "lighting-restore", core.Epoch,
                    core.Snapshot.LightingRevision, "SetLighting", LightingEffect: restoreLight, LightingChannels: channels));
                hubRestored = new(result.Outcome, Error: result.Message);
            }
            catch (Exception e) { error = (error ?? "") + "\nNZXT restore: " + e; }
            try
            {
                var current = (await monitor.ReadAsync()).Devices.Single();
                boardRestored = await new AsrockRgbControl().ApplyAsync(board.ControlIdentity!, zones.Select(z =>
                    new BoardLightChange(z.HardwareZone!.Value, current.Zones.Single(c => c.HardwareZone == z.HardwareZone).Configuration!, z.Configuration!)).ToArray());
                after = await monitor.ReadAsync();
            }
            catch (Exception e) { error = (error ?? "") + "\nASRock restore: " + e; }
            await core.TickAsync(); fansAfter = core.LastStatus.Channels;
            var fanOutputsUnchanged = fansBefore.All(b => fansAfter.Any(a => a.Id == b.Id && a.Duty == b.Duty && a.ControlMode == b.ControlMode));
            var boardUnchanged = after?.Devices.SingleOrDefault()?.Zones.All(a => board.Zones.Any(b => a.HardwareZone == b.HardwareZone && a.Configuration == b.Configuration)) == true;
            var passed = error is null && hubRestored?.Outcome == Outcome.WrittenUnverified && boardRestored?.Verified == true && boardUnchanged && fanOutputsUnchanged && hub.FanWriteAttempts == 0;
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, passed, checks, error,
                fansBefore, fansAfter, fanOutputsUnchanged, fanWriteAttempts = hub.FanWriteAttempts, hubRestored, boardRestored, boardUnchanged, before, after }, new JsonSerializerOptions(Contract.Json) { WriteIndented = true }));
            Console.WriteLine($"passed={passed}, checks={checks.Count}, fanOutputsUnchanged={fanOutputsUnchanged}, boardRestored={boardUnchanged}");
            if (!passed) Environment.ExitCode = 1;
        }
    }
    private sealed class NoTemperatures : ITemperatureSource
    {
        public Task<SensorReading[]> ReadAsync(CancellationToken ct = default) => Task.FromResult<SensorReading[]>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class CheckLog : IEventLog
    {
        public void Write(string category, object data) { }
    }
    private sealed class LightingOnlyHub(IHubAdapter inner) : IHubAdapter
    {
        public int FanWriteAttempts;
        public bool IsDemo => false;
        public string Version => inner.Version;
        public Task<HubIdentity[]> ProbeAsync(CancellationToken ct = default) => inner.ProbeAsync(ct);
        public Task<HubStatus> OpenAsync(HubIdentity identity, long generation, CancellationToken ct = default) => inner.OpenAsync(identity, generation, ct);
        public Task<HubStatus> ReadAsync(CancellationToken ct = default) => inner.ReadAsync(ct);
        public Task<HubResult> SetFanGroupAsync(string[] channels, int duty, CancellationToken ct = default)
        {
            Interlocked.Increment(ref FanWriteAttempts);
            throw new InvalidOperationException("照明試験からファン出力は変更できません");
        }
        public Task<HubResult> SetLightingAsync(string[] channels, string preset, CancellationToken ct = default) => inner.SetLightingAsync(channels, preset, ct);
        public Task<HubResult> SetLightingEffectAsync(string[] channels, HubLightSetting effect, CancellationToken ct = default) => inner.SetLightingEffectAsync(channels, effect, ct);
        public Task CloseAsync(CancellationToken ct = default) => inner.CloseAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
