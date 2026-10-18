using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task RunServiceAsync(FlowDeckApplication app, RuntimeHost host, FlowDeckViewModel vm, FlowDeckWindow window, string output)
    {
        if (!host.Demo || host.Coordinator is not RemoteControlCoordinator remote) throw new InvalidOperationException("A separate demo service is required");
        var report = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); report.Add("PASS " + message); }
        try
        {
            await Until(() => remote.Connected && remote.Snapshot.HubReady && vm.BoardFanRows.Count == 15);
            Check(host.DemoTemperature is null && host.Hub is not DemoHub && host.UsesService, "GUI uses IPC facades without constructing local cooling devices");
            Check(vm.DashboardTemperatures.Count >= 4 && vm.DashboardFans.Any(r => r.Item.Source == "nzxt") && vm.DashboardFans.Any(r => r.Item.Source == "motherboard"), "Service telemetry populates the unified dashboard");
            Check(vm.Themes.SequenceEqual(new[] { "system", "light", "dark" }), "All three themes remain available");
            foreach (var theme in vm.Themes) { ThemeManager.Apply(theme); await Settle(); Capture(window, output, "service-dashboard-" + theme); }
            vm.SelectedPage = 7; vm.HubControlsOpen = true; vm.ChooseMode.Execute("Manual"); vm.ManualDuty = 54; await Settle();
            Check(vm.ApplyFan.CanExecute(null), "Hub slider draft can be applied through the service");
            vm.ApplyFan.Execute(null); await Until(() => remote.Snapshot.ConfirmedDuty == 54 && !remote.Snapshot.FanBusy);
            Check(remote.Snapshot.Mode == FanMode.Manual, "Hub manual output is confirmed by the service");
            vm.SelectedBoardFan = MotherboardFans.Cpu; vm.CpuFanControlsOpen = true;
            vm.CpuFanDuty = "42"; vm.BoardFanApply.Execute(null); await Until(() => host.CpuFan.State.Managing && vm.BoardFanCanSelect);
            Check(host.CpuFan.State.Target == 42, "Motherboard slider applies only the selected service fan");
            vm.CpuFanRestore.Execute(null); await Until(() => !host.CpuFan.State.Managing && vm.BoardFanCanSelect);
            Check(vm.CpuFanDuty == "65", "Restoring motherboard management updates the slider from reported duty");
            vm.ManualDuty = 59; vm.ApplyFan.Execute(null);
            await Until(() => remote.Snapshot.ConfirmedDuty == 59);
            await host.ServicePowerAsync(false);
            Check(!remote.Connected && !remote.Snapshot.HubReady, "GUI power suspension releases the service owner and invalidates readings");
            await host.ServicePowerAsync(true); await Until(() => remote.Connected && remote.Snapshot.Managing && remote.Snapshot.ConfirmedDuty == 59);
            Check(!host.CpuFan.State.Managing, "Power resume restores confirmed Hub control and leaves motherboard fans under motherboard management");
            var resumedGeneration = remote.Snapshot.DeviceGeneration; await host.ServicePowerAsync(true);
            Check(remote.Snapshot.DeviceGeneration == resumedGeneration, "Duplicate resume notifications do not reconnect or replay the policy");
            var revision = remote.Snapshot.ControlRevision;
            await host.StartLightingSyncAsync(["led1"], "breathing", new(30, 110, 210), 2);
            await Until(() => host.LightingSynchronized && host.Rgb.Devices.SelectMany(d => d.Zones).Any(z => z.Configuration?.Blue is > 0));
            Check(remote.Snapshot.ControlRevision == revision && remote.Snapshot.ConfirmedDuty == 59, "Cross-device lighting animation leaves cooling unchanged");
            await host.StopLightingSyncAsync(); await remote.TickAsync();
            Check(host.LightingSyncError is null && !host.LightingSynchronized, "Synchronized lighting stops with an acknowledged remote lease release");
            remote = await RunServiceExtensionChecksAsync(host, vm, window, output, Check);
            await remote.StopControlAsync(); await Settle();
            vm.SelectedPage = 3; vm.RefreshSettings.Execute(null); vm.Theme = "dark"; vm.SaveSettings.Execute(null);
            await Until(() => remote.Settings.Ui.Theme == "dark"); await host.FlushSettingsAsync();
            Check((await host.Store.LoadAsync()).Ui.Theme == "dark", "Theme preference persists in user settings through the async facade");
            Check(vm.UsesService && !vm.UsesDirectCooling && vm.CoolingConnectionStatus.Contains("接続済み"), "Settings show service status and hide the local Python selector");
            window.PageScroll.ScrollToTop(); await Settle(); Capture(window, output, "service-settings-dark");
            window.Width = 960; window.Height = 640; ThemeManager.Apply("light"); await Settle(); Capture(window, output, "service-settings-minimum-light");
            var saved = remote.Settings;
            await using (var disconnected = new RemoteControlCoordinator(saved, () => throw new IOException("デモ: サービス未起動"), host.Log, true))
            {
                try { await disconnected.ConnectAsync(); } catch (IOException) { }
                Check(!disconnected.Snapshot.HubReady && disconnected.Snapshot.Sensors.Length == 0 && !disconnected.Board.IsFresh(), "Unavailable service exposes no fresh fallback readings");
            }
            await File.WriteAllLinesAsync(Path.Combine(output, "service-ui-smoke.txt"), report); await app.ExitAsync();
        }
        catch (Exception e)
        {
            report.Add("FAIL " + e); await File.WriteAllLinesAsync(Path.Combine(output, "service-ui-smoke.txt"), report);
            await app.ExitAsync(); Environment.ExitCode = 1;
        }
    }
}
