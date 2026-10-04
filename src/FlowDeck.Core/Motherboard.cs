using System.Text;

namespace FlowDeck.Core;

public record BoardSensor(string Id, string SourceName, string Hardware, string Kind, string Unit,
    double? Value, double? RawValue, string? Problem = null);
public record BoardSnapshot(string Name, BoardSensor[] Sensors, long? Timestamp = null,
    DateTimeOffset? UpdatedAt = null, string? Error = null, bool MappingVerified = false)
{
    public static BoardSnapshot Empty { get; } = new("マザーボード", [], Error: "未取得");
    public bool IsFresh(TimeProvider? time = null) => Timestamp is { } t &&
        (time ?? TimeProvider.System).GetElapsedTime(t).TotalSeconds is >= 0 and < 3 && Error is null;
}
public interface IMotherboardSource { BoardSnapshot Board { get; } }
public record BoardSensorSetting(string Id, string DisplayName, bool ShowOnM5 = false);
public record RgbZoneSetting(string Id, string Accessory = "", int? ConfirmedLedCount = null, BoardLightSetting? LastLighting = null);
public record MotherboardSettings(BoardSensorSetting[]? Sensors = null, bool OpenRgbEnabled = false, int OpenRgbPort = 6742,
    RgbZoneSetting[]? RgbZones = null);
public record BoardPanelRow(string Name, string Kind, string Unit, double? Value);
public record BoardPanel(string Name, bool Fresh, string Status, BoardPanelRow[] Rows, double? AgeMs = null);

public static class MotherboardDisplay
{
    public const int MaxPanelSensors = 8;
    public static string DefaultName(BoardSensor sensor)
    {
        var chip = sensor.Hardware.Replace("Nuvoton ", "", StringComparison.Ordinal);
        var kind = sensor.Kind switch { "temperature" => "温度", "fan" => "RPM", "control" => "出力", "voltage" => "電圧", _ => "項目" };
        return ShortText(chip, 20) + " " + kind + sensor.Id.Split('/').Last();
    }
    public static bool IsUsable(string kind, double? value) => value is { } v && double.IsFinite(v) && kind switch
    {
        "temperature" => v is > -40 and < 150,
        "fan" => v is >= 0 and <= 30000,
        "control" => v is >= 0 and <= 100,
        "voltage" => v is >= 0 and <= 20,
        _ => false
    };
    public static string ShortText(string? value, int length) => string.Concat((value ?? "").EnumerateRunes()
        .Where(c => !System.Text.Rune.IsControl(c)).Take(length).Select(c => c.ToString()));
    public static BoardPanel Panel(BoardSnapshot snapshot, MotherboardSettings settings, TimeProvider? time = null)
    {
        var fresh = snapshot.IsFresh(time);
        var choices = settings.Sensors ?? [];
        var selected = choices.Where(s => s.ShowOnM5).DistinctBy(s => s.Id).Take(MaxPanelSensors).ToArray();
        // A saved, missing selection remains visible as unavailable; never replace it with another sensor.
        var rows = selected.Select(s =>
        {
            var reading = snapshot.Sensors.FirstOrDefault(r => r.Id == s.Id);
            return new BoardPanelRow(ShortText(s.DisplayName, 18), reading?.Kind ?? "unknown", reading?.Unit ?? "",
                fresh && reading is not null && IsUsable(reading.Kind, reading.Value) ? reading.Value : null);
        }).ToArray();
        return new(ShortText(snapshot.Name, 24), fresh, snapshot.Error is not null ? "取得不可" : !fresh ? "更新待ち" :
            selected.Length == 0 ? "PCで表示項目を選択" : snapshot.MappingVerified ? "監視のみ" : "端子対応未確認", rows,
            snapshot.Timestamp is { } timestamp ? Math.Clamp((time ?? TimeProvider.System).GetElapsedTime(timestamp).TotalMilliseconds, 0, 999999) : null);
    }
    public static string? Validate(MotherboardSettings? settings)
    {
        if (settings is null || settings.OpenRgbPort is < 1024 or > 65535) return "OpenRGBポートは1024〜65535で入力してください";
        var rgb = settings.RgbZones ?? [];
        if (rgb.Length > 256 || rgb.Any(r => r is null || string.IsNullOrWhiteSpace(r.Id) || r.Id.Length > 256 ||
            r.Accessory is null || r.Accessory.Length > 80 || r.Accessory.Any(char.IsControl) || r.ConfirmedLedCount is < 0 or > 4096 || r.LastLighting is not null && !BoardLighting.IsValid(r.LastLighting)) ||
            rgb.DistinctBy(r => r.Id).Count() != rgb.Length) return "RGBの機器名は80文字以内、確認済みLED数は空欄または0〜4096で入力してください";
        var sensors = settings.Sensors ?? [];
        if (sensors.Length > 256 || sensors.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id) || s.Id.Length > 256 ||
            string.IsNullOrWhiteSpace(s.DisplayName) || s.DisplayName.Length > 64 || s.DisplayName.Any(char.IsControl)) ||
            sensors.DistinctBy(s => s.Id).Count() != sensors.Length) return "センサー名または識別IDが不正です";
        return sensors.Count(s => s.ShowOnM5) > MaxPanelSensors ? "M5GO表示は最大8項目です" : null;
    }
}
