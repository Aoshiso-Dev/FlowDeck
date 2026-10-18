using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

if (args.Length > 0 && args[0] == "--probe-openrgb-helper")
{
    try
    {
        // This branch must precede hardware inventory even when its arguments are invalid.
        if (args is not [_, var runtime, var output])
            throw new ArgumentException("--probe-openrgb-helper <pinned-runtime-directory> <new-output-directory>");
        var receipt = await OpenRgbHeadlessProbe.RunAsync(Path.GetFullPath(runtime), Path.GetFullPath(output));
        Console.WriteLine(JsonSerializer.Serialize(receipt, Contract.Json));
    }
    catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode = 1; }
    return;
}

if (args.Length > 0 && args[0] is "--prepare-openrgb-runtime" or "--verify-openrgb-runtime" or "--prepare-openrgb-helper" or "--verify-openrgb-helper")
{
    try
    {
        // Dispatch before every hardware path. Malformed preparation arguments must never fall through to inventory.
        var receipt = args switch
        {
            ["--prepare-openrgb-runtime", var archive, var destination] => OpenRgbRuntimePackage.Pinned.Prepare(Path.GetFullPath(archive), Path.GetFullPath(destination)),
            ["--verify-openrgb-runtime", var directory] => OpenRgbRuntimePackage.Pinned.Inspect(Path.GetFullPath(directory)),
            ["--prepare-openrgb-helper", var archive, var destination] => OpenRgbRuntimePackage.FlowDeckPinned.Prepare(Path.GetFullPath(archive), Path.GetFullPath(destination)),
            ["--verify-openrgb-helper", var directory] => OpenRgbRuntimePackage.FlowDeckPinned.Inspect(Path.GetFullPath(directory)),
            _ => throw new ArgumentException("--prepare-openrgb-runtime <official.zip> <new-directory> / --verify-openrgb-runtime <directory> / --prepare-openrgb-helper <helper.zip> <new-directory> / --verify-openrgb-helper <directory>")
        };
        Console.WriteLine(JsonSerializer.Serialize(receipt, Contract.Json));
    }
    catch (Exception e) { Console.Error.WriteLine(e.Message); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 2 && args[0] == "--asrock-color-order-check")
{
    try { AsrockColorOrderCheck.Run(args[1]); }
    catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 2 && args[0] == "--memory-rgb-sync-check")
{
    try { await MemorySyncCheck.RunAsync(args[1]); }
    catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode=1; }
    return;
}

if (args.Length == 2 && args[0] == "--memory-rgb-native-read")
{
    try
    {
        var path = Path.GetFullPath(args[1]);
        if (File.Exists(path)) throw new IOException("Diagnostic output already exists");
        var inventory = await new EneMemoryRgbControl().ReadAsync();
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(inventory, new JsonSerializerOptions(Contract.Json) { WriteIndented = true }));
    }
    catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 3 && args[0] == "--memory-rgb-read")
{
    try { MemoryRgbCheck.Run(args[1], args[2]); }
    catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 2 && args[0] == "--asrock-stream-check")
{
    await AsrockStreamCheck.RunAsync(args[1]); return;
}

if (args.Length == 3 && args[0] == "--asrock-animation-check")
{
    AsrockAnimationCheck.Run(args[1], args[2]); return;
}

if (args.Length == 5 && args[0] == "--lighting-sync-check")
{
    await LightingSyncCheck.RunAsync(args[1], args[2], args[3], args[4]); return;
}

if (args.Length == 2 && args[0] == "--board-light-check")
{
    await BoardLightCheck.RunAsync(args[1]); return;
}

if (args.Length == 2 && args[0] == "--board-fans-read")
{
    await AllFanReadCheck.RunAsync(args[1]); return;
}

if (args.Length == 2 && args[0] is "--cha-fan-read" or "--cha-fan-test")
{
    await CpuFanCheck.RunAsync(args[1], args[0] == "--cha-fan-test", "cha-fan1"); return;
}

if (args.Length == 2 && args[0] is "--cpu-fan-read" or "--cpu-fan-test")
{
    await CpuFanCheck.RunAsync(args[1], args[0] == "--cpu-fan-test"); return;
}

if (args.Length == 3 && args[0] == "--openrgb")
{
    var inventory = await new OpenRgbMonitor().ReadAsync(int.Parse(args[1]));
    var json = JsonSerializer.Serialize(inventory, new JsonSerializerOptions(Contract.Json) { WriteIndented = true });
    await File.WriteAllTextAsync(Path.GetFullPath(args[2]), json); Console.WriteLine(json); return;
}

if (args.Length == 2 && args[0] == "--asrock-rgb")
{
    var inventory = await new AsrockRgbMonitor().ReadAsync();
    var json = JsonSerializer.Serialize(inventory, new JsonSerializerOptions(Contract.Json) { WriteIndented = true });
    await File.WriteAllTextAsync(Path.GetFullPath(args[1]), json); Console.WriteLine(json); return;
}

if (args.Length == 2 && args[0] is "--motherboard" or "--fan-counters")
{
    await MotherboardCheck.RunAsync(Path.GetFullPath(args[1]), args[0] == "--fan-counters");
    return;
}

if (args.Length != 0 && args is not ["--hub", _, _, _])
{
    Console.Error.WriteLine("Unknown or incomplete diagnostic command.");
    Environment.ExitCode = 2;
    return;
}

// Explicit developer utility. --hub uses an explicitly identified Hub and only initializes/reads it.
await using var temperatures = new HardwareTemperatures();
try
{
    var values = await temperatures.ReadAsync();
    var reports = new List<HubStatus>();
    HubIdentity? selected = null;
    if (args.Length == 4 && args[0] == "--hub")
    {
        await using var hub = new HubWorkerClient(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), new InventoryLog());
        var candidates = await hub.ProbeAsync();
        selected = candidates.Single(d => d.Id == args[3]);
        reports.Add(await hub.OpenAsync(selected, 1));
        for (var i = 0; i < 3; i++) { await Task.Delay(1000); reports.Add(await hub.ReadAsync()); }
        await hub.CloseAsync();
    }
    Console.WriteLine(JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, sensors = values, selected, reports }, new JsonSerializerOptions(Contract.Json) { WriteIndented = true }));
}
catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode = 1; }

sealed class InventoryLog : IEventLog
{
    public void Write(string category, object data) => Console.Error.WriteLine(JsonSerializer.Serialize(new { category, data }, Contract.Json));
}
