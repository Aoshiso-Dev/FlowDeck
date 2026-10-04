using System.Diagnostics.CodeAnalysis;

namespace FlowDeck.Core;

// Observation records contain immutable scalar leaves and mutable arrays. Copy the arrays at every
// boundary without serializing the entire catalog during each frame's authorization checks.
internal static class DeviceSnapshotCopy
{
    public static DeviceObservation Observation(DeviceObservation value) => value is null ? null! : value with {
        Id = Text(value.Id), Provider = Text(value.Provider), Name = Text(value.Name), Locator = Text(value.Locator),
        DefinitionId = Text(value.DefinitionId), ResourceDomains = value.ResourceDomains?.Select(s => Text(s)!).ToArray()!,
        Endpoints = value.Endpoints?.Select(Endpoint).ToArray()!
    };

    public static DeviceInstance Instance(DeviceInstance value) => value with { Device = Observation(value.Device) };

    private static DeviceEndpoint Endpoint(DeviceEndpoint value) => value is null ? null! : value with {
        Id = Text(value.Id), Name = Text(value.Name), SensorId = Text(value.SensorId), Role = Text(value.Role),
        Capabilities = value.Capabilities?.Select(Capability).ToArray()!,
        NativeLightingEffects = value.NativeLightingEffects?.Select(NativeEffect).ToArray()
    };

    private static DeviceCapability Capability(DeviceCapability value)
    {
        if (value is null) return null!;
        var reason = Text(value.Reason);
        return ReferenceEquals(reason, value.Reason) ? value : value with { Reason = reason };
    }

    private static DeviceNativeLightingEffect NativeEffect(DeviceNativeLightingEffect value)
    {
        if (value is null) return null!;
        var id = Text(value.Id); var name = Text(value.Name);
        return ReferenceEquals(id, value.Id) && ReferenceEquals(name, value.Name) ? value : value with { Id = id, Name = name };
    }

    [return: NotNullIfNotNull(nameof(value))]
    private static string? Text(string? value) => value is null || value.AsSpan().IndexOfAnyInRange('\ud800', '\udfff') < 0
        ? value : Contract.Copy(value); // Preserve JSON's existing treatment of UTF-16 surrogate sequences.
}
