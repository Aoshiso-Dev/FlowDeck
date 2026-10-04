using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceM5SettingsAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, DeviceWorkspaceClient client, Window window, string output)
    {
        var report = new List<string>();
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); report.Add("PASS " + label); }
        try
        {
            model.SelectedPage = 3; model.M5Expanded = true; await Settle();
            var panel = Descendants<DeviceM5SettingsView>(window).Single(); var editor = model.M5Editor ?? throw new InvalidOperationException("M5 editor missing");
            ComboBox Combo(string name) => Descendants<ComboBox>(panel).Single(c => AutomationProperties.GetName(c) == name);
            Check(model.M5SettingsSupported && !model.CanSaveM5 && editor.Hub?.Binding is null && editor.Cpu?.Target is null && editor.Gpu?.Target is null,
                "M5GO begins with no automatically selected hardware or sensor roles");
            Check(editor.Hubs.Count == 3 && !editor.CanAutoConnect && demo.Hubs.All(h => h.Writes == 0), "Two explicit Hub choices and an unselected choice do not connect or actuate hardware");
            Combo("M5GOのHub").SetCurrentValue(Selector.SelectedItemProperty, editor.Hubs.Single(h => h.Binding?.DeviceId == demo.DeviceIds[0]));
            Combo("M5GOのCPU温度").SetCurrentValue(Selector.SelectedItemProperty, editor.Cpus.Single(c => c.Target is not null));
            Combo("M5GOのGPU温度").SetCurrentValue(Selector.SelectedItemProperty, editor.Gpus.Single(c => c.Target is not null));
            Check(model.CanSaveM5 && !model.M5Draft.Settings.Selection.HasHubControl(), "Monitoring selection is saveable without any selected fan, lighting or preset controls");
            await model.SaveM5Async(); await Settle();
            var monitor = new DeviceM5Control(client, model.M5Draft.Settings.Selection).ReadM5State();
            Check(monitor.Snapshot.HubReady && monitor.Snapshot.Channels.Any(c => c.Rpm is not null) && monitor.Snapshot.Sensors.All(s => s.Celsius is not null) &&
                monitor.Snapshot.Readiness != "制御可能", "Saved display settings expose live temperature and RPM while panel fan controls remain unavailable");
            Check(demo.Hubs.All(h => h.Writes == 0 && h.LightingWrites == 0), "Saving a monitoring-only M5 selection sends no fan or lighting output");
            panel.BringIntoView(); await Settle(); Capture(window, output, "m5-settings-monitoring");
            var fan = model.Cooling.Single(r => r.Draft.Target == new EndpointReference(demo.DeviceIds[0], "fan1"));
            fan.Mode = "固定"; fan.Duty = 55; await model.OperateAsync(fan); var writes = demo.Hubs[0].Writes;
            Combo("M5GOのHub").SetCurrentValue(Selector.SelectedItemProperty, editor.Hubs.Single(h => h.Binding?.DeviceId == demo.DeviceIds[0])); await Settle();
            Check(editor.HasHub && !editor.Fans.Any(p => p.Selected) && !editor.Leds.Any(p => p.Selected), "Selecting a Hub keeps every control port unselected");
            Descendants<CheckBox>(panel).Single(c => c.DataContext is DeviceM5PortEditor { Id: "fan1" }).SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Descendants<CheckBox>(panel).Single(c => c.DataContext is DeviceM5PortEditor { Id: "fan3" }).SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Descendants<CheckBox>(panel).Single(c => c.DataContext is DeviceM5PortEditor { Id: "led1" }).SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Combo("M5GOのCPU温度").SetCurrentValue(Selector.SelectedItemProperty, editor.Cpus.Single(c => c.Target is not null));
            Combo("M5GOのGPU温度").SetCurrentValue(Selector.SelectedItemProperty, editor.Gpus.Single(c => c.Target is not null)); await Settle();
            Descendants<ComboBox>(panel).Single(c => c.DataContext is DeviceM5ProfileEditor { Slot: "balanced" }).SetCurrentValue(Selector.SelectedIndexProperty, editor.Profiles.Single(p => p.Slot == "balanced").Choices.ToList().FindIndex(c => c.Id == "balanced")); await Settle();
            Check(model.M5Draft.Settings.Selection.Fans.SequenceEqual(new[] { "fan1", "fan3" }) && editor.Profiles.Single(p => p.Slot == "balanced").Selected == "balanced",
                "Real controls retain exact physical port numbers and the common temperature profile");
            Descendants<Expander>(panel).Single(e => e.Header?.ToString() == "接続・レイアウト").IsExpanded = true; await Settle();
            var port = Descendants<DraftTextBox>(panel).Single(t => AutomationProperties.GetName(t) == "M5GOのUSBポート");
            port.Focus(); port.Text = "COM14"; port.CaretIndex = port.Text.Length;
            Combo("M5GOの接続方式").SetCurrentValue(Selector.SelectedValueProperty, "auto"); Combo("M5GOのレイアウト").SetCurrentValue(Selector.SelectedItemProperty, "B");
            for (var i = 0; i < 3; i++) await model.PollAsync(); await Settle();
            Check(ReferenceEquals(editor, model.M5Editor) && port.Text == "COM14" && editor.Layout == "B" && model.CanSaveM5,
                "Polling retains the COM input, layout, selected ports and the editor instance");
            Check(demo.Hubs[0].Writes == writes && fan.CurrentDuty == "55%", "Editing settings never sends cooling or lighting commands");
            await model.SaveM5Async(); await Settle();
            Check(!model.M5Draft.Dirty && !model.CanSaveM5 && model.M5Message.Contains("保存"), "Saving persists the M5 settings and clears the edit state");
            Check(demo.Hubs[0].Writes == writes && fan.CurrentDuty == "55%" && demo.Hubs[1].Writes == 0 && demo.Hubs.All(h => h.LightingWrites == 0),
                "Persisting M5 settings preserves active cooling and both Hub lighting states");
            foreach (var theme in new[] { "light", "dark" }) { model.Theme = theme; panel.BringIntoView(); await Settle(); Capture(window, output, "m5-settings-" + theme); }
            Check(model.Theme == "dark" && model.Themes.Length == 3, "System, light and dark appearance remain available");
            port.Focus(); port.Text = "COM"; await model.PollAsync(); await Settle();
            Check(port.Text == "COM" && !model.CanSaveM5 && model.M5EditStatus.Contains("接続設定"), "Incomplete COM input remains editable while saving is disabled");
            port.Text = "COM15";
            var display = model.DashboardSettings.First(r => r.Item.Measurement == DeviceFeature.Temperature); display.Name = "温度の表示名"; await model.SaveDashboardAsync(); await model.PollAsync(); await Settle();
            Check(model.M5Draft.Conflict && !model.CanSaveM5 && port.Text == "COM15", "Another settings generation preserves the unsaved port and blocks a stale save");
            await model.ReloadM5Async(); await Settle();
            Check(!model.M5Draft.Conflict && port.Text == "COM14" && editor.Layout == "B", "Explicit reload restores only the saved M5 configuration");
            editor.Hub = editor.Hubs.Single(h => h.Binding?.DeviceId == demo.DeviceIds[1]); await Settle();
            Check(!editor.Fans.Any(p => p.Selected) && !editor.Leds.Any(p => p.Selected) && editor.Profiles.All(p => p.Selected.Length == 0) && editor.Cpu?.Target is not null,
                "Changing the physical Hub clears control assignments and retains temperature display choices");
            await model.ReloadM5Async(); await Settle();
            var restoredPreset = Descendants<ComboBox>(panel).Single(c => c.DataContext is DeviceM5ProfileEditor { Slot: "balanced" });
            var restoredEditor = (DeviceM5ProfileEditor)restoredPreset.DataContext;
            Check(restoredPreset.SelectedItem is DeviceM5ProfileChoice { Id: "balanced" },
                "Reloading after a Hub change visibly restores the saved preset selection: source=" + restoredEditor.Selected + "/" + restoredEditor.SelectedIndex +
                " choices=" + string.Join(",", restoredEditor.Choices.Select(c => c.Id)) + " actual=" + restoredPreset.SelectedIndex +
                " items=" + string.Join(",", restoredPreset.Items.Cast<DeviceM5ProfileChoice>().Select(c => c.Id)) + " binding=" + restoredPreset.GetBindingExpression(Selector.SelectedIndexProperty)?.Status);
            window.Width = 960; window.Height = 640; panel.BringIntoView(); await Settle(); Capture(window, output, "m5-settings-minimum");
            var save = Descendants<Button>(panel).Single(b => b.Content?.ToString() == "設定を保存");
            save.BringIntoView(); await Settle(); Capture(window, output, "m5-settings-minimum-connection");
            Check(save.IsVisible && !save.Command.CanExecute(save.CommandParameter), "At minimum window size the saved-state action remains reachable by scrolling");
            var preset = Descendants<ComboBox>(panel).Single(c => c.DataContext is DeviceM5ProfileEditor { Slot: "balanced" });
            Check(Descendants<TextBlock>(preset).Any(t => t.Text == "標準" && t.ActualWidth > 0),
                "The selected preset has visible text after changing Hub, reloading and resizing: index=" + preset.SelectedIndex + " box=" + preset.SelectionBoxItem + " text=" + preset.Text);
            // A read-only synthetic motherboard exercises the editor without adding a physical provider.
            var s = client.Snapshot!;
            var endpoints = Enumerable.Range(1, 9).Select(i => new DeviceEndpoint("sensor" + i, i < 4 ? "温度 " + i : "回転数 " + i,
                i < 4 ? EndpointKind.Sensor : EndpointKind.Cooling,
                [new(i < 4 ? DeviceFeature.Temperature : DeviceFeature.Rpm, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)], Role: i == 9 ? "pump" : null)).ToArray();
            var board = new DeviceInstance(new("demo-m5-board", "demo", "マザーボード", DeviceKind.Motherboard, IdentityConfidence.SerialBound, "demo", null, [], endpoints), 1, true);
            var synthetic = s with { Configuration = s.Configuration with { Configuration = s.Configuration.Configuration with {
                Devices = s.Configuration.Configuration.Devices with { Devices = [.. s.Configuration.Configuration.Devices.Devices,
                    new(board.Device.Id, board.Device.Name, board.Device.Provider, board.Device.Kind, endpoints.Select(e => new RegisteredEndpoint(e.Id, e.Name, e.Kind, e.Role ?? "")).ToArray())] } } },
                State = s.State with { Inventory = s.State.Inventory with { Devices = [.. s.State.Inventory.Devices, board] } } };
            model.M5Draft.Reset(synthetic); editor.Publish(); await Settle();
            Check(editor.HasBoard && editor.Board.Count == 9 && editor.Board.Last().Item.Role == "pump", "Motherboard choices include temperature, fan RPM and pump RPM only: " +
                string.Join(",", editor.Board.Select(r => r.Item.Target.EndpointId + "/" + r.Item.Role)));
            foreach (var row in editor.Board.ToArray().Take(8)) row.Selected = true;
            Check(editor.BoardCount == "8 / 8" && !editor.Board.Last().CanSelect, "The panel enforces its eight-row display capacity");
            var first = editor.Board[0]; var name = Descendants<DraftTextBox>(panel).Single(t => ReferenceEquals(t.DataContext, first)); name.BringIntoView(); name.Focus(); name.Text = "ポンプ付近の温度";
            for (var i = 0; i < 3; i++) { model.M5Draft.Update(synthetic); editor.Publish(); await Settle(); }
            Check(name.Text == "ポンプ付近の温度" && first.Name == name.Text, "Japanese motherboard display names survive monitoring updates");
            first.Down.Execute(null); await Settle();
            Check(model.M5Draft.Settings.Selection.Motherboard[1].Name == "ポンプ付近の温度", "Display ordering moves the selected item without changing its measurement identity");
            name = Descendants<DraftTextBox>(panel).First(t => t.DataContext is DeviceM5BoardEditor { Selected: true }); name.BringIntoView(); await Settle(); Capture(window, output, "m5-settings-motherboard");
            first.Selected = false; editor.Board.Single(r => r.Item.Role == "pump").Selected = true;
            Check(editor.BoardCount == "8 / 8" && model.M5Draft.Settings.Selection.Motherboard.Last().Role == "pump" && demo.Hubs[0].Writes == writes,
                "Replacing a displayed item with pump RPM preserves cooling output");
            await model.ReloadM5Async();
            Check(await model.CloseAsync() && (await demo.Hubs[0].ReadAsync()).Channels[0].Duty == 70, "Normal exit still restores the configured fan output");
            await File.WriteAllLinesAsync(Path.Combine(output, "m5-settings-smoke.txt"), report); return 0;
        }
        catch (Exception e) { report.Add("FAIL " + e); report.Add("STATE " + model.M5EditStatus + " / " + model.M5Message); await File.WriteAllLinesAsync(Path.Combine(output, "m5-settings-smoke.txt"), report); return 1; }
    }
}
