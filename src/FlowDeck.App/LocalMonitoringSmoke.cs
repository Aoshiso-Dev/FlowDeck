using System.Windows;
using System.Windows.Controls;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    private sealed class LocalGpuDemo : IHardwareSensorBackend
    {
        public double? Temperature = 48, Rpm = 0;
        public double Age;
        public DeviceMeasurementIssue Issue;
        public bool Disconnected, MissingDriver;
        public int Reads, Disposals, FailDispose;
        public TaskCompletionSource? Delay;
        public async Task<HardwareSensorDevice[]> ReadDevicesAsync(CancellationToken ct)
        {
            Reads++;
            if (Delay is not null) await Delay.Task.WaitAsync(ct);
            if (MissingDriver) throw new DllNotFoundException();
            if (Disconnected) return [];
            var stamp = TimeProvider.System.GetTimestamp() - (long)(Age * TimeProvider.System.TimestampFrequency);
            return [new("/demo/gpu", "DEMO GPU", DeviceKind.Graphics, new('a', 64), null, "11111111111111111111111111111111", null,
                [new("temperature", "GPU", "/demo/gpu", DeviceFeature.Temperature, Issue == DeviceMeasurementIssue.None ? Temperature : null, stamp, Issue: Issue),
                 new("fan", "GPU Fan", "/demo/gpu", DeviceFeature.Rpm, Issue == DeviceMeasurementIssue.None ? Rpm : null, stamp, Issue: Issue)])];
        }
        public ValueTask DisposeAsync() { Disposals++; if (FailDispose-- > 0) throw new IOException("demo cleanup failure"); return ValueTask.CompletedTask; }
    }
    private static async Task RunLocalMonitoringChecksAsync(FlowDeckApplication app, string output, Action<bool, string> check)
    {
        var directory = Path.Combine(output, "local-monitor"); var factoryCalls = 0; var selected = false;
        Task<DeviceStartupConnection> NoService(CancellationToken ct) => DeviceStartupConnection.ConnectAsync(() => DeviceServiceRegistration.Missing,
            () => { factoryCalls++; throw new InvalidOperationException("must not create a pipe"); }, ct);
        var routed = await DeviceStartupWindow.RunSessionAsync(app, new(directory, false, true), NoService,
            _ => throw new InvalidOperationException("must not open a service workspace"),
            current =>
            {
                check(current.MonitorButton.IsEnabled && current.MonitorButton.ActualWidth >= 100, "Missing-service screen offers monitoring without service connection");
                current.MonitorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                selected = current.MonitoringRequested;
            }, async () =>
            {
                var backend = new LocalGpuDemo(); await using var session = new LocalMonitoringSession(backend);
                var window = new LocalMonitoringWindow(session, new(Path.Combine(directory, "ui-v2.json")));
                app.MainWindow = window; window.Show();
                try
                {
                    await window.PollAsync(); await Settle();
                    check(session.Snapshot.Temperatures.Single().Value == 48 && session.Snapshot.Fans.Single().Value == 0,
                        "Local monitor displays acquired GPU temperature and real zero RPM");
                    check(Descendants<Slider>(window).Count() == 0 && Descendants<Button>(window).All(b => b.Content is not ("適用" or "照明")),
                        "Monitoring window exposes no cooling output or lighting controls");
                    foreach (var theme in new[] { "system", "light", "dark" })
                    {
                        window.ThemeChoice.SelectedValue = theme; await Settle(); Capture(window, output, "local-monitor-" + theme);
                        check(window.ServiceButton.IsVisible && Descendants<TelemetryMeter>(window).Any(), theme + " local monitor renders temperature and RPM indicators");
                    }
                    window.Width = 860; window.Height = 540; await Settle(); Capture(window, output, "local-monitor-minimum");
                    check(window.ServiceButton.ActualWidth > 100 && window.ThemeChoice.ActualWidth >= 120, "Minimum monitor layout preserves service and theme actions");
                    backend.Age = 5; await window.PollAsync();
                    check(session.Snapshot.Temperatures.Single().Value is null && session.Snapshot.Fans.Single().State == "更新停止", "Old monitoring samples stop displaying numeric values");
                    backend.Age = 0; backend.Issue = DeviceMeasurementIssue.OutOfRange; await window.PollAsync();
                    check(session.Snapshot.Temperatures.Single().State == "範囲外", "Out-of-range monitoring data preserves its reason");
                    backend.Issue = DeviceMeasurementIssue.Unavailable; await window.PollAsync();
                    check(session.Snapshot.Fans.Single().State == "未取得", "Unavailable RPM is distinct from a stopped fan");
                    backend.Issue = DeviceMeasurementIssue.None; backend.Disconnected = true; await window.PollAsync();
                    check(session.Snapshot.Temperatures.Single().State == "切断", "Removing a GPU leaves its previous row disconnected");
                    backend.Disconnected = false; await window.PollAsync();
                    check(session.Snapshot.Temperatures.Single().Value == 48, "Acquisition recovery restores fresh readings");
                    backend.MissingDriver = true; await window.PollAsync();
                    check(session.Snapshot.Temperatures.Single().Value is null && window.StatusText.Text.Contains("ドライバー"), "Missing driver cannot present cached readings as current");
                    backend.MissingDriver = false; await window.PollAsync(); backend.FailDispose = 1;
                    await window.CloseAsync(true);
                    check(!window.Completion.IsCompleted && window.IsVisible && window.ServiceButton.IsEnabled,
                        "Failed monitor cleanup prevents handoff and remains retryable");
                    var reads = backend.Reads; await window.PollAsync();
                    check(backend.Reads == reads, "Failed cleanup does not restart monitoring");
                    await window.CloseAsync(true);
                    check(await window.Completion && !window.IsVisible && backend.Disposals == 2, "Service handoff is requested only after monitoring cleanup succeeds");
                }
                finally { await window.CloseAsync(false); }
            });
        check(selected && !routed && factoryCalls == 0, "The initial monitoring route never authenticates an unrelated or legacy service");
        var delayed = new LocalGpuDemo { Delay = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var pending = new LocalMonitoringSession(delayed);
        var cancellable = new LocalMonitoringWindow(pending, new(Path.Combine(directory, "ui-v2.json")));
        app.MainWindow = cancellable; cancellable.Show(); var polling = cancellable.PollAsync();
        var anotherTick = cancellable.PollAsync();
        check(anotherTick.IsCompleted && delayed.Reads == 1 && !polling.IsCompleted,
            "Slow acquisition does not accumulate timer callbacks or duplicate reads");
        await cancellable.CloseAsync(false); await polling;
        check(!await cancellable.Completion && delayed.Disposals == 1, "Closing local monitoring cancels pending acquisition and releases the backend");
    }
}
