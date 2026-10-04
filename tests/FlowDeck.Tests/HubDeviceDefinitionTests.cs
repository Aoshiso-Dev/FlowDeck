using System.Text.Json.Nodes;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class HubDeviceDefinitionTests
{
    [Fact] public async Task FasterWorkerNegotiationPreservesTheSavedHardwareBindingAndTelemetryAge()
    {
        using var dir = new ServiceTestDirectory(); var clock = new TestClock();
        var hub = new HubLightingDriverTests.Hub();
        await using var driver = await HubLightingDriverTests.Ready(hub, dir.Path, clock);
        var binding = driver.BindingCandidate;
        Assert.Equal(100, driver.LightingLimits!.MinimumIntervalMilliseconds);
        hub.SupportsSmoothLightingFrames = true;
        clock.Advance(1);
        hub.Transform = s => s with { SampleAgeMilliseconds = 500 };
        await driver.RefreshAsync();
        Assert.Equal(33, driver.LightingLimits!.MinimumIntervalMilliseconds);
        Assert.Equal(binding, driver.BindingCandidate);
        Assert.All(driver.ReadCachedSamples(), s => Assert.Equal(500, clock.GetElapsedTime(s.Timestamp).TotalMilliseconds));
        hub.SupportsLightingDeadlines = false;
        Assert.Null(driver.LightingLimits);
    }

    [Theory] [InlineData(-1)] [InlineData(751)]
    public async Task InvalidSampleAgeCannotBecomeFreshTelemetry(int age)
    {
        using var dir = new ServiceTestDirectory(); var hub = new HubLightingDriverTests.Hub();
        await using var driver = await HubLightingDriverTests.Ready(hub, dir.Path);
        hub.Transform = s => s with { SampleAgeMilliseconds = age };
        await Assert.ThrowsAsync<IOException>(() => driver.RefreshAsync());
        Assert.Null(driver.Observe());
    }

    private const string Installation = "0123456789abcdef0123456789abcdef";
    private static JsonObject Document()
    {
        using var stream = typeof(HubDeviceDriver).Assembly.GetManifestResourceStream("FlowDeck.DeviceDefinitions.Controllers.json")!;
        Assert.NotNull(stream); return JsonNode.Parse(stream)!.AsObject();
    }
    [Fact] public async Task EmbeddedDefinitionFeedsSetupCapabilitiesBudgetsAndPerDeviceResources()
    {
        var catalog = HubDeviceDefinitionCatalog.BuiltIn; var definition = catalog.ResolveUsb(0x1e71, 0x2022)!;
        Assert.Equal(HubDeviceDriver.Definition, definition.Id); Assert.Null(catalog.ResolveUsb(0x1e71, 0x2023));
        Assert.Null(catalog.ResolveUsb(0x1e72, 0x2022));
        using var directory = new ServiceTestDirectory(); var hub = new HubLightingDriverTests.Hub();
        await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, directory.Path);
        await driver.InitializeAsync(); var observation = driver.Observe()!;
        Assert.Equal(definition.Cooling.Endpoints, observation.Endpoints.Where(e => e.Kind == EndpointKind.Cooling).Select(e => e.Id));
        Assert.All(observation.Endpoints.Where(e => e.Kind == EndpointKind.Cooling), e => Assert.Equal(definition.Cooling.Features, e.Capabilities.Select(c => c.Feature)));
        Assert.All(observation.Endpoints.Where(e => e.Kind == EndpointKind.Lighting), e => {
            Assert.Equal(definition.Lighting.Features, e.Capabilities.Select(c => c.Feature));
            Assert.Equal(definition.Lighting.NativeEffects.Select(id => "native/" + definition.Driver + "/" + id), e.NativeLightingEffects!.Select(e => e.Id));
        });
        Assert.Equal(definition.LightingLimits, driver.LightingLimits);
        Assert.Equal(new DeviceLightingLimits(100, 500, 120, EndpointColors: true, ConcurrentEndpointStreams: true), driver.LightingLimits);
        Assert.Equal([definition.ResourceDomainPrefix + driver.DeviceId], observation.ResourceDomains);
        var setup = driver.DescribeSetup();
        Assert.All(setup.Where(e => e.EndpointId.StartsWith("fan", StringComparison.Ordinal)), e => {
            Assert.Equal([definition.Cooling.ExitAction], e.CoolingExitActions);
            Assert.True(e.CoolingStopSupported); Assert.True(e.CoolingCalibrationSupported);
        });
        Assert.All(setup.Where(e => e.EndpointId.StartsWith("led", StringComparison.Ordinal)), e => {
            Assert.Equal(definition.Lighting.ExitActions, e.LightingExitActions); Assert.Equal(24, e.MaximumLeds);
        });
        // A data declaration cannot replace runtime feature negotiation with the actual worker.
        hub.SupportsLightingEndpoints = false; Assert.Null(driver.LightingLimits);
        Assert.DoesNotContain(driver.DescribeSetup(), e => e.EndpointId.StartsWith("led", StringComparison.Ordinal));
        Assert.All(driver.Observe()!.Endpoints.Where(e => e.Kind == EndpointKind.Lighting), e => Assert.Null(e.NativeLightingEffects));
        Assert.Empty(hub.Writes); Assert.Equal(0, hub.Fans.Writes);
    }
    [Theory]
    [InlineData("driver")] [InlineData("driver-revision")] [InlineData("usb-product")] [InlineData("port")]
    [InlineData("mask")] [InlineData("color-order")] [InlineData("pixel-count")] [InlineData("led-count")]
    [InlineData("effect")] [InlineData("budget")] [InlineData("expiry")] [InlineData("resource")]
    [InlineData("restore")] [InlineData("capability")] [InlineData("executable")] [InlineData("evidence-path")]
    public void DefinitionCannotExpandTheCompiledTransportOrInventReadback(string change)
    {
        var document = Document(); var device = document["devices"]![0]!;
        switch (change)
        {
            case "driver": device["driver"] = "generic-liquidctl"; break;
            case "driver-revision": device["driverRevision"] = 2; break;
            case "usb-product": device["transport"]!["productId"] = 0x2023; break;
            case "port": device["cooling"]!["endpoints"]![4] = "fan6"; break;
            case "mask": device["lighting"]!["channelMasks"]![2] = 6; break;
            case "color-order": device["lighting"]!["frameColors"] = "rgb"; break;
            case "pixel-count": device["lighting"]!["encodedPixelsPerEndpoint"] = 40; break;
            case "led-count": device["lighting"]!["maximumLedsPerEndpoint"] = 40; break;
            case "effect": device["lighting"]!["nativeEffects"]!.AsArray().Add("unknown-effect"); break;
            case "budget": device["lightingLimits"]!["minimumIntervalMilliseconds"] = 1; break;
            case "expiry": device["lightingLimits"]!["maximumFrameAgeMilliseconds"] = 50; break;
            case "resource": device["resourceDomainPrefix"] = "unshared:"; break;
            case "restore": device["lighting"]!["exitActions"]!.AsArray().Add("restoreOriginal"); break;
            case "capability": device["cooling"]!["features"]!.AsArray().Add("temperature"); break;
            case "executable": device["transport"]!["executable"] = "external.py"; break;
            case "evidence-path": device["verification"] = new JsonArray("docs/../outside.md"); break;
        }
        Assert.Throws<IOException>(() => HubDeviceDefinitionCatalog.Parse(document.ToJsonString()));
    }
    [Fact] public void AmbiguousProductsDuplicateFieldsAndMutableCollectionsAreRejected()
    {
        var document = Document(); var duplicate = document["devices"]![0]!.DeepClone(); duplicate["id"] = "duplicate-hub";
        document["devices"]!.AsArray().Add(duplicate);
        Assert.Throws<IOException>(() => HubDeviceDefinitionCatalog.Parse(document.ToJsonString()));
        var json = Document().ToJsonString();
        Assert.Throws<IOException>(() => HubDeviceDefinitionCatalog.Parse(json.Replace("\"version\":1", "\"version\":1,\"Version\":1", StringComparison.Ordinal)));
        var d = HubDeviceDriver.DefinitionData;
        Assert.Throws<NotSupportedException>(() => ((IList<int>)d.Lighting.ChannelMasks)[0] = 4);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)d.Cooling.Endpoints)[0] = "fan6");
        Assert.Throws<NotSupportedException>(() => ((IList<DeviceFeature>)d.Lighting.Features).Add(DeviceFeature.Temperature));
    }
    [Theory]
    [InlineData(1, "3.13.12", "1.16.0", true)] [InlineData(2, "3.13.12", "1.16.0", false)]
    [InlineData(1, "3.14.0", "1.16.0", false)] [InlineData(1, "3.13.12", "1.17.0", false)]
    [InlineData(1, null, "1.16.0", false)] [InlineData(1, "3.13.12", null, false)]
    public void WorkerVersionMustMatchThePinnedTransport(int protocol, string? python, string? liquidctl, bool expected) =>
        Assert.Equal(expected, HubDeviceDriver.DefinitionData.Transport.AcceptsWorker(protocol, python, liquidctl));
    [Fact] public async Task LiveBindingIncludesDefinitionAndDoesNotAdoptEarlierConfirmation()
    {
        var definition = HubDeviceDriver.DefinitionData;
        using var directory = new ServiceTestDirectory(); var hub = new HubLightingDriverTests.Hub();
        await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, directory.Path);
        await driver.InitializeAsync(); var status = driver.CachedStatus!;
        var expected = SettingsTransfer.Hash(new { Definition = definition.Id, DefinitionDigest = definition.Digest,
            Native = hub.Identity.Id, Serial = hub.Identity.Serial, Path = (string?)null, Connection = (string?)null,
            Wiring = ControlCoordinator.WiringSignature(status), status.Firmware });
        Assert.Equal(expected, driver.BindingCandidate!.Fingerprint);
        var previous = driver.BindingCandidate with { Fingerprint = SettingsTransfer.Hash(new { Definition = definition.Id,
            Native = hub.Identity.Id, Serial = hub.Identity.Serial, Path = (string?)null, Connection = (string?)null,
            Wiring = ControlCoordinator.WiringSignature(status), status.Firmware }) };
        CoolingEndpointConfiguration[] fans = [new(new(driver.DeviceId, "fan1"), true, new(30, 100, 65, CoolingExitAction.FixedDuty, []), [])];
        LightingEndpointConfiguration[] lights = [new(new(driver.DeviceId, "led1"), true, new(LightingExitAction.Off))];
        RegisteredEndpoint[] endpoints = [new("led1", "上", EndpointKind.Lighting, Setup: new("RGB", 24, true))];
        driver.Configure(previous, fans); driver.ConfigureLighting(previous, lights, endpoints);
        var light = new DeviceEndpointAction("led1", DeviceOperation.SetLighting, Lighting: new("fixed", [new(17, 34, 51)]));
        Assert.Equal(Outcome.Rejected, (await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 60), default)).Outcome);
        Assert.Equal(Outcome.Rejected, (await driver.ApplyAsync(light, default)).Outcome);
        Assert.Empty(hub.Writes); Assert.Equal(0, hub.Fans.Writes);
        driver.Configure(driver.BindingCandidate, fans); driver.ConfigureLighting(driver.BindingCandidate, lights, endpoints);
        Assert.Null(driver.ValidateCoolingSetup("fan1")); Assert.Null(driver.Validate(light));
        var changed = Document(); changed["devices"]![0]!["revision"] = 2;
        Assert.NotEqual(definition.Digest, HubDeviceDefinitionCatalog.Parse(changed.ToJsonString()).Get(definition.Id).Digest);
        changed = Document(); changed["devices"]![0]!["lightingLimits"]!["minimumIntervalMilliseconds"] = 200;
        Assert.NotEqual(definition.Digest, HubDeviceDefinitionCatalog.Parse(changed.ToJsonString()).Get(definition.Id).Digest);
    }
}
