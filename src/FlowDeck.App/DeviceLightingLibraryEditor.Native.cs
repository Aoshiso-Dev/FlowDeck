using System.Windows.Input;
using FlowDeck.Core;

namespace FlowDeck.App;

public sealed partial class DeviceLightingLibraryEditor
{
    private bool publishing;
    private readonly Dictionary<string, int> selectedColors = [];
    private DeviceNativeLightingEffect[] nativeEffects = [];
    public DeviceNativeLightingEffect? NativeEffect => nativeEffects.SingleOrDefault(e => e.Id == Effect);
    private bool EditableNative => Part is { } part && DeviceNativeLighting.Accepts(NativeEffect, part.Program);
    public bool CanEditBrightness => CommonPart || EditableNative && NativeEffect!.Brightness;
    public bool HasSpeed => AnimatedPart || EditableNative && NativeEffect!.Speed;
    public int SpeedMinimum => NativeEffect?.MinimumSpeed ?? 0;
    public int SpeedMaximum => NativeEffect?.MaximumSpeed ?? 4;
    public bool HasDirection => EditableNative && NativeEffect!.Direction;
    public bool HasPalette => NativeEffect is { Color: true, MaximumColors: > 1 } || PerLed;
    public bool AllowsPerLed => EditableNative && NativeEffect is { LedCount: > 0 };
    public string? CapabilityIssue => Draft.CapabilityIssue(selectedPart);
    public DeviceLightColorChoice[] Palette { get; private set; } = [];
    public int SelectedColor
    {
        get => selectedPart is null ? 0 : Math.Clamp(selectedColors.GetValueOrDefault(selectedPart), 0, Math.Max(0, Palette.Length - 1));
        set { if (publishing || selectedPart is null || value < 0 || value >= Palette.Length || value == SelectedColor) return;
            selectedColors[selectedPart] = value; Notify(nameof(SelectedColor)); Notify(nameof(Color)); }
    }
    public bool Reverse { get => Part?.Program.Reverse == true; set { if (!publishing && Part is { } part && HasDirection) ChangeProgram(part.Program with { Reverse = value }); } }
    public bool PerLed
    {
        get => Part?.Program.LedColors is not null;
        set { if (publishing || value == PerLed || Part is not { } part || !AllowsPerLed) return;
            ChangeProgram(part.Program with { LedColors = value ? Enumerable.Repeat(part.Program.Colors[0], NativeEffect!.LedCount).ToArray() : null }); }
    }
    public ICommand AddColor => new AsyncCommand(_ => {
        var part = Part!; selectedColors[part.Id] = part.Program.Colors.Length;
        ChangeProgram(part.Program with { Colors = [.. part.Program.Colors, new(255, 255, 255)] }); return Task.CompletedTask;
    }, _ => EditableNative && !PerLed && Part!.Program.Colors.Length < NativeEffect!.MaximumColors);
    public ICommand RemoveColor => new AsyncCommand(_ => {
        var part = Part!; var colors = part.Program.Colors.Where((_, i) => i != SelectedColor).ToArray(); selectedColors[part.Id] = Math.Min(SelectedColor, colors.Length - 1);
        ChangeProgram(part.Program with { Colors = colors }); return Task.CompletedTask;
    }, _ => EditableNative && !PerLed && Part!.Program.Colors.Length > NativeEffect!.MinimumColors);
    private void PublishNativePart()
    {
        var p = Part?.Program; var colors = (p?.LedColors ?? p?.Colors ?? []).Select((c, i) => new DeviceLightColorChoice(i, c)).ToArray();
        if (!Palette.SequenceEqual(colors)) { Palette = colors; Notify(nameof(Palette)); }
        foreach (var name in new[] { nameof(SelectedColor), nameof(CanEditBrightness), nameof(HasSpeed), nameof(SpeedMinimum), nameof(SpeedMaximum), nameof(HasDirection), nameof(HasPalette), nameof(AllowsPerLed), nameof(PerLed), nameof(Reverse), nameof(CapabilityIssue) }) Notify(name);
        CommandManager.InvalidateRequerySuggested();
    }
}
