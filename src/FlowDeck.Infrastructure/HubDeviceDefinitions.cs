using System.Text.Json;
using System.Text.Json.Serialization;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed record HubTransportDefinition(int VendorId, int ProductId, int WorkerProtocol, string PythonVersionPrefix, string LiquidctlVersion)
{
    public bool AcceptsWorker(int protocol, string? python, string? liquidctl) => protocol == WorkerProtocol &&
        python is not null && python.StartsWith(PythonVersionPrefix, StringComparison.Ordinal) && liquidctl == LiquidctlVersion;
}
internal sealed record HubCoolingDefinition(IReadOnlyList<string> Endpoints, IReadOnlyList<DeviceFeature> Features,
    CoolingExitAction ExitAction, bool StopSupported, bool CalibrationSupported);
internal sealed record HubLightingDefinition(IReadOnlyList<string> Endpoints, IReadOnlyList<int> ChannelMasks,
    IReadOnlyList<DeviceFeature> Features, string NativeColors, string FrameColors, int EncodedPixelsPerEndpoint,
    int MaximumLedsPerEndpoint, string LedCountPolicy, IReadOnlyList<LightingExitAction> ExitActions, IReadOnlyList<string> NativeEffects);
internal sealed record HubDriverDefinition(string Id, int Revision, string Driver, int DriverRevision, string Name,
    HubTransportDefinition Transport, HubCoolingDefinition Cooling, HubLightingDefinition Lighting, DeviceLightingLimits LightingLimits,
    string ResourceDomainPrefix, IReadOnlyList<string> DiscoveryResourceDomains, IReadOnlyList<string> PhysicalResources,
    IReadOnlyList<string> Verification, IReadOnlyList<string> Limitations)
{
    [JsonIgnore] public string Digest => SettingsTransfer.Hash(this);
}

