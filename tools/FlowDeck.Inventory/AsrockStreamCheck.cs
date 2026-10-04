using System.Diagnostics;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

internal static class AsrockStreamCheck
{
    public static async Task RunAsync(string output)
    {
        foreach (var name in new[] { "FlowDeck", "NzxtControl" })
        {
            var apps = Process.GetProcessesByName(name);
            try { if (apps.Length > 0) throw new IOException("FlowDeckの通常終了が必要です"); }
            finally { foreach (var p in apps) p.Dispose(); }
        }
        var path = Path.GetFullPath(output); var recovery = path + ".recovery";
        if (File.Exists(path) || Directory.Exists(recovery)) throw new IOException("Use a fresh test output path");
        var monitor = new AsrockRgbMonitor(); var before = (await monitor.ReadAsync()).Devices.Single();
        var all = before.Zones.Where(z => z.HardwareZone is not null && z.LedCount > 0).ToArray();
        if (all.Any(z => z.Configuration?.Mode is not (0 or 1))) throw new IOException("部分対象の試験には元の照明が固定色または消灯である必要があります");
        var reports = new List<object>(); string? error = null; bool restored = false;
        var json = new JsonSerializerOptions(Contract.Json) { WriteIndented = true };
        await File.WriteAllTextAsync(path + ".before.json", JsonSerializer.Serialize(before, json));
        try
        {
            foreach (var subset in new[] { false, true })
            {
                var current = (await monitor.ReadAsync()).Devices.Single();
                var targets = current.Zones.Where(z => z.LedCount > 0 && (!subset || z.HardwareZone == 5)).ToArray();
                if (targets.Length == 0) throw new IOException("試験対象のIO照明が見つかりません");
                var next = subset ? new RgbColor(30, 5, 10) : new RgbColor(10, 30, 80);
                await using (var stream = await AsrockRgbStream.OpenAsync(before.ControlIdentity!, targets, true, recovery))
                {
                    await stream.WriteAsync(new(5, 15, 40)); await Task.Delay(200); await stream.WriteAsync(next);
                    await stream.FinishAsync(true);
                    if (stream.RecoveryError is { } problem) throw new IOException(problem);
                }
                var after = (await monitor.ReadAsync()).Devices.Single();
                var selected = targets.Select(z => z.HardwareZone!.Value).ToHashSet();
                var expected = current.Zones.ToDictionary(z => z.HardwareZone!.Value, z => selected.Contains(z.HardwareZone!.Value)
                    ? new BoardLightSetting(1, next.R, next.G, next.B) : z.Configuration!);
                var matched = after.Zones.All(z => z.Configuration == expected[z.HardwareZone!.Value]);
                var cleared = !new AsrockRgbRecoveryStore(recovery).Exists;
                reports.Add(new { subset, selected, matched, recoveryCleared = cleared, after });
                if (!matched || !cleared) throw new IOException("Direct停止後の対象別設定が一致しません");
            }
        }
        catch (Exception e) { error = e.ToString(); }
        finally
        {
            try
            {
                await AsrockRgbRecovery.RecoverAsync(recovery);
                var current = (await monitor.ReadAsync()).Devices.Single();
                var result = await new AsrockRgbControl().ApplyAsync(before.ControlIdentity!, all.Select(z =>
                    new BoardLightChange(z.HardwareZone!.Value, current.Zones.Single(c => c.HardwareZone == z.HardwareZone).Configuration!, z.Configuration!)).ToArray());
                restored = result.Verified;
            }
            catch (Exception e) { error = (error ?? "") + " restore: " + e; }
            var passed = error is null && restored && reports.Count == 2;
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, passed, restored, reports, error }, json));
            Console.WriteLine($"passed={passed}, originalRestored={restored}, checks={reports.Count}");
            if (!passed) Environment.ExitCode = 1;
        }
    }
}
