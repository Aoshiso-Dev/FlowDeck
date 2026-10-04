using System.Text.Json;
using System.Text.Json.Nodes;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class OpenRgbStartupEnvironmentTests
{
    private static readonly OpenRgbStartupProfile Profile = new(OpenRgbStartupKind.AsrockPolychromeUsb, 6742);
    private static JsonObject Settings() => JsonNode.Parse(Profile.Settings)!.AsObject();
    private static OpenRgbSdkEnvironment Environment(JsonObject settings)
    {
        JsonElement Part(string name) => JsonSerializer.SerializeToElement(settings[name]);
        return new(new(Guid.NewGuid(), Guid.NewGuid(), 1, "receipt"), 25, Part("Detectors"),
            JsonSerializer.SerializeToElement(new { hid_safe_mode = new { type = "bool" } }), Part("Drivers"), Part("Server"), null,
            true, new(Part("Client"), Part("ProfileManager"), Part("QMKOpenRGBDevices"), Part("QMKVialRGBDevices"), Part("ASRock Polychrome USB")));
    }
    [Fact] public void CompiledOutDisabledDetectorsMayBePrunedButTheEnabledDetectorMustRemain()
    {
        var settings = Settings(); Profile.CheckLiveEnvironment(Environment(settings));
        settings["Detectors"]!["detectors"] = new JsonObject { ["ASRock Polychrome USB"] = true };
        Profile.CheckLiveEnvironment(Environment(settings));
        settings["Detectors"]!["detectors"]!.AsObject().Clear();
        Assert.Throws<IOException>(() => Profile.CheckLiveEnvironment(Environment(settings)));
    }
    [Theory]
    [InlineData("detector-added")] [InlineData("detector-enabled")] [InlineData("detector-disabled")]
    [InlineData("unsafe-hid")] [InlineData("detection-delay")] [InlineData("all-controllers")]
    [InlineData("legacy")] [InlineData("remote-host")] [InlineData("different-port")]
    [InlineData("shared-bus")] [InlineData("bus-mode")]
    [InlineData("remote-client")] [InlineData("dynamic-qmk")] [InlineData("dynamic-vial")]
    [InlineData("asrock-color-order")]
    public void ChangedDetectionBusOrNetworkSettingsCannotAuthorizeTheOwnedRoute(string change)
    {
        var settings = Settings();
        switch (change)
        {
            case "detector-added": settings["Detectors"]!["detectors"]!["unknown controller"] = false; break;
            case "detector-enabled": settings["Detectors"]!["detectors"]!["NZXT Control Hub"] = true; break;
            case "detector-disabled": settings["Detectors"]!["detectors"]!["ASRock Polychrome USB"] = false; break;
            case "unsafe-hid": settings["Detectors"]!["hid_safe_mode"] = false; break;
            case "detection-delay": settings["Detectors"]!["initial_detection_delay_ms"] = 500; break;
            case "all-controllers": settings["Server"]!["all_controllers"] = true; break;
            case "legacy": settings["Server"]!["legacy_workaround"] = true; break;
            case "remote-host": settings["Server"]!["default_host"] = "192.0.2.1"; break;
            case "different-port": settings["Server"]!["default_port"] = 6743; break;
            case "shared-bus": settings["Drivers"]!["shared_smbus_access"] = false; break;
            case "bus-mode": settings["Drivers"]!["smbus_sleep_mode"] = 0; break;
            case "remote-client": settings["Client"]!["clients"] = new JsonArray("fixture"); break;
            case "dynamic-qmk": settings["QMKOpenRGBDevices"]!["devices"] = new JsonArray("fixture"); break;
            case "dynamic-vial": settings["QMKVialRGBDevices"]!["devices"] = new JsonArray("fixture"); break;
            case "asrock-color-order": settings["ASRock Polychrome USB"]!["RGSwap"]!["PCH"] = true; break;
        }
        Assert.Throws<IOException>(() => Profile.CheckLiveEnvironment(Environment(settings)));
    }
    [Theory]
    [InlineData("open_profile")] [InlineData("exit_profile")] [InlineData("resume_profile")]
    [InlineData("suspend_profile")] [InlineData("service_startup_profile")] [InlineData("service_shutdown_profile")]
    public void EveryAutomaticProfileMustRemainDisabledAndUnselected(string name)
    {
        var settings = Settings(); settings["ProfileManager"]![name]!["enabled"] = true;
        Assert.Throws<IOException>(() => Profile.CheckLiveEnvironment(Environment(settings)));
        settings["ProfileManager"]![name]!["enabled"] = false; settings["ProfileManager"]![name]!["name"] = "auto";
        Assert.Throws<IOException>(() => Profile.CheckLiveEnvironment(Environment(settings)));
    }
    [Fact] public void SettingsAloneDoNotAuthorizeAnUnownedOrPrivilegedConnection()
    {
        var environment = Environment(Settings());
        foreach (var invalid in new[] { environment with { OwnedHeadless = false }, environment with { Startup = null },
            environment with { Plugins = [] }, environment with { ServerFlags = 65536 | 25 }, environment with { ServerFlags = 1 } })
            Assert.Throws<IOException>(() => Profile.CheckLiveEnvironment(invalid));
    }
}
