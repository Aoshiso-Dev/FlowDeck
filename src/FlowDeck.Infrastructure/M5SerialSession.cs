using System.IO.Ports;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed class M5SerialSession : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly M5TransportRouter router;
    private readonly bool ownsRouter;
    private readonly M5Settings settings;
    private readonly string portName;
    private readonly IEventLog log;
    private readonly Channel<string> writes = Channel.CreateBounded<string>(64);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> requests = new();
    private Task? loop;
    private readonly object lifecycle = new();
    private readonly M5Shutdown shutdown = new();
    private bool resourcesClosed;
    public string? Error { get; private set; }
    public string? UsbDeviceId { get; private set; }
    public string? UsbFirmware { get; private set; }
    public bool IsOpen { get; private set; }
    public event Action<JsonElement>? StatusReceived;
    public M5ProtocolSession Protocol => router.Protocol;
    public M5SerialSession(IM5ControlBackend coordinator, M5Settings settings, IEventLog log, M5TransportRouter? sharedRouter = null)
    {
        if (coordinator.Snapshot.Demo) throw new InvalidOperationException("デモではCOMポートを開けません");
        portName = settings.Port ?? throw new ArgumentException("COMポートを選択してください");
        this.settings = settings; this.log = log; ownsRouter = sharedRouter is null;
        router = sharedRouter ?? new(coordinator, settings);
        router.Outgoing += OnOutgoing;
    }
    private void OnOutgoing(string route, string value) { if (route == "usb") writes.Writer.TryWrite(value); }
    public static string[] Ports => SerialPort.GetPortNames().Order().ToArray();
    public void Start()
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(shutdown.Requested, this);
            loop ??= Task.Run(RunAsync);
        }
    }
    public async Task<JsonElement> ConfigureWifiAsync(object payload, CancellationToken ct = default)
    {
        if (!IsOpen || UsbDeviceId is null) throw new InvalidOperationException("USBでM5GOを接続して機器IDを確認してください");
        var id = Guid.NewGuid().ToString("N");
        var frame = JsonSerializer.Serialize(new { v = 1, type = "configureWifi", id, deviceId = UsbDeviceId, payload }, Contract.Json);
        if (Encoding.UTF8.GetByteCount(frame) + 1 > 4096) throw new InvalidOperationException("Wi-Fi設定が通信上限を超えています");
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously); requests[id] = response;
        try { await writes.Writer.WriteAsync(frame, ct); return await response.Task.WaitAsync(TimeSpan.FromSeconds(15), ct); }
        finally { requests.TryRemove(id, out _); }
    }
    private void Receive(string frame)
    {
        try
        {
            using var doc = JsonDocument.Parse(frame); var r = doc.RootElement;
            if (r.GetProperty("v").GetInt32() == 1 && r.TryGetProperty("deviceId", out var id) &&
                id.GetString() is { Length: > 0 and <= 64 } deviceId && (settings.DeviceId is null || deviceId == settings.DeviceId))
            {
                switch (r.GetProperty("type").GetString())
                {
                    case "hello":
                        if (!M5HelloIdentity.TryRead(r, settings.DeviceId, out var hello)) return;
                        UsbDeviceId = hello!.DeviceId; UsbFirmware = hello.Firmware; StatusReceived?.Invoke(r.Clone()); break;
                    case "wifiStatus": StatusReceived?.Invoke(r.Clone()); return;
                    case "wifiConfigured":
                        if (requests.TryGetValue(r.GetProperty("id").GetString() ?? "", out var pending)) pending.TrySetResult(r.Clone());
                        return;
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { }
        router.Receive("usb", frame);
    }
    private async Task RunAsync()
    {
        var attempt = 0; string? lastLoggedError = null;
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                using var port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
                { Handshake = Handshake.None, DtrEnable = false, RtsEnable = false, ReadTimeout = 100, WriteTimeout = 500, Encoding = new UTF8Encoding(false), NewLine = "\n" };
                port.Open(); IsOpen = true; Error = null; lastLoggedError = null;
                while (writes.Reader.TryRead(out _)) { }
                router.Disconnect("usb");
                port.WriteLine("{\"v\":1,\"type\":\"identify\"}");
                var buffer = new byte[1024]; var framer = new JsonLineFramer(4096); var lastTick = DateTime.MinValue;
                while (!lifetime.IsCancellationRequested)
                {
                    if (DateTime.UtcNow - lastTick >= TimeSpan.FromSeconds(1))
                    { if (ownsRouter) router.Tick(); lastTick = DateTime.UtcNow; if (UsbDeviceId is not null) attempt = 0; }
                    var budget = 16;
                    while (budget-- > 0 && writes.Reader.TryRead(out var frame)) port.WriteLine(frame);
                    try { var count = port.Read(buffer, 0, buffer.Length); foreach (var line in framer.Feed(buffer, count)) Receive(line); }
                    catch (TimeoutException) { }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
            {
                // In auto / Wi-Fi mode, an unplugged USB cable is an ordinary operating state.
                var present = Ports.Contains(portName, StringComparer.OrdinalIgnoreCase);
                Error = present ? e.Message : settings.Transport == "usb" ? "USB未接続（" + portName + "）" : null;
                if (Error is not null && Error != lastLoggedError) log.Write("m5serial", new { portName, error = Error });
                lastLoggedError = Error;
            }
            finally
            {
                IsOpen = false; UsbDeviceId = UsbFirmware = null; router.Disconnect("usb");
                foreach (var pending in requests.Values) pending.TrySetException(new IOException("USB接続が切れました。登録結果を確認してください"));
            }
            try { await Task.Delay(TimeSpan.FromSeconds(new[] { 1, 2, 5 }[Math.Min(attempt++, 2)]), lifetime.Token); }
            catch (OperationCanceledException) { }
        }
    }
    public ValueTask DisposeAsync()
    { lock (lifecycle) return new(shutdown.Run(ShutdownAsync)); }
    private async Task ShutdownAsync()
    {
        if (resourcesClosed) return;
        if (ownsRouter) router.Close();
        lifetime.Cancel(); writes.Writer.TryComplete();
        foreach (var pending in requests.Values) pending.TrySetException(new IOException("USB接続を終了しました。登録結果を確認してください"));
        try { if (loop is not null) await loop.ConfigureAwait(false); }
        finally
        {
            // Even a failed worker must detach ingress and release its cancellation source.
            router.Outgoing -= OnOutgoing; lifetime.Dispose(); resourcesClosed = true;
        }
    }
}
