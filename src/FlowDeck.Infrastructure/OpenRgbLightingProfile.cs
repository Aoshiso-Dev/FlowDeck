using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed record OpenRgbNativeChoice(int Index, uint ColorMode, uint Direction, DeviceNativeLightingEffect Effect);
internal sealed record OpenRgbLightingSurface(int? Zone, string Endpoint, string Name, int LedCount,
    int? DirectMode, int? FixedMode, OpenRgbNativeChoice[] Native, bool ControllerDirect = false);

// Describe only operations expressible by the current common lighting contract. Unknown SDK capabilities
// remain observable; they do not become arbitrary mode/resize/configuration writes.
internal static class OpenRgbLightingProfile
{
    public static string Label(string? value, string fallback, int maximum = 80)
    {
        var text = new string((value ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length == 0) text = fallback;
        if (text.Length > maximum) text = text[..maximum];
        if (char.IsHighSurrogate(text[^1])) text = text[..^1];
        return text.Length == 0 ? fallback : text;
    }
    public static bool Partitioned(OpenRgbSdkController controller) => controller.Zones.Length > 0 && controller.Leds.Length > 0 &&
        controller.Zones.All(z => (z.Flags & 1) == 0) && controller.Zones.Sum(z => (long)z.LedCount) == controller.Leds.Length;

    public static OpenRgbLightingSurface Surface(OpenRgbSdkController controller, int? zone)
    {
        if (zone is { } index && (index < 0 || index >= controller.Zones.Length || !Partitioned(controller)))
            throw new IOException("OpenRGBのゾーンとLEDの対応を確定できません");
        var modes = zone is { } z ? controller.Zones[z].Modes : controller.Modes;
        var count = zone is { } n ? (int)controller.Zones[n].LedCount : controller.Leds.Length;
        int? Find(params string[] names)
        {
            foreach (var name in names)
                for (var i = 0; i < modes.Length; i++)
                    if (modes[i].Name == name && (modes[i].Flags & 32) != 0 && (modes[i].Flags & (512 | ~4095u)) == 0 &&
                        (zone is null || (modes[i].Flags & 1024) == 0) && Writable(modes[i] with { ColorMode = 1 })) return i;
            return null;
        }
        var native = new List<OpenRgbNativeChoice>();
        for (var i = 0; i < modes.Length && native.Count < 32; i++)
        {
            var mode = modes[i];
            if ((mode.Flags & ~4095u) != 0 || (mode.Flags & 512) != 0 || zone is not null && (mode.Flags & 1024) != 0) continue;
            if ((mode.Flags & 2048) != 0 && (mode.Flags & (2 | 4 | 8)) == 0) continue;
            uint colorMode = (mode.Flags & 64) != 0 ? 2u : (mode.Flags & 32) != 0 ? 1u : (mode.Flags & 128) != 0 ? 3u : 0u;
            var minColors = colorMode == 2 ? Math.Max(1u, mode.ColorsMin) : colorMode == 1 ? 1u : 0;
            var maxColors = colorMode == 2 ? Math.Min(32u, mode.ColorsMax) : colorMode == 1 ? 1u : 0;
            if (minColors > maxColors) continue;
            uint direction = (mode.Flags & 2) != 0 ? 0u : (mode.Flags & 4) != 0 ? 2u : (mode.Flags & 8) != 0 ? 4u : 0;
            var proposed = mode with { Direction = direction, ColorMode = colorMode };
            if (!Writable(proposed)) continue;
            var id = "native/openrgb/" + SettingsTransfer.Hash(new { controller.Vendor, Model = controller.Name, controller.Type, Scope = zone is null ? "device" : "zone",
                Index = i, Effect = mode.Name, mode.Flags, mode.SpeedMin, mode.SpeedMax, mode.BrightnessMin, mode.BrightnessMax, mode.ColorsMin, mode.ColorsMax, colorMode }).ToLowerInvariant();
            var speed = (mode.Flags & 1) != 0 && mode.SpeedMin != mode.SpeedMax;
            var effect = new DeviceNativeLightingEffect(id, Label(mode.Name, "効果 " + (i + 1)), colorMode is 1 or 2, (int)minColors, (int)maxColors,
                0, speed ? 100 : 0, speed ? Percent(mode.Speed, mode.SpeedMin, mode.SpeedMax) : 0,
                Speed: speed, Direction: (mode.Flags & (2 | 4 | 8)) != 0,
                Brightness: HardwareBrightness(mode) || colorMode is 1 or 2, LedCount: (mode.Flags & 32) != 0 && colorMode is 1 or 2 ? count : 0);
            if (DeviceNativeLighting.Valid(effect)) native.Add(new(i, colorMode, direction, effect));
        }
        return new(zone, zone is { } slot ? "zone:" + slot : "device", zone is { } zoneIndex
            ? Label(controller.Zones[zoneIndex].DisplayName, Label(controller.Zones[zoneIndex].Name, "RGB " + (zoneIndex + 1))) : "全体",
            count, Find("Direct"), Find("Direct", "Custom", "Static"), native.ToArray());
    }
    private static bool Writable(OpenRgbSdkMode mode)
    {
        try { OpenRgbSdkConnection.CheckMode(mode, mode); return true; } catch (IOException) { return false; }
    }
    private static bool HardwareBrightness(OpenRgbSdkMode mode) => (mode.Flags & 16) != 0 && mode.BrightnessMin != mode.BrightnessMax;
    public static uint Scale(int percent, uint min, uint max)
    {
        if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        return (uint)Math.Round(min + ((double)max - min) * percent / 100d, MidpointRounding.AwayFromZero);
    }
    public static int Percent(uint value, uint min, uint max) => min == max ? 0 :
        (int)Math.Clamp(Math.Round(((double)value - min) * 100 / ((double)max - min), MidpointRounding.AwayFromZero), 0, 100);

    public static (int Index, OpenRgbSdkMode Mode, RgbColor[]? Leds) Native(OpenRgbSdkController controller, OpenRgbLightingSurface surface, LightingProgram program)
    {
        var choice = surface.Native.SingleOrDefault(n => n.Effect.Id == program.EffectId);
        if (choice is null || !DeviceNativeLighting.Accepts(choice.Effect, program)) throw new IOException("OpenRGBの効果・色・速度が対応範囲外です");
        var basis = (surface.Zone is { } z ? controller.Zones[z].Modes : controller.Modes)[choice.Index];
        var perLed = program.LedColors is not null || choice.ColorMode == 1;
        var softwareBrightness = !HardwareBrightness(basis) ? program.Brightness / 100d : 1d;
        var mode = basis with { ColorMode = perLed ? 1u : choice.ColorMode, Direction = choice.Direction + (program.Reverse ? 1u : 0),
            Speed = choice.Effect.Speed ? Scale(program.Speed, basis.SpeedMin, basis.SpeedMax) : basis.Speed,
            Brightness = HardwareBrightness(basis) ? Scale(program.Brightness, basis.BrightnessMin, basis.BrightnessMax) : basis.Brightness,
            Colors = !perLed && choice.ColorMode == 2 ? program.Colors.Select(c => c.Scale(softwareBrightness)).ToArray() : basis.Colors.ToArray() };
        RgbColor[]? leds = perLed ? (program.LedColors ?? Enumerable.Repeat(program.Colors[0], surface.LedCount).ToArray()).Select(c => c.Scale(softwareBrightness)).ToArray() : null;
        OpenRgbSdkConnection.CheckMode(basis, mode); return (choice.Index, mode, leds);
    }
}
