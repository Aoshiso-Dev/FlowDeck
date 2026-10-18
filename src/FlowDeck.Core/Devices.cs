using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlowDeck.Core;

public enum DeviceKind { Processor, Graphics, Motherboard, Controller, Memory, Other }
public enum EndpointKind { Sensor, Cooling, Lighting }
public enum DeviceFeature { Temperature, Rpm, DutyRead, FixedDuty, AutomaticDuty, ReleaseCooling, FixedColor, NativeEffect, ColorFrames, RestoreLighting }
public enum ImplementationSupport { Unknown, Unsupported, Candidate, Experimental, Supported }
public enum VerificationEvidence { None, SourceReview, Automated, FamilyHardware, MatchingHardware }
public enum DeviceAvailability { Ready, NeedsSetup, NeedsPermission, Conflict, Disconnected, Failed, TemporarilyUnavailable }
public enum IdentityConfidence { ConnectionBound, SerialBound }
public sealed record DeviceCapability(DeviceFeature Feature, ImplementationSupport Support,
    VerificationEvidence Evidence, DeviceAvailability Availability, string? Reason = null)
{
    public bool Usable => Support == ImplementationSupport.Supported && Availability == DeviceAvailability.Ready;
}
public sealed record DeviceEndpoint(string Id, string Name, EndpointKind Kind, DeviceCapability[] Capabilities,
    string? SensorId = null, string? Role = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeviceNativeLightingEffect[]? NativeLightingEffects = null);
public sealed record DeviceObservation(string Id, string Provider, string Name, DeviceKind Kind,
    IdentityConfidence IdentityConfidence, string Locator, string? DefinitionId, string[] ResourceDomains, DeviceEndpoint[] Endpoints);
public sealed record DeviceInstance(DeviceObservation Device, long Generation, bool Connected);
public sealed record DeviceInventory(string Epoch, long Revision, DeviceInstance[] Devices, long TopologyRevision = 0);
public sealed record DeviceCommandTarget(string Epoch, string DeviceId, string EndpointId, long Generation, DeviceFeature Feature);

public static class DeviceIdentity
{
    // The installation scope prevents imported settings from becoming live device bindings on another PC.
    // Provider-native identity must be a stable serial/physical identity, never a list index or display name.
    public static string Create(string installation, string provider, string nativeIdentity)
    {
        if (!Guid.TryParseExact(installation, "N", out _) || string.IsNullOrWhiteSpace(provider) || provider.Length > 64 ||
            provider.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) ||
            string.IsNullOrWhiteSpace(nativeIdentity) || nativeIdentity.Length > 2048)
            throw new ArgumentException("機器の識別情報が不正です");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { installation, provider, nativeIdentity });
        return provider + ":" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

