using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Microsoft.Win32;

namespace FlowDeck.App;

// The normal v2 window owns only its authenticated client, tray and per-user appearance.
internal static class DeviceWorkspaceInstalled
{
    internal static async Task<bool> RunAsync(FlowDeckApplication app, DeviceWorkspaceClient client, string directory, bool startInTray, bool smoke = false,
        Func<Window, Func<Task<bool>>, Task>? setup = null, Func<DeviceWorkspaceViewModel, Window, Task>? inspect = null)
    {
        await using var ownedClient = client;
        Directory.CreateDirectory(directory);
        using var listener = new TextWriterTraceListener(Path.Combine(directory, "bindings.log"));
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var login = client.IsDemo ? new DeviceLoginStartup(Environment.ProcessPath!, new DemoLoginStartupRegistration(), _ => true) : DeviceLoginStartup.ForCurrentProcess();
        var model = new DeviceWorkspaceViewModel(client, new DeviceUiPreferencesStore(Path.Combine(directory, "ui-v2.json")), login);
        model.ConfigureDial(directory);
        UserPreferenceChangedEventHandler? preferenceChanged = null;
        System.Windows.Forms.NotifyIcon? tray = null; System.Drawing.Icon? trayIcon = null; DispatcherTimer? timer = null;
        Action<NotificationEvent[]>? showNotifications = null;
        var closed = false; var closing = false; var reconnect = false; Window? window = null;
        var handoff = new DeviceUpdateHandoff(model.CloseAsync, async () =>
        { timer?.Stop(); await client.DisposeAsync(); });
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitCode = 0;
        try
        {
            await model.InitializeAsync();
            window = new Window { Title = client.IsDemo ? "FlowDeck · DEMO" : "FlowDeck", Width = 1120, Height = 820, MinWidth = 960, MinHeight = 640,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, DataContext = model,
                Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/FlowDeck.ico")), Content = new DeviceWorkspaceView() };
            var saved = model.WindowPreferences;
            window.Width = Math.Clamp(saved.Width, window.MinWidth, Math.Max(window.MinWidth, SystemParameters.VirtualScreenWidth));
            window.Height = Math.Clamp(saved.Height, window.MinHeight, Math.Max(window.MinHeight, SystemParameters.VirtualScreenHeight));
            if (saved.Left is { } x && saved.Top is { } y && x < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80 &&
                x + window.Width > SystemParameters.VirtualScreenLeft + 80 && y >= SystemParameters.VirtualScreenTop && y < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80)
            { window.Left = x; window.Top = y; window.WindowStartupLocation = WindowStartupLocation.Manual; }
            app.MainWindow = window;
            preferenceChanged = (_, _) => app.Dispatcher.BeginInvoke(model.ApplySystemTheme);
            SystemEvents.UserPreferenceChanged += preferenceChanged;
            using (var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/FlowDeck.ico")).Stream)
            using (var resource = new System.Drawing.Icon(iconStream, System.Windows.Forms.SystemInformation.SmallIconSize)) trayIcon = (System.Drawing.Icon)resource.Clone();
            tray = new() { Icon = trayIcon, Text = "FlowDeck", Visible = true };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            void Open() { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); }
            menu.Items.Add("開く", null, (_, _) => app.Dispatcher.Invoke(Open));
            menu.Items.Add("現在の状態", null, (_, _) => app.Dispatcher.Invoke(() => tray.ShowBalloonTip(4000, "FlowDeck", model.Status, System.Windows.Forms.ToolTipIcon.Info)));
            menu.Items.Add("アプリを終了", null, (_, _) => app.Dispatcher.InvokeAsync(ExitAsync));
            tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => app.Dispatcher.Invoke(Open);
            tray.BalloonTipClicked += (_, _) => app.Dispatcher.Invoke(() => { model.SelectedPage = 3; model.NotificationExpanded = true; Open(); });
            showNotifications = events =>
            {
                if (client.IsDemo || closing || closed || events.Length == 0) return;
                var message = string.Join(" / ", events.Take(3).Select(e => e.Message));
                if (events.Length > 3) message += $" / ほか {events.Length - 3}件";
                if (message.Length > 240) message = message[..(char.IsHighSurrogate(message[238]) ? 238 : 239)] + "…";
                tray.ShowBalloonTip(6000, "FlowDeck", message, events.All(e => e.Kind is "recovered" or "test") ?
                    System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning);
            };
            model.DesktopNotifications += showNotifications;
            window.Closing += (_, e) => { if (!closed) { e.Cancel = true; window.Hide(); } };
            if (!startInTray || smoke) window.Show();
            if (smoke)
            {
                if (!client.IsDemo) throw new InvalidOperationException("実機接続では画面試験を実行できません");
                window.Close(); var hidden = !window.IsVisible; Open();
                var checks = new[] { (tray.Visible, "Tray is available"), (hidden, "Closing the window keeps the session in the tray"),
                    (window.IsVisible && client.Snapshot?.Connected == true, "Opening the tray window preserves the service session") };
                await File.WriteAllLinesAsync(Path.Combine(directory, "device-shell-smoke.txt"), checks.Select(c => (c.Item1 ? "PASS " : "FAIL ") + c.Item2));
                exitCode = checks.All(c => c.Item1) ? await SmokeRunner.RunExternalDeviceWorkspaceAsync(model, client, window, directory) : 1;
                await model.SaveWindowAsync(window.RestoreBounds); closed = true; window.Close();
            }
            else
            {
                await model.PrepareFirstUseAsync();
                await model.RestoreStartupAsync();
                await model.StartDialAsync();
                if(!closed)
                {
                    timer = new(TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (_, _) => await model.PollAsync(), app.Dispatcher);
                    timer.Start();
                }
                if (!client.IsDemo || setup is not null) model.ConfigureServiceUpdate(UpdateAsync);
                if (inspect is not null) await inspect(model, window);
                await finished.Task;
            }
            async Task UpdateAsync()
            {
                if (closing || closed) return;
                closing = true; Open();
                try
                {
                    if (setup is not null) await setup(window, handoff.PrepareAsync);
                    else await ServiceSetupWindow.RunAsync(window, ServiceSetupSession.ForApplication(AppContext.BaseDirectory, handoff.PrepareAsync));
                }
                finally
                {
                    if (handoff.Prepared)
                    {
                        // Even a cancelled UAC or failed update returns through a fresh authenticated client.
                        // The previous client and its output ownership have already been closed.
                        try { await model.SaveWindowAsync(window.WindowState == WindowState.Normal ? new(window.Left, window.Top, window.Width, window.Height) : window.RestoreBounds); }
                        finally { reconnect = true; closed = true; timer?.Stop(); window.Close(); finished.TrySetResult(); }
                    }
                    closing = false;
                }
            }
            async Task ExitAsync()
            {
                if (closing || closed) return; closing = true;
                try
                {
                    while (!await model.CloseAsync())
                    {
                        Open(); var failure = new ExitFailureWindow(model.Notice) { Owner = window }; failure.ShowDialog();
                        if (failure.Choice == "retry") continue;
                        if (failure.Choice != "force") return;
                        break;
                    }
                    await model.SaveWindowAsync(window.WindowState == WindowState.Normal ? new(window.Left, window.Top, window.Width, window.Height) : window.RestoreBounds);
                    closed = true; timer?.Stop(); window.Close(); finished.TrySetResult();
                }
                finally { closing = false; }
            }
        }
        catch (Exception e)
        {
            exitCode = 1; await File.WriteAllTextAsync(Path.Combine(directory, "device-workspace-failure.txt"), e.ToString());
            if (inspect is not null) throw;
            if (!smoke) MessageBox.Show(e.Message, "FlowDeck");
        }
        finally
        {
            model.ConfigureServiceUpdate(null);
            timer?.Stop(); tray?.Dispose(); trayIcon?.Dispose();
            if (showNotifications is not null) model.DesktopNotifications -= showNotifications;
            if (preferenceChanged is not null) SystemEvents.UserPreferenceChanged -= preferenceChanged;
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            if (window is not null && !closed) { closed = true; window.Close(); }
            try { await model.DisconnectDialAsync(); }
            finally { await client.DisposeAsync(); }
        }
        if (!reconnect) app.Shutdown(exitCode);
        return reconnect;
    }
}
