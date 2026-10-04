using FlowDeck.Core;
using System.Diagnostics;

namespace FlowDeck.Infrastructure;

/// <summary>ASRock USB inventory using configuration queries only. Never initializes or changes RGB state.</summary>
public sealed class AsrockRgbMonitor
{
    public const int VendorId = 0x26ce, ProductId = 0x01a2;
    public static byte[] Query(byte operation, byte selector)
    {
        if (operation != 0x14 && operation != 0x11 || operation == 0x14 && selector is < 1 or > 3 || operation == 0x11 && selector > 7)
            throw new ArgumentOutOfRangeException(nameof(operation), "Only ASRock inventory queries are permitted");
        var report = new byte[65]; report[1] = operation; report[3] = selector; return report;
    }
    public static byte[] Response(byte[] report, int length, byte selector)
    {
        // HidSharp preserves the report ID; hidapi's Windows hid_read removes a zero report ID.
        if (length != 65 || report[0] != 0) throw new IOException("未対応のASRock HID応答です");
        var data = report.AsSpan(1, 64).ToArray();
        if (data[3] != selector) throw new IOException("ASRock HID応答が要求と一致しません。他のRGBアプリを終了してください");
        return data;
    }
    internal static void CheckConflicts() => CheckConflicts(null);
    internal static void CheckConflicts(uint? ownedOpenRgbPid)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try { name = process.ProcessName; } catch (InvalidOperationException) { continue; }
                if (name.Equals("OpenRGB", StringComparison.OrdinalIgnoreCase) && (uint)process.Id != ownedOpenRgbPid || name.Contains("Polychrome", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("ASRRGBLED", StringComparison.OrdinalIgnoreCase) || name.Equals("SignalRgb", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("他のRGBアプリが起動中です。直接取得前に終了するか、OpenRGB連携を選んでください");
            }
        }
    }
    public async Task<RgbInventory> ReadAsync(CancellationToken ct = default) => await Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var session = new AsrockRgbSession();
        byte[] Read(byte operation, byte selector)
        {
            ct.ThrowIfCancellationRequested();
            var buffer = session.Exchange(Query(operation, selector)); return Response(buffer, buffer.Length, selector);
        }
        var counts = Read(0x14, 2); var available = Read(0x14, 1); var order = Read(0x14, 3)[4];
        var zones = new List<RgbZone>();
        for (byte i = 0; i < 8; i++)
        {
            if ((available[4] & (1 << i)) == 0 || counts[4 + i] == 0x1e) continue;
            var configuration = Read(0x11, i);
            var name = i switch { 0 => "RGB Header 1", 1 => "RGB Header 2", 2 => "ARGB Header 1", 3 => "ARGB Header 2",
                4 => "Onboard PCH", 5 => "Onboard IO", 6 => "Onboard PCB", _ => "ARGB Header 3 / Audio" };
            zones.Add(new(name, 1, counts[4 + i], null, null, ModeName(configuration[4]), i, AsrockRgbControl.Decode(configuration, order)));
        }
        return new RgbInventory([new(0, "ASRock USB RGB", "ASRock", "USB設定照会 / 物理配置未確認", "未取得",
            "USB 26CE:01A2", BoardLighting.Effects.Select(e => e.Name).ToArray(), -1, zones.ToArray(), session.Identity)], DateTimeOffset.UtcNow);
    }, ct);
    public static string ModeName(byte value) => value switch
    {
        0 => "Off", 1 => "Static", 2 => "Breathing", 3 => "Strobe", 4 => "Spectrum Cycle", 5 => "Random",
        6 => "Music", 7 => "Wave", 8 => "Spring", 9 => "Stack", 10 => "Cram", 11 => "Scan", 12 => "Neon", 13 => "Water",
        14 => "Rainbow", 15 or 0xe2 or 0xe3 => "Direct", _ => $"未対応 (0x{value:X2})"
    };
}
