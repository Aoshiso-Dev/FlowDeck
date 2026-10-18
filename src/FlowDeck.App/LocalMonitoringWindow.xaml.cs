using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Microsoft.Win32;

namespace FlowDeck.App;

public partial class LocalMonitoringWindow : Window
{
    internal sealed class Readings
    {
        public ObservableCollection<DashboardReading> DashboardTemperatures { get; } = [];
        public ObservableCollection<DashboardReading> DashboardFans { get; } = [];
    }
    private readonly LocalMonitoringSession session;
    private readonly DeviceUiPreferencesStore store;
    private readonly DispatcherTimer timer;
    private readonly Readings readings = new();
    private readonly TaskCompletionSource<bool> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private UiSettings preferences;
    private Task save = Task.CompletedTask;
    private bool closing, closed, reading;
    internal LocalMonitoringSession Session => session;
    internal Task<bool> Completion => finished.Task;
    internal LocalMonitoringWindow(LocalMonitoringSession session, DeviceUiPreferencesStore store)
    {
        this.session = session; this.store = store; preferences = store.Load(); ThemeManager.Apply(preferences.Theme);
        InitializeComponent(); DataContext = readings; ThemeChoice.SelectedValue = preferences.Theme;
        NoticeText.Text = store.LoadWarning ?? "";
        ThemeChoice.SelectionChanged += (_, _) =>
        {
            if (ThemeChoice.SelectedValue is not string theme || preferences.Theme == theme) return;
            preferences = preferences with { Theme = theme }; ThemeManager.Apply(theme); save = SaveAsync(save, preferences);
        };
        timer = new(TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (_, _) => await PollAsync(), Dispatcher);
        Closing += async (_, e) => { if (!closed) { e.Cancel = true; await CloseAsync(false); } };
    }
    private async Task SaveAsync(Task previous, UiSettings value)
    {
        await previous;
        try { await store.SaveAsync(value); NoticeText.Text = ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { NoticeText.Text = "テーマの保存失敗"; }
    }
    internal void ApplySystemTheme() => ThemeManager.Apply(preferences.Theme);
    internal void Render()
    {
        var snapshot = session.Snapshot; StatusText.Text = snapshot.Status;
        Sync(readings.DashboardTemperatures, snapshot.Temperatures); Sync(readings.DashboardFans, snapshot.Fans);
    }
    private static void Sync(ObservableCollection<DashboardReading> target, DashboardReading[] values)
    {
        for (var i = target.Count - 1; i >= 0; i--) if (!values.Any(v => v.Key == target[i].Key)) target.RemoveAt(i);
        foreach (var value in values)
        {
            var index = target.ToList().FindIndex(v => v.Key == value.Key);
            if (index < 0) target.Add(value); else if (target[index] != value) target[index] = value;
        }
    }
    internal async Task PollAsync()
    {
        Render(); if (closing || closed || reading) return;
        reading = true;
        try { await session.PollAsync(); }
        finally { reading = false; Render(); }
    }
    internal void Start() { timer.Start(); _ = PollAsync(); }
    private async void ConnectService(object sender, RoutedEventArgs e) => await CloseAsync(true);
    private async void Exit(object sender, RoutedEventArgs e) => await CloseAsync(false);
    internal async Task CloseAsync(bool reconnect)
    {
        if (closing || closed) return;
        closing = true; timer.Stop(); ServiceButton.IsEnabled = false; CloseButton.IsEnabled = false; ThemeChoice.IsEnabled = false;
        StatusText.Text = "監視終了中…";
        try
        {
            await session.DisposeAsync(); await save;
            closed = true; Close(); finished.TrySetResult(reconnect);
        }
        catch (Exception)
        {
            StatusText.Text = "監視終了が未確認"; NoticeText.Text = "「閉じる」または「サービス接続」で再試行";
            ServiceButton.IsEnabled = true; CloseButton.IsEnabled = true;
        }
        finally { closing = false; }
    }
    internal static async Task<bool> RunAsync(FlowDeckApplication app, string directory)
    {
        await using var session = new LocalMonitoringSession(new NvidiaReadOnlyMonitor());
        var window = new LocalMonitoringWindow(session, new(Path.Combine(directory, "ui-v2.json")));
        UserPreferenceChangedEventHandler changed = (_, _) => app.Dispatcher.BeginInvoke(window.ApplySystemTheme);
        SystemEvents.UserPreferenceChanged += changed;
        try { app.MainWindow = window; window.Show(); window.Start(); return await window.Completion; }
        finally { SystemEvents.UserPreferenceChanged -= changed; }
    }
}
