using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>Memory-only firmware peer for the v2 development host. No COM, sockets or pairing files.</summary>
public sealed class DemoM5LinkFactory : IDeviceM5LinkFactory
{
    public bool Simulated => true;
    public bool UsbPresent { get; set; } = true;
    public bool WifiPresent { get; set; } = true;
    public bool Paired { get; set; }
    public bool FailStart { get; set; }
    public string DeviceId { get; set; } = "demo-panel";
    public M5LanAddress[] Lans { get; set; } = [new("demo-lan", "192.168.1.19", "255.255.255.0", "DEMO LAN")];
    public List<DemoM5Link> Links { get; } = [];
    public int PairCalls { get; private set; }
    public Func<CancellationToken, Task>? BeforePair { get; set; }
    public Func<Task>? BeforeDispose { get; set; }
    public M5LanAddress[] Addresses() => Lans;
    public IDeviceM5Link Create(DeviceM5Control control, M5Settings settings, IM5LightingBackend? lighting)
    {
        if (!control.Snapshot.Demo) throw new InvalidOperationException("模擬M5GOにはデモサービスを使用してください");
        var link = new DemoM5Link(this, control, settings, lighting); Links.Add(link); return link;
    }
    public sealed class DemoM5Link : IDeviceM5Link
    {
        private readonly DemoM5LinkFactory factory;
        private readonly M5Settings settings;
        private readonly string boot = Guid.NewGuid().ToString("N");
        private JsonElement? state;
        public DeviceM5Control Control { get; }
        public IM5LightingBackend? Lighting { get; }
        public M5TransportRouter Router { get; }
        public ConcurrentQueue<string> Frames { get; } = new();
        public bool Closed { get; private set; }
        public bool Disposed { get; private set; }
        public int Starts { get; private set; }
        public int DisposeCalls { get; private set; }
        internal DemoM5Link(DemoM5LinkFactory factory, DeviceM5Control control, M5Settings settings, IM5LightingBackend? lighting)
        {
            this.factory = factory; this.settings = settings; Control = control; Lighting = lighting;
            Router = new(control, settings, motherboard: control.ReadMotherboard, lighting: lighting);
            Router.Outgoing += (_, frame) => { Frames.Enqueue(frame); using var json = JsonDocument.Parse(frame); if (json.RootElement.GetProperty("type").GetString() == "state") state = json.RootElement.Clone(); };
        }
        public void Start() { if (Closed) throw new ObjectDisposedException(nameof(DemoM5Link)); Starts++; if (factory.FailStart) throw new IOException("demo start failure"); Pulse(); }
        private bool Usb => !Closed && factory.UsbPresent && (settings.DeviceId is null || settings.DeviceId == factory.DeviceId);
        public DeviceM5LinkState State
        {
            get
            {
                Pulse();
                return new(Router.Protocol.Connected, Router.Protocol.Synchronized, Router.Protocol.Connected ? Router.ActiveRoute == "usb" ? "USB" : "Wi-Fi" : "未接続",
                    Usb, Usb ? factory.DeviceId : null, Usb || Router.Protocol.Connected ? "1.3.0" : null,
                    factory.Paired, !Closed && settings.Transport != "usb" && factory.Paired,
                    !Closed && settings.Transport != "usb" && factory.Paired && factory.WifiPresent,
                    "192.168.1.33", -48);
            }
        }
        public void Pulse()
        {
            if (Closed || Starts == 0) return;
            var route = Usb && settings.Transport != "wifi" ? "usb" : factory.Paired && factory.WifiPresent && settings.Transport != "usb" ? "wifi" : null;
            if (Router.ActiveRoute is { } old && old != route) Router.Disconnect(old);
            if (route is null) return;
            if (Router.ActiveRoute != route || !Router.Protocol.Connected)
                Router.Receive(route, JsonSerializer.Serialize(new { v = 1, type = "hello", deviceType = "M5GO", deviceId = factory.DeviceId,
                    bootId = boot, firmwareVersion = "1.3.0", capabilities = new[] { "motherboard-v1", "lighting-v1" } }));
            else Router.Receive(route, JsonSerializer.Serialize(new { v = 1, type = "heartbeat", sessionId = Router.Protocol.SessionId, controllerEpoch = Control.Epoch }));
            Router.Tick();
            if (state is { } current && Router.Protocol.SessionId is { } session)
                Router.Receive(route, JsonSerializer.Serialize(new { v = 1, type = "stateApplied", sessionId = session, controllerEpoch = Control.Epoch,
                    snapshotSequence = current.GetProperty("snapshotSequence").GetInt64(), controlRevision = current.GetProperty("controlRevision").GetInt64(),
                    lightingRevision = current.GetProperty("lightingRevision").GetInt64() }));
        }
        public void StopAccepting() { Closed = true; Router.Close(); }
        public async Task PairAsync(string ssid, string password, M5LanAddress address, string mode, CancellationToken ct)
        {
            if (!Usb || settings.DeviceId != factory.DeviceId) throw new IOException("登録済みM5GOをUSBで接続してください");
            if (Encoding.UTF8.GetByteCount(ssid) is < 1 or > 32 || Encoding.UTF8.GetByteCount(password) is < 8 or > 63) throw new IOException("Wi-Fi登録の入力を確認してください");
            factory.PairCalls++; if (factory.BeforePair is { } before) await before(ct);
            ct.ThrowIfCancellationRequested(); if (!Usb) throw new IOException("USB接続が切れました"); factory.Paired = true;
        }
        public async ValueTask DisposeAsync()
        {
            StopAccepting(); DisposeCalls++;
            if (factory.BeforeDispose is { } before) await before();
            Disposed = true;
        }
    }
}
