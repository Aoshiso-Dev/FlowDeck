using System.Diagnostics;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

internal static class AllFanReadCheck
{
    public static async Task RunAsync(string output)
    {
        var readings = new List<object>();
        var errors = new List<string>();
        var watch = Stopwatch.StartNew();
        try
        {
            await using var hardware = new HardwareTemperatures();
            for (var sample = 0; sample < 3; sample++)
            {
                await hardware.ReadAsync();
                foreach (var port in MotherboardFans.Detected(hardware.Board))
                    try { readings.Add(new { sample, port.Id, port.SensorId, reading = await hardware.FanDriver(port.Id).ReadAsync() }); }
                    catch (Exception error) { errors.Add(port.Id + ": " + error.Message); }
                await Task.Delay(300);
            }
            if (readings.Count == 0) errors.Add("No supported fan sensors detected");
        }
        catch (Exception error) { errors.Add(error.ToString()); }
        await File.WriteAllTextAsync(Path.GetFullPath(output), JsonSerializer.Serialize(new { success = errors.Count == 0,
            utc = DateTimeOffset.UtcNow, elapsedMs = watch.ElapsedMilliseconds, controlWrites = 0, readings, errors }, new JsonSerializerOptions(Contract.Json) { WriteIndented = true }));
        Environment.ExitCode = errors.Count == 0 ? 0 : 1;
    }
}
