using System.Windows;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceWorkspaceAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, DeviceWorkspaceClient client, Window window, string output)
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
            await RunDeviceAppearanceAsync(model, demo, client, window, output, Check);
            await RunDeviceNameSettingsAsync(model, demo, window, output, Check);
            await RunDeviceCatalogAsync(model, demo, window, output, Check);
            var hiddenFan = model.Cooling.First(r => r.Draft.Target.DeviceId == demo.DeviceIds[0]);
            hiddenFan.Mode = "固定"; hiddenFan.Duty = 47;
            model.SelectLights(model.Lights.Select(l => l.Target));
            model.LightColor = System.Windows.Media.Colors.Magenta;
            demo.Hubs[0].Disconnected = true; await demo.Runtime.PollAsync(); await model.PollAsync();
            Check(model.DashboardFans.Count == 5 && model.DashboardFans.All(r => r.Value is not null),
                "Disconnected Hub is hidden from monitoring while the other five readings remain");
            Check(model.Devices.All(d => d.Id != demo.DeviceIds[0]) && model.VisibleCooling.All(r => r.Draft.Target.DeviceId != demo.DeviceIds[0]) &&
                model.Lights.All(l => l.Target.DeviceId != demo.DeviceIds[0]) && !model.CanApplyLighting &&
                model.LightColor == System.Windows.Media.Colors.Magenta && model.LightHasPendingChanges,
                "Disconnected hardware disappears from catalog cooling and lighting while the original lighting draft stays blocked and intact");
            Check(model.DashboardSettings.All(r => r.Item.Target.DeviceId != demo.DeviceIds[0]) &&
                model.DeviceNameChoices.All(d => d.Id != demo.DeviceIds[0]) && model.SetupChoices.All(c => c.Target.DeviceId != demo.DeviceIds[0]),
                "Disconnected hardware is hidden from monitor name and endpoint settings");
            model.SelectedPage = 1; await Settle();
            Check(!Descendants<System.Windows.Controls.Slider>(window).Any(s => ReferenceEquals(s.DataContext, hiddenFan)),
                "Disconnected cooling draft has no rendered slider");
            Capture(window, output, "devices-connected-only");
            Check(model.Cooling.Where(r => r.Draft.Target.DeviceId == demo.DeviceIds[0]).All(r => !r.CanApply), "Disconnected targets cannot apply output");
            Check(model.Cooling.Contains(row) && row.CanSelectMode, "Independent Hub remains operable after other Hub disconnects");
            demo.Hubs[0].Disconnected = false; await demo.Runtime.PollAsync(); await model.PollAsync();
            Check(model.DashboardFans.Count == 10 && model.VisibleCooling.Contains(hiddenFan) && hiddenFan.Duty == 47 && hiddenFan.Draft.Dirty &&
                model.Devices.Any(d => d.Id == demo.DeviceIds[0]) && model.Lights.Any(l => l.Target.DeviceId == demo.DeviceIds[0]),
                "Reconnection restores device rows and preserves the unsent cooling draft");
            hiddenFan.Reset();
            Check(model.LightColor == System.Windows.Media.Colors.Magenta && model.LightHasPendingChanges &&
                model.Lights.All(l => l.Selected) && !model.CanApplyLighting,
                "Reconnected lighting retains its unsent draft and requires explicit reload after a generation change");
            model.ReloadLights.Execute(null);
            demo.Hubs[0].Disconnected = true; await demo.Runtime.PollAsync(); await model.PollAsync();
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
        check(model.Lights.Count == 2 && model.LightingDraft.CanApply && !model.LightHasPendingChanges && !model.CanApplyLighting,
            "Two lighting endpoints are ready while unchanged lighting keeps Apply disabled");
        var picker = Descendants<ColorPicker>(window).Single(p => ReferenceEquals(p.DataContext, model));
        picker.Choose(System.Windows.Media.Color.FromRgb(10, 70, 220));
        var advanced = Descendants<System.Windows.Controls.Expander>(window).Single(e =>
            System.Windows.Automation.AutomationProperties.GetAutomationId(e) == "LightingAdvancedSection");
        advanced.SetCurrentValue(System.Windows.Controls.Expander.IsExpandedProperty, true); await Settle();
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
        advanced.SetCurrentValue(System.Windows.Controls.Expander.IsExpandedProperty, false);
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
        await RunDeviceLightingRecoveryAsync(model, demo, window, output, check);
    }
    private static async Task RunDeviceLightingRecoveryAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, Window window, string output, Action<bool, string> check)
    {
        var failed = model.Lights.Single(l => l.Target == new EndpointReference(demo.DeviceIds[0], "led1"));
        var other = model.Lights.Single(l => l.Target == new EndpointReference(demo.DeviceIds[1], "led1"));
        var fanWrites = demo.Hubs.Select(h => h.Writes).ToArray();
        model.SelectLights([failed.Target]); model.LightColor = System.Windows.Media.Colors.Lime;
        demo.Hubs[0].FailNextLightingWrite = true; await model.ApplyLightsAsync(); await Settle();
        check(failed.NeedsRecovery && !failed.NeedsSetup && !failed.Ready && !model.CanApplyLighting && model.StopLights.CanExecute(null),
            "An uncertain lighting write blocks new output and exposes recovery without treating the saved port as unconfigured");
        var setup = Descendants<System.Windows.Controls.Button>(window).Single(b => ReferenceEquals(b.DataContext, failed) && Equals(b.CommandParameter, failed));
        check(!setup.IsVisible && !model.OpenLightingSetup.CanExecute(failed), "A lighting failure never offers the first-use port setup action");
        await RunTransientLightingAvailabilityAsync(model, demo, window, output, failed, other, check);

        var beforePath = Path.Combine(output, "lighting-recovery-before.flowdeck.json");
        var afterPath = Path.Combine(output, "lighting-recovery-after-reload.flowdeck.json");
        await model.ExportSettingsToAsync(beforePath);
        var writes = demo.Hubs.Select(h => h.LightingWrites).ToArray();
        model.ReloadLights.Execute(null); model.SelectLights([failed.Target, other.Target]); await model.PollAsync(); await Settle();
        check(model.LightRecoveryRequired && !model.CanApplyLighting && !model.CanAdjustLighting && model.ReloadLights.CanExecute(null),
            "Reloading current settings and selecting a healthy light alongside a failed light retains the recovery guard");
        var stop = Descendants<System.Windows.Controls.Button>(window).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "選択した照明の制御を終了");
        var library = Descendants<System.Windows.Controls.Expander>(window).Single(e => Equals(e.Header, "シーン・グループ"));
        var advanced = Descendants<System.Windows.Controls.Expander>(window).Single(e =>
            System.Windows.Automation.AutomationProperties.GetAutomationId(e) == "LightingAdvancedSection");
        check(!advanced.IsExpanded && !library.IsExpanded && stop.IsVisible && stop.IsEnabled && Equals(stop.Content, "終了を再試行") && model.LightRecoveryMessage.Contains("保存済みの終了設定"),
            "Recovery remains visible outside collapsed lighting details and explains that the saved exit setting will be sent to selected lights");
        stop.BringIntoView(); await Settle(); Capture(window, output, "devices-lighting-recovery");
        model.SelectAllLights.Execute(null); await Settle();
        check(model.LightingDraft.Targets.SequenceEqual(new[] { other.Target }) && model.LightingDraft.CanApply &&
            !model.LightHasPendingChanges && !model.CanApplyLighting && !model.LightRecoveryRequired,
            "Selecting usable lights excludes the failed port and reads a healthy device without creating a pending edit");
        model.SelectedPage = 3; await Settle(); model.SelectedPage = 2; model.SelectLights([failed.Target]); model.ReloadLights.Execute(null);
        await model.PollAsync(); await model.ExportSettingsToAsync(afterPath); await Settle();
        var before = (await DeviceSettingsArchives.ReadAsync(beforePath)).Configuration;
        var after = (await DeviceSettingsArchives.ReadAsync(afterPath)).Configuration;
        check(System.Text.Json.JsonSerializer.Serialize(before, Contract.Json) == System.Text.Json.JsonSerializer.Serialize(after, Contract.Json) &&
            writes.SequenceEqual(demo.Hubs.Select(h => h.LightingWrites)) && failed.NeedsRecovery && !failed.NeedsSetup,
            "Reload, target selection, and page navigation preserve saved settings and perform no recovery or lighting output");

        stop.Command.Execute(stop.CommandParameter); await Until(() => model.LightingIdle); await Settle();
        check(!failed.NeedsRecovery && failed.Ready && demo.Hubs[0].LightEffects["led1"].Mode == "off" &&
            demo.Hubs[0].LightingWrites == writes[0] + 1 && demo.Hubs[1].LightingWrites == writes[1],
            "Explicit recovery sends the saved off action once to the selected DEMO port and clears only its failure");
        model.LightColor = System.Windows.Media.Color.FromRgb(11, 70, 220); check(model.CanApplyLighting, "Recovered lighting accepts another explicit color edit");
        await model.ApplyLightsAsync();
        check(demo.Hubs[0].LightEffects["led1"].Colors.SequenceEqual(new[] { new RgbColor(11, 70, 220) }) && !failed.NeedsRecovery &&
            demo.Hubs[1].LightingWrites == writes[1] && fanWrites.SequenceEqual(demo.Hubs.Select(h => h.Writes)),
            "Applying after recovery changes only the selected light while other lighting and all cooling outputs remain unchanged");
    }
    private static async Task RunTransientLightingAvailabilityAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, Window window,
        string output, DeviceLightRow failed, DeviceLightRow transient, Action<bool, string> check)
    {
        const string reason = "照明の状態を一時的に読み取れません。再取得を待っています";
        var writes = demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).ToArray();
        var stop = Descendants<System.Windows.Controls.Button>(window).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "選択した照明の制御を終了");
        try
        {
            model.ReloadLights.Execute(null); model.SelectLights([transient.Target]);
            model.RenderTransientLightingStateForSmoke(transient.Target, reason); await Settle();
            check(!model.CanApplyLighting && model.LightEditStatus == reason && !transient.Ready &&
                !transient.NeedsSetup && !transient.NeedsRecovery && !model.LightRecoveryRequired && model.StopLights.CanExecute(null),
                "A temporary lighting observation failure blocks Apply without requesting port setup or recovery and still permits Stop");
            var setup = Descendants<System.Windows.Controls.Button>(window).Single(b => ReferenceEquals(b.DataContext, transient) && Equals(b.CommandParameter, transient));
            var apply = Descendants<System.Windows.Controls.Button>(window).Single(b => ReferenceEquals(b.DataContext, model) && Equals(b.Content, "適用") && b.IsVisible);
            check(!setup.IsVisible && !model.OpenLightingSetup.CanExecute(transient) && !apply.IsEnabled && stop.IsVisible && stop.IsEnabled &&
                Equals(stop.Content, "制御終了") && model.LightRecoveryMessage.Length == 0,
                "The transient-only editor disables Apply, hides setup, and keeps the normal Stop label without a misleading recovery message");
            stop.BringIntoView(); await Settle(); Capture(window, output, "devices-lighting-transient");

            var originalRows = model.Lights.ToArray();
            try
            {
                // The edit problem follows selection order; the recovery notice must cover the failed light in either order.
                foreach (var first in new[] { failed, transient })
                {
                    await model.PollAsync(); model.SelectLights([transient.Target]);
                    model.Lights.Move(model.Lights.IndexOf(first), 0);
                    model.SelectLights([failed.Target, transient.Target]); model.RenderTransientLightingStateForSmoke(transient.Target, reason); await Settle();
                    check(model.LightingDraft.Targets.First() == first.Target &&
                        failed.NeedsRecovery && !failed.NeedsSetup && !transient.NeedsRecovery && !transient.NeedsSetup &&
                        !model.CanApplyLighting && model.StopLights.CanExecute(null) && stop.IsEnabled && Equals(stop.Content, "終了を再試行") &&
                        model.LightRecoveryMessage.Contains("保存済みの終了設定") &&
                        (ReferenceEquals(first, failed) ? model.LightEditStatus.Contains("終了処理") : model.LightEditStatus == reason),
                        "Mixing a genuine failed DEMO light with a transient unavailable light preserves Stop and reports recovery only for the genuine failure, with " +
                        (ReferenceEquals(first, failed) ? "the failed light first" : "the transient light first"));
                }
            }
            finally
            {
                for (var i = 0; i < originalRows.Length; i++) model.Lights.Move(model.Lights.IndexOf(originalRows[i]), i);
            }

            model.Render(); model.SelectLights([transient.Target]); await Settle();
            check(transient.Ready && model.LightingDraft.CanApply && !model.LightHasPendingChanges && !model.CanApplyLighting &&
                !model.LightRecoveryRequired && model.LightRecoveryMessage.Length == 0 && failed.NeedsRecovery,
                "A fresh ready observation restores edit readiness without inventing pending changes or clearing another light's recovery requirement");
            var current = demo.Runtime.State().Inventory.Devices.Single(d => d.Device.Id == transient.Target.DeviceId).Device.Endpoints.Single(e => e.Id == transient.Target.EndpointId);
            check(writes.SequenceEqual(demo.Hubs.Select(h => (h.Writes, h.LightingWrites))) &&
                current.Capabilities.Single(c => c.Feature == DeviceFeature.FixedColor).Usable,
                "The UI-only transient fixture changes neither service capabilities nor any lighting or cooling output");
        }
        finally { model.Render(); model.SelectLights([failed.Target]); }
    }
}

