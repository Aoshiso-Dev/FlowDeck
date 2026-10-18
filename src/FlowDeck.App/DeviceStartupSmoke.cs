using System.Diagnostics;
using System.Windows;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task RunDeviceStartupAsync(FlowDeckApplication app, DeviceStartupOptions options)
    {
        if (!options.Smoke) throw new InvalidOperationException("初回起動の画面試験には専用デモ指定が必要です");
        var output = options.Directory; Directory.CreateDirectory(output);
        using var listener = new TextWriterTraceListener(Path.Combine(output, "bindings.log"));
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var report = new List<string>(); var exitCode = 0; DeviceStartupWindow? window = null;
        void Check(bool value, string text)
        {
            if (!value) throw new InvalidOperationException(text);
            report.Add("PASS " + text); File.AppendAllText(Path.Combine(output, "progress.txt"), report[^1] + Environment.NewLine);
        }
        try
        {
            var userRoot = Path.Combine(output, "user"); Directory.CreateDirectory(Path.Combine(userRoot, "FlowDeck"));
            var oldPath = Path.Combine(userRoot, "FlowDeck", "settings.json"); await File.WriteAllTextAsync(oldPath, "old settings are not a v2 profile");
            var dataRoot = DeviceStartupOptions.DefaultDirectory(userRoot);
            var store = new DeviceUiPreferencesStore(Path.Combine(dataRoot, "ui-v2.json"));
            var registration = DeviceServiceRegistration.Missing; var calls = 0; var deny = false;
            await using var demo = await DemoDeviceWorkspace.CreateAsync(Path.Combine(output, "demo-service"));
            Task<DeviceStartupConnection> Connect(CancellationToken ct) => DeviceStartupConnection.ConnectAsync(
                () => deny ? throw new UnauthorizedAccessException() : registration,
                () => { calls++; return DeviceServicePipeClient.ForDemo(demo.PipeName, Environment.ProcessId); }, ct);
            window = new(store, Connect); app.MainWindow = window; window.Show(); await window.ConnectAsync(); await Settle();
            Check(window.State == DeviceStartupState.NotInstalled && window.IsVisible && calls == 0, "Missing service keeps a visible retry screen without creating a hardware host or pipe");
            Check(window.RetryButton.IsEnabled && window.CloseButton.IsEnabled && window.NextActionText.Text.Contains("導入"), "Missing service offers retry and close with the next required action");
            Check(window.Theme == "system" && !File.Exists(store.PathName), "First generic launch ignores the legacy profile and creates no settings automatically");
            foreach (var theme in new[] { "system", "light", "dark" })
            {
                window.ThemeChoice.SelectedValue = theme; await Settle(); Capture(window, output, "startup-" + theme);
                Check(window.Theme == theme && window.StatusText.ActualHeight > 20 && window.RetryButton.ActualWidth >= 100,
                    theme + " theme preserves the service state and actions");
            }
            window.Width = 480; window.Height = 360; await Settle(); Capture(window, output, "startup-minimum");
            Check(window.ThemeChoice.ActualWidth >= 120 && window.NextActionText.ActualHeight > 10, "Minimum window keeps theme, message and actions visible");
            registration = DeviceServiceRegistration.Waiting; await window.ConnectAsync();
            Check(window.State == DeviceStartupState.WaitingForService && calls == 0, "Registered service without a confirmed running process remains waiting");
            deny = true; await window.ConnectAsync();
            Check(window.State == DeviceStartupState.AccessDenied && window.RetryButton.IsEnabled, "Permission failure is retryable and never asks for elevation");
            deny = false; registration = DeviceServiceRegistration.Running; await window.ConnectAsync();
            await using var accepted = await window.Completion; await window.FinishAsync();
            Check(accepted?.Snapshot?.Connected == true && !window.IsVisible && calls == 1, "Retry transfers the authenticated session and closes the startup window");
            Check(store.Load().Theme == "dark" && await File.ReadAllTextAsync(oldPath) == "old settings are not a v2 profile", "Theme saves only to the separate generic profile");
            await accepted!.RefreshAsync(); Check(accepted.Snapshot!.State is not null, "Transferred session remains usable after the startup window closes");
            await accepted.DisposeAsync();
            window = null;
            await store.SaveAsync(store.Load(), accentColor: "#A3D936");

            var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var cancelled = false; var attempts = 0;
            window = new(store, async ct =>
            {
                attempts++; began.SetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { cancelled = true; throw; }
                throw new InvalidOperationException();
            });
            app.MainWindow = window; window.Show(); var connecting = window.ConnectAsync(); await began.Task;
            var duplicate = window.ConnectAsync();
            Check(window.Busy && !window.RetryButton.IsEnabled && ReferenceEquals(duplicate, connecting) && attempts == 1, "Repeated retry cannot start a second connection attempt");
            window.Close(); await connecting; Check(await window.Completion is null && cancelled && !window.IsVisible, "Closing during connection cancels and waits for cleanup without entering the workspace");
            await window.FinishAsync(); window = null;

            registration = DeviceServiceRegistration.Missing;
            var shown = false; var opened = false;
            var trayResult = await DeviceStartupWindow.RunSessionAsync(app, new(dataRoot, true, true), Connect,
                _ => { opened = true; return Task.CompletedTask; }, current => { shown = current.IsVisible; current.Close(); });
            Check(!trayResult && shown && !opened, "Actual tray-launch path shows missing-service failure and closes without opening a workspace");
            registration = DeviceServiceRegistration.Running;
            var sessionOpen = false; var startupHidden = false;
            var routed = await DeviceStartupWindow.RunSessionAsync(app, new(dataRoot, true, true), Connect,
                async client =>
                {
                    sessionOpen = client.Snapshot?.Connected == true; var session = client.Snapshot!.Session;
                    var model = new DeviceWorkspaceViewModel(client, store, loginStartup:
                        new DeviceLoginStartup(Environment.ProcessPath!, new DemoLoginStartupRegistration(), _ => true));
                    await model.InitializeAsync();
                    var workspace = new Window { Title = "FlowDeck · DEMO", Width = 1120, Height = 820,
                        Content = new DeviceWorkspaceView(), DataContext = model };
                    app.MainWindow = workspace; workspace.Show();
                    try
                    {
                        await Settle(); Capture(workspace, output, "startup-connected-workspace");
                        Check(client.Snapshot.Session == session && model.DashboardTemperatures.Count > 0 && model.DashboardFans.Count > 0,
                            "The normal workspace initializes its temperature and RPM views using the transferred session");
                        Check(model.Theme == "dark" && model.AccentColor == "#A3D936", "The normal workspace retains the theme and local accent selected before connection");
                        await client.RefreshAsync();
                    }
                    finally { workspace.Close(); }
                },
                current => { startupHidden = !current.IsVisible; if (!current.Completion.IsCompleted) current.Close(); });
            Check(routed && sessionOpen && startupHidden, "Actual successful tray-launch path transfers its connection without showing a temporary window");
            Check(demo.Hubs.All(h => h.Writes == 0 && h.LightingWrites == 0), "Every startup and retry leaves all synthetic fan and lighting outputs unchanged");
            Check(!File.Exists(Path.Combine(dataRoot, "settings.json")) && !File.Exists(Path.Combine(dataRoot, "cooling-connection.json")), "No legacy settings or direct-mode preference is copied into the generic profile");
            await RunLocalMonitoringChecksAsync(app, output, Check);
            await RunServiceSetupChecksAsync(app, output, Check);
            await RunWorkspaceUpdateChecksAsync(app, output, Check);
        }
        catch (Exception e) { exitCode = 1; report.Add("FAIL " + e); }
        finally
        {
            if (window is not null) { window.Close(); await window.FinishAsync(); }
            await File.WriteAllLinesAsync(Path.Combine(output, "device-startup-smoke.txt"), report);
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            app.Shutdown(exitCode);
        }
    }
}
