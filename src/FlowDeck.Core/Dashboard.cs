namespace FlowDeck.Core;

// Array order is the user's display order. Source and sensor identity never depend on a label.
public record DashboardItem(string Source, string SensorId, string Kind, string Name, bool Visible = true, string Role = "fan")
{
    [System.Text.Json.Serialization.JsonIgnore] public string Key => Source + ":" + SensorId;
}
public record DashboardSettings(DashboardItem[]? Items = null);
public record DashboardReading(DashboardItem Item, double? Value, double? Duty, string State, string Control, string Connection, string HistoryKey)
{
    public string Indicator { get; init; } = Item.Kind == "temperature" ? "circle" : "bar";
    public double? DisplayMaximum { get; init; }
    public bool Circular => Indicator == "circle";
    public bool Bar => Indicator == "bar";
    public bool Inline => !Circular;
    public double ScaleMaximum => DisplayMaximum is > 0 and <= 1_000_000 ? DisplayMaximum.Value : Item.Kind == "temperature" ? 100 :
        Math.Max(Item.Role == "pump" ? 6000 : 3000, Value is { } rpm && double.IsFinite(rpm) ? Math.Ceiling(rpm / 1000) * 1000 : 0);
    public string ScaleDescription => $"表示目盛 0–{ScaleMaximum:N0} {Unit}（保護設定・定格上限とは別です）";
    public string Key => Item.Key;
    public string Name => Item.Name;
    public string Unit => Item.Kind == "temperature" ? "°C" : "rpm";
    public string DisplayValue => Value?.ToString(Item.Kind == "temperature" ? "F1" : "N0") ?? "—";
    public string DisplayDuty => Duty is { } duty ? duty.ToString("0.#") + "%" : "—";
    public string Role => Item.Kind == "temperature" ? "温度" : Item.Role == "pump" ? "ポンプ" : "ファン";
}

public static class DashboardDisplay
{
    public static string? CpuFanSensorId(BoardSnapshot board) => MotherboardDefinitionCatalog.BuiltIn.CpuSensorId(board);
    public static bool Fresh(long? timestamp, TimeProvider? time = null) => timestamp is { } t &&
        (time ?? TimeProvider.System).GetElapsedTime(t).TotalSeconds is >= 0 and < 3;

    public static DashboardItem[] Catalog(AppSettings settings, ControlSnapshot control, BoardSnapshot board)
    {
        var result = new List<DashboardItem> { new("cpu", "selected", "temperature", "CPU"), new("gpu", "selected", "temperature", "GPU") };
        for (var i = 1; i <= 5; i++)
        {
            var id = "fan" + i;
            var present = control.Channels.FirstOrDefault(c => c.Id == id)?.Presence == Presence.Present;
            result.Add(new("nzxt", id, "fan", "NZXT FAN" + i, present || settings.Hardware.Fans?.Contains(id) == true));
        }
        var boardTemps = board.Sensors.Where(s => s.Kind == "temperature")
            .Take(2).Select(s => s.Id).ToHashSet();
        foreach (var sensor in board.Sensors.Where(s => s.Kind is "temperature" or "fan"))
        {
            var port = sensor.Kind == "fan" ? MotherboardFans.ForSensor(board, sensor.Id) : null;
            var cpuFan = port == MotherboardFans.Cpu;
            var name = settings.Motherboard.Sensors?.FirstOrDefault(s => s.Id == sensor.Id)?.DisplayName;
            if (name is null || (port is not null && name == MotherboardDisplay.DefaultName(sensor)))
                name = port?.Name ?? MotherboardDisplay.DefaultName(sensor);
            result.Add(new("motherboard", sensor.Id, sensor.Kind, name, cpuFan || (port is not null && sensor.Value > 0) || boardTemps.Contains(sensor.Id)));
        }
        return result.ToArray();
    }
    public static DashboardItem[] Items(DashboardSettings settings, DashboardItem[] catalog)
    {
        if (settings.Items is null) return catalog;
        // New discoveries stay hidden after customization; saved missing devices retain their position and kind.
        return settings.Items.Concat(catalog.Where(c => settings.Items.All(s => s.Key != c.Key)).Select(c => c with { Visible = false })).ToArray();
    }
    public static DashboardReading[] Read(AppSettings settings, ControlSnapshot control, BoardSnapshot board, CpuFanState cpuFan, TimeProvider? time = null)
        => ReadAll(settings, control, board, new Dictionary<string, CpuFanState> { [MotherboardFans.Cpu.Id] = cpuFan }, time);
    public static DashboardReading[] ReadAll(AppSettings settings, ControlSnapshot control, BoardSnapshot board, IReadOnlyDictionary<string, CpuFanState> fans, TimeProvider? time = null)
        => Items(settings.Dashboard, Catalog(settings, control, board)).Where(i => i.Visible)
            .Select(item => Read(item, settings, control, board, fans, time)).ToArray();