/// <summary>Pure catalog. Generation follows topology and connections, not display names or telemetry.</summary>
public sealed class DeviceRegistry
{
    public const int MaximumDevices = 32, MaximumEndpoints = 256;
    private readonly object gate = new();
    private readonly Dictionary<string, (DeviceInstance Instance, string Fingerprint)> devices = new(StringComparer.Ordinal);
    private readonly string epoch = Guid.NewGuid().ToString("N");
    private long revision, nextGeneration, topologyRevision;
    public DeviceInventory Snapshot { get { lock (gate) return CopySnapshot(); } }
    private DeviceInventory CopySnapshot() => new(epoch, revision, devices.Values.Select(d => DeviceSnapshotCopy.Instance(d.Instance)).OrderBy(d => d.Device.Id).ToArray(), topologyRevision);
    public DeviceInventory Reconcile(IEnumerable<DeviceObservation> observations)
    {
        // Validate/copy the complete inventory before mutating any remembered state.
        var incoming = observations.Take(MaximumDevices + 1).Select(DeviceSnapshotCopy.Observation).ToArray();
        if (incoming.Length > MaximumDevices || incoming.Any(d => !Valid(d)) || incoming.DistinctBy(d => d.Id).Count() != incoming.Length)
            throw new ArgumentException("機器一覧の形式・個数が不正です");
        lock (gate)
        {
            if (incoming.Select(d => d.Id).Union(devices.Keys).Count() > MaximumDevices)
                throw new InvalidOperationException("接続中・切断済みの機器数が32台の上限を超えています");
            var seen = incoming.Select(d => d.Id).ToHashSet(StringComparer.Ordinal); var beforeGeneration = nextGeneration;
            foreach (var d in incoming)
            {
                var fingerprint = Fingerprint(d);
                var previous = devices.GetValueOrDefault(d.Id);
                var generation = previous.Instance is null || !previous.Instance.Connected || previous.Fingerprint != fingerprint
                    ? ++nextGeneration : previous.Instance.Generation;
                devices[d.Id] = (new(d, generation, true), fingerprint);
            }
            foreach (var id in devices.Keys.Where(id => !seen.Contains(id)).ToArray())
            {
                var old = devices[id];
                if (old.Instance.Connected) devices[id] = (old.Instance with { Connected = false, Generation = ++nextGeneration }, old.Fingerprint);
            }
            if (beforeGeneration != nextGeneration) topologyRevision++;
            revision++;
            return CopySnapshot();
        }
    }
    public bool ForgetDisconnected(string id)
    {
        lock (gate)
        {
            if (!devices.TryGetValue(id, out var d) || d.Instance.Connected) return false;
            devices.Remove(id); revision++; topologyRevision++; return true;
        }
    }
    public string? Check(DeviceCommandTarget target)
    {
        lock (gate)
        {
            if (target.Epoch != epoch) return "以前の接続からの操作です";
            if (!devices.TryGetValue(target.DeviceId, out var d) || !d.Instance.Connected) return "対象機器が切断されています";
            if (d.Instance.Generation != target.Generation) return "対象機器の構成が変更されています";
            var endpoint = d.Instance.Device.Endpoints.SingleOrDefault(e => e.Id == target.EndpointId);
            var capability = endpoint?.Capabilities.SingleOrDefault(c => c.Feature == target.Feature);
            if (capability is null || capability.Support != ImplementationSupport.Supported) return "この操作に対応していません";
            return capability.Availability == DeviceAvailability.Ready ? null : capability.Reason ?? "現在この操作を利用できません";
        }
    }
    private static bool Text(string? text, int max) => !string.IsNullOrWhiteSpace(text) && text.Length <= max && !text.Any(char.IsControl);
    internal static bool Valid(DeviceObservation d) => d is not null && Text(d.Id, 160) && Text(d.Provider, 64) && Text(d.Name, 160) &&
        Enum.IsDefined(d.Kind) && Enum.IsDefined(d.IdentityConfidence) && Text(d.Locator, 2048) &&
        (d.DefinitionId is null || Text(d.DefinitionId, 160)) &&
        d.ResourceDomains is { Length: > 0 and <= 16 } && d.ResourceDomains.All(r => Text(r, 256)) && d.ResourceDomains.Distinct().Count() == d.ResourceDomains.Length &&
        d.Endpoints is { Length: <= MaximumEndpoints } && d.Endpoints.DistinctBy(e => e?.Id).Count() == d.Endpoints.Length &&
        d.Endpoints.All(e => e is not null && Text(e.Id, 160) && Text(e.Name, 160) && Enum.IsDefined(e.Kind) &&
            (e.SensorId is null || Text(e.SensorId, 512)) && (e.Role is null || Text(e.Role, 32)) &&
            (e.NativeLightingEffects is null || e.Kind == EndpointKind.Lighting && e.NativeLightingEffects.Length <= 32 &&
                e.NativeLightingEffects.All(DeviceNativeLighting.Valid) && e.NativeLightingEffects.DistinctBy(n => n.Id).Count() == e.NativeLightingEffects.Length) &&
            e.Capabilities is { Length: > 0 and <= 16 } && e.Capabilities.All(c => c is not null && Enum.IsDefined(c.Feature) && Enum.IsDefined(c.Support) &&
                Enum.IsDefined(c.Evidence) && Enum.IsDefined(c.Availability) && (c.Reason is null || Text(c.Reason, 512))) &&
            e.Capabilities.DistinctBy(c => c.Feature).Count() == e.Capabilities.Length &&
            e.Capabilities.All(c => e.Kind switch
            {
                EndpointKind.Sensor => c.Feature is DeviceFeature.Temperature or DeviceFeature.Rpm or DeviceFeature.DutyRead,
                EndpointKind.Cooling => c.Feature is DeviceFeature.Rpm or DeviceFeature.DutyRead or DeviceFeature.FixedDuty or DeviceFeature.AutomaticDuty or DeviceFeature.ReleaseCooling,
                EndpointKind.Lighting => c.Feature is DeviceFeature.FixedColor or DeviceFeature.NativeEffect or DeviceFeature.ColorFrames or DeviceFeature.RestoreLighting,
                _ => false
            }));
    private static string Fingerprint(DeviceObservation d)
    {
        // Readiness and names may change every poll; transport, definition and endpoint capability changes invalidate commands.
        var structural = new { d.Provider, d.Kind, d.IdentityConfidence, d.Locator, d.DefinitionId,
            Domains = d.ResourceDomains.Order().ToArray(), Endpoints = d.Endpoints.OrderBy(e => e.Id).Select(e => new
            { e.Id, e.Kind, e.SensorId, NativeEffects = e.NativeLightingEffects?.OrderBy(n => n.Id).Select(n => n with { Name = "" }).ToArray(),
                Features = e.Capabilities.OrderBy(c => c.Feature).Select(c => new { c.Feature, c.Support, c.Evidence }).ToArray() }).ToArray() };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(structural, Contract.Json)));
    }
}
