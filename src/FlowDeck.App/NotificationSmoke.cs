using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    private static async Task RunNotificationChecksAsync(FlowDeckApplication app, RuntimeHost host, FlowDeckViewModel vm, FlowDeckWindow window, string output, Action<bool,string> check)
    {
        var original = Contract.Copy(host.NotificationOptions); var cpu = host.DemoTemperature!.Cpu;
        var core = host.Coordinator; var fanRevision = core.Snapshot.ControlRevision; var fanWrites = ((DemoHub)host.Hub).Writes;
        var lightRevision = core.Snapshot.LightingRevision; var rgb = host.Rgb;
        var nativeRequests = app.NotificationDeliveryAttempts;
        vm.SelectedPage = 9; window.PageScroll.ScrollToTop(); window.Width = 1200; window.Height = 1000; await Settle();
        check(window.NotificationsPanel.IsVisible && vm.PageTitle == "通知", "Notification settings have a dedicated page under Settings");
        vm.NotificationsDesktop = false; vm.NotificationDelay = 0; vm.NotificationRecoveryDelay = 0; vm.NotificationRepeat = 0;
        check(host.NotificationOptions.Desktop && host.NotificationOptions.DelaySeconds == 5, "Editing notification preferences leaves the saved configuration unchanged until Save");
        vm.SaveNotificationSettings.Execute(null); await Until(() => vm.NotificationIdle && !host.NotificationOptions.Desktop && host.NotificationOptions.DelaySeconds == 0);
        check((await new NotificationStore(host.Store.DirectoryPath).LoadAsync()).Desktop == false, "Notification preferences persist separately from cooling policy");
        vm.NewNotificationRule.Execute(null);
        vm.NotificationTarget = vm.NotificationTargets.Single(t => t.Address.Source == "sensor" && t.Address.SensorId == "demo/cpu");
        vm.NotificationName = "CPU 温度"; vm.NotificationHysteresis = 3; await Settle();
        var peer = new SliderAutomationPeer(window.NotificationsPanel.NotificationThresholdSlider);
        ((IRangeValueProvider)peer.GetPattern(PatternInterface.RangeValue)).SetValue(55); await Settle();
        check(vm.NotificationThreshold == 55 && host.NotificationOptions.Rules is { Length:0 }, "The threshold slider edits only the draft rule");
        vm.SaveNotificationRule.Execute(null); await Until(() => vm.NotificationIdle && host.NotificationOptions.Rules is { Length:1 });
        var rule = host.NotificationOptions.Rules!.Single(); host.EvaluateNotifications(); await Settle();
        check(host.ActiveNotifications.Any(e => e.Key == rule.Id + ":limit") && vm.NotificationHistory.Any(e=>e.Kind=="警告"),
            "A saved temperature condition reaches the runtime monitor and notification history");
        var duplicateCount = host.NotificationHistory.Count(e=>e.Key == rule.Id + ":limit" && e.Kind=="raised");
        for(var i=0;i<5;i++) host.EvaluateNotifications();
        check(host.NotificationHistory.Count(e=>e.Key == rule.Id + ":limit" && e.Kind=="raised") == duplicateCount,
            "Repeated evaluations of the same abnormal value do not create duplicate notifications");
        host.DemoTemperature.Cpu = 45; await core.TickAsync(); await Until(() => core.Snapshot.Sensors.First(s=>s.Id=="demo/cpu").Celsius == 45);
        host.EvaluateNotifications(); await Settle();
        check(host.NotificationHistory.Any(e=>e.Key==rule.Id + ":limit" && e.Kind=="recovered") && !host.ActiveNotifications.Any(e=>e.Key==rule.Id + ":limit"),
            "A sustained return below the recovery margin emits a recovery and clears the alert");
        host.DemoTemperature.Missing = true; await core.TickAsync(); await Until(() => core.Snapshot.Sensors.All(s=>s.Celsius is null)); host.EvaluateNotifications();
        check(host.ActiveNotifications.Any(e=>e.Key==rule.Id + ":missing"), "Missing sensor values are a separate configurable alert");
        host.DemoTemperature.Missing = false; host.DemoTemperature.Cpu = cpu; await core.TickAsync(); await Until(()=>core.Snapshot.Sensors.First(s=>s.Id=="demo/cpu").Celsius==cpu);
        host.EvaluateNotifications(); await Settle();
        vm.NewNotificationRule.Execute(null); vm.NotificationTarget = vm.NotificationTargets.Single(t=>t.Address.Source=="motherboard" && t.Address.SensorId==MotherboardFans.Cpu.DemoSensorId);
        await Settle(); check(vm.NotificationIsFan && vm.NotificationHysteresis == 50, "Selecting a fan changes units and retains the RPM recovery margin in the slider");
        vm.NotificationThreshold = 2000; vm.NotificationName = "CPU_FAN1"; vm.SaveNotificationRule.Execute(null);
        await Until(()=>vm.NotificationIdle && host.NotificationOptions.Rules is { Length:2 }); host.EvaluateNotifications();
        check(host.ActiveNotifications.Any(e=>e.Name=="CPU_FAN1"), "Motherboard RPM watches operate alongside CPU temperature watches");
        vm.NotificationsDesktop = true; vm.SaveNotificationSettings.Execute(null); await Until(()=>vm.NotificationIdle && host.NotificationOptions.Desktop);
        vm.TestNotification.Execute(null); await Settle();
        check(app.LastNotificationBatch.Any(e=>e.Kind=="test") && app.NotificationDeliveryAttempts == nativeRequests && vm.NotificationHistory.Any(e=>e.Kind=="テスト"),
            "The test notification reaches the desktop delivery route while demo mode suppresses actual Windows popups");
        vm.ReloadNotifications.Execute(null); await Until(()=>vm.NotificationIdle && vm.NotificationResult.Contains("再読み込み"));
        vm.NotificationRule = vm.NotificationRules.Single(r=>r.Rule.Id==rule.Id); await Settle();
        check(vm.NotificationThreshold==55 && vm.NotificationHysteresis==3 && vm.NotificationTarget?.Address==rule.Address,
            "Reloaded notification rules retain their physical source, threshold and recovery margin");
        check(window.NotificationsPanel.NotificationTargetPicker.SelectedItem is NotificationTargetChoice t && t.Address==rule.Address &&
            window.NotificationsPanel.NotificationRulePicker.SelectedItem is NotificationRuleChoice r && r.Rule.Id==rule.Id &&
            window.NotificationsPanel.NotificationRepeatPicker.SelectedItem is NotificationRepeatChoice { Seconds:0 },
            "Target, rule and repetition selectors remain visible after reloading unchanged values");
        ThemeManager.Apply("light"); await Settle(); Capture(window,output,"notifications-light");
        ThemeManager.Apply("dark"); await Settle(); Capture(window,output,"notifications-dark");
        window.Width=960; window.Height=640; await Settle();
        check(window.NotificationsPanel.NotificationRulesCard.ActualWidth < window.ActualWidth-150 && window.NotificationsPanel.NotificationThresholdSlider.ActualWidth > 180,
            "Notification controls fit the minimum window width with vertical scrolling");
        Capture(window,output,"notifications-minimum");
        vm.NotificationsEnabled=false; vm.SaveNotificationSettings.Execute(null); await Until(()=>vm.NotificationIdle && !host.NotificationOptions.Enabled); host.EvaluateNotifications();
        check(host.ActiveNotifications.Length==0 && !vm.TestNotification.CanExecute(null), "Disabling notifications clears alert state without issuing hardware commands");
        vm.DeleteNotificationRule.Execute(null); await Until(()=>vm.NotificationIdle && host.NotificationOptions.Rules is { Length:1 });
        check(host.NotificationOptions.Rules!.Single().Name=="CPU_FAN1", "Deleting one notification rule preserves the other rule");
        check(core.Snapshot.ControlRevision==fanRevision && ((DemoHub)host.Hub).Writes==fanWrites && core.Snapshot.ConfirmedDuty==59 &&
            core.Snapshot.LightingRevision==lightRevision && ReferenceEquals(host.Rgb,rgb), "Notification configuration, alerts and tests leave all fan and lighting outputs unchanged");
        await host.SaveNotificationsAsync(original); vm.ReloadNotifications.Execute(null); await Until(()=>vm.NotificationIdle && vm.NotificationRules.Count==0);
        window.Width=1200; window.Height=900; ThemeManager.Apply("light"); vm.SelectedPage=8; await Settle(); vm.RevertFan.Execute(null);

        await using var isolated = new RuntimeHost(Path.Combine(output,"notification-failure-check"),demoOnly:true);
        await isolated.InitializeAsync(true); var isolatedCore=isolated.Coordinator; var isolatedHub=(DemoHub)isolated.Hub;
        await isolatedCore.RequestAsync(new(Guid.NewGuid().ToString("N"),"通知試験",isolatedCore.Epoch,isolatedCore.Snapshot.ControlRevision,"SetManualDuty",Duty:59));
        await isolated.SaveNotificationsAsync(new(DelaySeconds:0,RecoverySeconds:0,ControlFaults:false,Rules:[])); isolated.EvaluateNotifications();
        void ThrowingReceiver(NotificationEvent[] _) => throw new IOException("DEMO: 通知先の例外");
        isolated.NotificationsRaised += ThrowingReceiver;
        for(var i=0;i<2;i++)
        {
            isolatedHub.FailNextLightingWrite=true;
            await isolatedCore.RequestAsync(new(Guid.NewGuid().ToString("N"),"通知試験",isolatedCore.Epoch,isolatedCore.Snapshot.LightingRevision,"SetLighting",LightingEffect:HubLighting.Preset("blue"),LightingChannels:["led1"]));
            isolated.EvaluateNotifications();
        }
        check(isolated.NotificationHistory.Count(e=>e.Key.StartsWith("operation:"))==1 && isolatedCore.Snapshot.ConfirmedDuty==59 && isolatedCore.Snapshot.Mode==FanMode.Manual,
            "Failed-operation alerts are deduplicated and a throwing notification receiver cannot interrupt cooling");
        try
        {
            isolatedHub.Disconnected=true; await isolatedCore.TickAsync(); await Until(()=>!isolatedCore.Snapshot.HubReady); isolated.EvaluateNotifications();
            check(isolated.ActiveNotifications.Any(e=>e.Key.StartsWith("system:hub-offline:")), "A previously connected Hub produces a disconnection alert when updates stop");
        }
        finally { isolatedHub.Disconnected=false; await Until(()=>isolatedCore.Snapshot.HubReady); }
        isolated.EvaluateNotifications();
        check(isolated.NotificationHistory.Any(e=>e.Key.StartsWith("system:hub-offline:") && e.Kind=="recovered"), "Hub reconnection resolves the matching connection alert");
        isolated.SuspendNotifications(true); isolated.EvaluateNotifications(); check(isolated.ActiveNotifications.Length==0,"Suspending notification observation clears timers and active state"); isolated.SuspendNotifications(false);
        var path = new NotificationStore(isolated.Store.DirectoryPath).PathName; await File.WriteAllTextAsync(path,"{bad"); await isolated.LoadNotificationsAsync(); var refused=false;
        try { await isolated.SaveNotificationsAsync(new()); } catch(IOException) { refused=true; }
        check(refused && isolated.NotificationError is not null && await File.ReadAllTextAsync(path)=="{bad", "Corrupt notification settings are reported and preserved instead of overwritten");
    }
}
