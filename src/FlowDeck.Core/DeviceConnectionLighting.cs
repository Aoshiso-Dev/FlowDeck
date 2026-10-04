namespace FlowDeck.Core;

// This permission is created only in the authenticated runtime. It is never serialized or imported.
public sealed class DeviceConnectionLightingPermission
{
    private readonly CancellationToken session;
    private readonly Func<bool> configurationCurrent;
    private int revoked;
    public EndpointReference Target { get; }
    public PhysicalDeviceBinding Binding { get; }
    public long Generation { get; }
    internal DeviceConnectionLightingPermission(EndpointReference target, PhysicalDeviceBinding binding, long generation,
        CancellationToken session, Func<bool> configurationCurrent)
    { Target = target; Binding = binding; Generation = generation; this.session = session; this.configurationCurrent = configurationCurrent; }
    public bool IsCurrent
    {
        get
        {
            if (Volatile.Read(ref revoked) != 0) return false;
            try { if (!session.IsCancellationRequested && configurationCurrent()) return true; }
            catch { /* Missing proof is never permission. */ }
            Revoke(); return false;
        }
    }
    public void Revoke() => Interlocked.Exchange(ref revoked, 1);
}

// Opt-in by a compiled driver that can verify a connection generation without hardware writes.
public interface IDeviceConnectionLightingDriver : IPhysicalDeviceDriver
{
    bool UsesConnectionLighting { get; }
    bool TryPrepareConnectionLighting(DeviceConnectionLightingPermission permission);
}
public interface IDeviceConnectionLightingHandler : IDeviceConfigurationHandler { }
public static class DeviceConnectionLighting
{
    public const string Feature = "connection-lighting-preparation-v2";
}
