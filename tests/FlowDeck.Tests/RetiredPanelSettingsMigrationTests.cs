using System.Text.Json;
using System.Text.Json.Nodes;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class RetiredPanelSettingsMigrationTests
{
    private static readonly EndpointReference Cpu = new("cpu", "temperature"), Gpu = new("gpu", "temperature");
    private static DeviceConfigurationPackage Package()
    {
        var source = ServiceRuntimeBootstrap.MonitoringConfiguration(new('a', 32));
        return source with
        {
            Devices = new(2, [
                new("cpu", "CPU", "sensors", DeviceKind.Processor, [new("temperature", "CPU", EndpointKind.Sensor)]),
                new("gpu", "GPU", "sensors", DeviceKind.Graphics, [new("temperature", "GPU", EndpointKind.Sensor)]),
                new("light", "照明", "test", DeviceKind.Memory, [new("led", "照明", EndpointKind.Lighting, Setup: new("ARGB", 24))])]),
            Lighting = source.Lighting with { Scenes = [new("blue", "青", [new(new("light", "led"), new("fixed", [new(0, 0, 180)]))], [])] },
            Preferences = source.Preferences with { Ui = source.Preferences.Ui with { Theme = "dark" }, DialDisplay = new(Cpu, Gpu) }
        };
    }
    private static void AddRetiredFields(JsonObject configuration)
    {
        var preferences = configuration["preferences"]!.AsObject();
        preferences.Remove("dialDisplay");
        preferences["m5"] = JsonNode.Parse("""{"port":"COM14","deviceId":"old-panel","transport":"auto","wifiAddress":"192.0.2.1"}""");
        preferences["m5ConnectOnStart"] = true;
        preferences["m5Selection"] = new JsonObject
        {
            ["cpu"] = JsonSerializer.SerializeToNode(Cpu, Contract.Json),
            ["gpu"] = JsonSerializer.SerializeToNode(Gpu, Contract.Json),
            ["fans"] = new JsonArray("fan1"), ["leds"] = new JsonArray("led1"), ["profiles"] = new JsonArray(),
            ["motherboard"] = new JsonArray()
        };
        configuration["devices"]!["devices"]![2]!["endpoints"]![0]!["setup"]!["showOnM5"] = true;
        configuration["lighting"]!["scenes"]![0]!["showOnM5"] = true;
    }
    private static void AssertCurrentSettings(DeviceConfigurationPackage restored)
    {
        Assert.Equal("dark", restored.Preferences.Ui.Theme);
        Assert.Equal(new DeviceDialDisplaySettings(Cpu, Gpu), restored.Preferences.DialDisplay);
        Assert.Equal("青", Assert.Single(restored.Lighting.Scenes).Name);
        var json = JsonSerializer.Serialize(restored, Contract.Json);
        Assert.DoesNotContain("m5", json, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void OldDeviceSettingsDiscardPanelFieldsAndPreserveDialTemperatureSelections()
    {
        var node = JsonSerializer.SerializeToNode(Package(), Contract.Json)!.AsObject(); AddRetiredFields(node);
        var restored = DeviceConfigurationCodec.DeserializePackage(JsonSerializer.SerializeToUtf8Bytes(node, Contract.Json));
        AssertCurrentSettings(restored);
        AssertCurrentSettings(DeviceConfigurationCodec.Read(DeviceConfigurationCodec.Write(restored)));
    }
    [Fact]
    public void SevenFileStoreAcceptsOldPanelFieldsAndWritesOnlyCurrentSettings()
    {
        var node = JsonSerializer.SerializeToNode(Package(), Contract.Json)!.AsObject(); AddRetiredFields(node);
        var files = node.ToDictionary(p => p.Key + ".json", p => JsonSerializer.SerializeToUtf8Bytes(p.Value, Contract.Json));
        var restored = DeviceConfigurationCodec.Read(files); AssertCurrentSettings(restored);
        Assert.All(DeviceConfigurationCodec.Write(restored).Values, bytes =>
            Assert.DoesNotContain("m5", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase));
    }
    [Fact]
    public void OldSignedBackupValidatesOriginalPayloadBeforeDiscardingPanelFields()
    {
        var archive = DeviceSettingsArchives.Create(Package(), false);
        var node = JsonSerializer.SerializeToNode(archive, Contract.Json)!.AsObject(); AddRetiredFields(node["configuration"]!.AsObject());
        var signed = node.DeepClone().AsObject(); signed.Remove("sha256"); node["sha256"] = SettingsTransfer.Hash(signed);
        var restored = DeviceSettingsArchives.Read(JsonSerializer.SerializeToUtf8Bytes(node, Contract.Json));
        AssertCurrentSettings(restored.Configuration);
        AssertCurrentSettings(DeviceSettingsArchives.Read(DeviceSettingsArchives.Write(restored)).Configuration);
        node["configuration"]!["preferences"]!["m5"]!["port"] = "COM15";
        Assert.Throws<IOException>(() => DeviceSettingsArchives.Read(JsonSerializer.SerializeToUtf8Bytes(node, Contract.Json)));
    }
    [Fact]
    public void LegacyApplicationSettingsDiscardPanelConnectionAndVisibility()
    {
        var node = JsonSerializer.SerializeToNode(DemoHub.CreateSettings(), Contract.Json)!.AsObject();
        node["m5"] = JsonNode.Parse("""{"port":"COM14","deviceId":"retired-panel"}""");
        node["motherboard"]!["sensors"] = JsonNode.Parse("""[{"id":"board-rpm","displayName":"ラジエーターポンプ","showOnM5":true}]""");
        var settings = node.Deserialize<AppSettings>(Contract.Json)!;
        Assert.Equal("ラジエーターポンプ", Assert.Single(settings.Motherboard.Sensors!).DisplayName);
        Assert.DoesNotContain("m5", JsonSerializer.Serialize(settings, Contract.Json), StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void ExistingServiceRepliesIgnoreRetiredPanelTelemetryAndDoNotReemitIt()
    {
        var status = new DeviceLightingStatus("lighting-group", true, 3, 1, new(1, 2, 3), 4);
        var light = JsonSerializer.SerializeToNode(status, Contract.Json)!.AsObject();
        light["mirror"] = JsonNode.Parse("""{"effectId":"breathing","phaseMs":123,"validForMs":456}""");
        var restored = light.Deserialize<DeviceLightingStatus>(Contract.Json)!;
        Assert.Equal(status, restored); Assert.DoesNotContain("mirror", JsonSerializer.Serialize(restored, Contract.Json));
        var configuration = new DeviceDiagnosticConfiguration(1, 2, 3, 4, 5, 6, 7, false, false, []);
        var diagnostics = JsonSerializer.SerializeToNode(configuration, Contract.Json)!.AsObject();
        diagnostics["m5ConnectOnStart"] = true;
        var read = diagnostics.Deserialize<DeviceDiagnosticConfiguration>(Contract.Json)!;
        Assert.Equal(configuration.RegisteredDevices, read.RegisteredDevices);
        Assert.DoesNotContain("m5", JsonSerializer.Serialize(read, Contract.Json), StringComparison.OrdinalIgnoreCase);
    }
    [Theory]
    [InlineData("operation", "\"reportM5Connection\"")] [InlineData("operation", "19")]
    [InlineData("configurationOperation", "\"saveM5\"")] [InlineData("configurationOperation", "12")]
    public void RetiredOperationsRemainReadableOnlyInDiagnosticHistory(string property, string retiredValue)
    {
        var entry = new DeviceDiagnosticEvent(1, DateTimeOffset.UtcNow, DeviceOperation.State,
            DeviceConfigurationOperation.Snapshot, null, null, 1, Outcome.Confirmed, [], false);
        var node = JsonSerializer.SerializeToNode(entry, Contract.Json)!.AsObject();
        var normal = node.Deserialize<DeviceDiagnosticEvent>(Contract.Json)!;
        Assert.Equal(entry.Operation, normal.Operation); Assert.Equal(entry.ConfigurationOperation, normal.ConfigurationOperation);
        node[property] = JsonNode.Parse(retiredValue);
        var read = node.Deserialize<DeviceDiagnosticEvent>(Contract.Json)!;
        if (property == "operation") Assert.Null(read.Operation); else Assert.Null(read.ConfigurationOperation);
        Assert.DoesNotContain("m5", JsonSerializer.Serialize(read, Contract.Json), StringComparison.OrdinalIgnoreCase);

        var request = new DeviceServiceRequest(2, new('a', 32), new('b', 32), new('c', 32), 1, "PC",
            property == "operation" ? DeviceOperation.State : DeviceOperation.Configuration,
            Configuration: property == "operation" ? null : new(DeviceConfigurationOperation.Snapshot));
        var executable = JsonSerializer.SerializeToNode(request, Contract.Json)!.AsObject();
        if (property == "operation") executable["operation"] = JsonNode.Parse(retiredValue);
        else executable["configuration"]!["operation"] = JsonNode.Parse(retiredValue);
        Assert.Throws<IOException>(() => DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(executable, Contract.Json)));
    }
}
