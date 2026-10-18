using System.Windows;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Microsoft.Win32;

namespace FlowDeck.App;

public partial class DeviceStartupWindow : Window
{
    private readonly DeviceUiPreferencesStore store;
    private readonly Func<CancellationToken, Task<DeviceStartupConnection>> connect;
    private readonly Func<Window, Task<bool>> setup;
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource<DeviceWorkspaceClient?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private UiSettings preferences;
    private bool busy, closing, closed, settingUp;
    private Task attempt = Task.CompletedTask, preferenceSave = Task.CompletedTask;
    internal DeviceStartupState? State { get; private set; }
    internal Task<DeviceWorkspaceClient?> Completion => completion.Task;
    internal bool Busy => busy;
    internal string Theme => preferences.Theme;
    internal bool MonitoringRequested { get; private set; }

    internal DeviceStartupWindow(DeviceUiPreferencesStore store, Func<CancellationToken, Task<DeviceStartupConnection>> connect,
        Func<Window, Task<bool>>? setup = null)
    {
        this.store = store; this.connect = connect; this.setup = setup ?? ServiceSetupWindow.RunAsync; preferences = store.Load();
        ThemeManager.Apply(preferences.Theme, store.AccentColor); InitializeComponent();
        ThemeChoice.SelectedValue = preferences.Theme;
        PreferenceError.Text = store.LoadWarning ?? "";
        ThemeChoice.SelectionChanged += (_, _) =>
        {
            if (ThemeChoice.SelectedValue is not string theme || theme == preferences.Theme) return;
            preferences = preferences with { Theme = theme }; ThemeManager.Apply(theme, store.AccentColor);
            var value = preferences;
            preferenceSave = SaveThemeAsync(preferenceSave, value);
        };
        Closing += (_, e) =>
        {
            if (closed) return;
            if (settingUp) { e.Cancel = true; return; }
            closing = true; stop.Cancel();
            if (busy) { e.Cancel = true; StatusText.Text = "接続終了中…"; RefreshButtons(); }
            else { closed = true; completion.TrySetResult(null); }
        };
    }
    private async Task SaveThemeAsync(Task previous, UiSettings value)
    {
        await previous;
        try { await store.SaveAsync(value); PreferenceError.Text = ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { PreferenceError.Text = "テーマの保存失敗"; }
    }
    internal void ApplySystemTheme() => ThemeManager.Apply(preferences.Theme, store.AccentColor);
    private void RefreshButtons()
    {
        RetryButton.IsEnabled = !busy && !closing && !settingUp;
        MonitorButton.IsEnabled = SetupButton.IsEnabled = !busy && !closing && !settingUp && State is not (null or DeviceStartupState.Connected);
        CloseButton.IsEnabled = ThemeChoice.IsEnabled = !closing && !settingUp;
    }
    private async void Retry(object sender, RoutedEventArgs e) => await ConnectAsync();
    private void Exit(object sender, RoutedEventArgs e) => Close();
    private void OpenMonitoring(object sender, RoutedEventArgs e) { MonitoringRequested = true; Close(); }
    private async void OpenSetup(object sender, RoutedEventArgs e) => await OpenSetupAsync();
    internal async Task OpenSetupAsync()
    {
        if (busy || closing || closed || settingUp || State is null or DeviceStartupState.Connected) return;
        settingUp = true; RefreshButtons();
        var reconnect = false;
        try { reconnect = await setup(this); }
        catch (Exception) { NextActionText.Text = "導入画面の起動失敗 · 再試行"; }
        finally { settingUp = false; RefreshButtons(); }
        if (reconnect) await ConnectAsync();
    }
    internal Task ConnectAsync()
    {
        if (busy || closing || closed || settingUp) return attempt;
        attempt = ConnectCoreAsync(); return attempt;
    }
    private async Task ConnectCoreAsync()
    {
        busy = true; State = null; RefreshButtons(); StatusText.Text = "接続中…"; NextActionText.Text = "";
        DeviceWorkspaceClient? accepted = null;
        try
        {
            await using var result = await connect(stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            State = result.State;
            StatusText.Text = result.State switch
            {
                DeviceStartupState.Connected => "接続済み",
                DeviceStartupState.NotInstalled => "サービス未導入",
                DeviceStartupState.WaitingForService => "サービス接続待ち",
                DeviceStartupState.AccessDenied => "接続元・権限の確認が必要",
                DeviceStartupState.Incompatible => "サービス更新が必要",
                _ => "サービス接続失敗"
            };
            NextActionText.Text = result.State switch
            {
                DeviceStartupState.Connected => "",
                DeviceStartupState.NotInstalled => "サービスの導入・再接続",
                DeviceStartupState.WaitingForService => "サービス起動後に再接続",
                DeviceStartupState.AccessDenied => "アクセス権・実行元の確認",
                DeviceStartupState.Incompatible => "対応サービスへ更新",
                _ => "サービス状態の確認・再接続"
            };
            if (result.State == DeviceStartupState.Connected) accepted = result.TakeClient();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception)
        { State = DeviceStartupState.Unavailable; StatusText.Text = "サービス接続失敗"; NextActionText.Text = "サービス状態の確認・再接続"; }
        finally
        {
            busy = false;
            if (closing || accepted is not null)
            {
                closed = true; Close(); completion.TrySetResult(accepted);
            }
            RefreshButtons();
        }
    }
    internal async Task FinishAsync()
    { await attempt; await preferenceSave; stop.Dispose(); }

    internal static async Task RunAsync(FlowDeckApplication app, DeviceStartupOptions options)
    {
        while (true)
        {
            var reconnect = false;
            var opened = await RunSessionAsync(app, options, DeviceStartupConnection.ConnectAsync,
                async client => reconnect = await DeviceWorkspaceInstalled.RunAsync(app, client, options.Directory, options.StartInTray),
                monitor: async () => reconnect = await LocalMonitoringWindow.RunAsync(app, options.Directory));
            if (opened && !reconnect) return;
            if (!reconnect) { app.Shutdown(); return; }
            options = options with { StartInTray = false };
        }
    }
    internal static async Task<bool> RunSessionAsync(FlowDeckApplication app, DeviceStartupOptions options,
        Func<CancellationToken, Task<DeviceStartupConnection>> connect, Func<DeviceWorkspaceClient, Task> openWorkspace,
        Action<DeviceStartupWindow>? inspect = null, Func<Task>? monitor = null)
    {
        Directory.CreateDirectory(options.Directory);
        var window = new DeviceStartupWindow(new(Path.Combine(options.Directory, "ui-v2.json")), connect);
        app.MainWindow = window;
        UserPreferenceChangedEventHandler themeChanged = (_, _) => app.Dispatcher.BeginInvoke(window.ApplySystemTheme);
        SystemEvents.UserPreferenceChanged += themeChanged;
        try
        {
            // A failed tray launch must remain visible; otherwise a missing service gives no way to retry.
            // Successful tray launches connect without displaying a temporary window.
            if (!options.StartInTray) window.Show();
            await window.ConnectAsync();
            if (!window.Completion.IsCompleted && !window.IsVisible) window.Show();
            inspect?.Invoke(window);
            await using var client = await window.Completion;
            await window.FinishAsync();
            SystemEvents.UserPreferenceChanged -= themeChanged;
            if (client is null) { if (window.MonitoringRequested && monitor is not null) await monitor(); return false; }
            await openWorkspace(client); return true;
        }
        finally { SystemEvents.UserPreferenceChanged -= themeChanged; }
    }
}
