using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceM5LinkState(bool Connected = false, bool Synchronized = false, string Route = "未接続",
    bool UsbOpen = false, string? UsbDeviceId = null, string? Firmware = null,
    bool WifiPaired = false, bool WifiListening = false, bool WifiAuthenticated = false,
    string? WifiIp = null, int? Rssi = null, string? Error = null);
public interface IDeviceM5Link : IAsyncDisposable
{
    DeviceM5LinkState State { get; }
    void Start();
    void StopAccepting();
    Task PairAsync(string ssid, string password, M5LanAddress address, string mode, CancellationToken ct);
}
public interface IDeviceM5LinkFactory
{
    bool Simulated { get; }
    M5LanAddress[] Addresses();
    IDeviceM5Link Create(DeviceM5Control control, M5Settings settings, IM5LightingBackend? lighting);
}
public sealed class DeviceM5LinkFactory(string userDirectory, IEventLog log) : IDeviceM5LinkFactory
{
    // Fresh v2 pairing store. Never read the old user-root m5-wifi.protected file.
    public string IdentityDirectory { get; } = Path.Combine(Path.GetFullPath(userDirectory), "m5-v2");
    public bool Simulated => false;
    public M5LanAddress[] Addresses() => M5Network.Addresses();
    public IDeviceM5Link Create(DeviceM5Control control, M5Settings settings, IM5LightingBackend? lighting) =>
        new NativeLink(new(control, settings, IdentityDirectory, log, control.ReadMotherboard, lighting));
    private sealed class NativeLink(M5Connection connection) : IDeviceM5Link
    {
        public DeviceM5LinkState State => new(connection.Protocol.Connected, connection.Protocol.Synchronized, connection.ActiveTransport,
            connection.Serial?.IsOpen == true, connection.Serial?.UsbDeviceId, connection.Serial?.UsbFirmware ?? connection.Protocol.FirmwareVersion,
            connection.Identity is not null, connection.Wifi?.Listening == true, connection.Wifi?.Authenticated == true,
            connection.Ip, connection.Rssi, connection.Faulted ? "M5GO接続を再開してください" : connection.Error ?? connection.Wifi?.Error ?? connection.Serial?.Error);
        public void Start() => connection.Start();
        public void StopAccepting() => connection.Router.Close();
        public Task PairAsync(string ssid, string password, M5LanAddress address, string mode, CancellationToken ct) => connection.PairAsync(ssid, password, address, mode, ct);
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
public sealed record DeviceM5SessionState(bool Running, bool Wanted, bool NeedsRegistration, bool Busy,
    DeviceM5LinkState Link, string? Message = null, string? NotificationError = null)
{
    public bool CanRegister => Running && NeedsRegistration && Link.Connected && Link.Route == "USB" && Link.UsbOpen && !string.IsNullOrWhiteSpace(Link.UsbDeviceId) && !Busy;
    public bool CanPair => Running && !NeedsRegistration && Link.UsbOpen && !string.IsNullOrWhiteSpace(Link.UsbDeviceId) && !Busy;
}

/// <summary>Owns only the M5 link. Disconnect never releases PC cooling or lighting ownership.</summary>
public sealed class DeviceM5Session : IAsyncDisposable
{
    private readonly DeviceWorkspaceClient client;
    private readonly IDeviceM5LinkFactory factory;
    private readonly SemaphoreSlim actions = new(1, 1);
    private readonly object lifecycle = new();
    private IDeviceM5Link? link;
    private DeviceM5Settings? activeSettings;
    private string? requestedHash, activeSession, notificationId, notificationError;
    private bool wanted, startupAttempted;
    private int disposed;
    private readonly M5Shutdown shutdown = new();
    private CancellationTokenSource? pairing;
    public DeviceM5SessionState State { get; private set; } = new(false, false, false, false, new());
    public DeviceM5Session(DeviceWorkspaceClient client, IDeviceM5LinkFactory factory)
    {
        if (client.IsDemo && !factory.Simulated) throw new InvalidOperationException("デモではM5GOの実接続を開きません");
        this.client = client; this.factory = factory;
    }
    private static string Hash(DeviceM5Settings value) => SettingsTransfer.Hash(new { value.Connection, value.Selection });
    private static bool Fresh(DeviceWorkspaceSnapshot? s) => s is { Connected: true, ExitAttempted: false } && double.IsFinite(s.ElapsedSeconds) &&
        s.ElapsedSeconds is >= 0 and < 3 && !s.State.Releasing && !s.State.ConfigurationChanging;
    private DeviceWorkspaceSnapshot Current()
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (!Fresh(client.Snapshot)) throw new IOException("サービスの状態を更新してください");
        return client.Snapshot!;
    }
    private void Publish(string? message = null, bool busy = false)
    {
        State = new(link is not null, wanted, link is not null && activeSettings?.Connection.DeviceId is null, busy, link?.State ?? new(), message, notificationError);
    }
    private async Task ReportNotificationsAsync(bool disarm = false)
    {
        if (activeSession is not { } owner) return;
        try
        {
            // Notification reporting must not prevent closing the link or operating PC cooling.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.ReportM5ConnectionAsync(owner, () => !disarm && disposed == 0 && wanted && !State.Busy &&
                Fresh(client.Snapshot) && activeSettings?.Connection.DeviceId is not null && notificationId is not null && link is not null ?
                new(notificationId, link.State.Connected) : new(), timeout.Token).ConfigureAwait(false);
            notificationError = null;
        }
        catch (Exception) { notificationError = "M5GOの接続通知を更新できません"; }
        State = State with { NotificationError = notificationError };
    }
    public async Task StartupAsync(CancellationToken ct = default)
    {
        await actions.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (startupAttempted || disposed != 0) return;
            startupAttempted = true;
            var s = client.Snapshot;
            if (s is not null && DeviceM5Configuration.Read(s.Configuration.Configuration.Preferences) is { ConnectOnStart: true } saved)
            { wanted = true; requestedHash = Hash(saved); await ReconcileAsync().ConfigureAwait(false); }
        }
        catch (Exception e) { wanted = false; Publish(e.Message); }
        finally { await ReportNotificationsAsync().ConfigureAwait(false); actions.Release(); }
    }
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await actions.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var s = Current(); var settings = DeviceM5Configuration.Read(s.Configuration.Configuration.Preferences);
            if (DeviceM5Settings.Problem(settings) is { } problem) throw new IOException(problem);
            if (settings.Connection.DeviceId is null && settings.Connection.Port is null || settings.Connection.Transport == "usb" && settings.Connection.Port is null)
                throw new IOException("USBポートを保存してください");
            if (settings.Connection.DeviceId is not null && settings.Connection.Transport == "wifi" && settings.Connection.WifiAddress is null && settings.Connection.WifiInterfaceId is null)
                throw new IOException("PCのLANアドレスを保存してください");
            if (settings.Connection.Port is null && settings.Connection.WifiAddress is null && settings.Connection.WifiInterfaceId is null)
                throw new IOException("接続先を保存してください");
            await StopAsync().ConfigureAwait(false); wanted = true; requestedHash = Hash(settings);
            await ReconcileAsync().ConfigureAwait(false);
        }
        catch (Exception e) { wanted = false; Publish(e.Message); throw; }
        finally { await ReportNotificationsAsync().ConfigureAwait(false); actions.Release(); }
    }
    public async Task PollAsync(CancellationToken ct = default)
    {
        if (disposed != 0 || !await actions.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try { await ReconcileAsync().ConfigureAwait(false); }
        catch (Exception e) { wanted = false; Publish(e.Message); }
        finally { await ReportNotificationsAsync().ConfigureAwait(false); actions.Release(); }
    }
    private async Task ReconcileAsync()
    {
        if (disposed != 0) return;
        var s = client.Snapshot;
        if (!Fresh(s)) { await StopAsync().ConfigureAwait(false); Publish(wanted ? "サービスの接続待ち" : null); return; }
        var settings = DeviceM5Configuration.Read(s!.Configuration.Configuration.Preferences);
        if (wanted && Hash(settings) != requestedHash)
        { wanted = false; requestedHash = null; await StopAsync().ConfigureAwait(false); Publish("設定を変更しました。接続してください"); return; }
        if (!wanted) { Publish(State.Message); return; }
        if (DeviceM5Settings.Problem(settings) is { } problem) { wanted = false; await StopAsync().ConfigureAwait(false); Publish(problem); return; }
        var session = s.State.Inventory.Epoch + "/" + s.Session;
        if (link is not null && activeSession != session) await StopAsync().ConfigureAwait(false);
        if (link is null)
        {
            // Discovery may display telemetry, but neither the old fan commands nor lighting
            // commands can acquire authority until the USB-observed device ID is saved.
            var registered = settings.Connection.DeviceId is not null;
            var control = new DeviceM5Control(client, registered ? settings.Selection : settings.Selection.WithoutControl());
            var connection = registered ? settings.Connection : settings.Connection with { Transport = "usb" };
            link = factory.Create(control, connection, registered ? new DeviceM5Lighting(client) : null);
            activeSettings = settings; activeSession = session; notificationId = Guid.NewGuid().ToString("N");
            try { link.Start(); }
            catch { await StopAsync().ConfigureAwait(false); throw; }
        }
        Publish();
    }
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        lock (lifecycle) pairing?.Cancel();
        await actions.WaitAsync(ct).ConfigureAwait(false);
        try { wanted = false; requestedHash = null; await StopAsync().ConfigureAwait(false); Publish("切断しました"); }
        catch (Exception e) { Publish(e.Message); throw; }
        finally { actions.Release(); }
    }
    private async Task StopAsync()
    {
        if (link is not { } current) return;
        current.StopAccepting();
        await ReportNotificationsAsync(disarm: true).ConfigureAwait(false); notificationId = null;
        await current.DisposeAsync().ConfigureAwait(false);
        link = null; activeSettings = null; activeSession = null;
    }
    public async Task RegisterAsync(CancellationToken ct = default)
    {
        await actions.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var s = Current(); var settings = DeviceM5Configuration.Read(s.Configuration.Configuration.Preferences);
            if (link?.State is not { UsbOpen: true, UsbDeviceId: { } device } || !State.CanRegister || Hash(settings) != requestedHash ||
                !DeviceConfigurationRules.Text(device, 64)) throw new IOException("保存したUSBポートでM5GOを接続してください");
            Publish(busy: true);
            var saved = settings with { Connection = settings.Connection with { DeviceId = device }, ConnectOnStart = false };
            await client.SaveM5Async(new(s.State.Inventory.Epoch, s.Session!, s.Configuration.Generation, s.State.Inventory.TopologyRevision, saved), ct).ConfigureAwait(false);
            await StopAsync().ConfigureAwait(false); wanted = true; requestedHash = Hash(saved); await ReconcileAsync().ConfigureAwait(false); Publish("M5GOを登録しました");
        }
        catch (Exception e) { Publish(e.Message); throw; }
        finally { await ReportNotificationsAsync().ConfigureAwait(false); actions.Release(); }
    }
    public async Task PairAsync(string ssid, string password, CancellationToken ct = default)
    {
        await actions.WaitAsync(ct).ConfigureAwait(false);
        CancellationTokenSource? operation = null;
        try
        {
            var s = Current(); var settings = DeviceM5Configuration.Read(s.Configuration.Configuration.Preferences);
            if (link is null || !State.CanPair || link.State.UsbDeviceId != settings.Connection.DeviceId || Hash(settings) != requestedHash)
                throw new IOException("登録済みM5GOをUSBで接続してください");
            var addresses = factory.Addresses();
            var address = addresses.FirstOrDefault(a => a.InterfaceId == settings.Connection.WifiInterfaceId) ??
                addresses.FirstOrDefault(a => a.Address == settings.Connection.WifiAddress) ?? throw new IOException("現在のPCのLANアドレスを保存してください");
            operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (lifecycle) { ObjectDisposedException.ThrowIf(disposed != 0, this); pairing = operation; }
            Publish(busy: true);
            await ReportNotificationsAsync(disarm: true).ConfigureAwait(false);
            await link.PairAsync(ssid, password, address, settings.Connection.Transport, operation.Token).ConfigureAwait(false);
            await StopAsync().ConfigureAwait(false); await ReconcileAsync().ConfigureAwait(false); Publish("Wi-Fiを登録しました");
        }
        catch (Exception e) { Publish(e is OperationCanceledException ? "Wi-Fi登録を中断しました。登録結果を確認してください" : e.Message); throw; }
        finally { lock (lifecycle) pairing = null; operation?.Dispose(); await ReportNotificationsAsync().ConfigureAwait(false); actions.Release(); }
    }
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        return new(shutdown.Run(ShutdownAsync));
    }
    private async Task ShutdownAsync()
    {
        lock (lifecycle) pairing?.Cancel();
        await actions.WaitAsync().ConfigureAwait(false);
        try { wanted = false; requestedHash = null; await StopAsync().ConfigureAwait(false); Publish("切断しました"); }
        catch (Exception e) { Publish(e.Message); throw; }
        finally { actions.Release(); }
    }
}
