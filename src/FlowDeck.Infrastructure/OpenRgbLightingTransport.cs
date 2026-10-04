using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// Explicit stop intent. Null at the transport boundary preserves the durable native exit.
// Do not reconstruct Off/RestoreOriginal from an SDK palette; they may have identical colors.
internal sealed record OpenRgbLightingExitIntent(int? Zone, LightingExitPolicy? Policy, Dictionary<int, LightingExitPolicy>? Zones = null);

internal interface IOpenRgbLightingTransport
{
    bool NeedsRecovery { get; }
    bool HasPendingWrite { get; }
    Task TrackAsync(OpenRgbRecoveryRecord record, Action authorize, CancellationToken ct) { authorize(); return Task.CompletedTask; }
    Task SendAsync(OpenRgbSdkController controller, OpenRgbLightingStep step, OpenRgbLightingExitIntent? exit,
        Action authorize, Action beforeWrite, CancellationToken ct);
}
