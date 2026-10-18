using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    // Dedicated development host: only authenticated in-process DEMO pipes and memory-backed devices.
    public static async Task RunDeviceConnectionStatusHostAsync(FlowDeckApplication app, string output)
    {
        var report = new List<string>(); var screenshots = new List<string>(); string? error = null;
        Window? window = null; DeviceWorkspaceViewModel? model = null;
        void Check(bool condition, string label)
        { if (!condition) throw new InvalidOperationException(label); report.Add("PASS " + label); }
        using var bindingLog = new TextWriterTraceListener(Path.Combine(output, "bindings.log"));
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingLog);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            await using var demo = await DemoDeviceWorkspace.CreateAsync(Path.Combine(output, "run-" + Guid.NewGuid().ToString("N")));
            await using var relay = new ConnectionStatusRelay(demo.PipeName);
            await relay.StartAsync();
            await using var client = new DeviceWorkspaceClient(DeviceServicePipeClient.ForDemo(relay.PipeName, Environment.ProcessId));
            model = new(client, new DeviceUiPreferencesStore(Path.Combine(output, "ui.json")));
            ThemeManager.Apply(model.Theme);
            window = new Window { Title = "FlowDeck · DEMO", Width = 1120, Height = 820, MinWidth = 960, MinHeight = 640,
                DataContext = model, Content = new DeviceWorkspaceView(), WindowStartupLocation = WindowStartupLocation.CenterScreen };
            app.MainWindow = window; window.Show(); await Settle();
            var label = Descendants<TextBlock>(window).Single(t => AutomationProperties.GetAutomationId(t) == "ServiceConnectionLabel");
            var count = Descendants<TextBlock>(window).Single(t => AutomationProperties.GetAutomationId(t) == "ConnectedDeviceCount");
            var reconnect = Descendants<Button>(window).Single(b => AutomationProperties.GetAutomationId(b) == "ReconnectServiceButton");
            var warning = Descendants<TextBlock>(window).Single(t => AutomationProperties.GetAutomationId(t) == "WorkspaceWarningMessage");
            void Header(string state, bool show, bool enabled = false)
            {
                Check(model.ConnectionLabel == "共通サービス · デモ機器：" + state && label.Text == model.ConnectionLabel &&
                    model.ShowReconnect == show && reconnect.IsVisible == show && (!show || reconnect.IsEnabled == enabled),
                    $"{state}: the bound service label and reconnect visibility/enabled state agree");
            }
            async Task CaptureThemes(string state)
            {
                foreach (var theme in new[] { "light", "dark" })
                {
                    model.Theme = theme; await Settle();
                    var name = "connection-status-" + state + "-" + theme;
                    Capture(window, output, name); screenshots.Add(name + ".png");
                }
            }
            Header("接続待ち", false);
            Check(client.Snapshot is null && count.Text == "接続機器 —", "Before initialization the device count is unknown and no retry action is presented");
            using (var hold = relay.PauseNext(DeviceOperation.ReadStateSnapshot))
            {
                var initialize = model.InitializeAsync(); await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                try { Header("接続待ち", false); await CaptureThemes("waiting"); }
                finally { hold.Resume(); }
                await initialize.WaitAsync(TimeSpan.FromSeconds(8));
            }
            await Settle(); Header("接続中", false);
            var snapshot = client.Snapshot!; var generation = snapshot.Configuration.Generation;
            var configuration = SettingsTransfer.Hash(snapshot.Configuration.Configuration);
            var writes = demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).ToArray();
            Check(client.IsDemo && demo.Hubs.All(h => h.IsDemo) && writes.All(w => w == (0, 0)), "Initial connection uses only DEMO and does not emit lighting or cooling output");
            Check(count.Text == $"接続機器 {snapshot.State.Inventory.Devices.Count(d => d.Connected)}台", "The left header counts connected devices rather than service connections");
            Check(Equals(reconnect.Content, "再接続") && AutomationProperties.GetName(reconnect) == "サービスに再接続", "The retry action retains its visible caption and accessible name");
            await CaptureThemes("connected");

            using (var hold = relay.PauseNext(DeviceOperation.ReadStateSnapshot))
            {
                var poll = model.PollAsync(); await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                try { await Settle(); Header("接続中", false); Check(!poll.IsCompleted, "A real pending telemetry request does not masquerade as reconnecting"); }
                finally { hold.Resume(); }
                await poll.WaitAsync(TimeSpan.FromSeconds(8));
            }

            var originalSession = client.Snapshot!.Session;
            await relay.DropAsync(); await WaitForAsync(() => client.NeedsReconnect); model.Render(); await Settle();
            Header("未接続", true, true); Check(count.Text == "接続機器 —", "A broken pipe does not display the cached connected-device count");
            Check(model.Devices.Count == 0 && model.VisibleCooling.Count == 0 && model.Lights.Count == 0 &&
                model.DashboardFans.Count == 0 && model.DashboardTemperatures.Count == 0 && model.DashboardTrendOptions.Count == 0 &&
                model.DeviceNameChoices.Count == 0 && model.SetupChoices.Count == 0 && model.DashboardSettings.Count == 0,
                "A broken service connection hides all cached live device lists and selectors");
            relay.RejectNextHello();
            var retryCommand = reconnect.Command;
            retryCommand.Execute(null);
            await WaitForAsync(() => relay.RejectedHellos == 1 && client.NeedsReconnect && retryCommand.CanExecute(null) &&
                !string.IsNullOrWhiteSpace(model.Notice) && !string.IsNullOrWhiteSpace(model.WarningMessage));
            await Settle(); Header("未接続", true, true);
            var connectionWarning = model.Notice;
            Check(relay.RejectedHellos == 1 && client.NeedsReconnect && retryCommand.CanExecute(null),
                "A rejected handshake through the actual button command ends progress and leaves an actionable retry");
            Check(warning.IsVisible && warning.Text == model.WarningMessage && warning.Text.Contains(connectionWarning),
                "The button command reports its connection failure in the visible warning area");
            await CaptureThemes("failed");
            using (var hold = relay.PauseNext(DeviceOperation.ReadStateSnapshot))
            {
                var reconnecting = model.ReconnectAsync(); await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                try { await Settle(); Header("再接続中", true, false); await CaptureThemes("reconnecting"); }
                finally { hold.Resume(); }
                await reconnecting.WaitAsync(TimeSpan.FromSeconds(8));
            }
            await Settle(); Header("接続中", false);
            Check(client.Snapshot!.Session != originalSession, "Successful manual retry establishes a new authenticated session");
            Check(model.Devices.Count > 0 && model.VisibleCooling.Count > 0 && model.Lights.Count > 0 && model.DashboardFans.Count > 0,
                "Reconnecting the service restores the connected device lists");
            Check(model.Notice.Length == 0 && model.WarningMessage == model.PersistentWarning && warning.Text == model.WarningMessage &&
                warning.IsVisible == (model.WarningMessage.Length > 0) && model.StatusDetails.Contains(connectionWarning),
                "Successful retry clears its connection warning while preserving current-state warnings and the failure history");
            await CaptureThemes("recovered");

            await relay.DropAsync(); await WaitForAsync(() => client.NeedsReconnect); model.Render(); await Settle();
            using (var hold = relay.PauseNext(DeviceOperation.ReadStateSnapshot))
            {
                // Exercise the real five-second retry backoff without changing private ViewModel state.
                Task automatic = Task.CompletedTask;
                var deadline = Environment.TickCount64 + 8000;
                while (!hold.Entered.Task.IsCompleted && Environment.TickCount64 < deadline)
                {
                    automatic = model.PollAsync();
                    if (!automatic.IsCompleted) break;
                    await automatic; await Task.Delay(100);
                }
                await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                try { await Settle(); Header("再接続中", true, false); Check(!automatic.IsCompleted, "Automatic retry publishes reconnect progress while its real state read is pending"); }
                finally { hold.Resume(); }
                await automatic.WaitAsync(TimeSpan.FromSeconds(8));
            }
            await Settle(); Header("接続中", false);

            relay.FailNextReply(DeviceOperation.ReadStateSnapshot);
            await model.PollAsync(); await Settle(); Header("状態取得失敗", false);
            Check(!client.NeedsReconnect && client.Snapshot is { Connected: false }, "A failed state reply on a live pipe does not offer a redundant reconnect action");
            await model.PollAsync(); await Settle(); Header("接続中", false);
            Check(client.Snapshot!.Configuration.Generation == generation && SettingsTransfer.Hash(client.Snapshot.Configuration.Configuration) == configuration &&
                writes.SequenceEqual(demo.Hubs.Select(h => (h.Writes, h.LightingWrites))), "Polling, failed handshakes and both reconnect paths preserve configuration and all output counters");

            relay.FailNextReply(DeviceOperation.ReleaseSession);
            using (var hold = relay.PauseNext(DeviceOperation.ReleaseSession))
            {
                var close = model.CloseAsync(); await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                try { await Settle(); Header("終了処理中", false); Check(!close.IsCompleted, "Closing remains distinct from disconnection while the release reply is pending"); }
                finally { hold.Resume(); }
                Check(!await close.WaitAsync(TimeSpan.FromSeconds(8)), "An injected failed release result keeps the GUI available for its exit retry");
            }
            await Settle(); Header("終了処理待ち", false);
            Check(client.Snapshot is { ExitAttempted: true } && !model.Reconnect.CanExecute(null), "A consumed session awaiting exit retry cannot resume normal control");
            Check(await model.CloseAsync(), "Explicit exit retry completes through a fresh authenticated DEMO session");
            Check(writes.SequenceEqual(demo.Hubs.Select(h => (h.Writes, h.LightingWrites))) && demo.Runtime.State().Endpoints.All(e => !e.Owned),
                "Connection and exit status checks finish with no output writes or owned endpoints");
            await model.FlushUiAsync();
        }
        catch (Exception e) { error = e.ToString(); report.Add("FAIL " + error); report.Add("STATE " + model?.ConnectionLabel); }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingLog); }
        await File.WriteAllLinesAsync(Path.Combine(output, "device-connection-status-smoke.txt"), report);
        await File.WriteAllTextAsync(Path.Combine(output, "connection-status-result.json"), JsonSerializer.Serialize(new
        { passed = report.Count(l => l.StartsWith("PASS ", StringComparison.Ordinal)), failed = error is null ? 0 : 1, error,
            hardwareAccessed = false, scenario = "connection-status", screenshots }, new JsonSerializerOptions { WriteIndented = true }));
        window?.Close(); app.Shutdown(error is null ? 0 : 1);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition()) { if (Environment.TickCount64 >= deadline) throw new TimeoutException("DEMO connection transition timed out"); await Task.Delay(20); }
    }

    // A transparent DEMO relay exercises the production client, protocol and service. Hooks only delay,
    // close or replace one response; they never assign ViewModel/private transport state or actuate devices.
    private sealed class ConnectionStatusRelay(string upstreamPipe) : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private readonly object gate = new();
        private readonly TaskCompletionSource listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task serving = Task.CompletedTask;
        private CancellationTokenSource? active;
        private TaskCompletionSource? activeEnded;
        private ConnectionPause? pause;
        private DeviceOperation? failedReply;
        private int rejectHello, rejectedHellos;
        public string PipeName { get; } = "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N");
        public int RejectedHellos => Volatile.Read(ref rejectedHellos);
        public async Task StartAsync()
        {
            if (!DeviceWorkspaceOptions.DemoPipe(upstreamPipe)) throw new InvalidOperationException("Only a DEMO upstream is permitted");
            serving = ServeAsync(); await listening.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        public ConnectionPause PauseNext(DeviceOperation operation)
        { lock (gate) { if (pause is not null) throw new InvalidOperationException("A DEMO pause is already pending"); return pause = new(operation); } }
        public void RejectNextHello() => Interlocked.Exchange(ref rejectHello, 1);
        public void FailNextReply(DeviceOperation operation) { lock (gate) failedReply = operation; }
        public async Task DropAsync()
        {
            Task ended;
            lock (gate) { active?.Cancel(); ended = activeEnded?.Task ?? Task.CompletedTask; }
            await ended.WaitAsync(TimeSpan.FromSeconds(5));
        }
        private async Task ServeAsync()
        {
            using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User?.Value ?? throw new IOException("DEMO user identity is unavailable");
            while (!stop.IsCancellationRequested)
            {
                using var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance | PipeOptions.WriteThrough, 8192, 8192, CoolingServicePipeServer.Security([sid]));
                listening.TrySetResult();
                using var session = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                DeviceServicePipeClient? upstream = null;
                try
                {
                    await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                    lock (gate) { active = session; activeEnded = ended; }
                    string? peer = null; pipe.RunAsClient(() => { using var user = WindowsIdentity.GetCurrent(true); peer = user?.User?.Value; });
                    if (peer != sid) throw new UnauthorizedAccessException("Only the current DEMO user may connect");
                    if (Interlocked.Exchange(ref rejectHello, 0) != 0) { Interlocked.Increment(ref rejectedHellos); continue; }
                    upstream = DeviceServicePipeClient.ForDemo(upstreamPipe, Environment.ProcessId);
                    await upstream.ConnectAsync(renew: false, ct: session.Token).ConfigureAwait(false);
                    await CoolingPipeFrames.WriteAsync(pipe, upstream.Hello!, session.Token).ConfigureAwait(false);
                    while (!session.IsCancellationRequested)
                    {
                        var request = DeviceServiceProtocol.Read(await CoolingPipeFrames.ReadAsync(pipe, session.Token).ConfigureAwait(false));
                        if (request.Operation is not (DeviceOperation.Renew or DeviceOperation.State or DeviceOperation.ReadStateSnapshot or
                            DeviceOperation.ReadNotifications or DeviceOperation.ReleaseSession or DeviceOperation.Configuration) ||
                            request.Configuration is { Operation: not (DeviceConfigurationOperation.Snapshot or DeviceConfigurationOperation.SnapshotWithCandidates or
                                DeviceConfigurationOperation.ReadSnapshot or DeviceConfigurationOperation.PrepareConnectionLighting) })
                            throw new IOException("The connection scenario cannot issue output or settings changes");
                        ConnectionPause? hold = null; bool fail;
                        lock (gate)
                        {
                            if (pause?.Operation == request.Operation) { hold = pause; pause = null; }
                            fail = failedReply == request.Operation; if (fail) failedReply = null;
                        }
                        if (hold is not null) { hold.Entered.TrySetResult(); await hold.Continue.Task.WaitAsync(TimeSpan.FromSeconds(10), session.Token).ConfigureAwait(false); }
                        var reply = await upstream.SendAsync(request, session.Token).ConfigureAwait(false);
                        if (fail) reply = new(reply.Version, reply.Epoch, reply.Session, reply.Id, false, "DEMO: injected response failure");
                        await CoolingPipeFrames.WriteAsync(pipe, reply, session.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
                finally
                {
                    if (upstream is not null) await upstream.DisposeAsync().ConfigureAwait(false);
                    lock (gate) { if (ReferenceEquals(active, session)) active = null; }
                    ended.TrySetResult();
                }
            }
        }
        public async ValueTask DisposeAsync()
        {
            lock (gate) { pause?.Resume(); pause = null; }
            await stop.CancelAsync();
            try { await serving.WaitAsync(TimeSpan.FromSeconds(8)); }
            finally { stop.Dispose(); }
        }
    }
    private sealed class ConnectionPause(DeviceOperation operation) : IDisposable
    {
        public DeviceOperation Operation { get; } = operation;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Resume() => Continue.TrySetResult();
        public void Dispose() => Resume();
    }
}
