using Microsoft.Win32;
using FlowDeck.Infrastructure;
using System.Windows;
using System.Windows.Media;

namespace FlowDeck.App;

public static class ThemeManager
{
    private static string currentAccent = DeviceAccentColor.Default;

    public static void Apply(string setting, string? accentColor = null)
    {
        if (accentColor is not null) currentAccent = DeviceAccentColor.Normalize(accentColor);
        var dark = setting == "dark";
        if (setting == "system")
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        var colors = dark
            ? new[] { "#181A1F", "#1E2026", "#14161B", "#ECEEF4", "#ADB3C0", "#333740", "#B69AE8", "#30283F", "#E7BF80", "#22252C", "#3C414C", "#7752B2" }
            : new[] { "#F7F8FA", "#FFFFFF", "#F0F2F6", "#303442", "#667085", "#E0E4EB", "#7952AD", "#EEE8F6", "#8C5C10", "#F4F6F9", "#E1E5ED", "#7650A6" };
        var keys = new[] { "BackgroundBrush", "PanelBrush", "SidebarBrush", "TextBrush", "MutedBrush", "BorderBrush", "AccentBrush", "AccentSoftBrush", "WarningBrush", "InsetBrush", "TrackBrush", "PrimaryBrush" };
        for (var i = 0; i < keys.Length; i++) SetBrush(keys[i], Parse(colors[i]));
        SetBrush("PrimaryTextBrush", Colors.White);
        // Keep the original purple palette for existing users. Other hues are adjusted
        // for readable labels on every surface, independently of the button fill.
        if (currentAccent == DeviceAccentColor.Default) return;
        var selected = Parse(currentAccent);
        var soft = Mix(Parse(colors[1]), selected, dark ? 0.18 : 0.12);
        var surfaces = new[] { Parse(colors[0]), Parse(colors[1]), Parse(colors[2]), Parse(colors[9]), soft };
        var accent = selected;
        for (var step = 0; step <= 255; step++)
        {
            accent = Mix(selected, dark ? Colors.White : Colors.Black, step / 255d);
            if (surfaces.All(surface => Contrast(accent, surface) >= 4.5)) break;
        }
        var ink = Colors.Black;
        SetBrush("AccentBrush", accent);
        SetBrush("AccentSoftBrush", soft);
        SetBrush("PrimaryBrush", selected);
        SetBrush("PrimaryTextBrush", Contrast(selected, Colors.White) >= Contrast(selected, ink) ? Colors.White : ink);
    }

    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);
    private static void SetBrush(string key, Color color) => Application.Current.Resources[key] = new SolidColorBrush(color);
    private static Color Mix(Color start, Color end, double amount) => Color.FromRgb(
        (byte)Math.Round(start.R + (end.R - start.R) * amount),
        (byte)Math.Round(start.G + (end.G - start.G) * amount),
        (byte)Math.Round(start.B + (end.B - start.B) * amount));
    private static double Contrast(Color first, Color second)
    {
        static double Linear(byte channel) => channel / 255d <= 0.04045 ? channel / 255d / 12.92 : Math.Pow((channel / 255d + 0.055) / 1.055, 2.4);
        static double Luminance(Color color) => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}