    private static DashboardReading Read(DashboardItem item, AppSettings settings, ControlSnapshot control, BoardSnapshot board, IReadOnlyDictionary<string, CpuFanState> fans, TimeProvider? time)
    {
        double? value = null, duty = null;
        var status = "未取得"; var mode = "監視のみ"; var connection = ""; var historyKey = item.Key;
        if (item.Source is "cpu" or "gpu")
        {
            var id = item.Source == "cpu" ? settings.CpuSensorId : settings.GpuSensorId;
            var sensor = control.Sensors.FirstOrDefault(s => s.Id == id);
            connection = item.Source.ToUpperInvariant(); historyKey += ":" + id;
            status = sensor is null ? "未取得" : !Fresh(sensor.Timestamp, time) ? "更新停止" :
                !MotherboardDisplay.IsUsable("temperature", sensor.Celsius) ? "未取得" : "取得中";
            if (status == "取得中") value = sensor!.Celsius;
        }
        else if (item.Source == "nzxt")
        {
            var fan = control.Channels.FirstOrDefault(c => c.Id == item.SensorId);
            connection = "NZXT / " + item.SensorId.ToUpperInvariant();
            historyKey += ":" + settings.Hardware.Hub?.Id;
            status = !control.HubReady ? "未接続" : !Fresh(control.TelemetryTimestamp, time) ? "更新停止" :
                fan is null ? "未取得" : fan.Presence == Presence.Absent ? "未接続" :
                !MotherboardDisplay.IsUsable("fan", fan.Rpm) ? "未取得" : fan.Rpm == 0 ? "0 RPM" : "取得中";
            if (status is "取得中" or "0 RPM") { value = fan!.Rpm; duty = MotherboardDisplay.IsUsable("control", fan.Duty) ? fan.Duty : null; }
            mode = control.Mode switch { FanMode.Auto => "共通・自動", FanMode.Manual => "共通・手動", FanMode.Fallback => "共通・異常時", _ => "共通・監視のみ" };
        }
        else if (item.Source == "motherboard")
        {
            var sensor = board.Sensors.FirstOrDefault(s => s.Id == item.SensorId && s.Kind == item.Kind);
            connection = "マザー / " + (sensor?.Hardware.Replace("Nuvoton ", "", StringComparison.Ordinal) ?? "未検出");
            historyKey += ":" + board.Name;
            status = sensor is null ? "未取得" : !Fresh(board.Timestamp, time) ? "更新停止" :
                board.Error is not null || sensor.Problem is not null || !MotherboardDisplay.IsUsable(item.Kind, sensor.Value) ? "未取得" : "取得中";
            if (status == "取得中") value = sensor!.Value;
            if (item.Kind == "fan" && MotherboardFans.ForSensor(board, item.SensorId) is { } port)
            {
                var cpuFan = fans.GetValueOrDefault(port.Id) ?? new();
                mode = cpuFan.Managing ? cpuFan.Mode switch { "auto" => "個別・自動", "manual" => "個別・手動", "test" => "端子テスト", _ => "個別・異常時" } : "マザー管理";
                if (value is not null && cpuFan.Reading is { } reading && Fresh(reading.Timestamp, time) && MotherboardDisplay.IsUsable("control", reading.Duty))
                { duty = reading.Duty; if (MotherboardDisplay.IsUsable("fan", reading.Rpm)) value = reading.Rpm; }
                connection = "マザー / " + port.Name;
            }
            if (item.Kind == "fan" && value == 0) status = "0 RPM";
        }
        return new(item, value, duty, status, mode, connection, historyKey);
    }
    public static string? Validate(DashboardSettings? settings)
    {
        if (settings is null) return "ダッシュボード設定が不正です";
        var items = settings.Items ?? [];
        if (items.Length > 256 || items.Any(i => i is null || i.Source is not ("cpu" or "gpu" or "nzxt" or "motherboard") ||
            string.IsNullOrWhiteSpace(i.SensorId) || i.SensorId.Length > 256 || i.SensorId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(i.Name) || i.Name.Length > 64 || i.Name.Any(char.IsControl) ||
            i.Kind is not ("temperature" or "fan") || i.Role is not ("fan" or "pump") ||
            (i.Source is "cpu" or "gpu" && (i.Kind != "temperature" || i.SensorId != "selected")) ||
            (i.Source == "nzxt" && (i.Kind != "fan" || !new[] { "fan1", "fan2", "fan3", "fan4", "fan5" }.Contains(i.SensorId)))) ||
            items.DistinctBy(i => i.Key).Count() != items.Length) return "表示項目は重複なし、名前は1〜64文字で設定してください";
        return null;
    }
}

public record DashboardSample(double Seconds, double? Value);
// Process-local history: bounded to ten minutes, with gaps rather than connecting missing observations.
public sealed class DashboardHistory(TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly Dictionary<string, (string Identity, List<DashboardSample> Samples)> series = [];
    private readonly long start = (time ?? TimeProvider.System).GetTimestamp();
    public double Now => clock.GetElapsedTime(start).TotalSeconds;
    public void Add(IEnumerable<DashboardReading> readings)
    {
        var now = Now; var active = new HashSet<string>();
        foreach (var reading in readings.Take(256))
        {
            active.Add(reading.Key);
            if (!series.TryGetValue(reading.Key, out var data) || data.Identity != reading.HistoryKey)
                series[reading.Key] = data = (reading.HistoryKey, []);
            data.Samples.RemoveAll(p => p.Seconds < now - 600);
            if (data.Samples.Count == 0 || now - data.Samples[^1].Seconds >= 1)
                data.Samples.Add(new(now, reading.Value));
        }
        foreach (var key in series.Keys.Where(k => !active.Contains(k)).ToArray()) series.Remove(key);
    }
    public DashboardSample[] Get(string? key) => key is not null && series.TryGetValue(key, out var data)
        ? data.Samples.Where(p => p.Seconds >= Now - 600).ToArray() : [];
}
