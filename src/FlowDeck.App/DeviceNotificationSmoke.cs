using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceNotificationsAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, Window window, string output)
    {
        var report = new List<string>(); var delivery = new List<NotificationEvent>();
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); report.Add("PASS " + label); }
        void Received(NotificationEvent[] events) => delivery.AddRange(events);
        model.DesktopNotifications += Received;
        try
        {
            model.SelectedPage = 3; model.NotificationExpanded = true; await Settle();
            var panel = Descendants<DeviceNotificationsView>(window).Single();
            Check(model.NotificationSupported && model.CanAddNotification && !model.CanSaveNotifications && model.NotificationRules.Count == 0,
                "The notification panel starts with saved service settings and no invented rules");
            Check(model.NotificationHistory.Count == 0 && demo.Hubs.All(h => h.Writes == 0), "Reading initial history neither notifies nor changes output");
            var fan = model.Cooling.Single(r => r.Draft.Target == new EndpointReference(demo.DeviceIds[0], "fan1"));
            fan.Mode = "固定"; fan.Duty = 55; await model.OperateAsync(fan); var writes = demo.Hubs[0].Writes;
            model.AddNotificationRule(); await Settle();
            var editor = model.NotificationRuleEditor ?? throw new InvalidOperationException("Notification editor missing");
            var choices = Descendants<ComboBox>(panel).Single(c => AutomationProperties.GetName(c) == "通知の対象");
            choices.SetCurrentValue(Selector.SelectedItemProperty, editor.Targets.Single(t => t.Measurement == DeviceFeature.Temperature && t.Name == "CPU")); await Settle();
            Check(!editor.IsRpm && editor.Target is { Measurement: DeviceFeature.Temperature }, "The generic target picker switches RPM to a registered temperature");
            var name = Descendants<DraftTextBox>(panel).Single(); name.Focus(); name.Text = "日本語の通知"; name.CaretIndex = name.Text.Length;
            var threshold = Descendants<Slider>(panel).Single(s => AutomationProperties.GetName(s) == "通知のしきい値");
            threshold.SetCurrentValue(Slider.ValueProperty, 40d);
            Descendants<Slider>(panel).Single(s => AutomationProperties.GetName(s) == "通知までの秒数").SetCurrentValue(Slider.ValueProperty, 0d);
            Descendants<Slider>(panel).Single(s => AutomationProperties.GetName(s) == "復帰までの秒数").SetCurrentValue(Slider.ValueProperty, 0d);
            Descendants<ComboBox>(panel).Single(c => AutomationProperties.GetName(c) == "通知の繰り返し").SetCurrentValue(Selector.SelectedValueProperty, 60);
            for (var i = 0; i < 3; i++) await model.PollAsync(); await Settle();
            Check(ReferenceEquals(editor, model.NotificationRuleEditor) && name.Text == "日本語の通知" && editor.Threshold == 40 && model.NotificationOptions!.Repeat == 60,
                "Polling preserves the Japanese name, editor instance, threshold and repeat selection");
            Check(model.CanSaveNotifications && model.NotificationDraft.Dirty && demo.Hubs[0].Writes == writes && delivery.Count == 0,
                "Unsaved conditions neither actuate hardware nor evaluate as active notifications");
            await model.SaveNotificationsAsync(); await Settle();
            Check(model.ActiveNotifications.Count == 1 && model.NotificationHistory.Any(e => e.Entry.Event.Kind == "raised") && delivery.Count == 1,
                "Saving a temperature rule raises its service warning and one desktop-delivery event");
            Check(!model.CanSaveNotifications && fan.CurrentDuty == "55%" && demo.Hubs[0].Writes == writes && demo.Hubs[1].Writes == 0,
                "Saving notification preferences preserves active cooling and other devices");
            foreach (var theme in new[] { "light", "dark" }) { model.Theme = theme; panel.BringIntoView(); await Settle(); Capture(window, output, "notifications-" + theme); }
            Descendants<DataGrid>(panel).Single().BringIntoView(); await Settle(); Capture(window, output, "notifications-history-dark");
            var test = Descendants<Button>(panel).Single(b => b.Content?.ToString() == "通知テスト");
            Check(test.Command.CanExecute(test.CommandParameter), "The notification test command is available for saved enabled desktop settings");
            await model.TestNotificationAsync(); var count = delivery.Count; await model.PollAsync(); await model.PollAsync();
            Check(count == 2 && delivery.Count == count && model.NotificationHistory.Count == 2 && model.NotificationHistory[0].Kind == "テスト",
                "A test appears once in history and delivery, with no duplicate on subsequent polls");
            editor.Name = "保存前の日本語"; var display = model.DashboardSettings.First(r => r.Item.Measurement == DeviceFeature.Temperature);
            display.Name = "監視の名前変更"; await model.SaveDashboardAsync(); await model.PollAsync();
            Check(model.NotificationDraft.Conflict && !model.CanSaveNotifications && editor.Name == "保存前の日本語",
                "A different settings save retains unsaved notification text and rejects its stale generation");
            await model.ReloadNotificationsAsync(); editor = model.NotificationRuleEditor!;
            Check(!model.NotificationDraft.Conflict && editor.Name == "日本語の通知", "Explicit reload restores the saved notification draft");
            demo.CpuTemperature = 35; await demo.Runtime.PollAsync(); await model.PollAsync();
            Check(model.ActiveNotifications.Count == 0 && model.NotificationHistory.Any(r => r.Kind == "復帰"), "A confirmed normal reading produces a recovery entry");
            model.AddNotificationRule(); editor = model.NotificationRuleEditor!;
            editor.Target = editor.Targets.First(t => t.Target == new EndpointReference(demo.DeviceIds[1], "fan1")); await Settle();
            Check(editor.IsRpm && editor.ThresholdLabel.Contains("RPM") && editor.Targets.Count(t => t.Target.EndpointId == "fan1") == 2,
                "The RPM editor distinguishes the same header name on two devices");
            editor.Name = "ラジエーター"; editor.Threshold = 1; await model.SaveNotificationsAsync();
            Check(model.NotificationRules.Count == 2 && !model.NotificationDraft.Dirty && demo.Hubs[0].Writes == writes,
                "Adding a second device's RPM rule saves without changing fan output");
            window.Width = 960; window.Height = 640; name.BringIntoView(); await Settle(); Capture(window, output, "notifications-minimum");
            Descendants<DataGrid>(panel).Single().BringIntoView(); await Settle(); Capture(window, output, "notifications-minimum-history");
            model.DeleteNotificationRule(); Check(model.NotificationDraft.Dirty && model.NotificationRules.Count == 1, "Deleting a condition is an unsaved edit");
            await model.SaveNotificationsAsync(); await model.ReloadNotificationsAsync(); Check(model.NotificationRules.Count == 1, "Deleted notification conditions remain deleted after reload");
            void FailingReceiver(NotificationEvent[] _) => throw new IOException("demo delivery unavailable");
            model.DesktopNotifications += FailingReceiver;
            await model.TestNotificationAsync(); model.DesktopNotifications -= FailingReceiver;
            Check(model.NotificationMessage.Contains("Windows通知要求失敗") && model.NotificationHistory[0].Kind == "テスト" && demo.Hubs[0].Writes == writes,
                "Desktop delivery failure retains history and cannot affect cooling");
            model.NotificationOptions!.Desktop = false; await model.SaveNotificationsAsync(); await Settle();
            Check(!model.CanTestNotification && !test.Command.CanExecute(test.CommandParameter), "Saved desktop opt-out disables the test button");
            model.NotificationOptions.Enabled = false; await model.SaveNotificationsAsync();
            Check(model.NotificationSummary == "通知無効" && model.NotificationHistory.Count > 0, "Disabling notifications retains existing history");
            Check(await model.CloseAsync() && (await demo.Hubs[0].ReadAsync()).Channels[0].Duty == 70,
                "Normal exit retains the configured cooling release behavior after notification edits");
            await File.WriteAllLinesAsync(Path.Combine(output, "notifications-smoke.txt"), report); return 0;
        }
        catch (Exception e) { report.Add("FAIL " + e); report.Add("STATE " + model.NotificationEditStatus + " / " + model.NotificationMessage); await File.WriteAllLinesAsync(Path.Combine(output, "notifications-smoke.txt"), report); return 1; }
        finally { model.DesktopNotifications -= Received; }
    }
}
