using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    private static async Task RunDeviceAccentAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, DeviceWorkspaceClient client, Window window, string output, Action<bool, string> check)
    {
        var originalAccent = model.AccentColor;
        var originalTheme = model.Theme;
        var originalPage = model.SelectedPage;
        var originalTargets = model.LightingDraft.Targets.ToArray();
        var originalWindow = model.WindowPreferences;
        var generation = client.Snapshot!.Configuration.Generation;
        var configuration = SettingsTransfer.Hash(client.Snapshot.Configuration.Configuration);
        var writes = demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).ToArray();

        // Keep the same controls alive: a resource update must repaint an already open page.
        model.SelectedPage = 1; await Settle();
        var primary = Descendants<Button>(window).First(b => AutomationProperties.GetAutomationId(b) == "CoolingApplyButton");
        model.SelectedPage = 0; await Settle();
        var nav = Descendants<Button>(window).Single(b => b.Content?.ToString() == "モニター" && b.CommandParameter?.ToString() == "0");
        var meters = Descendants<TelemetryMeter>(window).Where(m => m.IsVisible).ToArray();
        var choice = Descendants<ComboBox>(window).Single(c => AutomationProperties.GetAutomationId(c) == "AccentColorChoice");
        check(choice.IsVisible && Descendants<Button>(window).Any(b => b.IsVisible && AutomationProperties.GetAutomationId(b) == "CustomAccentColorButton") &&
            model.AccentChoices.Any(c => c.Hex == "#A3D936") && model.AccentChoices.Any(c => c.Hex == "#A079EC"),
            "Appearance exposes lime, original purple and a custom accent action");
        model.AccentColor = "#A079EC"; await Settle();
        var purple = AccentSmokeColor(nav.Foreground);
        choice.SetCurrentValue(ComboBox.SelectedValueProperty, "#A3D936"); await Settle();
        check(model.AccentColor == "#A3D936" && AccentSmokeColor(nav.Foreground) != purple,
            "Selecting lime in the actual accent ComboBox immediately changes the existing selected navigation text");

        foreach (var theme in new[] { "light", "dark" })
        {
            model.Theme = theme; await Settle();
            check(AccentSmokeColor(nav.Foreground) == AccentSmokeResource("AccentBrush") &&
                AccentSmokeColor(nav.Background) == AccentSmokeResource("AccentSoftBrush") &&
                meters.Length > 0 && meters.All(m => AccentSmokeColor(m.Fill) == AccentSmokeResource("AccentBrush")) &&
                AccentSmokeColor(primary.Background) == AccentSmokeResource("PrimaryBrush") &&
                AccentSmokeColor(primary.Foreground) == AccentSmokeResource("PrimaryTextBrush"),
                theme + " existing navigation, telemetry meters and primary buttons repaint with the selected accent");
            Capture(window, output, "devices-accent-lime-" + theme);
        }

        // Compare the rendered text/background colors, including colors far outside the presets.
        var colors = model.AccentChoices.Select(c => c.Hex).Concat(new[] { "#FFFFFF", "#000000" }).Distinct().ToArray();
        foreach (var theme in new[] { "light", "dark" })
        {
            model.Theme = theme;
            foreach (var accent in colors)
            {
                model.AccentColor = accent; await Settle();
                var ink = AccentSmokeResource("AccentBrush");
                check(AccentSmokeContrast(ink, AccentSmokeResource("AccentSoftBrush")) >= 4.5 &&
                    AccentSmokeContrast(ink, AccentSmokeResource("BackgroundBrush")) >= 4.5 &&
                    AccentSmokeContrast(AccentSmokeResource("PrimaryTextBrush"), AccentSmokeResource("PrimaryBrush")) >= 4.5,
                    theme + " " + accent + " keeps accent navigation/text and primary labels at 4.5:1 contrast or better");
            }
        }

        await RunAccentCheckboxSmokeAsync(model, window, check);
        model.AccentColor = "#A3D936";
        foreach (var theme in new[] { "system", "light", "dark", "system" })
        {
            model.Theme = theme;
            var before = AccentSmokeResource("PrimaryBrush");
            ThemeManager.Apply(theme); model.ApplySystemTheme(); await Settle();
            check(model.AccentColor == "#A3D936" && AccentSmokeResource("PrimaryBrush") == before,
                theme + " theme refresh preserves the selected lime accent");
        }

        model.SelectedPage = originalPage;
        var presets = model.AccentChoices.Select(c => c.Hex).ToArray();
        foreach (var color in presets) choice.SetCurrentValue(ComboBox.SelectedValueProperty, color);
        model.Theme = "light"; model.Theme = "dark";
        await model.FlushUiAsync();
        var local = new DeviceUiPreferencesStore(Path.Combine(output, "ui.json"));
        var saved = local.Load();
        check(local.AccentColor == presets[^1] && saved.Theme == "dark",
            "Rapid accent and theme selections persist the latest choices in input order");

        const string custom = "#27B4A8";
        model.AccentColor = custom; await Settle(); await model.FlushUiAsync();
        check(model.AccentChoices.Any(c => c.Hex == custom) && choice.SelectedValue?.ToString() == custom,
            "A custom accent stays visible as the selected color instead of leaving the ComboBox blank");
        var accepted = AccentSmokeResource("PrimaryBrush");
        foreach (var invalid in new[] { "", "red", "#FFF", "#GG1234", "#11223344" }) model.AccentColor = invalid;
        await Settle(); await model.FlushUiAsync();
        saved = local.Load();
        check(model.AccentColor == custom && local.AccentColor == custom && AccentSmokeResource("PrimaryBrush") == accepted,
            "Invalid accent strings leave the valid custom color and persisted preference unchanged");
        check(saved.Left == originalWindow.Left && saved.Top == originalWindow.Top && saved.Width == originalWindow.Width && saved.Height == originalWindow.Height &&
            local.Workspace.SelectedPage == originalPage && model.SelectedPage == originalPage &&
            local.Workspace.LightingTargets!.SequenceEqual(originalTargets) && model.LightingDraft.Targets.SequenceEqual(originalTargets),
            "Accent changes preserve window geometry, navigation and lighting target selection");

        // The DEMO service has a single GUI pipe; reuse its connected client without
        // reconnecting, closing or disposing the running workspace's session.
        var restored = new DeviceWorkspaceViewModel(client, new DeviceUiPreferencesStore(local.PathName));
        await restored.InitializeAsync(); await Settle();
        check(restored.AccentColor == custom && restored.Theme == "dark" && restored.SelectedPage == originalPage &&
            restored.WindowPreferences == saved && restored.LightingDraft.Targets.SequenceEqual(originalTargets),
            "A newly initialized workspace restores the custom accent together with the existing per-user appearance and selection");
        await restored.FlushUiAsync();
        await client.RefreshAsync();
        check(client.Snapshot!.Configuration.Generation == generation && SettingsTransfer.Hash(client.Snapshot.Configuration.Configuration) == configuration &&
            demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).SequenceEqual(writes),
            "Accent selection, theme changes and preference reload leave service configuration and all DEMO hardware outputs unchanged");

        model.AccentColor = originalAccent; model.Theme = originalTheme;
        ThemeManager.Apply(model.Theme, model.AccentColor);
        await model.FlushUiAsync(); await Settle();
    }

    private static async Task RunAccentCheckboxSmokeAsync(DeviceWorkspaceViewModel model, Window window, Action<bool, string> check)
    {
        var view = Descendants<DeviceWorkspaceView>(window).Single();
        var root = (Panel)view.Content;
        // Attach an unbound probe to the real resource tree so theme changes reach
        // the actual application template without modifying any user setting.
        var box = new CheckBox { Style = (Style)window.FindResource(typeof(CheckBox)), IsThreeState = true, Visibility = Visibility.Collapsed };
        root.Children.Add(box);
        try
        {
            box.ApplyTemplate();
            var border = (Border)box.Template.FindName("CheckBorder", box);
            var tick = (System.Windows.Shapes.Path)box.Template.FindName("Tick", box);
            var dash = (Border)box.Template.FindName("Dash", box);
            foreach (var theme in new[] { "light", "dark" })
            foreach (var accent in new[] { "#FFFFFF", "#A3D936" })
            {
                model.Theme = theme; model.AccentColor = accent;
                box.IsChecked = true; await Settle();
                check(tick.Visibility == Visibility.Visible && dash.Visibility == Visibility.Collapsed &&
                    AccentSmokeColor(border.Background) == AccentSmokeResource("PrimaryBrush") &&
                    AccentSmokeContrast(AccentSmokeColor(tick.Stroke), AccentSmokeColor(border.Background)) >= 4.5,
                    theme + " " + accent + " checked CheckBox tick stays visible against its actual accent fill");
                box.IsChecked = null; await Settle();
                check(tick.Visibility == Visibility.Collapsed && dash.Visibility == Visibility.Visible &&
                    AccentSmokeColor(border.Background) == AccentSmokeResource("PrimaryBrush") &&
                    AccentSmokeContrast(AccentSmokeColor(dash.Background), AccentSmokeColor(border.Background)) >= 4.5,
                    theme + " " + accent + " indeterminate CheckBox dash stays visible against its actual accent fill");
            }
        }
        finally { root.Children.Remove(box); }
    }

    private static Color AccentSmokeResource(string key) => AccentSmokeColor((Brush)Application.Current.Resources[key]);
    private static Color AccentSmokeColor(Brush brush) => ((SolidColorBrush)brush).Color;
    private static double AccentSmokeContrast(Color first, Color second)
    {
        static double Linear(byte component)
        {
            var value = component / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        static double Luminance(Color color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
