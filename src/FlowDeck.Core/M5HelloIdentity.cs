using System.Text.Json;

namespace FlowDeck.Core;

public sealed record M5HelloIdentity(string DeviceId, string BootId, string Firmware)
{
    public static bool TryRead(JsonElement root, string? expectedDeviceId, out M5HelloIdentity? identity)
    {
        identity = null;
        try
        {
            if (root.GetProperty("v").GetInt32() != 1 || root.GetProperty("type").GetString() != "hello" || root.GetProperty("deviceType").GetString() != "M5GO") return false;
            var device = root.GetProperty("deviceId").GetString(); var boot = root.GetProperty("bootId").GetString(); var firmware = root.GetProperty("firmwareVersion").GetString();
            static bool Text(string? value) => value is { Length: > 0 and <= 64 } && !string.IsNullOrWhiteSpace(value);
            if (!Text(device) || !Text(boot) || !Text(firmware) || expectedDeviceId is not null && device != expectedDeviceId) return false;
            identity = new(device!, boot!, firmware!); return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { return false; }
    }
}
