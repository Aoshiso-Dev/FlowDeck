using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace FlowDeck.App;

public static class ThemeManager
{
    public static void Apply(string setting)
    {
        var dark = setting == "dark";
        if (setting == "system")
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        var colors = dark
            ? new[] { "#18191D", "#1D1E23", "#141519", "#EEEFF5", "#A2A5B3", "#303138", "#B296E5", "#2B2539", "#E5BD7C", "#24252B", "#383A43", "#7450AE" }
            : new[] { "#FFFFFF", "#FFFFFF", "#F5F5F8", "#373847", "#747888", "#E9EAEE", "#8055B2", "#F0EAF7", "#916016", "#F7F7FA", "#E8E9EE", "#7950AA" };
        var keys = new[] { "BackgroundBrush", "PanelBrush", "SidebarBrush", "TextBrush", "MutedBrush", "BorderBrush", "AccentBrush", "AccentSoftBrush", "WarningBrush", "InsetBrush", "TrackBrush", "PrimaryBrush" };
        for (var i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
}
