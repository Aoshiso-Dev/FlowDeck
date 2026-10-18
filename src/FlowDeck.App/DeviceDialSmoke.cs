using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceDialAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo,
        DeviceWorkspaceClient client, Window window, string output)
    {
        var report = new List<string>();
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); report.Add("PASS " + message); }
        try
        {
            Check(client.IsDemo && DeviceWorkspaceOptions.DemoPipe(demo.PipeName) && demo.Hubs.All(h => h.IsDemo), "Only the isolated DEMO service and memory-backed hardware are used");
            var configuration = SettingsTransfer.Hash(client.Snapshot!.Configuration.Configuration);
            var writes = demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).ToArray();
            model.EnableDialPreview(); model.SelectedPage = 3; model.Theme = "dark"; await Settle();
            var panel = Descendants<DeviceDialSettingsView>(window).Single();
            Descendants<Expander>(panel).Single().IsExpanded = true; panel.BringIntoView(); await Settle();
            Check(panel.IsVisible && model.DialPort == "COM6" && model.DialStatus == "未接続", "Dial settings show the default port and disconnected state");
            Check(!model.CanConnectDial && !model.CanRegisterDial && !model.CanDisconnectDial && !model.CanEditDial,
                "DEMO cannot open, register or operate any physical serial port");
            Check(Descendants<Button>(panel).Where(b => b.Content?.ToString() is "更新" or "接続" or "切断" or "登録").All(b => !b.IsEnabled), "Every serial action is visibly disabled in DEMO");
            var selector = Descendants<ComboBox>(panel).Single(c => AutomationProperties.GetName(c) == "ダイヤルのUSBポート");
            Check(selector.ActualWidth > 180 && Equals(selector.SelectedItem, "COM6"),
                "The independent port selector visibly shows COM6 with usable width");
            Check(Descendants<TextBlock>(panel).Where(t => t.IsVisible).All(t => t.Text.Length < 80),
                "Dial settings keep visible labels and status concise without a standing specification paragraph");
            Check(Descendants<Button>(panel).Any(b => Equals(b.Content, "接続")) &&
                Descendants<Button>(panel).Any(b => Equals(b.Content, "切断")) &&
                Descendants<Button>(panel).Any(b => Equals(b.Content, "登録")),
                "Concise connection, disconnection and registration actions remain available");
            var legend = Descendants<WrapPanel>(panel).Single(p => AutomationProperties.GetAutomationId(p) == "DialModeLegend");
            var fanIcon = Descendants<System.Windows.Shapes.Path>(legend).Single(p => AutomationProperties.GetAutomationId(p) == "DialFanIcon");
            var pumpIcon = Descendants<System.Windows.Shapes.Path>(legend).Single(p => AutomationProperties.GetAutomationId(p) == "DialPumpIcon");
            Check(fanIcon.IsVisible && pumpIcon.IsVisible && fanIcon.ActualWidth >= 18 && pumpIcon.ActualHeight >= 23 &&
                fanIcon.Data.Bounds != pumpIcon.Data.Bounds &&
                fanIcon.Fill is SolidColorBrush { Color: var fanColor } && fanColor == Color.FromRgb(0xA3, 0xD9, 0x36) &&
                pumpIcon.Fill is SolidColorBrush { Color: var pumpColor } && pumpColor == Color.FromRgb(0x4D, 0xB8, 0xFF),
                "The dial legend shows distinct lime fan and blue droplet vector icons");
            foreach (var theme in new[] { "dark", "light" })
            {
                model.Theme = theme; await Settle();
                var labels = Descendants<TextBlock>(legend).ToArray();
                var textColor = ((SolidColorBrush)Application.Current.Resources["TextBrush"]).Color;
                Check(labels.Length == 2 && labels.Any(t => t.Text.Contains("全ファン")) && labels.Any(t => t.Text.Contains("ポンプ")) &&
                    labels.All(t => t.IsVisible && t.Foreground is SolidColorBrush brush && brush.Color == textColor),
                    $"Fan and pump labels retain readable theme text in {theme} mode");
                Capture(window, output, "dial-settings-" + theme);
            }
            await model.StartDialAsync(); await model.StartDialAsync(); await model.ConnectDialAsync(); await model.PollAsync();
            Check(!model.CanDisconnectDial && writes.SequenceEqual(demo.Hubs.Select(h => (h.Writes, h.LightingWrites))) &&
                configuration == SettingsTransfer.Hash(client.Snapshot!.Configuration.Configuration), "Preview, startup and disabled Connect preserve saved settings and every hardware output");
            Check(!File.Exists(Path.Combine(output, "dial-v1.json")), "Preview writes no live dial configuration");
            Check(model.DialDisplaySupported && !model.CanSaveDialDisplay, "Temperature display settings are available without creating a pending edit");
            var cpu = Descendants<ComboBox>(panel).Single(c => AutomationProperties.GetName(c) == "ダイヤルのCPU温度");
            var gpu = Descendants<ComboBox>(panel).Single(c => AutomationProperties.GetName(c) == "ダイヤルのGPU温度");
            var selectedCpu = model.DialCpuSensors.First(c => c.Target is not null);
            var selectedGpu = model.DialGpuSensors.First(c => c.Target is not null);
            cpu.SetCurrentValue(ComboBox.SelectedItemProperty, selectedCpu); gpu.SetCurrentValue(ComboBox.SelectedItemProperty, selectedGpu);
            for (var i = 0; i < 3; i++) await model.PollAsync();
            Check(model.DialCpuSensor == selectedCpu && model.DialGpuSensor == selectedGpu && model.CanSaveDialDisplay &&
                configuration == SettingsTransfer.Hash(client.Snapshot!.Configuration.Configuration), "CPU and GPU selections survive polling without changing saved settings");
            await model.SaveDialDisplayAsync(); await Settle();
            Check(!model.CanSaveDialDisplay && client.Snapshot!.Configuration.Configuration.Preferences.DialDisplay == new DeviceDialDisplaySettings(selectedCpu.Target, selectedGpu.Target) &&
                new DeviceDialBackend(client).Read() is { CpuTempDeciC: not null, GpuTempDeciC: not null } &&
                writes.SequenceEqual(demo.Hubs.Select(h => (h.Writes, h.LightingWrites))), "Saving temperature roles feeds the dial display and preserves every hardware output");
            cpu.SetCurrentValue(ComboBox.SelectedItemProperty, model.DialCpuSensors[0]);
            var snapshot = client.Snapshot!;
            await client.SaveDialDisplayAsync(new(snapshot.State.Inventory.Epoch, snapshot.Session!, snapshot.Configuration.Generation,
                snapshot.State.Inventory.TopologyRevision, new(selectedCpu.Target, null)));
            await model.PollAsync(); await Settle();
            Check(model.DialCpuSensor?.Target is null && model.DialGpuSensor == selectedGpu && !model.CanSaveDialDisplay &&
                model.DialDisplayStatus.Contains("別の操作"), "A concurrent settings save preserves unsaved temperature choices and blocks stale replacement");
            await model.ReloadDialDisplayAsync(); await Settle();
            Check(model.DialCpuSensor == selectedCpu && model.DialGpuSensor?.Target is null && !model.DialDisplayDirty &&
                writes.SequenceEqual(demo.Hubs.Select(h => (h.Writes, h.LightingWrites))), "Explicit reload adopts saved temperature roles without changing cooling or lighting");
            panel.BringIntoView(); await Settle(); Capture(window, output, "dial-temperature-settings");
            await File.WriteAllLinesAsync(Path.Combine(output, "dial-smoke.txt"), report); return 0;
        }
        catch (Exception e)
        {
            report.Add("FAIL " + e); await File.WriteAllLinesAsync(Path.Combine(output, "dial-smoke.txt"), report); return 1;
        }
    }
}
