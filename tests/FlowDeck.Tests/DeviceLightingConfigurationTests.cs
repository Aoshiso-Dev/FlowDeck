using System.Text.Json;
using System.Text.Json.Nodes;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceLightingConfigurationTests
{
    private static DeviceConfigurationPackage Package() => new(
        new(2, [new("memory", "照明", "memory", DeviceKind.Memory, [new("rgb", "照明", EndpointKind.Lighting, Setup: new("RGB", 12, true))])]),
        new(2, Guid.NewGuid().ToString("N"), [new("memory", "serial", "memory-v1", new('a', 64))]), new(2, [], []),
        new(2, [], [], [], [], [], Endpoints: [new(new("memory", "rgb"), true, new(LightingExitAction.FixedColor, new(2, 4, 8)))]),
        new(2, []), new(2, new(), new(), new(), new(Rules: []), new([], [])), new(2, "native-v2", new('a', 64), [], []));

    [Fact] public void ExitPolicyRoundTripsAndOlderV2LightingWithoutEndpointPoliciesStaysAValidDraft()
    {
        var package = Package(); var files = DeviceConfigurationCodec.Write(package);
        Assert.Equal(package.Lighting.Endpoints, DeviceConfigurationCodec.Read(files).Lighting.Endpoints);
        var lighting = JsonNode.Parse(files["lighting.json"])!.AsObject(); lighting.Remove("endpoints");
        files["lighting.json"] = JsonSerializer.SerializeToUtf8Bytes(lighting, Contract.Json);
        Assert.Null(DeviceConfigurationCodec.Read(files).Lighting.Endpoints);
    }
    [Fact] public void PortableLightingKeepsChosenExitColorButRequiresWiringAndLedCountConfirmationAgain()
    {
        var original = Package(); var portable = DeviceConfigurationCodec.Portable(original);
        var saved = Assert.Single(portable.Lighting.Endpoints!); Assert.False(saved.WiringConfirmed);
        Assert.Equal(original.Lighting.Endpoints![0].Exit, saved.Exit); Assert.True(original.Lighting.Endpoints[0].WiringConfirmed);
        Assert.False(portable.Devices.Devices[0].Endpoints[0].Setup!.LedCountConfirmed);
        Assert.Equal(12, portable.Devices.Devices[0].Endpoints[0].Setup!.LedCount); Assert.Empty(portable.Bindings.Devices);
    }
    [Fact] public void InvalidExitPoliciesAndReferencesCannotEnterTheTypedConfiguration()
    {
        var p = Package(); var original = p.Lighting.Endpoints![0];
        foreach (var invalid in new[] { original with { Target = new("missing", "rgb") }, original with { Exit = new(LightingExitAction.FixedColor) },
                     original with { Exit = new(LightingExitAction.FixedColor, new(256, 0, 0)) }, original with { Exit = new(LightingExitAction.Off, new(1, 2, 3)) },
                     original with { Exit = new((LightingExitAction)999) } })
            Assert.Throws<ArgumentException>(() => DeviceConfigurationRules.Validate(p.Devices, p.Bindings, p.Cooling, p.Lighting with { Endpoints = [invalid] }, p.Dashboard));
        Assert.Throws<ArgumentException>(() => DeviceConfigurationRules.Validate(p.Devices, p.Bindings, p.Cooling, p.Lighting with { Endpoints = [original, original] }, p.Dashboard));
        DeviceConfigurationRules.Validate(p.Devices, p.Bindings, p.Cooling, p.Lighting with { Endpoints = [original with { Exit = null, WiringConfirmed = false }] }, p.Dashboard);
        Assert.Throws<ArgumentException>(() => DeviceLightingConfiguration.ExitProgram(new(LightingExitAction.RestoreOriginal)));
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task ColdServiceConfigurationEnablesOnlyMatchingHubLightingWithoutAutomaticOutput(bool sameInstallation)
    {
        using var dir = new ServiceTestDirectory(); var hub = new DemoHub { DelayMs = 0 };
        var config = await DeviceRuntimeStartupTests.Package(hub, dir.Path, resume: false); var id = config.Devices.Devices[0].Id;
        config = config with
        {
            Devices = config.Devices with { Devices = config.Devices.Devices.Select(d => d with { Endpoints = d.Endpoints.Select(e => e.Id == "led1" ?
                e with { Setup = new("DEMO RGB", 24, true) } : e).ToArray() }).ToArray() },
            Lighting = config.Lighting with { Endpoints = [new(new(id, "led1"), true, new(LightingExitAction.Off))] }
        };
        var store = DeviceConfigurationCodec.Store(Path.Combine(dir.Path, "config")); store.Activate(store.Stage(DeviceConfigurationCodec.Write(config)));
        var installation = sameInstallation ? config.Bindings.InstallationId! : Guid.NewGuid().ToString("N");
        var driver = new HubDeviceDriver(config.Bindings.InstallationId!, DemoHub.Identity.Id, hub, dir.Path);
        await using var startup = await DeviceRuntimeStartup.CreateAsync(store, installation, () => new([driver], []), ["user"], new TestLog(), demo: true);
        Assert.Equal(0, hub.LightingWrites); Assert.Equal(0, hub.Writes);
        var hello = startup.Runtime.OpenSession("user"); var state = startup.Runtime.State(); var target = state.Endpoints.Single(e => e.Target.EndpointId == "led1");
        var request = new DeviceServiceRequest(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "test", DeviceOperation.SetLighting,
            state.Inventory.TopologyRevision, [new(target.Target, target.Generation, target.PolicyRevision)], Lighting: new("fixed", [new(30, 60, 90)]));
        var result = await startup.Runtime.ExecuteAsync(request); Assert.Equal(sameInstallation, result.Success);
        Assert.Equal(sameInstallation ? 1 : 0, hub.LightingWrites);
        await startup.Runtime.DisconnectAsync(hello.Session);
        if (sameInstallation) { Assert.Equal("off", hub.LightEffects["led1"].Mode); Assert.Empty(new HubLightingRecoveryJournal(dir.Path, id).Pending); }
    }
    [Fact] public void LightingRecoveryDoesNotAcceptDifferentHardwareExitOrAnOldOwnerCompletion()
    {
        using var dir = new ServiceTestDirectory(); var journal = new HubLightingRecoveryJournal(dir.Path, "hub");
        var saved = new HubLightingObligation("hub", "native", new('a', 64), "wiring", "led1", Guid.NewGuid().ToString("N"), new(LightingExitAction.Off));
        journal.Prepare([saved]);
        Assert.Throws<IOException>(() => journal.Prepare([saved with { Fingerprint = new('b', 64) }]));
        Assert.Throws<IOException>(() => journal.Prepare([saved with { Exit = new(LightingExitAction.FixedColor, new(1, 2, 3)) }]));
        var newer = saved with { OwnerId = Guid.NewGuid().ToString("N") }; journal.Prepare([newer]);
        Assert.Throws<IOException>(() => journal.Complete(saved)); Assert.Equal(newer, Assert.Single(new HubLightingRecoveryJournal(dir.Path, "hub").Pending));
        var path = Directory.GetFiles(dir.Path, "hub-lighting-*.json").Single();
        File.WriteAllText(path, File.ReadAllText(path).Replace("native", "tampered"));
        Assert.Throws<IOException>(() => new HubLightingRecoveryJournal(dir.Path, "hub"));
    }
}
