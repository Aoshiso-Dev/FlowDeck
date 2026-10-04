using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

internal static class M5ReadOnlyCheck
{
    public static async Task RunAsync(string port)
    {
        var root = Directory.GetCurrentDirectory();
        var store = new JsonSettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowDeck", "hardware"));
        var settings = await store.LoadAsync();
        var identity = settings.Hardware.Hub ?? throw new InvalidOperationException("No confirmed Hub configured");
        var log = new InventoryLog();
        var hub = new ReadOnlyHub(new HubWorkerClient(settings.PythonPath ?? Path.Combine(root, "dist", "FlowDeck", "runtime", "python", "python.exe"),
            Path.Combine(root, "worker", "hub_worker.py"), log));
        await using var core = new ControlCoordinator(hub, new HardwareTemperatures(), settings, log);
        await core.OpenAsync(identity);
        await core.TickAsync();
        core.Start();
        var connections = new List<object>();
        string? previousSession = null, firstDevice = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var serial = new M5SerialSession(core, new(port, firstDevice, attempt == 0 ? "A" : "B"), log);
            serial.Start();
            var deadline = DateTime.UtcNow.AddSeconds(20);
            var synchronizedSamples = 0;
            while (DateTime.UtcNow < deadline && synchronizedSamples < 3)
            {
                await Task.Delay(1000);
                synchronizedSamples = serial.Protocol.Synchronized ? synchronizedSamples + 1 : 0;
            }
            if (synchronizedSamples < 3) throw new InvalidOperationException("M5GO did not synchronize: " + serial.Error);
            if (previousSession == serial.Protocol.SessionId) throw new InvalidOperationException("M5GO session was not renewed");
            previousSession = serial.Protocol.SessionId;
            firstDevice ??= serial.Protocol.DeviceId;
            connections.Add(new { layout = attempt == 0 ? "A" : "B", serial.Protocol.DeviceId, serial.Protocol.FirmwareVersion,
                serial.Protocol.SessionId, serial.Protocol.Connected, serial.Protocol.Synchronized, synchronizedSamples,
                snapshot = core.Snapshot });
        }
        Console.WriteLine(JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, port, connections, hub.WriteAttempts },
            new JsonSerializerOptions(Contract.Json) { WriteIndented = true }));
        if (hub.WriteAttempts != 0) throw new InvalidOperationException("Unexpected output command during read-only check");
    }

    private sealed class ReadOnlyHub(IHubAdapter inner) : IHubAdapter
    {
        public int WriteAttempts { get; private set; }
        public bool IsDemo => false;
        public string Version => inner.Version;
        public Task<HubIdentity[]> ProbeAsync(CancellationToken ct = default) => inner.ProbeAsync(ct);
        public Task<HubStatus> OpenAsync(HubIdentity identity, long generation, CancellationToken ct = default) => inner.OpenAsync(identity, generation, ct);
        public Task<HubStatus> ReadAsync(CancellationToken ct = default) => inner.ReadAsync(ct);
        public Task<HubResult> SetFanGroupAsync(string[] channels, int duty, CancellationToken ct = default)
        { WriteAttempts++; throw new InvalidOperationException("Read-only device check"); }
        public Task<HubResult> SetLightingAsync(string[] channels, string preset, CancellationToken ct = default)
        { WriteAttempts++; throw new InvalidOperationException("Read-only device check"); }
        public Task CloseAsync(CancellationToken ct = default) => inner.CloseAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
