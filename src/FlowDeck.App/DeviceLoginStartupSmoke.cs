using System.Windows;
using System.Windows.Controls;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Microsoft.Win32;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceLoginStartupAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo,
        DeviceWorkspaceClient client, DemoLoginStartupRegistration registration, Window window, string output)
    {
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks.Add("PASS " + message); }
        try
        {
            model.SelectedPage = 3; model.LoginExpanded = true; await Settle();
            var panel = Descendants<DeviceLoginStartupView>(window).Single();
            Check(!model.LoginEnabled && model.LoginStatus == "未登録" && registration.Writes == 0, "Opening the app only reads the current user's registration");
            Check(model.LoginEditable && !model.CanSaveLogin && !model.LoginConflict, "An unchanged registration cannot be saved accidentally");
            var fan = model.Cooling.Single(r => r.Draft.Target == new EndpointReference(demo.DeviceIds[0], "fan1"));
            fan.Mode = "固定"; fan.Duty = 55; await model.OperateAsync(fan);
            var writes = demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).ToArray(); var generation = client.Snapshot!.Configuration.Generation;
            var reads = registration.Reads;
            model.LoginEnabled = true;
            for (var i = 0; i < 3; i++) await model.PollAsync();
            Check(model.LoginEnabled && model.LoginStatus == "未保存" && model.CanSaveLogin && registration.Reads == reads,
                "Polling preserves the draft without repeatedly reading or rewriting OS settings");
            await model.SaveLoginAsync();
            Check(model.LoginEnabled && !model.CanSaveLogin && registration.Writes == 1 && registration.Value.Command?.EndsWith(" --devices --tray", StringComparison.Ordinal) == true,
                "Saving explicitly registers this application in service and tray mode");
            await model.SaveLoginAsync(); Check(registration.Writes == 1, "Repeated save does not rewrite unchanged registration");
            Check(generation == client.Snapshot.Configuration.Generation && writes.SequenceEqual(demo.Hubs.Select(h => (h.Writes, h.LightingWrites))) && fan.CurrentDuty == "55%",
                "Startup registration leaves active cooling, lighting and the service configuration unchanged");
            var other = DeviceLoginStartupValue.From(RegistryValueKind.String, @"""C:\Other\FlowDeck.exe"" --tray");
            registration.Value = other; model.LoginEnabled = false;
            try { await model.SaveLoginAsync(); throw new InvalidOperationException("Expected stale registration rejection"); } catch (IOException) { }
            Check(registration.Value == other && registration.Writes == 1, "A stale edit cannot delete another application's registration");
            await model.ReloadLoginAsync();
            Check(model.LoginConflict && !model.LoginEnabled && !model.CanSaveLogin, "Reload reveals the other registered application");
            model.LoginEnabled = true; Check(!model.CanSaveLogin, "Enabling alone does not authorize replacement");
            model.LoginReplace = true; Check(model.CanSaveLogin, "The explicit replacement choice enables adoption");
            await model.SaveLoginAsync();
            Check(!model.LoginConflict && model.LoginEnabled && registration.Writes == 2 && !model.LoginReplace, "Adoption reads back ownership and clears the replacement choice");
            await model.OpenLoginSettingsAsync();
            Check(registration.SettingsOpened == 1 && registration.Writes == 2, "The demo's Windows-settings action never launches a shell or changes registration");
            registration.DenyRead = true; await model.ReloadLoginAsync();
            Check(!model.LoginEditable && !model.CanSaveLogin && model.LoginStatus.Contains("取得できません"), "Access denied is visible instead of appearing unregistered");
            registration.DenyRead = false; await model.ReloadLoginAsync(); model.LoginEnabled = false;
            registration.DenyWrite = true;
            try { await model.SaveLoginAsync(); throw new InvalidOperationException("Expected access denied"); } catch (UnauthorizedAccessException) { }
            Check(model.LoginIdle && !model.LoginEnabled && model.CanSaveLogin && registration.Writes == 2, "Write failure keeps the draft available for retry");
            registration.DenyWrite = false; await model.SaveLoginAsync();
            Check(!model.LoginEnabled && registration.Value == DeviceLoginStartupValue.Missing && registration.Writes == 3, "Disabling removes only the owned registration");
            foreach (var theme in model.Themes)
            {
                model.Theme = theme; panel.BringIntoView(); await Settle(); Capture(window, output, "login-startup-" + theme);
                Check(panel.IsVisible && Descendants<Button>(panel).All(b => b.ActualWidth > 30), theme + " displays the startup actions");
            }
            window.Width = 960; window.Height = 640; panel.BringIntoView(); await Settle(); Capture(window, output, "login-startup-minimum");
            Check(panel.ActualWidth > 600, "Minimum window size preserves the startup controls");
            Check(await model.CloseAsync() && registration.Writes == 3, "Normal exit does not rewrite login registration");
            await File.WriteAllLinesAsync(Path.Combine(output, "login-startup-smoke.txt"), checks); return 0;
        }
        catch (Exception e) { checks.Add("FAIL " + e); await File.WriteAllLinesAsync(Path.Combine(output, "login-startup-smoke.txt"), checks); return 1; }
    }
}
