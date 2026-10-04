namespace FlowDeck.Core;

public record BoardLightEffect(byte Id, string Name, bool Color, bool Speed);
public record BoardLightSetting(byte Mode, int Red, int Green, int Blue, int Speed = 224,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? MemoryPalette = null);

public static class BoardLighting
{
    // Hardware effects only. Music requires another source; Direct requires a frame stream.
    public static IReadOnlyList<BoardLightEffect> Effects { get; } = Array.AsReadOnly<BoardLightEffect>(
    [new(1, "固定", true, false), new(2, "呼吸", true, true), new(4, "スペクトラム", false, true),
     new(14, "レインボー", false, true), new(7, "ウェーブ", true, true), new(8, "スプリング", true, true),
     new(9, "スタック", true, true), new(10, "クラム", true, true), new(11, "スキャン", true, true),
     new(12, "ネオン", true, true), new(13, "ウォーター", true, true), new(5, "ランダム", false, true),
     new(3, "点滅", true, true), new(0, "消灯", false, false)]);
    public static bool IsValid(BoardLightSetting? value) => value is not null && Effects.Any(e => e.Id == value.Mode) &&
        value.Red is >= 0 and <= 255 && value.Green is >= 0 and <= 255 && value.Blue is >= 0 and <= 255 && value.Speed is >= 0 and <= 255 &&
        MemoryLighting.ValidPalette(value);
    public static string Name(byte mode) => Effects.FirstOrDefault(e => e.Id == mode)?.Name ?? $"未対応 (0x{mode:X2})";
}
