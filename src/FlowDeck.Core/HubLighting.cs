namespace FlowDeck.Core;

public record RgbColor(int R, int G, int B)
{
    public bool Valid => R is >= 0 and <= 255 && G is >= 0 and <= 255 && B is >= 0 and <= 255;
    public RgbColor Scale(double value) => new((int)Math.Round(R * value), (int)Math.Round(G * value), (int)Math.Round(B * value));
}
public record HubLightEffect(string Id, string Name, bool Color, bool Palette, bool Speed, bool Direction);
public record HubLightSetting(string Mode, RgbColor[] Colors, int Speed = 2, bool Reverse = false);
public record LightingLease(long Revision, long Generation, string[] Channels);

public static class HubLighting
{
    public static IReadOnlyList<HubLightEffect> Effects { get; } = Array.AsReadOnly<HubLightEffect>(
    [new("fixed", "固定", true, false, false, false), new("breathing", "呼吸", true, false, true, false),
     new("fading", "フェード", true, true, true, false), new("spectrum-wave", "スペクトラムウェーブ", false, false, true, true),
     new("covering-marquee", "流れる光", true, true, true, true), new("super-rainbow", "レインボー", false, false, true, true),
     new("off", "消灯", false, false, false, false)]);
    public static string Name(string? mode) => Effects.FirstOrDefault(e => e.Id == mode)?.Name ?? mode switch
        { "color-cycle" => "カラーサイクル", "synchronized" => "同期", null => "未送信", _ => mode };
    public static bool Valid(HubLightSetting? setting) => setting is { Speed: >= 0 and <= 4, Colors: not null } &&
        setting.Colors.All(c => c is not null && c.Valid) && setting.Mode switch
        {
            "fixed" or "breathing" => setting.Colors.Length == 1,
            "fading" or "covering-marquee" => setting.Colors.Length is >= 1 and <= 8,
            "off" or "spectrum-wave" or "super-rainbow" => setting.Colors.Length == 0,
            _ => false
        };
    public static HubLightSetting Preset(string name) => name switch
    {
        "white" => new("fixed", [new(233, 242, 255)]), "purple" => new("fixed", [new(160, 121, 236)]),
        "off" => new("off", []), _ => new("fixed", [new(52, 137, 255)])
    };
    public static double Period(int speed) => new[] { 12d, 9d, 6d, 4d, 2d }[Math.Clamp(speed, 0, 4)];
    public static RgbColor Frame(string mode, RgbColor color, double seconds, double period)
    {
        if (!color.Valid || !double.IsFinite(seconds) || !double.IsFinite(period) || period <= 0) throw new ArgumentException("不正な照明周期です");
        var phase = ((seconds / period) % 1 + 1) % 1;
        if (mode == "breathing") return color.Scale((1 + Math.Cos(phase * Math.Tau)) / 2);
        if (mode == "fixed") return color;
        if (mode == "off") return new(0, 0, 0);
        if (mode != "color-cycle") throw new ArgumentException("同期に対応していない効果です");
        // Uniform hue across devices; a spatial wave is not comparable across different LED layouts.
        var h = phase * 6; var sector = (int)h; var f = h - sector;
        var v = Math.Max(color.R, Math.Max(color.G, color.B)); var rise = (int)Math.Round(v * f); var fall = v - rise;
        return sector switch { 0 => new(v, rise, 0), 1 => new(fall, v, 0), 2 => new(0, v, rise),
            3 => new(0, fall, v), 4 => new(rise, 0, v), _ => new(v, 0, fall) };
    }
}
