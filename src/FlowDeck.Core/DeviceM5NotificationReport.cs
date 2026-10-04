namespace FlowDeck.Core;

// A GUI-owned link observation, not authority to discover or control hardware.
// Null ConnectionId explicitly ends monitoring (disconnect, pairing, settings change).
public sealed record DeviceM5NotificationReport(string? ConnectionId = null, bool Connected = false)
{
    public const string Feature = "m5-notifications-v2", SignalKey = "m5-offline";
    public const int FreshSeconds = 3;
    public static bool Valid(DeviceM5NotificationReport? value) => value is not null &&
        (value.ConnectionId is null ? !value.Connected : DeviceServiceProtocol.Id(value.ConnectionId));
}
