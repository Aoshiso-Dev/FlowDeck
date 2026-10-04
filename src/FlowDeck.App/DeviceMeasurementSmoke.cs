using System.Windows;
using System.Windows.Controls;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    private static async Task RunDeviceMeasurementStatesAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, Window window,
        string output, Action<bool, string> check)
    {
        var editor = model.DashboardSettings.Single(r => r.Name == "CPU");
        editor.Name = "CPUの入力途中";
        var writes = demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).ToArray();
        try
        {
            foreach (var (issue, age, label) in new[] {
                (DeviceMeasurementIssue.OutOfRange, 0d, "範囲外"),
                (DeviceMeasurementIssue.Unavailable, 0d, "未取得"),
                (DeviceMeasurementIssue.None, 4d, "更新停止"),
                (DeviceMeasurementIssue.None, 0d, "取得中") })
            {
                demo.CpuMeasurementIssue = issue; demo.CpuMeasurementAge = age;
                await demo.Runtime.PollAsync(); await model.PollAsync(); await Settle();
                var row = model.DashboardTemperatures.Single(r => r.Name == "CPU");
                check(row.State == label && (row.Value is not null) == (label == "取得中"), "Service measurement state reaches the monitor: " + label);
                check(model.DashboardSettings.Contains(editor) && editor.Name == "CPUの入力途中" && !model.DashboardDraft.Conflict,
                    "Measurement changes retain the pending name and device identity: " + label);
            }
            demo.CpuMeasurementIssue = DeviceMeasurementIssue.OutOfRange;
            await demo.Runtime.PollAsync(); await model.PollAsync();
            var fans = model.DashboardFans.Take(4).ToArray();
            foreach (var theme in model.Themes)
            {
                model.Theme = theme;
                // Exercise all three existing RPM templates; source classification is tested through the service above and in Core.
                model.DashboardFans[0] = fans[0] with { Value = 0, State = "0 RPM", Indicator = "bar" };
                model.DashboardFans[1] = fans[1] with { Value = null, State = "範囲外", Indicator = "circle" };
                model.DashboardFans[2] = fans[2] with { Value = null, State = "未取得", Indicator = "bar" };
                model.DashboardFans[3] = fans[3] with { Value = null, State = "更新停止", Indicator = "number" };
                await Settle(); Capture(window, output, "measurement-states-" + theme);
                var text = Descendants<TextBlock>(window).Where(t => t.IsVisible).Select(t => t.Text).ToArray();
                check(text.Count(t => t == "範囲外") >= 2 && text.Contains("未取得") && text.Contains("更新停止"),
                    theme + " shows range missing and stale labels on temperature and RPM readouts");
            }
            check(demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).SequenceEqual(writes), "Measurement-state inspection leaves cooling and lighting outputs untouched");
        }
        finally
        {
            demo.CpuMeasurementIssue = DeviceMeasurementIssue.None; demo.CpuMeasurementAge = 0;
            await demo.Runtime.PollAsync(); await model.ReloadDashboardAsync();
            check(!model.DashboardDraft.Dirty && !model.DashboardDraft.Conflict && editor.Name == "CPU",
                "Measurement preview explicitly discards its own unsaved draft before subsequent UI tests");
        }
    }
}
