using System.Windows.Input;
using System.Windows.Media;
using FlowDeck.Core;

namespace FlowDeck.App;

public sealed record DeviceLightColorChoice(int Index, RgbColor Value)
{
    public string Name => (Index + 1).ToString();
    public Brush Brush => new SolidColorBrush(Color.FromRgb((byte)Value.R, (byte)Value.G, (byte)Value.B));
}
public sealed partial class DeviceWorkspaceViewModel
{
    private bool publishingLightProgram;
    private int selectedLightColor;
    public DeviceNativeLightingEffect? NativeLightEffect => LightingDraft.NativeEffects.SingleOrDefault(e => e.Id == LightEffect);
    public DeviceLightColorChoice[] LightPalette { get; private set; } = [];
    public bool LightHasPalette => NativeLightEffect is { Color: true, MaximumColors: > 1 } || LightPerLed;
    public bool LightHasDirection => NativeLightEffect?.Direction == true;
    public bool LightHasSpeed => LightAnimated || NativeLightEffect?.Speed == true;
    public int LightSpeedMinimum => NativeLightEffect?.MinimumSpeed ?? 0;
    public int LightSpeedMaximum => NativeLightEffect?.MaximumSpeed ?? 4;
    public bool CanEditLightBrightness => LightingIdle && (NativeLightEffect?.Brightness ?? DeviceLighting.FrameProgram(LightingDraft.Program));
    public bool LightAllowsPerLed => NativeLightEffect is { LedCount: > 0 };
    public string LightPreviewLabel => LightEffect.StartsWith("native/", StringComparison.Ordinal) ? NativeLightEffect?.Color == true ? "選択色" : "機器内蔵効果" : "プレビュー";
    public int SelectedLightColor
    {
        get => selectedLightColor;
        set { if (value < 0 || value >= LightPalette.Length || value == selectedLightColor) return; selectedLightColor = value; Notify(nameof(SelectedLightColor)); Notify(nameof(LightColor)); Notify(nameof(LightPreviewBrush)); }
    }
    public bool LightReverse { get => LightingDraft.Program.Reverse; set { if (!publishingLightProgram && LightHasDirection) ChangeLight(LightingDraft.Program with { Reverse = value }); } }
    public bool LightPerLed
    {
        get => LightingDraft.Program.LedColors is not null;
        set {
            if (publishingLightProgram || value == LightPerLed || NativeLightEffect is not { LedCount: > 0 } effect) return;
            var p = LightingDraft.Program; ChangeLight(p with { LedColors = value ? Enumerable.Repeat(p.Colors[0], effect.LedCount).ToArray() : null });
        }
    }
    public ICommand AddLightColor => new AsyncCommand(_ => {
        var p = LightingDraft.Program; selectedLightColor = p.Colors.Length; ChangeLight(p with { Colors = [.. p.Colors, new(255, 255, 255)] }); return Task.CompletedTask;
    }, _ => LightingIdle && !LightPerLed && NativeLightEffect is { Color: true } e && LightingDraft.Program.Colors.Length < e.MaximumColors, e => Notice = e.Message);
    public ICommand RemoveLightColor => new AsyncCommand(_ => {
        var p = LightingDraft.Program; var colors = p.Colors.Where((_, i) => i != selectedLightColor).ToArray(); selectedLightColor = Math.Min(selectedLightColor, colors.Length - 1);
        ChangeLight(p with { Colors = colors }); return Task.CompletedTask;
    }, _ => LightingIdle && !LightPerLed && NativeLightEffect is { Color: true } e && LightingDraft.Program.Colors.Length > e.MinimumColors, e => Notice = e.Message);
    private void PublishLightNative(bool programChanged)
    {
        var p = LightingDraft.Program; var colors = (p.LedColors ?? p.Colors).Select((c, i) => new DeviceLightColorChoice(i, c)).ToArray();
        selectedLightColor = Math.Clamp(selectedLightColor, 0, Math.Max(0, colors.Length - 1));
        if (!LightPalette.SequenceEqual(colors)) { LightPalette = colors; Notify(nameof(LightPalette)); }
        if (programChanged) Notify(nameof(LightColor));
        foreach (var name in new[] { nameof(SelectedLightColor), nameof(LightHasPalette), nameof(LightHasDirection), nameof(LightHasSpeed), nameof(LightSpeedMinimum), nameof(LightSpeedMaximum), nameof(CanEditLightBrightness), nameof(LightAllowsPerLed), nameof(LightReverse), nameof(LightPerLed), nameof(LightPreviewLabel), nameof(LightPreviewBrush) }) Notify(name);
    }
}
