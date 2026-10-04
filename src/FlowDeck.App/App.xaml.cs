using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using Microsoft.Win32;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public partial class FlowDeckApplication : Application
{
    private ApplicationInstance? instance;
    private RuntimeHost? host;
    private FlowDeckViewModel? model;
    private System.Windows.Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private bool notified, exitPending;
    public bool Exiting { get; private set; }
    internal NotificationEvent[] LastNotificationBatch { get; private set; } = [];
    internal int NotificationDeliveryAttempts { get; private set; }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if (e.Args.Contains("--devices-preview"))
            { await DeviceWorkspacePreview.RunAsync(this, e.Args); return; }
            if (!DeviceStartupOptions.LegacyDemo(e.Args))
            {
                var startup = DeviceStartupOptions.Parse(e.Args, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                if (startup.Smoke) { await SmokeRunner.RunDeviceStartupAsync(this, startup); return; }
                instance = new(false, service: true);
                if (!instance.Acquired) { MessageBox.Show("FlowDeckが既に起動しています。タスクトレイから開いてください。", "FlowDeck"); Shutdown(); return; }
                await DeviceStartupWindow.RunAsync(this, startup); return;
            }
            var smoke = e.Args.Contains("--smoke");
            if (smoke && (!e.Args.Contains("--demo") || !e.Args.Contains("--data-dir")))
                throw new InvalidOperationException("--smoke requires --demo and an isolated --data-dir");
            var dataIndex = Array.IndexOf(e.Args, "--data-dir");
            var dataRoot = dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? Path.GetFullPath(e.Args[dataIndex + 1])
                : ApplicationDataMigration.EnsureRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            var options = CoolingConnectionOptions.Resolve(e.Args, new CoolingConnectionPreferenceStore(dataRoot).Load(), smoke ? false : WindowsCoolingServiceIdentity.IsInstalled());
            instance = new(smoke, service: options.UseService);
            if (!instance.Acquired) { MessageBox.Show("FlowDeckまたは旧版が既に起動しています。タスクトレイから開いてください。", "FlowDeck"); Shutdown(); return; }
            CoolingServicePipeClient? legacyConnection = null;
            if (options.UseService && !e.Args.Contains("--demo") && !smoke)
            {
                await using var selected = await ServiceWorkspaceConnection.ConnectAsync();
                if (selected.UsesDevices)
                { await DeviceWorkspaceInstalled.RunAsync(this, selected.TakeDevices(), dataRoot, e.Args.Contains("--tray")); return; }
                legacyConnection = selected.TakeLegacy();
            }
            host = new(dataRoot, demoOnly: smoke, connection: options, initialServiceClient: legacyConnection);
            var listener = new TextWriterTraceListener(Path.Combine(EnsureDirectory(dataRoot), "wpf-bindings.log"));
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            await host.InitializeAsync(e.Args.Contains("--demo"));
            if (e.Args.Contains("--asrock-rgb-usb")) await host.UseAsrockRgbAsync();
            if (!host.Demo)
            {
                RuntimeHost.ConfigureLogin(host.Coordinator.Settings.Startup.Login);
                try { M5Firewall.MigrateLegacy(Environment.ProcessPath!); }
                catch (Exception migrationError) { host.Log.Write("firewallMigration", new { error = migrationError.Message }); }
            }
            ThemeManager.Apply(host.Coordinator.Settings.Ui.Theme);
            model = new(host); var window = new FlowDeckWindow(this, model); MainWindow = window;
            var ui = host.Coordinator.Settings.Ui;
            window.Width = Math.Max(960, ui.Width); window.Height = Math.Max(640, ui.Height);
            if (ui.Left is { } left && ui.Top is { } top && left >= SystemParameters.VirtualScreenLeft && top >= SystemParameters.VirtualScreenTop &&
                left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
            { window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = left; window.Top = top; }
            using (var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/FlowDeck.ico")).Stream)
            using (var resourceIcon = new System.Drawing.Icon(iconStream, System.Windows.Forms.SystemInformation.SmallIconSize))
                trayIcon = (System.Drawing.Icon)resourceIcon.Clone();
            tray = new() { Icon = trayIcon, Text = "FlowDeck", Visible = true };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("開く", null, (_, _) => Dispatcher.Invoke(OpenWindow));
            menu.Items.Add("現在の状態", null, (_, _) => Dispatcher.Invoke(() => tray.ShowBalloonTip(4000, "FlowDeck", model.CurrentMode + " / " + model.DutyDisplay + "%", System.Windows.Forms.ToolTipIcon.Info)));
            menu.Items.Add("アプリを終了", null, (_, _) => Dispatcher.InvokeAsync(ExitAsync));
            tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(OpenWindow);
            SystemEvents.UserPreferenceChanged += PreferenceChanged; SystemEvents.PowerModeChanged += PowerChanged;
            host.NotificationsRaised += ShowNotifications;
            if (!e.Args.Contains("--tray")) window.Show();
            if (smoke)
            {
                if (options.DemoPipe is not null) await SmokeRunner.RunServiceAsync(this, host, model, window, dataRoot);
                else await SmokeRunner.RunAsync(this, host, model, window, dataRoot);
            }
        }
        catch (UnauthorizedAccessException e1) { MessageBox.Show("別セッションの起動またはアクセス権を確認してください。\n" + e1.Message); Shutdown(1); }
        catch (Exception error) { MessageBox.Show(error.ToString(), "起動エラー"); Shutdown(1); }
    }
    private static string EnsureDirectory(string path) { Directory.CreateDirectory(path); return path; }
    private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(() => ThemeManager.Apply(host!.Coordinator.Settings.Ui.Theme));
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (host is { UsesService: true } serviceHost)
        {
            if (e.Mode is PowerModes.Suspend or PowerModes.Resume) _ = serviceHost.ServicePowerAsync(e.Mode == PowerModes.Resume);
            return;
        }
        if (e.Mode == PowerModes.Suspend)
        {
            host?.SuspendNotifications(true);
            host?.Coordinator.Suspend();
            try { host?.BoardFans.SuspendAsync().GetAwaiter().GetResult(); }
            catch (Exception error) { host?.Log.Write("cpuFanSuspend", new { error = error.Message }); }
        }
        if (e.Mode == PowerModes.Resume) _ = Task.Run(async () => { if (host is not null) { host.BoardFans.Resume(); await host.Coordinator.ResumeFromSleepAsync(); host.SuspendNotifications(false); } });
    }
    private void ShowNotifications(NotificationEvent[] events)
    {
        LastNotificationBatch = events;
        if(host is null || host.Demo || !host.NotificationOptions.Desktop || !host.NotificationOptions.Enabled) return;
        Dispatcher.BeginInvoke(() =>
        {
            if(Exiting || host.Demo || !host.NotificationOptions.Desktop || !host.NotificationOptions.Enabled) return;
            var message = string.Join(" / ",events.Take(3).Select(e => e.Message));
            if(events.Length > 3) message += $" / ほか {events.Length - 3} 件";
            if(message.Length > 240) { var cut = char.IsHighSurrogate(message[238]) ? 238 : 239; message = message[..cut] + "…"; }
            try
            {
                NotificationDeliveryAttempts++;
                tray?.ShowBalloonTip(6000,"FlowDeck",message,events.All(e => e.Kind is "recovered" or "test") ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning);
            }
            catch(Exception e) { host.Log.Write("desktopNotification",new { e.Message }); }
        });
    }
    public void OpenWindow() { MainWindow.Show(); MainWindow.WindowState = WindowState.Normal; MainWindow.Activate(); }
    public void HideToTray()
    {
        MainWindow.Hide();
        if (!notified) { notified = true; tray?.ShowBalloonTip(4000, "FlowDeck", "タスクトレイで制御を継続しています", System.Windows.Forms.ToolTipIcon.Info); }
    }
    public async Task ExitAsync()
    {
        if (exitPending || host is null) return;
        exitPending = true;
        var forcedExit = false;
        try
        {
            while (true)
            {
                model!.Notice = "終了時出力を確認しています。自動制御を終了します。";
                var progress = new Window { Title = "FlowDeck の終了", Width = 420, Height = 150,
                    ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ShowInTaskbar = false, Content = new System.Windows.Controls.StackPanel { Margin = new Thickness(20), Children =
                    { new System.Windows.Controls.TextBlock { Text = "終了時出力を確認しています。自動制御を終了します。", Margin = new Thickness(0, 0, 0, 16) },
                      new System.Windows.Controls.ProgressBar { IsIndeterminate = true, Height = 5 } } } };
                var completed = false;
                progress.Closing += (_, e) => e.Cancel = !completed;
                progress.Show();
                OperationResult result;
                try
                {
                    result = await host.Coordinator.StopControlAsync();
                    try { await host.BoardFans.RestoreAsync(); }
                    catch (Exception error) { result = result with { Outcome = Outcome.Unknown, Message = "マザーファンの復元が未確認です: " + error.Message }; }
                }
                finally { completed = true; progress.Close(); }
                if (result.Outcome == Outcome.Confirmed) break;
                var dialog = new ExitFailureWindow(result.Message) { Owner = MainWindow.IsVisible ? MainWindow : null };
                dialog.ShowDialog();
                if (dialog.Choice == "retry") continue;
                if (dialog.Choice == "force") { host.Log.Write("forcedExit", result); forcedExit = true; break; }
                model.Notice = "終了を中止して常駐を続けています"; return;
            }
            var settings = host.Coordinator.Settings;
            await host.Coordinator.UpdateSettingsAsync(settings with { Ui = settings.Ui with { Left = MainWindow.Left, Top = MainWindow.Top, Width = MainWindow.RestoreBounds.Width, Height = MainWindow.RestoreBounds.Height } }, false);
            Exiting = true; model!.Dispose(); tray?.Dispose();
            SystemEvents.UserPreferenceChanged -= PreferenceChanged; SystemEvents.PowerModeChanged -= PowerChanged;
            try { await host.DisposeAsync(); }
            catch (Exception error) when (forcedExit) { host.Log.Write("forcedExitDispose", new { error = error.Message }); }
            instance?.Dispose(); instance = null;
            Shutdown();
        }
        finally { exitPending = false; }
    }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        host?.Log.Write("osSessionEnding", new { e.ReasonSessionEnding, message = "OS終了中の出力確認は保証されません" });
        try { host?.BoardFans.SuspendAsync().GetAwaiter().GetResult(); }
        catch (Exception error) { host?.Log.Write("cpuFanSessionEnding", new { error = error.Message }); }
        base.OnSessionEnding(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose();
        trayIcon?.Dispose();
        instance?.Dispose(); instance = null;
        base.OnExit(e);
    }
}
