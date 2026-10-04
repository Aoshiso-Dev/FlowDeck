using System.Text.Json;
using System.Text.Json.Nodes;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class BroadInputMutationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static IEnumerable<string[]> Paths(JsonNode node, string[] prefix)
    {
        yield return prefix;
        if (node is JsonObject obj)
            foreach (var (key, child) in obj)
                if (child is not null) foreach (var path in Paths(child, [.. prefix, key])) yield return path;
        if (node is JsonArray array && array.Count > 0 && array[0] is { } first)
            foreach (var path in Paths(first, [.. prefix, "0"])) yield return path;
    }
    private static JsonNode? Replace(JsonNode original, string[] path, JsonNode? value)
    {
        if (path.Length == 0) return value;
        var copy = original.DeepClone(); var parent = copy;
        foreach (var part in path.SkipLast(1)) parent = parent is JsonArray a ? a[int.Parse(part)]! : parent[part]!;
        if (parent is JsonArray array) array[int.Parse(path[^1])] = value;
        else parent[path[^1]] = value;
        return copy;
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("archive")]
    [InlineData("request")]
    [InlineData("state")]
    public void NestedInputMutationsReturnValidationErrorsInsteadOfUnhandledExceptions(string reader)
    {
        var id = new string('a', 32); var target = new EndpointReference("device", "led1");
        var config = ServiceRuntimeBootstrap.MonitoringConfiguration(new('d', 32));
        var light = new LightingProgram("fixed", [new(1, 2, 3)]);
        config = config with { Devices = new(2, [new("device", "Light", "demo", DeviceKind.Controller,
            [new("led1", "LED", EndpointKind.Lighting)])]),
            Lighting = config.Lighting with { Preferences = [new(target, light)], Groups = [new("lights", "Lights", EndpointKind.Lighting, [target])] } };
        var hello = new DeviceServiceHello(2, id, id, 12, []);
        var state = new DeviceServiceState(new(id, 1, [new(new("device", "demo", "Light", DeviceKind.Controller,
            IdentityConfidence.SerialBound, "serial", "definition", ["usb"], [new("led1", "LED", EndpointKind.Lighting,
                [new(DeviceFeature.FixedColor, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)])]), 1, true)], 1),
            [new(target, 1, 1, Outcome.WrittenUnverified, RequestedLighting: light, ReportedLighting: light, Owned: true)], false, null);
        object original = reader switch {
            "configuration" => config,
            "archive" => DeviceSettingsArchives.Create(config, false),
            "request" => new DeviceServiceRequest(2, id, id, id, 1, "test", DeviceOperation.SetLighting, 1, [new(target, 1, 1)], Lighting: light),
            _ => new DeviceServiceReply(2, id, id, id, true, State: state)
        };
        void Read(byte[] bytes)
        {
            switch (reader)
            {
                case "configuration": _ = DeviceConfigurationCodec.DeserializePackage(bytes); break;
                case "archive": _ = DeviceSettingsArchives.Read(bytes); break;
                case "request": _ = DeviceServiceProtocol.Read(bytes); break;
                default: _ = DeviceServiceProtocol.ReadStateSnapshot(bytes, hello); break;
            }
        }
        var root = JsonSerializer.SerializeToNode(original, original.GetType(), Contract.Json)!;
        Read(JsonSerializer.SerializeToUtf8Bytes(root));
        var variants = new[] { "null", "{}", "[]", "true", "-2147483648", "2147483647", "1.5", "\"unexpected\"" };
        var accepted = 0; var rejected = 0;
        foreach (var path in Paths(root, []))
        foreach (var variant in variants)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(Replace(root, path, JsonNode.Parse(variant)));
            var error = Record.Exception(() => Read(bytes));
            Assert.True(error is null or IOException, $"{reader} /{string.Join('/', path)} = {variant}: {error}");
            if (error is null) accepted++; else rejected++;
        }
        Assert.True(accepted + rejected >= 100);
        output.WriteLine($"MUTATIONS {reader}: total={accepted + rejected}, accepted={accepted}, rejected={rejected}");
    }
}
