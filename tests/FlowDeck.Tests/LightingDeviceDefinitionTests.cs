using System.Text.Json.Nodes;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class LightingDeviceDefinitionTests
{
    private static JsonObject Document()
    {
        using var stream = typeof(AsrockDeviceProvider).Assembly.GetManifestResourceStream("FlowDeck.DeviceDefinitions.Lighting.json")!;
        Assert.NotNull(stream); return JsonNode.Parse(stream)!.AsObject();
    }
    [Fact] public void EmbeddedDefinitionsMatchExistingDevicesAndKeepDriverBoundsAndEvidence()
    {
        var catalog = LightingDeviceDefinitionCatalog.BuiltIn;
        var asrock = catalog.ResolveUsb(0x26ce, 0x01a2)!;
        Assert.Equal(AsrockDeviceProvider.Definition, asrock.Id);
        Assert.Equal(new DeviceLightingLimits(100, 500, 2040, EndpointColors: true), asrock.Limits);
        Assert.Equal("grb-streaming-order-0", asrock.Protocol.FrameColors);
        Assert.Contains("docs/同期中のマザー色順修正-1.23.1.md", asrock.Verification);
        var ene = catalog.ResolveEne(new("AUDA0-E6K5-0101", EneMemoryDefinition.EightLed.ConfigurationHex))!;
        Assert.Equal(EneMemoryDefinition.EightLed.Id, ene.Id); Assert.Equal(8, ene.Protocol.FixedLeds);
        Assert.Equal("rbg", ene.Protocol.NativeColors); Assert.Equal("rbg", ene.Protocol.FrameColors);
        Assert.Equal(EneMemoryDeviceBackend.ResourceDomains, ene.ResourceDomains);
        Assert.Equal(MemoryLighting.Effects.Select(e => e.Id).Order(), ene.NativeModes.Order());
        Assert.All(catalog.Devices, d => { Assert.Equal(64, d.Digest.Length); Assert.NotEmpty(d.PhysicalResources); Assert.NotEmpty(d.Limitations); });
    }
    [Theory] [InlineData(0x26ce, 0x01a3)] [InlineData(0x26cf, 0x01a2)]
    public void SimilarUsbProductsAreNotMatched(int vendor, int product) => Assert.Null(LightingDeviceDefinitionCatalog.BuiltIn.ResolveUsb(vendor, product));
    [Fact] public void EneRequiresExactFirmwareAndAllConfigurationBytes()
    {
        var d = EneMemoryDefinition.EightLed;
        Assert.Null(EneMemoryDefinition.Resolve(new(d.Firmware + "x", d.ConfigurationHex)));
        Assert.Null(EneMemoryDefinition.Resolve(new(d.Firmware, d.ConfigurationHex[..^2] + "01")));
        Assert.Equal(d, EneMemoryDefinition.Resolve(new(d.Firmware, d.ConfigurationHex)));
    }
    [Theory]
    [InlineData("driver")] [InlineData("driver-revision")] [InlineData("color-order")] [InlineData("frame-budget")]
    [InlineData("resource")] [InlineData("led-count")] [InlineData("effect")] [InlineData("register-write")]
    [InlineData("evidence-path")] [InlineData("firmware-wildcard")]
    public void DataCannotExpandACompiledTransportOrEncoder(string change)
    {
        var document = Document(); var device = document["devices"]![change is "led-count" or "firmware-wildcard" ? 1 : 0]!;
        switch (change)
        {
            case "driver": device["driver"] = "user-plugin.dll"; break;
            case "driver-revision": device["driverRevision"] = 2; break;
            case "color-order": device["protocol"]!["frameColors"] = "rgb"; break;
            case "frame-budget": device["limits"]!["minimumIntervalMilliseconds"] = 1; break;
            case "resource": device["resourceDomains"] = new JsonArray("independent"); break;
            case "led-count": device["protocol"]!["fixedLeds"] = 16; break;
            case "effect": device["nativeModes"]!.AsArray().Add(255); break;
            case "register-write": device["registerWrites"] = new JsonArray(128); break;
            case "evidence-path": device["verification"] = new JsonArray("docs/../untrusted.md"); break;
            case "firmware-wildcard": device["match"]!["configurationHex"] = "*"; break;
        }
        Assert.Throws<IOException>(() => LightingDeviceDefinitionCatalog.Parse(document.ToJsonString()));
    }
    [Fact] public void AmbiguousMatchesAndDuplicateFieldsAreRejected()
    {
        var document = Document(); var duplicate = document["devices"]![0]!.DeepClone(); duplicate["id"] = "another-asrock";
        document["devices"]!.AsArray().Add(duplicate);
        Assert.Throws<IOException>(() => LightingDeviceDefinitionCatalog.Parse(document.ToJsonString()));
        var json = Document().ToJsonString();
        Assert.Throws<IOException>(() => LightingDeviceDefinitionCatalog.Parse(json.Replace("\"version\":1", "\"version\":1,\"Version\":1", StringComparison.Ordinal)));
    }
    [Fact] public void DefinitionRevisionAndConditionsAreIncludedInTheBindingDigest()
    {
        var original = LightingDeviceDefinitionCatalog.BuiltIn.Devices[0];
        var document = Document(); document["devices"]![0]!["revision"] = 2;
        var revised = LightingDeviceDefinitionCatalog.Parse(document.ToJsonString()).Devices[0];
        Assert.Equal(original.Id, revised.Id); Assert.NotEqual(original.Digest, revised.Digest);
        document = Document(); document["devices"]![0]!["limits"]!["minimumIntervalMilliseconds"] = 200;
        Assert.NotEqual(original.Digest, LightingDeviceDefinitionCatalog.Parse(document.ToJsonString()).Devices[0].Digest);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)original.ResourceDomains)[0] = "changed");
        Assert.Throws<IOException>(() => LightingDeviceDefinitionCatalog.BuiltIn.Get(original.Id, "ene-dram-rgb"));
    }
    [Fact] public void AdditionalEneDefinitionsUseExactMatchingAndDoNotChangeTheKnownDefinition()
    {
        var document = Document(); var extra = document["devices"]![1]!.DeepClone();
        extra["id"] = "ene-test-fixture"; extra["match"]!["firmware"] = "FIXTURE-ONLY"; document["devices"]!.AsArray().Add(extra);
        var parsed = LightingDeviceDefinitionCatalog.Parse(document.ToJsonString());
        Assert.Equal("ene-test-fixture", parsed.ResolveEne(new("FIXTURE-ONLY", EneMemoryDefinition.EightLed.ConfigurationHex))!.Id);
        Assert.Null(LightingDeviceDefinitionCatalog.BuiltIn.ResolveEne(new("FIXTURE-ONLY", EneMemoryDefinition.EightLed.ConfigurationHex)));
        Assert.Equal(EneMemoryDefinition.EightLed.Id, parsed.ResolveEne(new(EneMemoryDefinition.EightLed.Firmware, EneMemoryDefinition.EightLed.ConfigurationHex))!.Id);
    }
}
