using System.Net;
using System.Net.Sockets;

namespace FlowDeck.Core;

public sealed record DeviceM5ProfileSlot(string Slot, string ProfileId);
public sealed record DeviceM5Selection
{
    // Firmware 1.3 describes this Hub, not an arbitrary generic cooling group.
    public const string HubProvider = "nzxt-control-hub", HubDefinition = "nzxt-control-hub-1e71-2022-v1";
    public PhysicalDeviceBinding? Hub { get; init; }
    public string[] Fans { get; init; } = [];
    public string[] Leds { get; init; } = [];
    public EndpointReference? Cpu { get; init; }
    public EndpointReference? Gpu { get; init; }
    public DeviceM5ProfileSlot[] Profiles { get; init; } = [];
    public DeviceDashboardItem[] Motherboard { get; init; } = [];
    public static IReadOnlyList<string> Slots { get; } = Array.AsReadOnly<string>(["quiet", "balanced", "cool"]);
    public static bool Fan(string? id) => id is "fan1" or "fan2" or "fan3" or "fan4" or "fan5";
    public static bool Led(string? id) => id is "led1" or "led2" or "led3" or "led4" or "led5";
    public bool HasHubControl() => Fans.Length > 0 || Leds.Length > 0 || Profiles.Length > 0;
    public static void Validate(DeviceM5Selection value)
    {
        if (value is null || value.Fans is not { Length: <= 5 } || value.Fans.Any(f => !Fan(f)) ||
            value.Leds is not { Length: <= 5 } || value.Leds.Any(l => !Led(l)) ||
            value.Fans.Distinct().Count() != value.Fans.Length || value.Leds.Distinct().Count() != value.Leds.Length ||
            value.Profiles is not { Length: <= 3 } || value.Profiles.Any(p => p is null || !Slots.Contains(p.Slot) || !DeviceConfigurationRules.ProfileId(p.ProfileId)) ||
            value.Profiles.DistinctBy(p => p.Slot).Count() != value.Profiles.Length || value.Profiles.DistinctBy(p => p.ProfileId).Count() != value.Profiles.Length ||
            value.Cpu is not null && !DeviceConfigurationRules.ValidReference(value.Cpu) || value.Gpu is not null && !DeviceConfigurationRules.ValidReference(value.Gpu) ||
            value.Cpu is not null && value.Cpu == value.Gpu || value.Motherboard is not { Length: <= 8 } || value.Motherboard.Any(i => i is null ||
                !DeviceConfigurationRules.ValidReference(i.Target) || !DeviceConfigurationRules.Text(i.Name, 160) || i.Measurement is not (DeviceFeature.Temperature or DeviceFeature.Rpm) ||
                i.Role is not ("temperature" or "fan" or "pump") || i.Indicator is not null || i.DisplayMaximum is not null) ||
            value.Motherboard.DistinctBy(i => (i.Target, i.Measurement)).Count() != value.Motherboard.Length ||
            value.Hub is null && (value.Fans.Length > 0 || value.Leds.Length > 0 || value.Profiles.Length > 0) || value.Hub is { } h &&
                (!DeviceConfigurationRules.Key(h.DeviceId) || !DeviceConfigurationRules.Text(h.ProviderIdentity, 2048) || h.DefinitionId != HubDefinition ||
                 h.Fingerprint?.Length != 64 || !h.Fingerprint.All(Uri.IsHexDigit)))
            throw new ArgumentException("M5GOの表示・操作先の指定が不正です");
    }
    public DeviceM5Selection WithoutControl() => this with { Hub = null, Fans = [], Leds = [], Profiles = [] };
}
public sealed record DeviceM5Settings(M5Settings Connection, DeviceM5Selection Selection, bool ConnectOnStart = false)
{
    public static string? Problem(DeviceM5Settings? value)
    {
        if (value is not { Connection: { } c, Selection: not null }) return "M5GOの設定がありません";
        try { DeviceM5Selection.Validate(value.Selection); } catch (ArgumentException e) { return e.Message; }
        static bool Optional(string? text, int max) => text is null || DeviceConfigurationRules.Text(text, max);
        if (c.Layout is not ("A" or "B") || c.Transport is not ("usb" or "wifi" or "auto") || c.WifiPort is < 1024 or > 65535 ||
            !Optional(c.DeviceId, 64) || !Optional(c.WifiInterfaceId, 128) ||
            c.Port is not null && (c.Port.Length is < 4 or > 8 || !c.Port.StartsWith("COM", StringComparison.Ordinal) ||
                c.Port[3] == '0' || !c.Port[3..].All(char.IsAsciiDigit)) ||
            c.WifiAddress is not null && (!IPAddress.TryParse(c.WifiAddress, out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
                IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast) || address.GetAddressBytes()[0] is 0 or >= 224))
            return "M5GOの接続設定が不正です";
        if (value.ConnectOnStart && (c.DeviceId is null || c.Transport == "usb" && c.Port is null ||
            c.Transport == "wifi" && c.WifiAddress is null && c.WifiInterfaceId is null ||
            c.Transport == "auto" && c.Port is null && c.WifiAddress is null && c.WifiInterfaceId is null))
            return "自動接続にはM5GOの機器IDと接続先を設定してください";
        return null;
    }
}
public interface IDeviceM5ConfigurationHandler : IDeviceConfigurationHandler { }
