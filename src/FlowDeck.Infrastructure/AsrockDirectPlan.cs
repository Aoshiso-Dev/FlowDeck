using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed class AsrockDirectPlan
{
    private readonly AsrockRgbSnapshot before;
    private readonly HashSet<byte> selected;
    private readonly Dictionary<byte, RgbColor> settled;
    public AsrockDirectPlan(AsrockRgbSnapshot before, RgbZone[] targets, IReadOnlyDictionary<byte, RgbColor>? settled = null)
    {
        this.before = before;
        if (targets.Length == 0 || targets.Any(z => z.HardwareZone is null) || targets.DistinctBy(z => z.HardwareZone).Count() != targets.Length)
            throw new IOException("同期するRGBゾーンが不正です");
        selected = targets.Select(z => z.HardwareZone!.Value).ToHashSet();
        this.settled = settled?.ToDictionary() ?? [];
        if (this.settled.Any(p => selected.Contains(p.Key) || !before.Zones.Any(z => z.Address == p.Key) || p.Value?.Valid != true))
            throw new IOException("終了したRGBゾーンの固定色が不正です");
        foreach (var target in targets)
        {
            var original = before.Zones.SingleOrDefault(z => z.Address == target.HardwareZone);
            if (original is null || original.Count != target.LedCount || original.Setting != target.Configuration)
                throw new IOException("照明の構成または設定が変わっています。再取得してください");
        }
        // Direct is one complete device frame. Unknown native animation phases cannot be preserved.
        if (before.Zones.Any(z => !selected.Contains(z.Address) && !this.settled.ContainsKey(z.Address) && z.Setting.Mode is not (0 or 1)))
            throw new IOException("同期対象外のマザー照明が動作中です。対象外を固定色・消灯にするか、同期対象へ含めてください");
    }
    public AsrockDirectProtocol.Zone[] Frame(RgbColor color) => FrameByZone(selected.ToDictionary(z => z, _ => color));
    public AsrockDirectProtocol.Zone[] FrameByZone(IReadOnlyDictionary<byte, RgbColor> colors)
    {
        Check(colors);
        return before.Zones.Select(z => new AsrockDirectProtocol.Zone(z.Address, z.Count, colors.TryGetValue(z.Address, out var color)
            ? color : settled.GetValueOrDefault(z.Address) ?? (z.Setting.Mode == 0 ? new(0, 0, 0) : new(z.Setting.Red, z.Setting.Green, z.Setting.Blue)))).ToArray();
    }
    public Dictionary<byte, BoardLightSetting> Settled(RgbColor color) => SettledByZone(selected.ToDictionary(z => z, _ => color));
    public Dictionary<byte, BoardLightSetting> SettledByZone(IReadOnlyDictionary<byte, RgbColor> colors)
    {
        Check(colors);
        return before.Zones.ToDictionary(z => z.Address, z => (colors.GetValueOrDefault(z.Address) ?? settled.GetValueOrDefault(z.Address)) is { } c
            ? new BoardLightSetting(1, c.R, c.G, c.B) : z.Setting);
    }
    private void Check(IReadOnlyDictionary<byte, RgbColor> colors)
    {
        if (!selected.SetEquals(colors.Keys) || colors.Values.Any(c => c?.Valid != true)) throw new IOException("照明フレームの対象・色が不正です");
    }
}