// Embedded, validated descriptions only. Transport code still bounds USB discovery, port masks,
// packet encoding and recovery. Neither a client nor a settings file can supply a driver or register write.
internal sealed class HubDeviceDefinitionCatalog
{
    private sealed record Document(int Version, HubDriverDefinition[] Devices);
    public static HubDeviceDefinitionCatalog BuiltIn { get; } = LoadBuiltIn();
    public IReadOnlyList<HubDriverDefinition> Devices { get; }
    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
    private HubDeviceDefinitionCatalog(IEnumerable<HubDriverDefinition> devices) => Devices = Freeze(devices.Select(d => d with
    {
        Cooling = d.Cooling with { Endpoints = Freeze(d.Cooling.Endpoints), Features = Freeze(d.Cooling.Features) },
        Lighting = d.Lighting with { Endpoints = Freeze(d.Lighting.Endpoints), ChannelMasks = Freeze(d.Lighting.ChannelMasks),
            Features = Freeze(d.Lighting.Features), ExitActions = Freeze(d.Lighting.ExitActions), NativeEffects = Freeze(d.Lighting.NativeEffects) },
        DiscoveryResourceDomains = Freeze(d.DiscoveryResourceDomains), PhysicalResources = Freeze(d.PhysicalResources),
        Verification = Freeze(d.Verification), Limitations = Freeze(d.Limitations)
    }));
    private static HubDeviceDefinitionCatalog LoadBuiltIn()
    {
        using var stream = typeof(HubDeviceDefinitionCatalog).Assembly.GetManifestResourceStream("FlowDeck.DeviceDefinitions.Controllers.json")
            ?? throw new IOException("コントローラー対応定義が見つかりません");
        using var reader = new StreamReader(stream); return Parse(reader.ReadToEnd());
    }
    public HubDriverDefinition Get(string id) => Devices.SingleOrDefault(d => d.Id == id)
        ?? throw new IOException("Hubドライバーの対応定義が見つかりません");
    public HubDriverDefinition? ResolveUsb(int vendor, int product) => Devices.SingleOrDefault(d => d.Transport.VendorId == vendor && d.Transport.ProductId == product);
    internal static HubDeviceDefinitionCatalog Parse(string json)
    {
        if (json.Length > 256 * 1024) throw new IOException("コントローラー対応定義が大きすぎます");
        try
        {
            using var document = JsonDocument.Parse(json); SettingsTransfer.NoDuplicates(document.RootElement);
            var value = document.RootElement.Deserialize<Document>(Contract.Json);
            if (value is not { Version: 1, Devices.Length: > 0 and <= 64 } || value.Devices.Any(d => !Valid(d)) ||
                value.Devices.DistinctBy(d => d.Id).Count() != value.Devices.Length ||
                value.Devices.DistinctBy(d => (d.Transport.VendorId, d.Transport.ProductId)).Count() != value.Devices.Length)
                throw new IOException("コントローラー対応定義の内容または照合条件が不正・重複しています");
            return new(value.Devices);
        }
        catch (JsonException e) { throw new IOException("コントローラー対応定義の形式が不正です", e); }
    }
    private static bool Exact<T>(IReadOnlyList<T>? values, params T[] expected) => values is not null && values.SequenceEqual(expected);
    private static bool Texts(IReadOnlyList<string>? values, int maximum, int length) => values is { Count: > 0 } && values.Count <= maximum &&
        values.All(v => DeviceConfigurationRules.Text(v, length)) && values.Distinct(StringComparer.Ordinal).Count() == values.Count;
    private static bool Valid(HubDriverDefinition? d) => d is not null && DeviceConfigurationRules.Key(d.Id) && d.Revision >= 1 &&
        d.Driver == DeviceM5Selection.HubProvider && d.DriverRevision == 1 && DeviceConfigurationRules.Text(d.Name, 160) &&
        d.Transport == new HubTransportDefinition(0x1e71, 0x2022, 1, "3.13.", "1.16.0") &&
        d.Cooling is { ExitAction: CoolingExitAction.FixedDuty } cooling &&
        Exact(cooling.Endpoints, "fan1", "fan2", "fan3", "fan4", "fan5") &&
        Exact(cooling.Features, DeviceFeature.Rpm, DeviceFeature.DutyRead, DeviceFeature.FixedDuty, DeviceFeature.AutomaticDuty, DeviceFeature.ReleaseCooling) &&
        d.Lighting is { NativeColors: "grb", FrameColors: "grb", EncodedPixelsPerEndpoint: 24, MaximumLedsPerEndpoint: >= 1 and <= 24,
            LedCountPolicy: "confirmed-accessory" } lighting &&
        Exact(lighting.Endpoints, "led1", "led2", "led3", "led4", "led5") && Exact(lighting.ChannelMasks, 2, 1, 4, 8, 16) &&
        Exact(lighting.Features, DeviceFeature.FixedColor, DeviceFeature.NativeEffect, DeviceFeature.ColorFrames, DeviceFeature.RestoreLighting) &&
        Exact(lighting.ExitActions, LightingExitAction.FixedColor, LightingExitAction.Off) && Texts(lighting.NativeEffects, 7, 64) &&
        lighting.NativeEffects.Contains("fixed") && lighting.NativeEffects.Contains("off") &&
        lighting.NativeEffects.All(id => HubLighting.Effects.Any(e => e.Id == id)) &&
        d.LightingLimits is { MinimumIntervalMilliseconds: >= 100 and <= 500, MaximumFrameAgeMilliseconds: <= 500,
            MaximumLeds: > 0, EndpointColors: true, ConcurrentEndpointStreams: true } limits &&
        limits.MaximumFrameAgeMilliseconds >= limits.MinimumIntervalMilliseconds && limits.MaximumLeds <= lighting.Endpoints.Count * lighting.MaximumLedsPerEndpoint &&
        d.ResourceDomainPrefix == "usb:" && Exact(d.DiscoveryResourceDomains, "usb:control-hub-discovery") &&
        Exact(d.PhysicalResources, "usb-product:1e71:2022") && Texts(d.Verification, 16, 256) &&
        d.Verification.All(v => v.StartsWith("docs/", StringComparison.Ordinal) && v.EndsWith(".md", StringComparison.Ordinal) &&
            !v.Contains("..", StringComparison.Ordinal) && !v.Contains('\\') && !v.Contains(':')) && Texts(d.Limitations, 16, 512);
}
