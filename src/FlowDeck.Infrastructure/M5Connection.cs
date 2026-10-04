using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed class M5Connection : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly M5Settings settings;
    private readonly M5WifiIdentityStore identities;
    private readonly IEventLog log;
    private Task? loop;
    private readonly object lifetime = new();
    private readonly M5Shutdown shutdown = new();
    private bool loopDrained, serialClosed, wifiClosed, resourcesClosed;
    private M5LanAddress? bound;
    public M5TransportRouter Router { get; }
    public M5ProtocolSession Protocol => Router.Protocol;
    public M5SerialSession? Serial { get; }
    public M5WifiServer? Wifi { get; private set; }
    public M5WifiIdentity? Identity { get; private set; }
    public string? Error { get; private set; }
    public string? WifiStatus { get; private set; }
    public string? Ssid { get; private set; }
    public string? Ip { get; private set; }
    public int? Rssi { get; private set; }
    public DateTimeOffset? LastStatusAt { get; private set; }
    public string ActiveTransport => Protocol.Connected ? Router.ActiveRoute == "usb" ? "USB" : "Wi-Fi" : "未接続";
    public M5Connection(IM5ControlBackend core, M5Settings settings, string directory, IEventLog log, Func<BoardPanel>? motherboard = null, IM5LightingBackend? lighting = null)
    {
        if (core.Snapshot.Demo) throw new InvalidOperationException("デモでは実機接続を開きません");
        this.settings = settings; this.log = log; identities = new(directory); Router = new(core, settings, motherboard: motherboard, lighting: lighting);
        try { Identity = identities.Load(); } catch (Exception) { Error = "Wi-Fi登録情報を読み込めません。USBで再登録してください"; }
        if (Identity?.DeviceId != settings.DeviceId) Identity = null;
        if (settings.Port is not null) { Serial = new(core, settings, log, Router); Serial.StatusReceived += ReadStatus; }
    }
    public void Start()
    {
        lock (lifetime)
        {
            if (shutdown.Requested) throw new ObjectDisposedException(nameof(M5Connection));
            if (loop is not null) return;
            Serial?.Start(); loop = Task.Run(RunAsync);
        }
    }
    public bool Faulted => loop?.IsFaulted == true;
    private async Task RunAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            if (settings.Transport != "usb" && Identity is not null)
            {
                var addresses = M5Network.Addresses();
                var address = addresses.FirstOrDefault(a => a.InterfaceId == settings.WifiInterfaceId) ?? addresses.FirstOrDefault(a => a.Address == settings.WifiAddress);
                if (address != bound)
                {
                    if (Wifi is not null) { await Wifi.DisposeAsync(); Wifi = null; }
                    bound = address;
                    if (address is not null) { Wifi = new(Router, Identity, address, settings.WifiPort, log); Wifi.StatusReceived += ReadStatus; Wifi.Start(); Error = null; }
                }
                if (address is null) Error = "待受するLANアドレスを選択してください";
            }
            Router.Tick();
            try { await Task.Delay(1000, stop.Token); } catch (OperationCanceledException) { }
        }
    }
    private void ReadStatus(JsonElement r)
    {
        if (!r.TryGetProperty("link", out var link)) return;
        try
        {
            WifiStatus = link.GetProperty("status").GetString(); Ssid = link.GetProperty("ssid").GetString(); Ip = link.GetProperty("ip").GetString();
            Rssi = link.TryGetProperty("rssi", out var signal) && signal.TryGetInt32(out var value) ? value : null;
            LastStatusAt = DateTimeOffset.UtcNow;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { }
    }
    public async Task PairAsync(string ssid, string password, M5LanAddress address, string mode, CancellationToken ct = default)
    {
        if (Serial?.UsbDeviceId is not { } device) throw new InvalidOperationException("USBでM5GOを接続してください");
        if (Serial.UsbFirmware is not { } version || !Version.TryParse(version, out var parsed) || parsed < new Version(1, 1, 0))
            throw new InvalidOperationException("Wi-Fi対応ファームウェア1.1.0以降を書き込んでください");
        if (System.Text.Encoding.UTF8.GetByteCount(ssid) is < 1 or > 32 || System.Text.Encoding.UTF8.GetByteCount(password) is < 8 or > 63)
            throw new InvalidOperationException("SSIDは1〜32バイト、Wi-Fiパスワードは8〜63バイトで入力してください");
        if (mode is not ("usb" or "wifi" or "auto")) throw new ArgumentException("接続方式が不正です");
        var identity = Identity ?? M5WifiIdentity.Create(device);
        using var cert = identity.Certificate();
        // Save the PC half first so an interrupted USB acknowledgement cannot lose its key.
        identities.Save(identity); Identity = identity;
        var response = await Serial.ConfigureWifiAsync(new { ssid, password, serverId = identity.ServerId, serverName = identity.ServerName,
            token = identity.Token, ca = cert.ExportCertificatePem(), host = address.Address, port = settings.WifiPort, mode }, ct);
        if (!response.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException(response.TryGetProperty("error", out var error) && error.GetString() == "INVALID_WIFI_CERTIFICATE"
                ? "USBで受信した証明書が不正です。ファームを1.1.2以降へ更新し、Wi-Fi登録を再試行してください"
                : "M5GOがWi-Fi設定を保存できませんでした");
    }
    public ValueTask DisposeAsync()
    {
        lock (lifetime) return new(shutdown.Run(ShutdownAsync));
    }
    private async Task ShutdownAsync()
    {
        if (resourcesClosed) return;
        // Keep failed owners, but still close the independent transport. Never reopen on retry.
        Router.Close(); stop.Cancel();
        var errors = new List<Exception>();
        if (!loopDrained)
        {
            try { if (loop is not null) await loop.ConfigureAwait(false); }
            catch (Exception e) { errors.Add(e); }
            finally { loopDrained = true; } // A faulted worker has ended; report its error once.
        }
        if (!serialClosed)
        {
            try { if (Serial is not null) await Serial.DisposeAsync().ConfigureAwait(false); serialClosed = true; }
            catch (Exception e) { errors.Add(e); }
        }
        if (!wifiClosed)
        {
            try { if (Wifi is not null) await Wifi.DisposeAsync().ConfigureAwait(false); wifiClosed = true; }
            catch (Exception e) { errors.Add(e); }
        }
        if (serialClosed && wifiClosed) { stop.Dispose(); resourcesClosed = true; }
        if (errors.Count != 0) throw new AggregateException("M5GOの接続終了を確認してください", errors);
    }
}
