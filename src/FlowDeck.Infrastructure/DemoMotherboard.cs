using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed class DemoMotherboard : IMotherboardSource
{
    public BoardSnapshot Board => new("DEMO / ASRock X870E Taichi White",
    [new("demo/board/temperature/0", "Temperature #1", "DEMO Super I/O", "temperature", "°C", 42, 42),
     new("demo/board/temperature/1", "Temperature #2", "DEMO Super I/O", "temperature", "°C", null, null, "未取得"),
     new("demo/board/fan/0", "Fan #1", "DEMO Super I/O", "fan", "rpm", 1250, 1250),
     new("demo/board/fan/1", "Fan #2", "DEMO Super I/O", "fan", "rpm", 0, 0),
     new("demo/board/fan/2", "Fan #3", "DEMO Super I/O", "fan", "rpm", 0, 0),
     .. MotherboardFans.Ports.Skip(2).Select(p => new BoardSensor(p.DemoSensorId, p.Name, p.Chip, "fan", "rpm", 0, 0)),
     new("demo/board/voltage/0", "Voltage #1", "DEMO Super I/O", "voltage", "V", 1.12, 1.12),
     new("demo/board/control/0", "Fan Control #1", "DEMO Super I/O", "control", "%", 45, 45)],
        TimeProvider.System.GetTimestamp(), DateTimeOffset.UtcNow);
    public static RgbInventory Rgb => new([new(0, "DEMO ASRock RGB", "ASRock", "デモ表示", "DEMO", "DEMO", ["Static", "Breathing"], 0,
        [new("Onboard IO", 1, 15, 15, 15, "Rainbow", 5, new(14, 0, 0, 255, 128)),
         new("Onboard PCB", 1, 9, 9, 9, "Rainbow", 6, new(14, 0, 0, 255, 128)),
         new("ARGB Header 1", 1, 24, 0, 100, "Static", 2, new(1, 52, 137, 255)),
         new("ARGB Header 2", 1, 0, 0, 100, "Static", 3, new(1, 52, 137, 255))], "demo-asrock")], DateTimeOffset.UtcNow);
}
