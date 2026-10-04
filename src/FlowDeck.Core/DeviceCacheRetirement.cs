namespace FlowDeck.Core;

// Compiled provider boundary. Called only after the runtime drains operations and confirms release.
// Remove the exact cached object only after its disposal succeeds. Never delete recovery files,
// recreate a driver, close shared parent resources, or throw cancellation after removing it.
public interface IDeviceCacheRetirement : IDeviceDriverProvider
{
    // Cached result of validated provider discovery, not a driver read failure.
    // Invalidate before each discovery attempt. A capacity-only refusal may retain this proof.
    bool IsConfirmedAbsent(IDeviceControlDriver driver);
    Task RetireDisconnectedAsync(IDeviceControlDriver driver, CancellationToken ct);
}

public sealed record DeviceCacheRetirementTarget(string DeviceId, long Generation)
{
    public const string Feature = "device-cache-retirement-v2";
    public static bool Valid(DeviceCacheRetirementTarget? target) => target is not null &&
        DeviceConfigurationRules.Key(target.DeviceId) && target.Generation > 0;
}
public interface IDeviceCacheRetirementHandler : IDeviceConfigurationHandler { }

// Complete discovery succeeded; new drivers cannot fit in the bounded provider cache.
// Existing connections and verified absences remain usable for explicit retirement.
public sealed class DeviceDiscoveryCapacityException(string message) : IOException(message);
