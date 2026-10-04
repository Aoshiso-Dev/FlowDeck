using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using FlowDeck.Infrastructure;
using Microsoft.Win32;

namespace FlowDeck.App;

// Development host for the service-only view. Never constructs RuntimeHost or a real hardware provider.
public static class DeviceWorkspacePreview
{
    public static async Task RunAsync(FlowDeckApplication app, string[] args)
    {
        var options = DeviceWorkspaceOptions.Parse(args);
        var directory = options.Directory; Directory.CreateDirectory(directory);
        DemoDeviceWorkspace? demo = null; DeviceWorkspaceClient? client = null;
        DeviceM5Session? m5 = null; DemoM5LinkFactory? m5Factory = null;
        UserPreferenceChangedEventHandler? preferenceChanged = null;
        TextWriterTraceListener? listener = null;
        try
        {
            if (options.External) client = new(DeviceServicePipeClient.ForDemo(options.PipeName!, options.ServerProcessId!.Value));
            else { demo = await DemoDeviceWorkspace.CreateAsync(Path.Combine(directory, "run-" + Guid.NewGuid().ToString("N")), monitoringOnly: options.MonitoringSetup || options.EndpointSetup || options.CoolingCalibration, providerRouting: options.ProviderRoutes, manySetupCandidates: options.EndpointSetup); client = demo.CreateClient(); }
            if (options.External && options.Smoke)
            { await DeviceWorkspaceInstalled.RunAsync(app, client, directory, startInTray: false, smoke: true); client = null; return; }
            if (options.M5Connection) { m5Factory = new(); m5 = new(client, m5Factory); }
            var loginRegistration = new DemoLoginStartupRegistration();
            var login = new DeviceLoginStartup(Environment.ProcessPath!, loginRegistration, _ => true);
            var model = new DeviceWorkspaceViewModel(client, new DeviceUiPreferencesStore(Path.Combine(directory, "ui.json")), m5, login);
            listener = new TextWriterTraceListener(Path.Combine(directory, "bindings.log"));
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            await model.InitializeAsync(); ThemeManager.Apply(model.Theme);
            var window = new Window { Title = "FlowDeck · DEMO", Width = 1120, Height = 820, MinWidth = 960, MinHeight = 640,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, DataContext = model,
                Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/FlowDeck.ico")),
                Content = new DeviceWorkspaceView() };
            preferenceChanged = (_, _) => app.Dispatcher.BeginInvoke(model.ApplySystemTheme);
            SystemEvents.UserPreferenceChanged += preferenceChanged;
            if (!options.Smoke)
            {
                var saved = model.WindowPreferences;
                window.Width = Math.Clamp(saved.Width, window.MinWidth, Math.Max(window.MinWidth, SystemParameters.VirtualScreenWidth));
                window.Height = Math.Clamp(saved.Height, window.MinHeight, Math.Max(window.MinHeight, SystemParameters.VirtualScreenHeight));
                if (saved.Left is { } x && saved.Top is { } y && x < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80 &&
                    x + window.Width > SystemParameters.VirtualScreenLeft + 80 && y >= SystemParameters.VirtualScreenTop && y < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80)
                { window.Left = x; window.Top = y; window.WindowStartupLocation = WindowStartupLocation.Manual; }
            }
            app.MainWindow = window; window.Show();
            if (options.Smoke)
            {
                var result = options.CacheRetirement ? await SmokeRunner.RunDeviceCacheRetirementAsync(model, demo!, client, window, directory) :
                    options.RegistrationRemoval ? await SmokeRunner.RunDeviceRegistrationRemovalAsync(model, demo!, client, window, directory) :
                    options.ProviderRoutes ? await SmokeRunner.RunDeviceProviderRoutesAsync(model, demo!, client, window, directory) :
                    options.LoginStartup ? await SmokeRunner.RunDeviceLoginStartupAsync(model, demo!, client, loginRegistration, window, directory) :
                    options.Diagnostics ? await SmokeRunner.RunDeviceDiagnosticsAsync(model, demo!, client, window, directory) :
                    options.OpenRgbInspection ? await SmokeRunner.RunDeviceOpenRgbInspectionAsync(model, demo!, client, window, directory) :
                    options.LightingIdentification ? await SmokeRunner.RunDeviceLightingIdentificationAsync(model, demo!, client, window, directory) :
                    options.LightingPersonalization ? await SmokeRunner.RunDeviceLightingPersonalizationAsync(model, demo!, client, window, directory) :
                    options.NativeLighting ? await SmokeRunner.RunDeviceNativeLightingAsync(model, demo!, client, window, directory) :
                    options.LightingLevels ? await SmokeRunner.RunDeviceLightingLevelsAsync(model, demo!, client, window, directory) :
                    options.LightingLibrary ? await SmokeRunner.RunDeviceLightingLibraryAsync(model, demo!, client, window, directory) :
                    options.M5Connection ? await SmokeRunner.RunDeviceM5ConnectionAsync(model, demo!, m5Factory!, window, directory) :
                    options.M5Settings ? await SmokeRunner.RunDeviceM5SettingsAsync(model, demo!, client, window, directory) :
                    options.Notifications ? await SmokeRunner.RunDeviceNotificationsAsync(model, demo!, window, directory) :
                    options.CoolingCalibration ? await SmokeRunner.RunDeviceCoolingCalibrationAsync(model, demo!, window, directory) :
                    options.CoolingGroups ? await SmokeRunner.RunDeviceCoolingGroupsAsync(model, demo!, window, directory) :
                    options.CoolingLifecycle ? await SmokeRunner.RunDeviceCoolingLifecycleAsync(model, demo!, client, window, directory) :
                    options.SettingsTransfer ? await SmokeRunner.RunDeviceSettingsTransferAsync(model, demo!, window, directory) :
                    options.CoolingSettings ? await SmokeRunner.RunDeviceCoolingSettingsAsync(model, demo!, window, directory) :
                    options.EndpointSetup ? await SmokeRunner.RunDeviceEndpointSetupAsync(model, demo!, window, directory) :
                    options.MonitoringSetup ? await SmokeRunner.RunDeviceMonitoringSetupAsync(model, demo!, window, directory) :
                    demo is null ? await SmokeRunner.RunExternalDeviceWorkspaceAsync(model, client, window, directory) :
                    await SmokeRunner.RunDeviceWorkspaceAsync(model, demo, window, directory);
                await model.FlushUiAsync();
                if (m5 is not null) { await m5.DisposeAsync(); m5 = null; }
                await client.DisposeAsync(); client = null; if (demo is not null) { await demo.DisposeAsync(); demo = null; }
                window.Close(); app.Shutdown(result); return;
            }
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closing = false; var closed = false;
            var timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (_, _) => await model.PollAsync(), app.Dispatcher);
            timer.Start();
            window.Closing += async (_, e) =>
            {
                if (closed) return;
                e.Cancel = true; if (closing) return; closing = true;
                try { if (await model.CloseAsync()) {
                    await model.SaveWindowAsync(window.WindowState == WindowState.Normal ? new(window.Left, window.Top, window.Width, window.Height) : window.RestoreBounds);
                    closed = true; timer.Stop(); window.Close(); finished.TrySetResult(); } }
                finally { closing = false; }
            };
            await finished.Task;
        }
        catch (Exception e) { await File.WriteAllTextAsync(Path.Combine(directory, "failure.txt"), e.ToString()); app.Shutdown(1); }
        finally { if (listener is not null) { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); listener.Dispose(); }
            if (preferenceChanged is not null) SystemEvents.UserPreferenceChanged -= preferenceChanged;
            try { if (m5 is not null) await m5.DisposeAsync(); }
            finally { if (client is not null) await client.DisposeAsync(); if (demo is not null) await demo.DisposeAsync(); } }
        app.Shutdown();
    }
}
