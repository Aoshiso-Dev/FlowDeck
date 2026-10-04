using System.Diagnostics;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

internal static class CpuFanCheck
{
    public static async Task RunAsync(string output, bool test, string portId = "cpu-fan1")
    {
        var path = Path.GetFullPath(output);
        var events = new List<object>();
        try
        {
            if (Process.GetProcessesByName("FlowDeck").Length > 0 || Process.GetProcessesByName("NzxtControl").Length > 0) throw new InvalidOperationException("FlowDeckを通常終了してから診断を実行してください");
            await using var hardware = new HardwareTemperatures();
            for (var i = 0; i < 3; i++) { await hardware.ReadAsync(); await Task.Delay(300); }
            var before = hardware.Board;
            var port = MotherboardFans.Find(portId);
            ICpuFanDriver driver = hardware.FanDriver(port.Id);
            var reading = await driver.ReadAsync();
            CpuFanState? result = null;
            var samples = new List<BoardSnapshot>();
            if (test)
            {
                await using var owner = new CpuFanController(driver, () => [], new CaptureLog(events), name: port.Name);
                var running = owner.TestAsync();
                while (!running.IsCompleted)
                {
                    await hardware.ReadAsync(); samples.Add(hardware.Board); await Task.Delay(300);
                }
                await running; result = owner.State;
                if (result.Test?.Restored != true) throw new IOException("復元確認がありません");
            }
            await Task.Delay(1200);
            await hardware.ReadAsync();
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { success = true, test, utc = DateTimeOffset.UtcNow,
                port = port.Name, controlId = $"/lpc/nct6799d/0/control/{port.Channel}", rpmId = port.SensorId, reading, before, samples, result, after = hardware.Board, events },
                new JsonSerializerOptions(Contract.Json) { WriteIndented = true }));
            Console.WriteLine(port.Name + (test ? " test and restoration completed" : " read-only check completed"));
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { success = false, test, error = error.ToString(), events },
                new JsonSerializerOptions(Contract.Json) { WriteIndented = true }));
            Console.Error.WriteLine(error.Message); Environment.ExitCode = 1;
        }
    }
    private sealed class CaptureLog(List<object> events) : IEventLog { public void Write(string category, object data) => events.Add(new { category, data }); }
}
