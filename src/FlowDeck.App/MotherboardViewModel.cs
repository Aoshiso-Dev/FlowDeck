using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using FlowDeck.Core;

namespace FlowDeck.App;

public sealed class BoardSensorRow(string id, string name) : Observable
{
    public string Id { get; } = id;
    private string displayName = name;
    public string DisplayName { get => displayName; set => Set(ref displayName, value); }
    public string Source { get; private set; } = "";
    public string Kind { get; private set; } = "";
    public string Value { get; private set; } = "—";
    public string Status { get; private set; } = "未取得";
    public void Update(BoardSensor? sensor, bool fresh)
    {
        Source = sensor is null ? Id : sensor.Hardware + " / " + sensor.SourceName;
        Kind = sensor?.Kind switch { "temperature" => "温度", "voltage" => "電圧", "fan" => "回転数", "control" => "出力率", _ => "未取得" };
        Value = fresh && sensor?.Value is { } value ? value.ToString(sensor.Kind == "fan" ? "N0" : "F2", CultureInfo.CurrentCulture) + " " + sensor.Unit : "—";
        Status = sensor is null ? "未検出" : !fresh ? "更新停止" : sensor.Problem ?? (sensor.Kind == "voltage" ? "換算・接続先未確認" : "端子対応未確認");
        Notify(nameof(Source)); Notify(nameof(Kind)); Notify(nameof(Value)); Notify(nameof(Status));
    }
}
public record RgbZoneDetails(string Controller, string Zone, string Leds, string Mode, string Modes, string Source);
public sealed class RgbZoneRow(string id, RgbZoneDetails details, RgbZoneSetting? saved) : Observable
{
    public string Id { get; } = id;
    private string accessory = saved?.Accessory ?? "", confirmedLeds = saved?.ConfirmedLedCount?.ToString() ?? "";
    public string Accessory { get => accessory; set => Set(ref accessory, value); }
    public string ConfirmedLeds { get => confirmedLeds; set => Set(ref confirmedLeds, value); }
    public RgbZoneDetails Details { get; private set; } = details;
    public void Update(RgbZoneDetails value) { if (Details != value) { Details = value; Notify(nameof(Details)); } }
}

public sealed partial class FlowDeckViewModel
{
    public ObservableCollection<BoardSensorRow> BoardSensors { get; } = [];
    public ObservableCollection<RgbZoneRow> RgbZones { get; } = [];
    public ICommand SaveMotherboard { get; private set; } = null!;
    public ICommand ReloadMotherboard { get; private set; } = null!;
    public ICommand RefreshRgb { get; private set; } = null!;
    public bool OpenRgbEnabled { get; set; }
    public string OpenRgbPort { get; set; } = "6742";
    private bool rgbBusy;
    public string BoardName => host.Motherboard.Board.Name;
    public string BoardStatus
    {
        get
        {
            var board = host.Motherboard.Board;
            return (Demo ? "デモ値 / " : host.CpuFan.State.Managing ? "CPU_FAN1制御中 / " : "監視のみ / ") + (board.Error ?? (board.IsFresh() ? "取得中" : "更新停止")) +
                (board.UpdatedAt is { } updated ? " / 更新 " + updated.ToLocalTime().ToString("HH:mm:ss") : "");
        }
    }
    public string RgbStatus => rgbBusy ? "RGB取得中…" : host.Rgb.Error ??
        $"{host.Rgb.Devices.Length} コントローラー / ASRock {host.Rgb.Devices.Count(d => d.IsAsrock)} 台 / 取得 {host.Rgb.UpdatedAt?.ToLocalTime():HH:mm:ss}";
    private void InitializeMotherboard()
    {
        SaveMotherboard = Command(async _ =>
        {
            var settings = new MotherboardSettings(BoardSensors.Select(s => new BoardSensorSetting(s.Id, s.DisplayName.Trim())).ToArray(),
                OpenRgbEnabled, RequiredInt(OpenRgbPort),
                (core.Settings.Motherboard.RgbZones ?? []).Where(s => RgbZones.All(r => r.Id != s.Id))
                    .Concat(RgbZones.Select(r => new RgbZoneSetting(r.Id, r.Accessory.Trim(), ParseInt(r.ConfirmedLeds), core.Settings.Motherboard.RgbZones?.FirstOrDefault(s => s.Id == r.Id)?.LastLighting)))
                    .Where(s => s.Accessory.Length > 0 || s.ConfirmedLedCount is not null || s.LastLighting is not null).ToArray());
            if (MotherboardDisplay.Validate(settings) is { } error) throw new InvalidOperationException(error);
            await core.UpdateSettingsAsync(core.Settings with { Motherboard = settings }, false); await host.FlushSettingsAsync();
            Notice = "表示設定を保存済み";
            if (!settings.OpenRgbEnabled) { await host.RefreshRgbAsync(); RefreshMotherboard(); }
        });
        ReloadMotherboard = Command(_ => { LoadMotherboardSettings(); return Task.CompletedTask; });
        RefreshRgb = Command(async _ =>
        {
            rgbBusy = true; Notify(nameof(RgbStatus));
            try { await host.RefreshRgbAsync(); }
            finally { rgbBusy = false; RefreshMotherboard(); }
        }, _ => !rgbBusy);
    }
    private void LoadMotherboardSettings()
    {
        var settings = core.Settings.Motherboard;
        OpenRgbEnabled = settings.OpenRgbEnabled; OpenRgbPort = settings.OpenRgbPort.ToString();
        BoardSensors.Clear();
        foreach (var saved in settings.Sensors ?? []) BoardSensors.Add(new(saved.Id, saved.DisplayName));
        RgbZones.Clear();
        Notify(nameof(OpenRgbEnabled)); Notify(nameof(OpenRgbPort)); RefreshMotherboard();
    }
    private void RefreshMotherboard()
    {
        var board = host.Motherboard.Board;
        foreach (var sensor in board.Sensors.Take(256))
            if (BoardSensors.All(r => r.Id != sensor.Id)) BoardSensors.Add(new(sensor.Id, MotherboardDisplay.DefaultName(sensor)));
        foreach (var row in BoardSensors) row.Update(board.Sensors.FirstOrDefault(s => s.Id == row.Id), board.IsFresh());
        var current = new HashSet<string>();
        foreach (var device in host.Rgb.Devices)
        {
            var index = 0;
            foreach (var zone in device.Zones)
            {
                var id = device.ZoneRegistrationId(zone.Name, index++); current.Add(id);
                var details = new RgbZoneDetails(device.Name, zone.Name, $"{zone.LedCount}（設定値）", zone.ReportedMode ?? device.Mode,
                    device.Modes.Length == 0 ? "対応一覧未取得" : string.Join(" / ", device.Modes), device.Location);
                var row = RgbZones.FirstOrDefault(r => r.Id == id);
                if (row is null) RgbZones.Add(new(id, details, core.Settings.Motherboard.RgbZones?.FirstOrDefault(r => r.Id == id)));
                else row.Update(details);
            }
        }
        // Keep edits and registered labels when a device disappears. Do not present its old mode as live.
        foreach (var row in RgbZones.Where(r => !current.Contains(r.Id)))
            row.Update(row.Details with { Leds = "未取得", Mode = "未取得", Modes = "未取得" });
        RefreshBoardLighting(); RefreshMemoryLighting();
        Notify(nameof(BoardName)); Notify(nameof(BoardStatus)); Notify(nameof(RgbStatus));
    }
}
