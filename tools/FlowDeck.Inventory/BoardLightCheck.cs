using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

internal static class BoardLightCheck
{
    // Explicit bounded developer check: one onboard zone, then restore its captured configuration.
    public static async Task RunAsync(string output)
    {
        var monitor = new AsrockRgbMonitor(); var control = new AsrockRgbControl();
        var before = await monitor.ReadAsync(); var device = before.Devices.Single();
        var zone = device.Zones.Single(z => z.HardwareZone == 5);
        var original = zone.Configuration!;
        if (!BoardLighting.IsValid(original)) throw new IOException("元の効果へ確実に復元できないため試験を開始しません");
        var path = Path.GetFullPath(output);
        await File.WriteAllTextAsync(path + ".before.json", JsonSerializer.Serialize(before, Contract.Json));
        var checks = new List<BoardLightApplyResult>(); var expected = original;
        BoardLightApplyResult? restored = null;
        try
        {
            foreach (var value in new[] { new BoardLightSetting(1, 52, 137, 255), new BoardLightSetting(2, 160, 121, 236, 128) })
            {
                var result = await control.ApplyAsync(device.ControlIdentity!, [new(5, expected, value)]); checks.Add(result);
                if (!result.Verified) throw new IOException("照明設定の読み戻しが一致しません");
                expected = value; await Task.Delay(500);
            }
        }
        finally
        {
            var current = (await monitor.ReadAsync()).Devices.Single();
            var actual = current.Zones.Single(z => z.HardwareZone == 5).Configuration!;
            restored = await control.ApplyAsync(device.ControlIdentity!, [new(5, actual, original)]);
            var after = await monitor.ReadAsync();
            var unchanged = after.Devices.Single().Zones.All(z => z.Configuration == device.Zones.Single(b => b.HardwareZone == z.HardwareZone).Configuration);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { before, checks, restored, after, unchanged }, new JsonSerializerOptions(Contract.Json) { WriteIndented = true }));
            Console.WriteLine($"checks={checks.Count}, restored={restored.Verified}, allZonesUnchanged={unchanged}");
            if (!restored.Verified || !unchanged) throw new IOException("照明の復元結果を確認してください");
        }
    }
}
