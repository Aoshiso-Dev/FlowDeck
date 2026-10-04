using System.Text.Json;
using System.Text.Json.Serialization;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed record LightingDeviceMatch(int? VendorId = null, int? ProductId = null, string? Firmware = null, string? ConfigurationHex = null);
internal sealed record LightingProtocolDefinition(string NativeColors, string FrameColors, string LedCount, int? FixedLeds,
    int MaximumZones, int MaximumLedsPerZone, string Restore);
internal sealed record LightingDriverDefinition(string Id, int Revision, string Driver, int DriverRevision, string Name,
    LightingDeviceMatch Match, LightingProtocolDefinition Protocol, DeviceLightingLimits Limits,
    IReadOnlyList<string> ResourceDomains, IReadOnlyList<string> PhysicalResources, IReadOnlyList<byte> NativeModes,
    IReadOnlyList<string> Verification, IReadOnlyList<string> Limitations)
{
    [JsonIgnore] public string Digest => SettingsTransfer.Hash(this);
}

// Only embedded definitions are adopted. The parser is an internal validation seam, not a file loader,
// plugin mechanism, or permission to execute addresses/registers supplied by a client.
internal sealed class LightingDeviceDefinitionCatalog
{
    private sealed record Document(int Version, LightingDriverDefinition[] Devices);
    public const string AsrockId = "asrock-usb-26ce-01a2-v1", EneId = "ene-auda0-e6k5-0101-eight-led-v1";
    public IReadOnlyList<LightingDriverDefinition> Devices { get; }
    private LightingDeviceDefinitionCatalog(IEnumerable<LightingDriverDefinition> definitions) => Devices = Array.AsReadOnly(definitions.Select(d => d with {
        ResourceDomains = Array.AsReadOnly(d.ResourceDomains.ToArray()), PhysicalResources = Array.AsReadOnly(d.PhysicalResources.ToArray()),
        NativeModes = Array.AsReadOnly(d.NativeModes.ToArray()), Verification = Array.AsReadOnly(d.Verification.ToArray()),
        Limitations = Array.AsReadOnly(d.Limitations.ToArray()) }).ToArray());
    public static LightingDeviceDefinitionCatalog BuiltIn { get; } = LoadBuiltIn();
    private static LightingDeviceDefinitionCatalog LoadBuiltIn()
    {
        using var stream = typeof(LightingDeviceDefinitionCatalog).Assembly.GetManifestResourceStream("FlowDeck.DeviceDefinitions.Lighting.json")
            ?? throw new IOException("照明対応定義が見つかりません");
        using var reader = new StreamReader(stream); return Parse(reader.ReadToEnd());
    }
    public LightingDriverDefinition Get(string id, string driver) => Devices.SingleOrDefault(d => d.Id == id && d.Driver == driver)
        ?? throw new IOException("ドライバーに一致する照明対応定義が見つかりません");
    public LightingDriverDefinition? ResolveUsb(int vendor, int product) => Devices.SingleOrDefault(d =>
        d.Driver == "asrock-polychrome-usb" && d.Match.VendorId == vendor && d.Match.ProductId == product);
    public LightingDriverDefinition? ResolveEne(EneMemoryIdentification identity) => Devices.SingleOrDefault(d =>
        d.Driver == "ene-dram-rgb" && d.Match.Firmware == identity.Firmware && d.Match.ConfigurationHex == identity.ConfigurationHex);
    internal static LightingDeviceDefinitionCatalog Parse(string json)
    {
        if (json.Length > 256 * 1024) throw new IOException("照明対応定義が大きすぎます");
        try
        {
            using var document = JsonDocument.Parse(json); SettingsTransfer.NoDuplicates(document.RootElement);
            var value = document.RootElement.Deserialize<Document>(Contract.Json);
            if (value is not { Version: 1, Devices.Length: > 0 and <= 64 } || value.Devices.Any(d => !Valid(d)) ||
                value.Devices.DistinctBy(d => d.Id).Count() != value.Devices.Length ||
                value.Devices.DistinctBy(d => d.Match).Count() != value.Devices.Length)
                throw new IOException("照明対応定義の内容または照合条件が不正・重複しています");
            return new(value.Devices);
        }
        catch (JsonException e) { throw new IOException("照明対応定義の形式が不正です", e); }
    }
    private static bool Texts(IReadOnlyList<string>? values, int maximum, int length) => values is { Count: > 0 } && values.Count <= maximum &&
        values.All(v => DeviceConfigurationRules.Text(v, length)) && values.Distinct(StringComparer.Ordinal).Count() == values.Count;
    private static bool Valid(LightingDriverDefinition? d)
    {
        if (d is null || !DeviceConfigurationRules.Key(d.Id) || d.Revision < 1 || d.DriverRevision != 1 ||
            !DeviceConfigurationRules.Text(d.Name, 160) || d.Match is null || d.Protocol is null ||
            d.Limits is not { MinimumIntervalMilliseconds: >= 100 and <= 500, MaximumFrameAgeMilliseconds: <= 500,
                MaximumLeds: > 0, EndpointColors: true, ConcurrentEndpointStreams: false } ||
            d.Limits.MaximumFrameAgeMilliseconds < d.Limits.MinimumIntervalMilliseconds ||
            !Texts(d.ResourceDomains, 8, 128) || !Texts(d.PhysicalResources, 8, 128) || !Texts(d.Verification, 16, 256) ||
            d.Verification.Any(v => !v.StartsWith("docs/", StringComparison.Ordinal) || !v.EndsWith(".md", StringComparison.Ordinal) ||
                v.Contains("..", StringComparison.Ordinal) || v.Contains('\\') || v.Contains(':')) || !Texts(d.Limitations, 16, 512) ||
            d.NativeModes is not { Count: > 0 and <= 32 } || d.NativeModes.Distinct().Count() != d.NativeModes.Count) return false;
        // These are the compiled transport/encoder bounds, not user-defined writes. A different
        // protocol, bus, color encoding or restoration method requires its own driver revision.
        return d.Driver switch
        {
            "asrock-polychrome-usb" => d.Match is { VendorId: 0x26ce, ProductId: 0x01a2, Firmware: null, ConfigurationHex: null } &&
                d.Protocol == new LightingProtocolDefinition("reported-rg-swap", "grb-streaming-order-0", "reported-per-zone", null, 8, 255, "native-zones-and-order") &&
                d.Limits.MaximumLeds <= 2040 && d.ResourceDomains.SequenceEqual(["usb:asrock-native"]) && d.PhysicalResources.SequenceEqual(["usb-product:26ce:01a2"]) &&
                d.NativeModes.Order().SequenceEqual(BoardLighting.Effects.Select(e => e.Id).Order()),
            "ene-dram-rgb" => d.Match is { VendorId: null, ProductId: null, ConfigurationHex.Length: 128 } &&
                DeviceConfigurationRules.Text(d.Match.Firmware, 16) && d.Match.Firmware!.All(c => c is >= '!' and <= '~') &&
                d.Match.ConfigurationHex.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F') &&
                d.Protocol == new LightingProtocolDefinition("rbg", "rbg", "fixed", 8, 1, 8, "native-control-and-palettes") &&
                d.Limits.MaximumLeds == 8 && d.ResourceDomains.SequenceEqual(["smbus"]) &&
                d.PhysicalResources.SequenceEqual(["smbus-controller:amd-1022-790b", "smbus-port:0b00-p0"]) &&
                d.NativeModes.Order().SequenceEqual(MemoryLighting.Effects.Select(e => e.Id).Order()),
            _ => false
        };
    }
}
