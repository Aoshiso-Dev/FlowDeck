namespace FlowDeck.Core;

public sealed record MotherboardFanPort(string Id, string Name, int Channel, string SensorId, string DemoSensorId, string Chip = "NCT6799D")
{
    public string HardwareId => "/lpc/" + Chip.ToLowerInvariant() + "/0";
}

// Electrical channels, not guessed physical header names. Enable each fan after its own setup.
public static class MotherboardFans
{
    // Compatibility view for v1 settings. New device identities use definition-scoped endpoints.
    private static readonly MotherboardDefinition Legacy = MotherboardDefinitionCatalog.BuiltIn.Boards.Single(b => b.Id == "asrock-x870e-taichi-white-v1");
    public static readonly MotherboardFanPort Cpu = Legacy.Ports.Single(p => p.Id == Legacy.CpuFanId);
    public static readonly MotherboardFanPort Chassis = Legacy.Ports.Single(p => p.Id == Legacy.ChassisFanId);
    public static IReadOnlyList<MotherboardFanPort> Ports => Legacy.Ports;
    public static MotherboardFanPort Find(string id) => Ports.SingleOrDefault(p => p.Id == id)
        ?? throw new NotSupportedException("制御に対応していないマザーボード端子です");
    public static MotherboardFanPort? ForSensor(BoardSnapshot board, string id) => MotherboardDefinitionCatalog.BuiltIn.PortForSensor(board, id);
    // Retain the existing CPU_FAN1 settings without migration or importing another fan's limits.
    public static MotherboardFanPort[] Detected(BoardSnapshot board) => board.Sensors.Where(s => s.Kind == "fan")
        .Select(s => ForSensor(board, s.Id)).OfType<MotherboardFanPort>().DistinctBy(p => p.Id).ToArray();
    public static CpuFanSettings Settings(AppSettings settings, string id) => Find(id) == Cpu ? settings.CpuFan :
        id == Chassis.Id ? settings.ChassisFan : settings.BoardFans.GetValueOrDefault(id) ?? new();
    public static AppSettings WithSettings(AppSettings settings, string id, CpuFanSettings value)
    {
        var port = Find(id);
        if (port == Cpu) return settings with { CpuFan = value };
        if (port == Chassis) return settings with { ChassisFan = value };
        return settings with { BoardFans = new(settings.BoardFans) { [id] = value } };
    }
    public static string? ValidateSettings(Dictionary<string, CpuFanSettings>? settings)
    {
        if (settings is null || settings.Count > Ports.Count - 2) return "マザーファン設定の形式が不正です";
        foreach (var (id, configuration) in settings)
        {
            var port = Ports.FirstOrDefault(p => p.Id == id && p != Cpu && p != Chassis);
            if (port is null) return "未対応または重複したマザーファン設定です";
            if (CpuFanController.Validate(configuration, port.Name) is { } error) return error;
        }
        return null;
    }
}

public sealed class MotherboardFanControllers(Func<MotherboardFanPort, ICpuFanDriver> drivers,
    Func<SensorReading[]> temperatures, IEventLog log) : IBoardFanControllers
{
    private readonly Dictionary<string, CpuFanController> controllers = MotherboardFans.Ports.ToDictionary(p => p.Id,
        p => new CpuFanController(drivers(p), temperatures, log, name: p.Name));
    public CpuFanController this[string id] => controllers[id];
    IBoardFanController IBoardFanControllers.this[string id] => controllers[id];
    public IReadOnlyDictionary<string, CpuFanState> States => controllers.ToDictionary(p => p.Key, p => p.Value.State);
    public void Start() { foreach (var c in controllers.Values) c.Start(); }
    public void Resume() { foreach (var c in controllers.Values) c.Resume(); }
    // Try every restoration even when one port fails. Preserve errors for the shutdown UI.
    private async Task All(Func<CpuFanController, Task> action)
    {
        var errors = new List<Exception>();
        foreach (var (id, c) in controllers)
            try { await action(c); } catch (Exception e) { errors.Add(new IOException(MotherboardFans.Find(id).Name + ": " + e.Message, e)); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }
    public Task RestoreAsync() => All(c => c.RestoreAsync());
    public Task SuspendAsync() => All(c => c.SuspendAsync());
    public async ValueTask DisposeAsync() => await All(c => c.DisposeAsync().AsTask());
}