public sealed partial class DeviceWorkspaceViewModel
{
    // UI-only snapshot fixture: retain the DEMO Hub identity and the separate real DEMO write failure.
    // This renders a copy through the production UI path and never sends a command or mutates the client snapshot.
    internal void RenderTransientLightingStateForSmoke(EndpointReference target, string reason)
    {
        if (!client.IsDemo) throw new InvalidOperationException("一時状態の画面試験にはDEMO接続が必要です");
        var snapshot = client.Snapshot ?? throw new InvalidOperationException("DEMOの状態がありません");
        var device = snapshot.State.Inventory.Devices.Single(d => d.Device.Id == target.DeviceId);
        _ = device.Device.Endpoints.Single(e => e.Id == target.EndpointId && e.Kind == EndpointKind.Lighting);
        var devices = snapshot.State.Inventory.Devices.Select(d => d != device ? d : d with { Device = d.Device with {
            Endpoints = d.Device.Endpoints.Select(e => e.Id != target.EndpointId ? e : e with {
                Capabilities = e.Capabilities.Select(c => c.Feature is DeviceFeature.FixedColor or DeviceFeature.NativeEffect or DeviceFeature.ColorFrames
                    ? c with { Availability = DeviceAvailability.TemporarilyUnavailable, Reason = reason } : c).ToArray() }).ToArray() } }).ToArray();
        RenderLighting(snapshot with { State = snapshot.State with { Inventory = snapshot.State.Inventory with { Devices = devices } } });
    }
}
