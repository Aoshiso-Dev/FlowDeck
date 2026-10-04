using System.Text.Json;

namespace FlowDeck.Core;

public sealed record MotherboardDefinition(string Id, string Name, string CpuFanId, string ChassisFanId,
    IReadOnlyList<MotherboardFanPort> Ports);

/// <summary>Packaged data selects compiled drivers; no user file can supply register writes or code.</summary>
public sealed class MotherboardDefinitionCatalog
{
    private sealed record Document(int Version, Board[] Boards);
    private sealed record Board(string Id, string Name, string CpuFanId, string ChassisFanId, MotherboardFanPort[] Ports);
    public IReadOnlyList<MotherboardDefinition> Boards { get; }
    private MotherboardDefinitionCatalog(MotherboardDefinition[] boards) => Boards = Array.AsReadOnly(boards);
    public static MotherboardDefinitionCatalog BuiltIn { get; } = LoadBuiltIn();
    private static MotherboardDefinitionCatalog LoadBuiltIn()
    {
        using var stream = typeof(MotherboardDefinitionCatalog).Assembly.GetManifestResourceStream("FlowDeck.DeviceDefinitions.Motherboards.json")
            ?? throw new IOException("マザー対応定義が見つかりません");
        using var reader = new StreamReader(stream); return Parse(reader.ReadToEnd());
    }
    public static MotherboardDefinitionCatalog Parse(string json)
    {
        if (json.Length > 128 * 1024) throw new IOException("マザー対応定義が大きすぎます");
        using var document = JsonDocument.Parse(json);
        NoDuplicateProperties(document.RootElement);
        var value = document.RootElement.Deserialize<Document>(Contract.Json);
        if (value is not { Version: 1, Boards.Length: > 0 and <= 64 } ||
            value.Boards.Any(b => b is null || !Text(b.Id, 96) || !Text(b.Name, 160) || b.Ports is not { Length: > 0 and <= 32 }) ||
            value.Boards.DistinctBy(b => b.Id).Count() != value.Boards.Length ||
            value.Boards.DistinctBy(b => b.Name).Count() != value.Boards.Length)
            throw new IOException("マザー対応定義の識別情報が不正です");
        foreach (var b in value.Boards)
        {
            if (b.Ports.Any(p => p is null || !Text(p.Id, 96) || !Text(p.Name, 80) || !Text(p.DemoSensorId, 160) ||
                !(p.Chip == "NCT6799D" && p.Channel is >= 0 and < 7 || p.Chip == "NCT6686D" && p.Channel is >= 0 and < 8) ||
                p.SensorId != p.HardwareId + "/fan/" + p.Channel) ||
                b.Ports.DistinctBy(p => p.Id).Count() != b.Ports.Length ||
                b.Ports.DistinctBy(p => p.SensorId).Count() != b.Ports.Length ||
                b.Ports.DistinctBy(p => p.DemoSensorId).Count() != b.Ports.Length ||
                b.Ports.All(p => p.Id != b.CpuFanId) || b.Ports.All(p => p.Id != b.ChassisFanId) || b.CpuFanId == b.ChassisFanId)
                throw new IOException("マザー対応定義の端子情報が不正です");
        }
        return new(value.Boards.Select(b => new MotherboardDefinition(b.Id, b.Name, b.CpuFanId, b.ChassisFanId,
            Array.AsReadOnly(b.Ports.ToArray()))).ToArray());
    }
    private static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
    private static void NoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new IOException("対応定義の項目が重複しています"); NoDuplicateProperties(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) NoDuplicateProperties(item);
    }
    public MotherboardDefinition? Resolve(string name, bool allowDemo = false)
    {
        if (allowDemo && name.StartsWith("DEMO / ", StringComparison.Ordinal)) name = name[7..];
        return Boards.SingleOrDefault(b => b.Name == name);
    }
    public MotherboardFanPort? PortForSensor(BoardSnapshot board, string sensorId)
    {
        var demo = board.Name.StartsWith("DEMO / ", StringComparison.Ordinal);
        return Resolve(board.Name, allowDemo: true)?.Ports.SingleOrDefault(p => (demo ? p.DemoSensorId : p.SensorId) == sensorId);
    }
    public string? CpuSensorId(BoardSnapshot board)
    {
        var definition = Resolve(board.Name, allowDemo: true);
        var port = definition?.Ports.Single(p => p.Id == definition.CpuFanId);
        return board.Name.StartsWith("DEMO / ", StringComparison.Ordinal) ? port?.DemoSensorId : port?.SensorId;
    }
}
