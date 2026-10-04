using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceConfigurationAuthority
{
    public LightingAdjustmentPlan ResolveLightingAdjustment(LightingAdjustmentRequest request, EndpointReference[] targets)
    {
        var view = Volatile.Read(ref lightingLibrary);
        if (failure is not null || request.ExpectedGeneration != view.Generation) throw new IOException(failure ?? "照明設定が変更されました。再取得してください");
        return DeviceLightingAdjustments.Resolve(view.Lighting, view.Registered, request, targets);
    }
    public string? CheckLightingAdjustment(LightingAdjustmentPlan plan)
    {
        var view = Volatile.Read(ref lightingLibrary);
        return failure ?? (view.Lighting.Revision != plan.LibraryRevision || plan.Entries.Any(e => !DeviceLightingAdjustments.Matches(view.Lighting, e))
            ? "明るさの基準・対象が変更されました。再取得してください" : null);
    }
}
