using System.Diagnostics;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

internal static class AsrockColorOrderCheck
{
    // Compare the native color with both Direct encodings before changing production behavior.
    // The named HID mutex, durable journal, and finally restore cover the whole bounded probe.
    public static void Run(string output)
    {
        foreach (var name in new[] { "FlowDeck", "NzxtControl" })
        {
            var apps = Process.GetProcessesByName(name);
            try { if (apps.Length != 0) throw new IOException("FlowDeckを通常終了してください"); }
            finally { foreach (var app in apps) app.Dispose(); }
        }
        var path = Path.GetFullPath(output);
        var directory = path + ".recovery";
        if (File.Exists(path) || Directory.Exists(directory)) throw new IOException("Use a fresh diagnostic output path");
        using var session = new AsrockRgbSession();
        var baseline = new AsrockVolatileTransaction(session.Identity, session.Exchange).Snapshot;
        var journal = new AsrockRgbRecoveryStore(directory);
        var boot = AsrockRgbRecovery.BootIdentity();
        var phases = new List<object>();
        string? error = null;
        var restored = true;
        try
        {
            foreach (var color in new[] { new RgbColor(180, 0, 0) })
            {
                var transaction = new AsrockVolatileTransaction(session.Identity, session.Exchange);
                var timer = Stopwatch.StartNew();
                try
                {
                    transaction.Begin(snapshot => journal.Save(boot, snapshot), false);
                    // Restore the original order before the native reference, with readback.
                    _ = session.Exchange(AsrockDirectProtocol.ColorOrder(baseline.Order));
                    var order = AsrockRgbMonitor.Response(session.Exchange(AsrockRgbMonitor.Query(0x14, 3)), 65, 3)[4];
                    if (order != baseline.Order) throw new IOException("Native reference order mismatch");
                    foreach (var zone in baseline.Zones)
                        _ = session.Exchange(AsrockRgbControl.Packet(zone.Address, new(1, color.R, color.G, color.B), order));
                    Show("native", order);
                    _ = session.Exchange(AsrockDirectProtocol.ColorOrder(0));
                    transaction.CheckFrameOwnership();
                    foreach (var zone in baseline.Zones) _ = session.Exchange(AsrockDirectProtocol.Enter(zone.Address));
                    foreach (var encodingOrder in new[] { baseline.Order, (byte)0 })
                    {
                        transaction.CheckFrameOwnership();
                        foreach (var packet in AsrockDirectProtocol.Frame(baseline.Zones.Select(z => new AsrockDirectProtocol.Zone(z.Address, z.Count, color)).ToArray(), encodingOrder))
                            _ = session.Exchange(packet);
                        Show(encodingOrder == baseline.Order ? "direct-original-order" : "direct-zero-order", encodingOrder);
                    }
                    void Show(string mode, byte encodingOrder)
                    {
                        AsrockRgbMonitor.CheckConflicts();
                        phases.Add(new { color, mode, encodingOrder, elapsedMilliseconds = timer.Elapsed.TotalMilliseconds });
                        Console.WriteLine($"{mode}: R={color.R}, G={color.G}, B={color.B}");
                        Thread.Sleep(3000);
                    }
                }
                finally
                {
                    var result = transaction.Restore();
                    restored &= result.Restored;
                    if (result.Restored) journal.Clear();
                }
                if (!restored) throw new IOException("Original lighting restoration was not confirmed");
            }
        }
        catch (Exception e) { error = e.ToString(); }
        finally
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, baseline, phases,
                restored, error, visualResult = "Awaiting user observation" }, new JsonSerializerOptions(Contract.Json) { WriteIndented = true }));
            Console.WriteLine($"restored={restored}, error={error}");
            if (!restored || error is not null) Environment.ExitCode = 1;
        }
    }
}
