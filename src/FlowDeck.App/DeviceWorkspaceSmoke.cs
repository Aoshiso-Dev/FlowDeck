using System.Windows;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceWorkspaceAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, Window window, string output)
    {
        var report = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); report.Add("PASS " + message); }
        try
        {
            await Settle();
            Check(model.DashboardTemperatures.Count == 2 && model.DashboardFans.Count == 10, "v2 service telemetry renders CPU GPU and two independent Hubs");
            Check(model.DashboardFans.Select(r => r.Key).Distinct().Count() == 10, "Same endpoint names have distinct device identities");
            Check(model.DashboardFans.Count(r => r.Role == "ポンプ") == 1, "Pump role follows saved configuration");
            Check(demo.Hubs.All(h => h.Writes == 0), "Opening monitor performs no output writes");
            await RunDeviceMeasurementStatesAsync(model, demo, window, output, Check);
            foreach (var theme in model.Themes)
            {
                model.Theme = theme; ThemeManager.Apply(theme); await Settle(); Capture(window, output, "devices-monitor-" + theme);
                Check(Descendants<TelemetryMeter>(window).Count(m => m.IsVisible && m.Circular) == 2, theme + " temperature rings render");
                Check(Descendants<TelemetryMeter>(window).Count(m => m.IsVisible && !m.Circular) == 10, theme + " RPM bars render");
            }
            model.Theme = "light"; model.SelectedPage = 1;
            var row = model.Cooling.Single(r => r.Draft.Target == new EndpointReference(demo.DeviceIds[1], "fan3"));
            Check(!row.CanEdit, "Released slider is read-only until a control mode is selected");
            row.Mode = "固定"; await Settle();
            var slider = Descendants<System.Windows.Controls.Slider>(window).Single(s => ReferenceEquals(s.DataContext, row));
            slider.SetCurrentValue(System.Windows.Controls.Slider.ValueProperty, 55d);
            await model.PollAsync(); await Settle();
            Check(slider.IsEnabled && slider.Value == 55 && row.Duty == 55, "WPF slider binding updates only the selected endpoint draft");
            Check(model.Cooling.Contains(row) && row.Duty == 55 && row.Draft.Dirty && row.CanApply, "Polling retains row instance and unsent slider draft");
            Check(demo.Hubs.All(h => h.Writes == 0), "Moving a slider sends no output before Apply");
            await model.OperateAsync(row);
            Check(demo.Hubs[0].Writes == 0 && demo.Hubs[1].Writes == 1, "Applying second Hub FAN3 does not write the first Hub");
            Check(row.CurrentDuty == "55%" && !row.Draft.Dirty, "Confirmed output resets draft and reports 55 percent");
            row.Mode = "自動"; await model.OperateAsync(row);
            Check(!row.Draft.Dirty && row.Draft.Automatic, "Saved temperature curve is applied to selected endpoint");
            await model.OperateAsync(row, DeviceOperation.ReleaseCooling); await model.PollAsync();
            Check(row.Duty == 70 && row.CurrentDuty == "70%" && !row.Draft.Dirty && !row.CanEdit, "Release follows fresh device output on read-only slider");
            await RunIndependentCoolingDraftsAsync(model, demo, window, Check);
            foreach (var theme in new[] { "light", "dark" })
            { model.Theme = theme; await Settle(); Capture(window, output, "devices-cooling-" + theme); }
            await RunDeviceLightingWorkspaceAsync(model, demo, window, output, Check);
            await RunDeviceDashboardSettingsAsync(model, demo, window, output, Check);
            await RunDeviceAppearanceAsync(model, demo, window, output, Check);
            await RunDeviceNameSettingsAsync(model, demo, window, output, Check);
            await RunDeviceCatalogAsync(model, demo, window, output, Check);
            demo.Hubs[0].Disconnected = true; await demo.Runtime.PollAsync(); await model.PollAsync();
            Check(model.DashboardFans.Count(r => r.Value is null) == 5 && model.DashboardFans.Count(r => r.Value is not null) == 5,
                "Disconnected Hub blanks only its five RPM readings");
            Check(model.Cooling.Where(r => r.Draft.Target.DeviceId == demo.DeviceIds[0]).All(r => !r.CanApply), "Disconnected targets cannot apply output");
            Check(model.Cooling.Contains(row) && row.CanSelectMode, "Independent Hub remains operable after other Hub disconnects");
            Check(!await model.CloseAsync() && !model.CanApplyLighting, "Unplugged owned light prevents claiming a confirmed normal exit");
            demo.Hubs[0].Disconnected = false; await demo.Runtime.PollAsync();
            Check(await model.CloseAsync(), "Explicit exit retry reauthenticates and confirms restored lighting after reconnect");
            await File.WriteAllLinesAsync(Path.Combine(output, "device-workspace-smoke.txt"), report);
            return 0;
        }
        catch (Exception e)
        {
            report.Add("FAIL " + e); await File.WriteAllLinesAsync(Path.Combine(output, "device-workspace-smoke.txt"), report);
            return 1;
        }
    }
    private static async Task RunIndependentCoolingDraftsAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, Window window, Action<bool, string> check)
    {
        var rows = new[]
        {
            model.Cooling.Single(r => r.Draft.Target == new EndpointReference(demo.DeviceIds[0], "fan1")),
            model.Cooling.Single(r => r.Draft.Target == new EndpointReference(demo.DeviceIds[0], "fan2")),
            model.Cooling.Single(r => r.Draft.Target == new EndpointReference(demo.DeviceIds[1], "fan1"))
        };
        var writes = demo.Hubs.Select(h => h.Writes).ToArray();
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i].Mode = "固定"; await Settle();
            var slider = Descendants<System.Windows.Controls.Slider>(window).Single(s => ReferenceEquals(s.DataContext, rows[i]));
            slider.SetCurrentValue(System.Windows.Controls.Slider.ValueProperty, 61d + i);
        }
        await model.PollAsync(); await Settle();
        check(demo.Hubs.Select(h => h.Writes).SequenceEqual(writes), "Editing several fan sliders performs no writes before Apply");
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var button = Descendants<System.Windows.Controls.Button>(window).Single(b => ReferenceEquals(b.DataContext, row) && b.Content?.ToString() == "適用");
            check(button.IsEnabled && row.Draft.Dirty && row.Duty == 61 + i && !row.Draft.Conflict,
                "Apply stays enabled for the unsent fan on " + row.Connection + " / " + row.Name);
            button.Command.Execute(button.CommandParameter);
            await Until(() => !row.Busy && !row.Draft.Dirty); await Settle();
            check(row.CurrentDuty == (61 + i) + "%", "The actual Apply command confirms only the selected slider value");
        }
        check(demo.Hubs[0].Writes == writes[0] + 2 && demo.Hubs[1].Writes == writes[1] + 1,
            "Sequential Apply writes each selected endpoint once across same-Hub and separate-Hub drafts");
        foreach (var row in rows) await model.OperateAsync(row, DeviceOperation.ReleaseCooling);
    }
    private static async Task RunDeviceDashboardSettingsAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, Window window, string output, Action<bool, string> check)
    {
        model.SelectedPage = 3; model.Theme = "light"; await Settle();
        var originals = model.DashboardSettings.Select(r => r.Item).ToArray();
        var row = model.DashboardSettings.First(r => r.Item.Measurement == DeviceFeature.Rpm);
        var second = model.DashboardSettings.Last(r => r.Item.Measurement == DeviceFeature.Rpm);
        var name = Descendants<DraftTextBox>(window).Single(t => ReferenceEquals(t.DataContext, row));
        name.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "前面の冷却ポンプ"); row.Role = "ポンプ"; second.Visible = false;
        var writes = demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).ToArray();
        await model.PollAsync(); await Settle();
        check(name.Text == "前面の冷却ポンプ" && row.Name == name.Text && model.DashboardSettings.Contains(row) && model.CanSaveDashboard,
            "Dashboard name input and unsaved role survive telemetry refresh");
        check(demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).SequenceEqual(writes), "Editing display metadata does not write hardware");
        model.MoveDashboard(row, 1); await model.SaveDashboardAsync();
        check(!model.DashboardDraft.Dirty && model.DashboardFans.Count == 9 && model.DashboardFans.Single(r => r.Name == row.Name).Role == "ポンプ",
            "Saved display name visibility and pump role appear in monitor");
        check(model.DashboardFans[1].Item.SensorId == DeviceDashboard.Key(row.Item.Target, row.Item.Measurement), "Saved row order follows endpoint identity");
        check(demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).SequenceEqual(writes), "Saving display settings retains live hardware output");
        foreach (var theme in new[] { "light", "dark" })
        { model.Theme = theme; await Settle(); Capture(window, output, "devices-settings-" + theme); }
        await model.ReloadDashboardAsync();
        check(model.DashboardSettings.Single(r => r.Item.Target == row.Item.Target).Name == row.Name, "Explicit reload reads persisted display configuration");
        foreach (var original in originals) model.DashboardDraft.Set(original);
        for (var index = 0; index < originals.Length; index++)
            while (Array.FindIndex(model.DashboardDraft.Items, i => i.Target == originals[index].Target && i.Measurement == originals[index].Measurement) > index)
                model.DashboardDraft.Move(originals[index].Target, originals[index].Measurement, -1);
        await model.SaveDashboardAsync(); model.SelectedPage = 0;
    }
    private static async Task RunDeviceLightingWorkspaceAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, Window window, string output, Action<bool, string> check)
    {
        model.SelectedPage = 2; model.Theme = "light"; model.SelectLights(model.Lights.Select(l => l.Target)); await Settle();
        check(model.Lights.Count == 2 && model.CanApplyLighting, "Two lighting endpoints are selectable without vendor-specific GUI branches");
        var picker = Descendants<ColorPicker>(window).Single(p => ReferenceEquals(p.DataContext, model));
        picker.Choose(System.Windows.Media.Color.FromRgb(10, 70, 220));
        var library = Descendants<System.Windows.Controls.Expander>(window).Single(e => e.Header?.ToString() == "シーン・グループ");
        library.SetCurrentValue(System.Windows.Controls.Expander.IsExpandedProperty, true); await Settle();
        var names = Descendants<DraftTextBox>(window).ToArray();
        var sceneName = names.Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "新しい照明シーンの名前");
        sceneName.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "同期用の青");
        var rows = model.Lights.ToArray(); await model.PollAsync(); await Settle();
        check(model.LightColor.B == 220 && model.LightingDraft.Dirty && rows.All(model.Lights.Contains), "Color draft and target rows survive telemetry polling");
        check(sceneName.Text == "同期用の青" && model.LightSceneName == "同期用の青", "Japanese scene name draft survives polling using DraftTextBox");
        check(demo.Hubs.All(h => h.LightingWrites == 0), "Color picking and preview send no lighting output before Apply");
        await model.ApplyLightsAsync();
        check(demo.Hubs.All(h => h.LightEffects["led1"].Colors.SequenceEqual(new[] { new RgbColor(10, 70, 220) })), "Apply writes the chosen RGB to both selected devices");
        check(model.Lights.All(l => l.Result.Contains("点灯未確認")), "GUI distinguishes sent lighting from optically confirmed lighting");
        var writes = demo.Hubs.Select(h => h.LightingWrites).ToArray();
        await model.SaveLightLibraryAsync(true); model.LightGroupName = "ケース全体"; await model.SaveLightLibraryAsync(false);
        check(model.LightScenes.Any(s => s.Name == "同期用の青") && model.LightGroups.Any(g => g.Name == "ケース全体") && demo.Hubs.Select(h => h.LightingWrites).SequenceEqual(writes),
            "Saving scene and group changes configuration without writing lights");
        library.SetCurrentValue(System.Windows.Controls.Expander.IsExpandedProperty, false);
        model.LightEffect = "breathing"; model.LightBrightness = 50; await model.ApplyLightsAsync();
        var running = demo.Runtime.State().Endpoints.Where(e => e.Target.EndpointId == "led1").ToArray();
        check(running.Length == 2 && running.All(e => e.Lighting?.Running == true) && running.Select(e => e.Lighting!.GroupId).Distinct().Count() == 1,
            "Breathing runs on a shared service clock across the selected devices");
        var preview = model.LightPreviewBrush.ToString(); await Task.Delay(180); model.TickLightingPreview();
        check(preview != model.LightPreviewBrush.ToString(), "Animated preview changes locally while service owns the actual effect");
        await model.ApplyLightsAsync(LightingAdjustmentKind.Off);
        check(demo.Runtime.State().Endpoints.Where(e => e.Target.EndpointId == "led1").All(e => e.Lighting?.LastColor == new RgbColor(0, 0, 0)), "Off blanks both synchronized lights");
        await model.ApplyLightsAsync(LightingAdjustmentKind.Restore);
        check(model.LightBrightness == 50 && model.LightEffect == "breathing", "Restore recovers applied effect and 50 percent brightness");
        foreach (var theme in new[] { "light", "dark" })
        { model.Theme = theme; await model.PollAsync(); await Settle(); Capture(window, output, "devices-lighting-" + theme); }
        model.SelectedLightScene = model.LightScenes.Single(s => s.Name == "同期用の青"); await model.RecallLightSceneAsync();
        check(model.LightEffect == "fixed" && model.LightBrightness == 100 && demo.Hubs.All(h => h.LightEffects["led1"].Colors.SequenceEqual(new[] { new RgbColor(10, 70, 220) })),
            "Saved scene restores both static chosen colors");
        model.SelectLights([new(demo.DeviceIds[1], "led1")]); model.LightColor = System.Windows.Media.Colors.Red;
        var firstWrites = demo.Hubs[0].LightingWrites; await model.ApplyLightsAsync();
        check(demo.Hubs[0].LightingWrites == firstWrites && demo.Hubs[1].LightEffects["led1"].Colors.SequenceEqual(new[] { new RgbColor(255, 0, 0) }),
            "Selecting only the second light leaves the first unchanged");
    }
}
