using System.Text.Json;

namespace FlowDeck.Core;

/// <summary>One protocol authority for both links. USB can preempt Wi-Fi, never the reverse while live.</summary>
public sealed class M5TransportRouter
{
    private readonly object gate = new();
    private readonly string? expectedDevice;
    private volatile bool closed;
    public M5ProtocolSession Protocol { get; }
    public string Mode { get; }
    public string? ActiveRoute { get; private set; }
    public event Action<string, string>? Outgoing;
    public M5TransportRouter(IM5ControlBackend core, M5Settings settings, TimeProvider? clock = null, Func<BoardPanel>? motherboard = null, IM5LightingBackend? lighting = null)
    {
        if (settings.Transport is not ("usb" or "wifi" or "auto")) throw new ArgumentException("接続方式が不正です");
        Mode = settings.Transport; expectedDevice = settings.DeviceId;
        Protocol = new(core, settings.DeviceId, settings.Layout, clock, motherboard, lighting);
        // Never take gate here: protocol callbacks can run while holding the protocol lock.
        Protocol.Outgoing += frame => { if (!closed && ActiveRoute is { } route) Outgoing?.Invoke(route, frame); };
    }
    public bool Allows(string route) => !closed && route switch { "usb" => Mode != "wifi", "wifi" => Mode != "usb", _ => false };
    public void Receive(string route, string frame)
    {
        lock (gate)
        {
            if (!Allows(route)) return;
            if (route != ActiveRoute)
            {
                if (ActiveRoute == "usb" && Protocol.Connected) return;
                if (!ValidHello(frame)) return;
                Protocol.Disconnect(); ActiveRoute = route;
            }
            Protocol.Receive(frame);
        }
    }
    private bool ValidHello(string frame)
    {
        try
        {
            if (System.Text.Encoding.UTF8.GetByteCount(frame) + 1 > 4096) return false;
            using var doc = JsonDocument.Parse(frame); var r = doc.RootElement;
            return M5HelloIdentity.TryRead(r, expectedDevice, out _);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { return false; }
    }
    public void Tick()
    {
        lock (gate)
        {
            if (closed) return;
            if (ActiveRoute is not null) Protocol.Tick();
            const string hello = "{\"v\":1,\"type\":\"helloRequest\",\"deviceType\":\"M5GO\"}";
            if (Mode != "wifi" && ActiveRoute != "usb") Outgoing?.Invoke("usb", hello);
            if (Mode != "usb" && ActiveRoute != "wifi") Outgoing?.Invoke("wifi", hello);
        }
    }
    public void Disconnect(string route)
    {
        lock (gate) if (ActiveRoute == route) { Protocol.Disconnect(); ActiveRoute = null; }
    }
    public void Close()
    {
        lock (gate)
        {
            if (closed) return;
            closed = true; ActiveRoute = null; Protocol.Disconnect();
        }
    }
}
