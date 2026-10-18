using System.Diagnostics;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

internal static class AsrockAnimationCheck
{
    // Explicit, bounded visual diagnostic. This utility has no Hub, fan or service connection.
    // Holds the HID mutex on a single thread until native settings and the exact order mask are restored.
    public static void Run(string mode, string output)
    {
        if (mode is not ("direct" or "native-no-swap")) throw new ArgumentException("Unsupported probe mode");
        foreach (var name in new[] { "FlowDeck", "NzxtControl" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length != 0) throw new IOException("トレイからFlowDeckを通常終了してください"); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        var path = Path.GetFullPath(output);
        if (File.Exists(path) || File.Exists(path + ".before.json")) throw new IOException("Use a new diagnostic output path");
        using var session = new AsrockRgbSession();
        var transaction = new AsrockVolatileTransaction(session.Identity, session.Exchange);
        var before = transaction.Snapshot;
        var json = new JsonSerializerOptions(Contract.Json) { WriteIndented = true };
        var frames = 0; double maximumWriteMilliseconds = 0; string? error = null;
        try
        {
            transaction.Begin(snapshot =>
            {
                using var backup = new FileStream(path + ".before.json", FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
                JsonSerializer.Serialize(backup, snapshot, json); backup.Flush(true);
            }, mode == "direct");
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(12))
            {
                if (frames % 10 == 0) AsrockRgbMonitor.CheckConflicts();
                var started = timer.Elapsed.TotalMilliseconds;
                var color = HubLighting.Frame("breathing", new(52, 137, 255), timer.Elapsed.TotalSeconds, 4);
                if (mode == "direct")
                    foreach (var packet in AsrockDirectProtocol.Frame(before.Zones.Select(z => new AsrockDirectProtocol.Zone(z.Address, z.Count, color)).ToArray(), AsrockDirectProtocol.StreamingOrder))
                        _ = session.Exchange(packet);
                else foreach (var zone in before.Zones)
                    _ = session.Exchange(AsrockRgbControl.Packet(zone.Address, new(1, color.R, color.G, color.B), 0));
                maximumWriteMilliseconds = Math.Max(maximumWriteMilliseconds, timer.Elapsed.TotalMilliseconds - started);
                frames++;
                // Absolute frame deadlines: a slow exchange cannot create a backlog or an unbounded run.
                var wait = frames * 100 - timer.Elapsed.TotalMilliseconds;
                if (wait > 0) Thread.Sleep((int)Math.Min(100, wait));
                transaction.CheckFrameOwnership();
            }
        }
        catch (Exception e) { error = e.ToString(); }
        finally
        {
            var restored = transaction.Restore();
            var passed = error is null && restored.Restored && frames >= 90;
            File.WriteAllText(path, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, mode, frames, maximumWriteMilliseconds,
                passed, visualResult = "Awaiting user observation", error, before.Order, restored }, json));
            Console.WriteLine($"frames={frames}, maxWriteMs={maximumWriteMilliseconds:F1}, restored={restored.Restored}, passed={passed}");
            if (!passed) Environment.ExitCode = 1;
        }
    }
}
