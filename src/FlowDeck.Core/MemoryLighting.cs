namespace FlowDeck.Core;

/// <summary>Native effects for the identified eight-LED ENE DIMMs. Scene effect IDs remain compatible with BoardLightSetting.</summary>
public static class MemoryLighting
{
    public const string Prefix = "ene-dram:1022-790b:p0:";
    public static bool IsIdentity(string? identity) => identity is Prefix + "71" or Prefix + "73";
    public static byte Address(string identity) => identity switch
    { Prefix + "71" => 0x71, Prefix + "73" => 0x73, _ => throw new ArgumentException("未確認のメモリRGBです") };
    public static string Identity(byte address) => address is 0x71 or 0x73 ? Prefix + address.ToString("x2") : throw new ArgumentOutOfRangeException(nameof(address));
    public static IReadOnlyList<BoardLightEffect> Effects { get; } = Array.AsReadOnly<BoardLightEffect>(
        [new(1,"固定",true,false), new(2,"呼吸",true,true), new(4,"スペクトラム",false,true),
         new(14,"レインボー",false,true), new(3,"点滅",true,true), new(0,"消灯",false,false)]);
    public static bool Valid(BoardLightSetting? setting) => BoardLighting.IsValid(setting) && Effects.Any(e => e.Id == setting!.Mode);
    public static bool ValidPalette(BoardLightSetting setting)
    {
        if (setting.MemoryPalette is null) return true;
        if (setting.MemoryPalette.Length != 48 || setting.MemoryPalette.Any(c => !Uri.IsHexDigit(c))) return false;
        var colors = Convert.FromHexString(setting.MemoryPalette);
        return colors[0] == setting.Red && colors[1] == setting.Blue && colors[2] == setting.Green;
    }
    public static byte[] Colors(BoardLightSetting setting) => setting.MemoryPalette is { } p ? Convert.FromHexString(p) :
        Enumerable.Range(0, 8).SelectMany(_ => new byte[] { (byte)setting.Red, (byte)setting.Blue, (byte)setting.Green }).ToArray();
    public static string? Palette(byte[] colors) => Enumerable.Range(1, 7).All(i => colors.AsSpan(i * 3, 3).SequenceEqual(colors.AsSpan(0, 3))) ? null : Convert.ToHexString(colors);
    public static BoardLightSetting Normalize(BoardLightSetting setting)
    {
        if (!Valid(setting)) throw new ArgumentException("未対応のメモリ照明設定です");
        return setting with { Speed = setting.Mode is 0 or 1 ? 224 : (int)Math.Round(Math.Round(setting.Speed * 4d / 255) * 255 / 4), MemoryPalette = Palette(Colors(setting)) };
    }
    public static byte NativeMode(byte mode) => mode switch { <= 4 => mode, 14 => 5, _ => throw new ArgumentException("未対応のメモリ効果です") };
    public static byte SceneMode(byte mode) => mode switch { <= 4 => mode, 5 => 14, _ => throw new ArgumentException("未対応のメモリ効果です") };
}
