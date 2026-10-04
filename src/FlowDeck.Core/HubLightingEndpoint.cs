namespace FlowDeck.Core;

// The worker session/generation is also checked by the transport. The wiring signature
// includes the LED accessories captured at initialization; it is not live LED readback.
public sealed record HubLightingExpectation(string HubId, string WiringSignature, long? ValidUntilTick64 = null);

public interface IHubLightingEndpointAdapter : IHubEndpointAdapter
{
    bool SupportsLightingEndpoints { get; }
    bool SupportsLightingDeadlines { get; }
    bool SupportsSmoothLightingFrames => false;
    Task<HubResult> SetLightingEndpointsAsync(string[] channels, HubLightSetting effect,
        HubLightingExpectation expected, CancellationToken ct = default);
}
