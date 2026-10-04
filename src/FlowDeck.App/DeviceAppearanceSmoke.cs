using System.Windows;
using System.Windows.Controls;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    private static async Task RunDeviceAppearanceAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, Window window, string output, Action<bool, string> check)
    {
        model.SelectedPage = 3; await Settle();
        var originals = model.DashboardSettings.Select(r => r.Item).ToArray();
        var fan = model.DashboardSettings.First(r => r.Item.Measurement == DeviceFeature.Rpm);
        var otherFan = model.DashboardSettings.Where(r => r.Item.Measurement == DeviceFeature.Rpm).Skip(1).First();
        var temperatures = model.DashboardSettings.Where(r => r.Item.Measurement == DeviceFeature.Temperature).ToArray();
        var panel = Descendants<Expander>(window).Single(e => ReferenceEquals(e.DataContext, fan) && e.Header?.ToString() == "表示形式・目盛");
        panel.SetCurrentValue(Expander.IsExpandedProperty, true); await Settle();
        var selector = Descendants<ComboBox>(window).Single(c => ReferenceEquals(c.DataContext, fan) && System.Windows.Automation.AutomationProperties.GetName(c) == "インジケーターの形式");
        selector.SetCurrentValue(ComboBox.SelectedItemProperty, "円形");
        var automatic = Descendants<CheckBox>(window).Single(c => ReferenceEquals(c.DataContext, fan) && c.Content?.ToString() == "表示目盛を自動調整");
        automatic.SetCurrentValue(CheckBox.IsCheckedProperty, false); await Settle();
        var slider = Descendants<Slider>(window).Single(s => ReferenceEquals(s.DataContext, fan));
        slider.SetCurrentValue(Slider.ValueProperty, 4200d);
        otherFan.Indicator = "数値"; temperatures[0].Indicator = "バー"; temperatures[0].AutomaticRange = false; temperatures[0].Maximum = 80; temperatures[1].Indicator = "数値";
        await model.PollAsync(); await Settle();
        check(fan.Indicator == "円形" && fan.Maximum == 4200 && slider.IsEnabled && model.CanSaveDashboard, "Indicator and range slider drafts survive polling");
        var writes = demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).ToArray();
        await model.SaveDashboardAsync(); model.SelectedPage = 0; await Settle();
        var fanReading = model.DashboardFans.Single(r => r.Item.SensorId == DeviceDashboard.Key(fan.Item.Target, DeviceFeature.Rpm));
        check(fanReading.Circular && fanReading.ScaleMaximum == 4200 && fanReading.Value is not null, "Saved RPM ring uses selected display scale and keeps numeric telemetry");
        var meters = Descendants<TelemetryMeter>(window).Where(m => m.IsVisible).ToArray();
        check(meters.Length == 10 && meters.Count(m => m.Circular) == 1 && meters.Any(m => m.Maximum == 80), "Mixed circle bar and number views render without hidden meters showing");
        check(demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).SequenceEqual(writes), "Changing display scales performs no cooling or lighting output writes");
        foreach (var theme in new[] { "light", "dark" })
        { model.Theme = theme; await Settle(); Capture(window, output, "devices-mixed-indicators-" + theme); }
        model.Theme = "system"; model.Theme = "light"; model.Theme = "dark"; await model.FlushUiAsync();
        var local = new DeviceUiPreferencesStore(Path.Combine(output, "ui.json"));
        check(local.Load(new("light")).Theme == "dark" && model.ThemeChoices.Select(c => c.Name).SequenceEqual(new[] { "システム", "ライト", "ダーク" }),
            "Latest theme choice persists locally and all three localized options remain available");
        await model.SaveWindowAsync(new Rect(120, 100, 1120, 820));
        check(local.Load() is { Theme: "dark", Left: 120, Top: 100, Width: 1120, Height: 820 }, "Window geometry is saved with user appearance independently of service configuration");
        model.SelectedPage = 3; panel.SetCurrentValue(Expander.IsExpandedProperty, false);
        foreach (var original in originals) model.DashboardDraft.Set(original);
        await model.SaveDashboardAsync(); model.SelectedPage = 0;
    }
}
