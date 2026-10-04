namespace FlowDeck.Core;

// Descriptive constraints from compiled drivers, never executable setup instructions.
public sealed record EndpointSetupOptions(string EndpointId, CoolingExitAction[] CoolingExitActions,
    LightingExitAction[] LightingExitActions, int? MinimumLeds = null, int? MaximumLeds = null,
    int? DetectedLeds = null, string? Accessory = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool CoolingStopSupported = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool CoolingCalibrationSupported = false);
public sealed record DeviceSetupCandidate(EndpointReference Target, long Generation, EndpointSetupOptions Options);
public interface IDeviceSetupDriver { EndpointSetupOptions[] DescribeSetup(); }
public static class DeviceSetup
{
    public const int MaximumCandidates = DeviceRegistry.MaximumDevices * DeviceRegistry.MaximumEndpoints;
    public static bool ValidCandidates(DeviceSetupCandidate[]? candidates) => candidates is { Length: <= MaximumCandidates } &&
        candidates.All(Valid) && candidates.DistinctBy(c => c.Target).Count() == candidates.Length &&
        candidates.GroupBy(c => c.Target.DeviceId).Count() <= DeviceRegistry.MaximumDevices &&
        candidates.GroupBy(c => c.Target.DeviceId).All(g => g.Count() <= DeviceRegistry.MaximumEndpoints && g.Select(c => c.Generation).Distinct().Count() == 1);
    public static bool Valid(DeviceSetupCandidate? c) => c is { Generation: > 0, Options: { } o } &&
        DeviceConfigurationRules.ValidReference(c.Target) && c.Target.EndpointId == o.EndpointId &&
        o.CoolingExitActions is { Length: <= 2 } && o.CoolingExitActions.All(Enum.IsDefined) && o.CoolingExitActions.Distinct().Count() == o.CoolingExitActions.Length &&
        o.LightingExitActions is { Length: <= 3 } && o.LightingExitActions.All(Enum.IsDefined) && o.LightingExitActions.Distinct().Count() == o.LightingExitActions.Length &&
        (o.CoolingExitActions.Length > 0 ? o.LightingExitActions.Length == 0 && o.MinimumLeds is null && o.MaximumLeds is null && o.DetectedLeds is null && o.Accessory is null :
            !o.CoolingStopSupported && !o.CoolingCalibrationSupported && o.LightingExitActions.Length > 0 && o.MinimumLeds is >= 1 and <= 4096 && o.MaximumLeds >= o.MinimumLeds && o.MaximumLeds <= 4096 &&
            (o.DetectedLeds is null || o.DetectedLeds >= o.MinimumLeds && o.DetectedLeds <= o.MaximumLeds) &&
            (o.Accessory is null || DeviceConfigurationRules.Text(o.Accessory, 256)));
}
