using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Tests;

public sealed class DeviceSnapshotCopyTests
{
    private static DeviceObservation Sample() => new("device", "provider", "機器", DeviceKind.Controller,
        IdentityConfidence.SerialBound, "usb:demo", "definition", ["usb:one", "shared"], [
            new("fan", "ファン", EndpointKind.Cooling, [new(DeviceFeature.FixedDuty, ImplementationSupport.Supported,
                VerificationEvidence.Automated, DeviceAvailability.Ready, "設定済み")], Role: "fan"),
            new("rgb", "照明", EndpointKind.Lighting, [new(DeviceFeature.NativeEffect, ImplementationSupport.Supported,
                VerificationEvidence.MatchingHardware, DeviceAvailability.Ready)], NativeLightingEffects: [
                    new("native/test/effect", "効果", true, 1, 3, 0, 4, 2, Direction: true, LedCount: 27)])
        ]);

    [Theory]
    [InlineData("normal")] [InlineData("null")] [InlineData("null-domains")] [InlineData("null-endpoints")]
    [InlineData("null-endpoint")] [InlineData("null-capabilities")] [InlineData("null-capability")]
    [InlineData("null-native-effect")] [InlineData("empty-arrays")] [InlineData("unknown-enums")]
    [InlineData("text-null")] [InlineData("text-control")] [InlineData("text-surrogates")]
    public void CopyMatchesThePreviousJsonProjectionIncludingMalformedObservations(string kind)
    {
        var input = Sample();
        input = kind switch {
            "null" => null!, "null-domains" => input with { ResourceDomains = null! },
            "null-endpoints" => input with { Endpoints = null! }, "null-endpoint" => input with { Endpoints = [null!] },
            "null-capabilities" => input with { Endpoints = [input.Endpoints[0] with { Capabilities = null! }] },
            "null-capability" => input with { Endpoints = [input.Endpoints[0] with { Capabilities = [null!] }] },
            "null-native-effect" => input with { Endpoints = [input.Endpoints[1] with { NativeLightingEffects = [null!] }] },
            "empty-arrays" => input with { ResourceDomains = [], Endpoints = [] },
            "unknown-enums" => input with { Kind = (DeviceKind)999, IdentityConfidence = (IdentityConfidence)(-1),
                Endpoints = [input.Endpoints[0] with { Kind = (EndpointKind)99, Capabilities = [new((DeviceFeature)98,
                    (ImplementationSupport)97, (VerificationEvidence)96, (DeviceAvailability)95)] }] },
            "text-null" => TextEverywhere(input, null!), "text-control" => TextEverywhere(input, "\0\n\t"),
            "text-surrogates" => TextEverywhere(input, "前\ud800後\udc00\ud83d\ude00"), _ => input
        };
        var expected = Contract.Copy(input); var actual = DeviceSnapshotCopy.Observation(input);
        Assert.Equal(JsonSerializer.Serialize(expected, Contract.Json), JsonSerializer.Serialize(actual, Contract.Json));
        DetachedArrays(input, actual);
    }

    private static DeviceObservation TextEverywhere(DeviceObservation input, string text) => input with {
        Id = text, Provider = text, Name = text, Locator = text, DefinitionId = text, ResourceDomains = [text],
        Endpoints = input.Endpoints.Select(e => e with { Id = text, Name = text, SensorId = text, Role = text,
            Capabilities = e.Capabilities.Select(c => c with { Reason = text }).ToArray(),
            NativeLightingEffects = e.NativeLightingEffects?.Select(n => n with { Id = text, Name = text }).ToArray() }).ToArray()
    };

    private static void DetachedArrays(object? original, object? copied)
    {
        if (original is null || original is string || original.GetType().IsValueType) return;
        Assert.NotNull(copied);
        if (original is Array array)
        {
            // Empty arrays have no elements a caller could change and may use Array.Empty<T>().
            if (array.Length > 0) Assert.NotSame(array, copied);
            var target = Assert.IsAssignableFrom<Array>(copied); Assert.Equal(array.Length, target.Length);
            for (var i = 0; i < array.Length; i++) DetachedArrays(array.GetValue(i), target.GetValue(i));
        }
        else foreach (var property in original.GetType().GetProperties()) DetachedArrays(property.GetValue(original), property.GetValue(copied));
    }

    [Fact]
    public void CallerMutationsCannotAlterRetainedCatalogOrAnyLaterSnapshot()
    {
        var input = Sample(); var expected = Contract.Copy(input); var registry = new DeviceRegistry();
        var first = registry.Reconcile([input]); var generation = first.Devices[0].Generation;
        input.ResourceDomains[0] = "changed";
        input.Endpoints[0].Capabilities[0] = input.Endpoints[0].Capabilities[0] with { Availability = DeviceAvailability.Failed };
        input.Endpoints[1].NativeLightingEffects![0] = input.Endpoints[1].NativeLightingEffects![0] with { LedCount = 1 };
        input.Endpoints[0] = input.Endpoints[0] with { Id = "changed" };
        Assert.Equal(JsonSerializer.Serialize(expected, Contract.Json), JsonSerializer.Serialize(first.Devices[0].Device, Contract.Json));
        first.Devices[0].Device.ResourceDomains[0] = "snapshot-change";
        first.Devices[0].Device.Endpoints[1].NativeLightingEffects![0] = first.Devices[0].Device.Endpoints[1].NativeLightingEffects![0] with { Name = "changed" };
        first.Devices[0].Device.Endpoints[0].Capabilities[0] = first.Devices[0].Device.Endpoints[0].Capabilities[0] with { Feature = DeviceFeature.Rpm };
        var later = registry.Snapshot;
        Assert.Equal(JsonSerializer.Serialize(expected, Contract.Json), JsonSerializer.Serialize(later.Devices[0].Device, Contract.Json));
        Assert.Equal(generation, later.Devices[0].Generation);
        Assert.Equal(generation, registry.Reconcile([expected]).Devices[0].Generation);
    }

    [Fact]
    public void InvalidIncomingInventoryCannotPartlyReplaceAnExistingGeneration()
    {
        var original = Sample(); var registry = new DeviceRegistry(); var first = registry.Reconcile([original]);
        var changed = original with { Locator = "new-port" };
        Assert.Throws<ArgumentException>(() => registry.Reconcile([changed, original with { Id = "other", Endpoints = [null!] }]));
        Assert.Equal(JsonSerializer.Serialize(first, Contract.Json), JsonSerializer.Serialize(registry.Snapshot, Contract.Json));
    }
}
