using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceM5ConnectionAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, DemoM5LinkFactory transport, Window window, string output)
    {
        var report = new List<string>(); var delivery = new List<NotificationEvent>();
        void Received(NotificationEvent[] events) => delivery.AddRange(events);
        model.DesktopNotifications += Received;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); report.Add("PASS " + label); }
        try
        {
            model.SelectedPage = 3; model.M5Expanded = true; await Settle(); var panel = Descendants<DeviceM5SettingsView>(window).Single();
            var editor = model.M5Editor ?? throw new InvalidOperationException("M5 editor missing");
            await model.StartM5Async(); Check(model.M5ConnectionSupported && model.M5LinkStatus == "未接続" && transport.Links.Count == 0, "Initial settings never auto-open a physical or simulated port");
            var fan = model.Cooling.Single(r => r.Draft.Target == new EndpointReference(demo.DeviceIds[0], "fan1")); fan.Mode = "固定"; fan.Duty = 55; await model.OperateAsync(fan); var writes = demo.Hubs[0].Writes;
            editor.Hub = editor.Hubs.Single(h => h.Binding?.DeviceId == demo.DeviceIds[0]); editor.Fans.Single(f => f.Id == "fan1").Selected = true;
            editor.Cpu = editor.Cpus.Single(c => c.Target is not null); editor.Port = "COM14"; editor.Address = "192.168.1.19"; editor.Transport = "auto";
            Check(!model.CanConnectM5 && model.CanSaveM5, "Unsaved connection choices must be saved before connecting");
            await model.SaveM5Async(); await model.ConnectM5Async(); await Settle();
            Check(model.CanRegisterM5 && model.M5LinkStatus == "USB · 登録待ち" && transport.Links[0].Control.Selection.Hub is null && transport.Links[0].Lighting is null,
                "Unregistered USB discovery exposes registration without fan or lighting authority");
            var register = Descendants<Button>(panel).Single(b => b.Content?.ToString() == "このM5GOを登録"); Check(register.Command.CanExecute(null), "The observed device can be registered through the displayed command");
            await model.RegisterM5Async(); await Settle();
            Check(!model.CanRegisterM5 && model.M5Draft.Settings.Connection.DeviceId == transport.DeviceId && model.M5LinkStatus == "USB · 同期済み",
                "Registration saves the observed ID and opens a new synchronized session");
            Check(transport.Links.Count == 2 && transport.Links[0].Closed && demo.Hubs[0].Writes == writes && fan.CurrentDuty == "55%", "Registration closes discovery and preserves PC cooling");
            Descendants<Expander>(panel).Single(e => e.Header?.ToString() == "Wi-Fi登録").IsExpanded = true; await Settle();
            var ssid = Descendants<DraftTextBox>(panel).Single(t => AutomationProperties.GetName(t) == "M5GOのSSID"); ssid.Focus(); ssid.Text = "自宅の試験LAN";
            var password = Descendants<PasswordBox>(panel).Single(); password.Password = "demo-passphrase";
            for (var i = 0; i < 3; i++) await model.PollAsync(); await Settle();
            Check(ssid.Text == "自宅の試験LAN" && model.CanPairM5 && password.Password == "demo-passphrase", "Polling preserves SSID and the locally held password");
            var pair = Descendants<Button>(panel).Single(b => b.Content?.ToString() == "USBでWi-Fiを登録"); pair.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
            Check(password.Password.Length == 0 && transport.PairCalls == 1 && transport.Paired && model.M5LinkDetail.Contains("Wi-Fiを登録"),
                "The Wi-Fi registration action clears the password box and restarts the paired session");
            transport.UsbPresent = false; await model.PollAsync(); Check(model.M5LinkStatus == "Wi-Fi · 同期済み", "USB removal switches the same registered panel to Wi-Fi");
            transport.UsbPresent = true; await model.PollAsync(); Check(model.M5LinkStatus == "USB · 同期済み", "Restoring USB takes priority without changing saved configuration");
            model.NotificationOptions!.Delay = model.NotificationOptions.RecoveryDelay = 0; await model.SaveNotificationsAsync();
            Check(!model.NotificationHistory.Any(e => e.Entry.Event.Key == "system:m5-offline"), "Registration and USB/Wi-Fi handover have no spurious disconnect history");
            transport.UsbPresent = transport.WifiPresent = false; await model.PollAsync(); await model.PollAsync(); await Settle();
            Check(model.ActiveNotifications.Any(e => e.Key == "system:m5-offline") && delivery.Count(e => e.Key == "system:m5-offline" && e.Kind == "raised") == 1,
                "A real simulated disconnect reaches the common active list and desktop delivery once");
            await model.PollAsync(); await model.PollAsync();
            Check(delivery.Count(e => e.Key == "system:m5-offline") == 1 && demo.Hubs[0].Writes == writes && fan.CurrentDuty == "55%",
                "Repeated offline reports do not duplicate notifications or interrupt cooling");
            model.NotificationExpanded = true; var notificationPanel = Descendants<DeviceNotificationsView>(window).Single();
            foreach (var theme in new[] { "light", "dark" }) { model.Theme = theme; notificationPanel.BringIntoView(); await Settle(); Capture(window, output, "m5-notification-" + theme); }
            transport.WifiPresent = true; await model.PollAsync(); await model.PollAsync();
            Check(!model.ActiveNotifications.Any(e => e.Key == "system:m5-offline") && delivery.Count(e => e.Key == "system:m5-offline" && e.Kind == "recovered") == 1,
                "Wi-Fi recovery clears the alarm and uses the same notification history and delivery");
            transport.UsbPresent = true; await model.PollAsync();
            foreach (var theme in new[] { "light", "dark" }) { model.Theme = theme; panel.BringIntoView(); await Settle(); Capture(window, output, "m5-connection-" + theme); }
            await model.DisconnectM5Async(); var count = transport.Links.Count; await model.PollAsync(); await model.StartM5Async();
            Check(model.M5LinkStatus == "未接続" && transport.Links.Count == count && demo.Hubs[0].Writes == writes, "Explicit disconnect remains disconnected and preserves PC control");
            await model.ConnectM5Async(); editor.Port = "COM15"; await model.SaveM5Async();
            Check(model.M5LinkStatus == "未接続" && model.M5LinkDetail.Contains("設定を変更"), "Saving a changed port closes the old link and requires a new connect action");
            await model.ConnectM5Async();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.BeforePair = async ct => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
            var pending = model.PairM5Async("demo-passphrase"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(model.CanDisconnectM5 && !model.CanSaveM5 && !model.CanConnectM5, "During pairing disconnect remains available while conflicting edits are disabled");
            await model.DisconnectM5Async(); await pending; transport.BeforePair = null;
            Check(model.M5LinkStatus == "未接続" && !model.CanRegisterM5, "Disconnect cancels an in-flight pairing attempt without reopening a link");
            await model.PollAsync();
            Check(delivery.Count(e => e.Key == "system:m5-offline") == 2 && !model.ActiveNotifications.Any(e => e.Key == "system:m5-offline"),
                "Manual disconnect, settings edits and cancelled pairing do not announce extra alarms or recoveries");
            window.Width = 960; window.Height = 640; panel.BringIntoView(); await Settle(); Capture(window, output, "m5-connection-minimum");
            await model.ConnectM5Async(); Check(await model.CloseAsync() && transport.Links.All(l => l.Closed) && (await demo.Hubs[0].ReadAsync()).Channels[0].Duty == 70,
                "Normal exit closes M5 ingress before restoring the configured fan output");
            await File.WriteAllLinesAsync(Path.Combine(output, "m5-connection-smoke.txt"), report); return 0;
        }
        catch (Exception e) { report.Add("FAIL " + e); report.Add("STATE " + model.M5LinkStatus + " / " + model.M5LinkDetail + " / " + model.M5Message); await File.WriteAllLinesAsync(Path.Combine(output, "m5-connection-smoke.txt"), report); return 1; }
        finally { model.DesktopNotifications -= Received; }
    }
}
