using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceFirstUseResult(int PreparedLighting, string? Deferred = null);

public sealed partial class DeviceWorkspaceClient
{
    private int firstUseAttempted;

    /// <summary>Read service-owned readiness on launch. Opening the GUI never saves setup or approves a port.</summary>
    public async Task<DeviceFirstUseResult> PrepareFirstUseAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref firstUseAttempted, 1) != 0 || !SupportsEndpointSetup) return new(0);
        try
        {
            if (client.Hello?.Features.Contains(DeviceCurrentConnectionControl.Feature) != true)
                return new(0, "現在の接続から操作を準備できるサービスへ更新してください");
            await RefreshAsync(true, ct).ConfigureAwait(false);
            var snapshot = Snapshot!;
            if (!snapshot.Connected || snapshot.ExitAttempted || snapshot.State.Releasing || snapshot.State.ConfigurationChanging ||
                snapshot.ElapsedSeconds is not (>= 0 and < 3) || snapshot.State.LifecycleError is not null)
                return new(0, snapshot.State.LifecycleError);
            var available = snapshot.State.Inventory.Devices.Where(d => d.Connected).Sum(d => d.Device.Endpoints.Count(e =>
                e.Kind == EndpointKind.Lighting && e.Capabilities.Any(c => c.Feature == DeviceFeature.FixedColor && c.Usable) &&
                snapshot.State.Endpoints.Any(s => s.Target == new EndpointReference(d.Device.Id, e.Id) && s.Generation == d.Generation)));
            return new(available);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            return new(0, e.Message);
        }
    }
}
