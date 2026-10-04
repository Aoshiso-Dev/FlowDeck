namespace FlowDeck.Core;

// Created from authenticated, current runtime results. These are chosen programs, not optical measurements
// or transferable hardware ownership. Stream frames must never replace their unscaled base colors.
public sealed record LightingPolicyChange(long Order, SynchronizedLighting[] Parts, PhysicalDeviceBinding[] Bindings,
    LightingAdjustmentPlan? Adjustment = null, EndpointLighting[][]? FrameGroups = null);
public sealed record LightingPolicySaveResult(EndpointReference Target, DevicePolicySaveResult Result);
public interface IDeviceLightingPersistence
{
    Task<LightingPolicySaveResult[]> SaveLightingPoliciesAsync(LightingPolicyChange change);
}
