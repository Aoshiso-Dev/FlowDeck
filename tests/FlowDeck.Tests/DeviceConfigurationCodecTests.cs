using System.Text;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceConfigurationCodecTests
{
    private static DeviceConfigurationPackage Package()
    {
        var fan = new EndpointReference("controller", "fan1"); var sensor = new EndpointReference("controller", "temperature");
        var profile = new CoolingTemperatureProfile("balanced", "標準", 1, [new(sensor, [new(20, 30), new(80, 100)], 95, 80)], 3, 2);
        return new(new(2, [new("controller", "ケース", "test", DeviceKind.Controller,
                [new("fan1", "前面", EndpointKind.Cooling), new("temperature", "水温", EndpointKind.Sensor)])]),
            new(2, Guid.NewGuid().ToString("N"), [new("controller", "native-private-id", "known-definition", new('b', 64))]),
            new(2, [new(fan, true, new(30, 100, 50, CoolingExitAction.FixedDuty, []), [profile],
                new(FanMode.Auto, Profile: profile, SafetySnapshot: new(30, 100, 50, CoolingExitAction.FixedDuty, [])), true)], []),
            new(2, [], [], [], [], []), new(2, [new(sensor, DeviceFeature.Temperature, "水温", "temperature")]),
            new(2, new(Left: 200, Top: 100), new(Port: "COM14", DeviceId: "panel", Transport: "auto", WifiAddress: "192.168.1.19"),
                new(true, true), new(Rules: []), new(["#123456"], ["#345678"])),
            new(2, "legacy-v1", new('c', 64), [new("sensor", "legacy-native", "temperature", sensor)], []));
    }
    [Fact]
    public void SevenFileRoundTripPreservesNamesOrderCurvesAndExplicitBindings()
    {
        var original = Package(); var files = DeviceConfigurationCodec.Write(original); var copy = DeviceConfigurationCodec.Read(files);
        Assert.Equal(7, files.Count); Assert.Equal(SettingsTransfer.Canonical(original), SettingsTransfer.Canonical(copy));
        Assert.DoesNotContain(files.Values, b => Encoding.UTF8.GetString(b).Contains("pythonPath"));
    }
    [Fact]
    public void PortableSettingsRetainPreferencesButClearBindingsConfirmationAndAutomaticResume()
    {
        var original = Package(); var portable = DeviceConfigurationCodec.Portable(original);
        Assert.Null(portable.Bindings.InstallationId); Assert.Empty(portable.Bindings.Devices);
        Assert.False(portable.Cooling.Endpoints[0].WiringConfirmed); Assert.Null(portable.Cooling.Endpoints[0].SavedPolicy); Assert.False(portable.Cooling.Endpoints[0].ResumeOnStart);
        Assert.False(portable.Preferences.Startup.ResumeControl); Assert.Null(portable.Preferences.M5.Port);
        Assert.Null(portable.Preferences.M5.DeviceId); Assert.Null(portable.Preferences.M5.WifiAddress); Assert.Null(portable.Preferences.Ui.Left);
        Assert.Equal(30, portable.Cooling.Endpoints[0].Safety.Minimum); Assert.Equal("前面", portable.Devices.Devices[0].Endpoints[0].Name);
        Assert.Equal(original.Preferences.Palette.Favorites, portable.Preferences.Palette.Favorites); Assert.Empty(portable.Migration.References);
        Assert.True(original.Cooling.Endpoints[0].WiringConfirmed); Assert.NotEmpty(original.Bindings.Devices);
        portable.Devices.Devices[0].Endpoints[0] = new("other", "changed", EndpointKind.Cooling);
        Assert.Equal("fan1", original.Devices.Devices[0].Endpoints[0].Id);
    }
    [Fact]
    public void InvalidCrossReferencesAndUnknownFieldsCannotEnterTheStore()
    {
        var files = DeviceConfigurationCodec.Write(Package());
        files["dashboard.json"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files["dashboard.json"]).Replace("temperature\"}", "missing\"}"));
        Assert.Throws<IOException>(() => DeviceConfigurationCodec.Read(files));
        files = DeviceConfigurationCodec.Write(Package());
        files["bindings.json"] = Encoding.UTF8.GetBytes("{\"version\":2,\"installationId\":null,\"devices\":[],\"driverDll\":\"evil.dll\"}");
        Assert.Throws<IOException>(() => DeviceConfigurationCodec.Read(files));
    }
    [Fact]
    public void DuplicatedJsonKeysAreRejectedBeforeSchemaInterpretation()
    {
        var files = DeviceConfigurationCodec.Write(Package());
        files["bindings.json"] = Encoding.UTF8.GetBytes("{\"version\":2,\"Version\":2,\"installationId\":null,\"devices\":[]}");
        Assert.Throws<IOException>(() => DeviceConfigurationCodec.Read(files));
    }
    [Fact]
    public void EndpointSetupAndNotificationsMustMatchTheRegisteredEndpointKind()
    {
        var value = Package(); var controller = value.Devices.Devices[0];
        var light = new RegisteredEndpoint("light", "ARGB", EndpointKind.Lighting, Setup: new("ケース", 24));
        value = value with { Devices = value.Devices with { Devices = [controller with { Endpoints = [.. controller.Endpoints, light] }] } };
        var rule = new DeviceNotificationRule(Guid.NewGuid().ToString("N"), new("controller", "light"), DeviceFeature.Temperature, "照明", 80);
        Assert.Throws<IOException>(() => DeviceConfigurationCodec.Validate(value with {
            Preferences = value.Preferences with { Notifications = new(Rules: [rule]) } }));
        Assert.Throws<IOException>(() => DeviceConfigurationCodec.Validate(value with {
            Devices = new(2, [controller with { Endpoints = [.. controller.Endpoints, light with { Setup = new("ARGB", 24, ShowOnM5: true) }] }]) }));
        Assert.Throws<IOException>(() => DeviceConfigurationCodec.Validate(value with {
            Devices = new(2, [controller with { Endpoints = [controller.Endpoints[0] with { Setup = new(LedCount: 24) }, controller.Endpoints[1]] }]) }));
        Assert.Throws<IOException>(() => DeviceConfigurationCodec.Validate(value with {
            Preferences = value.Preferences with { Providers = new(OpenRgbPort: 1) } }));
    }
    [Fact]
    public void TypedStoreValidatesEveryComponentBeforeActivating()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FlowDeck-typed-config-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = DeviceConfigurationCodec.Store(directory); var original = Package(); var staged = store.Stage(DeviceConfigurationCodec.Write(original));
            Assert.Null(store.ReadActive()); store.Activate(staged);
            var loaded = DeviceConfigurationCodec.Read(store.ReadActive()!.Files);
            Assert.Equal(original.Bindings.InstallationId, loaded.Bindings.InstallationId);
            Assert.Equal(original.Cooling.Endpoints[0].SavedPolicy!.Profile!.Id, loaded.Cooling.Endpoints[0].SavedPolicy!.Profile!.Id);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
