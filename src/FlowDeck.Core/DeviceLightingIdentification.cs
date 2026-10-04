namespace FlowDeck.Core;

public enum LightingIdentificationPhase { Preparing, Lighting, Restoring, Finished }
public sealed record LightingIdentificationStatus(string RequestId, LightingIdentificationPhase Phase, bool Restored = false, bool Cancelled = false, string Message = "");
public static class DeviceLightingIdentification
{
    public const string Feature = "lighting-identification-v2";
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(3);
    public static LightingProgram White => new("fixed", [new(255, 255, 255)]);
    public static bool Valid(LightingIdentificationStatus? value) => value is null || DeviceServiceProtocol.Id(value.RequestId) &&
        Enum.IsDefined(value.Phase) && value.Message is { Length: <= 16384 } &&
        (!(value.Restored || value.Cancelled) || value.Phase == LightingIdentificationPhase.Finished);
    public static bool Running(LightingIdentificationStatus? value) => value is { Phase: not LightingIdentificationPhase.Finished };
    public static string? Problem(DeviceServiceState state, EndpointReference target)
    {
        if (state.Releasing || state.ConfigurationChanging || state.LifecycleError is not null) return state.LifecycleError ?? "サービスの処理が完了するまでお待ちください";
        var device = state.Inventory.Devices.SingleOrDefault(d => d.Device.Id == target.DeviceId);
        var endpoint = device?.Device.Endpoints.SingleOrDefault(e => e.Id == target.EndpointId && e.Kind == EndpointKind.Lighting);
        if (device?.Connected != true || endpoint is null) return "照明へ接続してください";
        if (endpoint.Capabilities.Any(c => c.Feature == DeviceFeature.FixedColor && c.Usable) != true) return "この照明は固定色の識別に対応していません";
        var others = state.Endpoints.Where(e => e.Target.DeviceId == target.DeviceId).ToArray();
        if (others.Any(e => Running(e.Identification))) return "この機器の識別が終了するまでお待ちください";
        if (others.Any(e => e.Lighting?.Running == true)) return "この機器の同期を終了し、照明を適用してから識別してください";
        var current = others.SingleOrDefault(e => e.Target == target);
        if (current is null || current.Generation != device.Generation || !current.Owned || current.Outcome is not (Outcome.Confirmed or Outcome.WrittenUnverified) ||
            !DeviceConfigurationRules.ValidProgram(current.RequestedLighting) || DeviceLighting.Animated(current.RequestedLighting))
            return "元に戻す照明を一度適用してください";
        return null;
    }
}
