using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DetectedLightingSetup(int LedCount, LightingExitAction Exit);

/// <summary>First-use setup from a compiled driver's detected count, never from its allowed range or a saved mismatch.</summary>
public static class DeviceDetectedLightingSetup
{
    public static EndpointReference[] AutomaticTargets(DeviceWorkspaceSnapshot snapshot) => (snapshot.Configuration.Setup ?? [])
        .Where(c => snapshot.State.Inventory.Devices.Any(d => d.Device.Id == c.Target.DeviceId && d.Connected &&
            d.Device.IdentityConfidence == IdentityConfidence.SerialBound) && Describe(snapshot, c.Target) is not null)
        .Select(c => c.Target).Take(DeviceControlCapacity.MaximumLighting).ToArray();

    public static DetectedLightingSetup? Describe(DeviceWorkspaceSnapshot snapshot, EndpointReference target)
    {
        if (!snapshot.Connected || snapshot.ExitAttempted || snapshot.State.Releasing || snapshot.State.ConfigurationChanging ||
            snapshot.ElapsedSeconds is not (>= 0 and < 3) || snapshot.State.LifecycleError is not null ||
            snapshot.Configuration.TopologyRevision is { } topology && topology != snapshot.State.Inventory.TopologyRevision) return null;
        var config = snapshot.Configuration.Configuration;
        if ((config.Lighting.Endpoints ?? []).Any(e => e.Target == target)) return null;
        var registered = config.Devices.Devices.SingleOrDefault(d => d.Id == target.DeviceId)?.Endpoints.SingleOrDefault(e => e.Id == target.EndpointId);
        // A cleared/imported/removed setup is not a never-configured port. Do not silently reapprove it.
        if (registered?.Setup is not null) return null;
        var current = snapshot.State.Inventory.Devices.SingleOrDefault(d => d.Device.Id == target.DeviceId);
        var endpoint = current?.Device.Endpoints.SingleOrDefault(e => e.Id == target.EndpointId && e.Kind == EndpointKind.Lighting);
        if (current?.Connected != true || endpoint?.Capabilities.Any(c => c.Feature == DeviceFeature.FixedColor &&
                c.Support == ImplementationSupport.Supported && c.Availability == DeviceAvailability.NeedsSetup) != true) return null;
        var state = snapshot.State.Endpoints.SingleOrDefault(e => e.Target == target);
        if (state is null || state.Generation != current.Generation || state.Owned || state.Lighting?.Fault is not null ||
            state.Lighting?.Running == true || DeviceLightingIdentification.Running(state.Identification)) return null;
        var candidate = snapshot.Configuration.Candidates.SingleOrDefault(b => b.DeviceId == target.DeviceId);
        var savedBinding = config.Bindings.Devices.SingleOrDefault(b => b.DeviceId == target.DeviceId);
        if (candidate is null || savedBinding is not null && savedBinding != candidate) return null;
        if (savedBinding is null && (config.Cooling.Endpoints.Any(e => e.Target.DeviceId == target.DeviceId) ||
            (config.Lighting.Endpoints ?? []).Any(e => e.Target.DeviceId == target.DeviceId))) return null;
        var descriptor = snapshot.Configuration.Setup?.SingleOrDefault(c => c.Target == target);
        if (!DeviceSetup.Valid(descriptor) || descriptor!.Generation != current.Generation || descriptor.Options.CoolingExitActions.Length != 0 ||
            descriptor.Options.DetectedLeds is not { } count) return null;
        var exit = descriptor.Options.LightingExitActions.Contains(LightingExitAction.RestoreOriginal) ? LightingExitAction.RestoreOriginal : LightingExitAction.Off;
        if (!descriptor.Options.LightingExitActions.Contains(exit) ||
            DeviceControlCapacity.ReplacementProblem(config.Devices, config.Cooling, config.Lighting, target, false, count) is not null) return null;
        return new(count, exit);
    }

    public static DeviceEndpointSetupDraft? Create(DeviceWorkspaceSnapshot snapshot, EndpointReference target)
    {
        if (Describe(snapshot, target) is not { } detected) return null;
        try
        {
            var draft = new DeviceEndpointSetupDraft(snapshot, target);
            draft.Change(draft.Values with { Confirmed = true, LedCount = detected.LedCount, LightingExit = new(detected.Exit) });
            return draft.Problem is null ? draft : null;
        }
        catch (IOException) { return null; }
    }

    public static string Description(DetectedLightingSetup detected) => $"LED {detected.LedCount}個を検出済み · 終了時は" +
        (detected.Exit == LightingExitAction.RestoreOriginal ? "元の設定へ戻す" : "消灯");
}
