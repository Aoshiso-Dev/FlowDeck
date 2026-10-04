using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed partial class EneMemoryDeviceDriver : IDeviceConnectionLightingDriver
{
    private DeviceConnectionLightingPermission? connectionPermission;
    private long observedAt;
    public bool UsesConnectionLighting { get { lock (gate) return connectionPermission is not null; } }
    private bool ConnectionPermissionCurrent()
    {
        if (connectionPermission is null) return true;
        if (!connectionPermission.IsCurrent || connection is null) return false;
        try { backend.Check(connection); return true; }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { connectionPermission.Revoke(); return false; }
    }
    private void CheckConnectionPermission()
    {
        lock (gate) if (!ConnectionPermissionCurrent())
            throw new IOException("接続限定の照明設定が失効しました。現在の接続で再準備してください");
    }
    public bool TryPrepareConnectionLighting(DeviceConnectionLightingPermission permission)
    {
        if (!io.Wait(0)) return false;
        try
        {
            lock (gate)
            {
                if (disposed || connection is not { } current || observed is null || readError is not null || journalError is not null ||
                    journal?.Pending is not null || active is not null || nativeApplying || owned || faulted || observed.Control[0] != 0 ||
                    System.Diagnostics.Stopwatch.GetElapsedTime(observedAt).TotalSeconds is not (>= 0 and < 2) ||
                    permission.Target != new EndpointReference(DeviceId, Endpoint) || permission.Binding != Candidate() || !permission.IsCurrent ||
                    connectionPermission is null && (binding is not null || configuration is not null || setup is not null) ||
                    backend is not EneMemoryDeviceBackend evidence) return false;
                // The backend lock makes publishing permission atomic with discovery/invalidation. No bus or file I/O.
                evidence.WithPreparationEvidence(current, () =>
                {
                    if (!permission.IsCurrent) throw new IOException("準備中に接続限定の許可が失効しました");
                    connectionPermission?.Revoke(); connectionPermission = permission;
                    binding = permission.Binding;
                    configuration = new(permission.Target, true, new(LightingExitAction.RestoreOriginal));
                    setup = new(LedCount: 8, LedCountConfirmed: true);
                });
                return true;
            }
        }
        finally { io.Release(); }
    }
}
