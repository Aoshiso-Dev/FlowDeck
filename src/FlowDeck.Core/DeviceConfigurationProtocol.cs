namespace FlowDeck.Core;

public enum DeviceConfigurationOperation { Snapshot, ReadSnapshot, BeginUpload, Append, Validate, Apply, Discard, SaveDashboard, RegisterMonitoring, SaveCoolingStartup, SaveCoolingGroup, SaveNotifications, InspectOpenRgb = 13, ValidateProviderRoute, ValidateProviderSelection, SaveDeviceName, InspectOpenRgbRuntime, SnapshotWithCandidates, PreviewCacheRetirement, RetireDisconnected, ValidateCoolingSetup, ValidateLightingSetup, PrepareConnectionLighting, SaveDialDisplay }
public sealed record DeviceConfigurationRequest(DeviceConfigurationOperation Operation, string? TransferId = null,
    string? ExpectedGeneration = null, int? TotalBytes = null, string? Sha256 = null, int? Offset = null, byte[]? Data = null,
    long? TopologyRevision = null, DeviceDashboardConfiguration? Dashboard = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeviceMonitoringSelection[]? Monitoring = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] CoolingStartupSelection? CoolingStartup = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] CoolingGroupChange? CoolingGroup = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeviceNotificationSettings? Notifications = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? OpenRgbPort = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeviceNameChange? DeviceName = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeviceCacheRetirementTarget? CacheRetirement = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] EndpointReference? CoolingSetupTarget = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] EndpointReference? LightingSetupTarget = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeviceDialDisplaySettings? DialDisplay = null);
public sealed record DeviceBindingStatus(string DeviceId, bool Matches, string Reason);
public sealed record DeviceConfigurationReply(Outcome Outcome, string Message, string Generation, string? TransferId = null,
    int? TotalBytes = null, string? Sha256 = null, int? Offset = null, byte[]? Data = null,
    string[]? ChangedComponents = null, DeviceBindingStatus[]? Bindings = null, PhysicalDeviceBinding[]? Candidates = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeviceSetupCandidate[]? Setup = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeviceOpenRgbInspection? OpenRgbInspection = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeviceProviderRouteReview? ProviderRoute = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeviceOpenRgbRuntimeInspection? OpenRgbRuntime = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeviceCacheRetirementTarget? CacheRetirement = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? PreparedLighting = null);
public interface IDeviceConfigurationHandler
{
    Task<DeviceConfigurationReply> ExecuteAsync(DeviceConfigurationRequest request, string requestId, string owner,
        DeviceControlRuntime runtime, CancellationToken ct);
}
// Capability marker: older configuration handlers need not implement non-actuating edits.
public interface IDeviceDashboardConfigurationHandler : IDeviceConfigurationHandler { }
public interface IDeviceNamingConfigurationHandler : IDeviceConfigurationHandler { }
public interface IDeviceMonitoringConfigurationHandler : IDeviceDashboardConfigurationHandler { }
public interface IDeviceSetupConfigurationHandler : IDeviceMonitoringConfigurationHandler { }
public interface IDeviceLightingSetupHandler : IDeviceSetupConfigurationHandler { }
public interface IDeviceIndividualCoolingSetupHandler : IDeviceSetupConfigurationHandler { }
public interface IDeviceChunkedConfigurationSnapshotHandler : IDeviceConfigurationHandler { }
public interface IDeviceStartupConfigurationHandler : IDeviceSetupConfigurationHandler { }
public static class DeviceConfigurationProtocol
{
    public const int MaximumBytes = 8 * 1024 * 1024, ChunkBytes = 48 * 1024;
    public const int MaximumSnapshotBytes = 16 * 1024 * 1024;
    public const string SnapshotFeature = "configuration-snapshot-chunks-v2";
    public static bool Digest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool Valid(DeviceConfigurationRequest? r)
    {
        if (r is null || !Enum.IsDefined(r.Operation)) return false;
        if (r.Operation != DeviceConfigurationOperation.SaveDialDisplay && r.DialDisplay is not null) return false;
        if (r.Operation != DeviceConfigurationOperation.ValidateLightingSetup && r.LightingSetupTarget is not null) return false;
        if (r.Operation != DeviceConfigurationOperation.ValidateCoolingSetup && r.CoolingSetupTarget is not null) return false;
        if (r.Operation != DeviceConfigurationOperation.SaveDashboard && r.Dashboard is not null) return false;
        if (r.Operation != DeviceConfigurationOperation.RegisterMonitoring && r.Monitoring is not null) return false;
        if (r.Operation != DeviceConfigurationOperation.SaveCoolingStartup && r.CoolingStartup is not null) return false;
        if (r.Operation != DeviceConfigurationOperation.SaveCoolingGroup && r.CoolingGroup is not null) return false;
        if (r.Operation != DeviceConfigurationOperation.SaveNotifications && r.Notifications is not null) return false;
        if (r.Operation != DeviceConfigurationOperation.InspectOpenRgb && r.OpenRgbPort is not null) return false;
        if (r.Operation != DeviceConfigurationOperation.SaveDeviceName && r.DeviceName is not null) return false;
        if (r.Operation is not (DeviceConfigurationOperation.PreviewCacheRetirement or DeviceConfigurationOperation.RetireDisconnected) && r.CacheRetirement is not null) return false;
        var transfer = DeviceServiceProtocol.Id(r.TransferId);
        var emptyBody = r.Data is null && r.Offset is null && r.TotalBytes is null && r.Sha256 is null;
        return r.Operation switch
        {
            DeviceConfigurationOperation.ValidateLightingSetup => transfer && emptyBody && r.ExpectedGeneration is null && r.TopologyRevision is null && DeviceConfigurationRules.ValidReference(r.LightingSetupTarget),
            DeviceConfigurationOperation.ValidateCoolingSetup => transfer && emptyBody && r.ExpectedGeneration is null && r.TopologyRevision is null && DeviceConfigurationRules.ValidReference(r.CoolingSetupTarget),
            DeviceConfigurationOperation.PreviewCacheRetirement or DeviceConfigurationOperation.RetireDisconnected => r.TransferId is null && DeviceServiceProtocol.Id(r.ExpectedGeneration) &&
                emptyBody && r.TopologyRevision is >= 0 && DeviceCacheRetirementTarget.Valid(r.CacheRetirement),
            DeviceConfigurationOperation.InspectOpenRgbRuntime => r.TransferId is null && DeviceServiceProtocol.Id(r.ExpectedGeneration) &&
                emptyBody && r.TopologyRevision is null,
            DeviceConfigurationOperation.SaveDeviceName => r.TransferId is null && DeviceServiceProtocol.Id(r.ExpectedGeneration) &&
                emptyBody && r.TopologyRevision is null && DeviceNameChange.Valid(r.DeviceName),
            DeviceConfigurationOperation.InspectOpenRgb => r.TransferId is null && DeviceServiceProtocol.Id(r.ExpectedGeneration) &&
                emptyBody && r.TopologyRevision is null && r.OpenRgbPort is { } port && DeviceOpenRgbInspection.PortValid(port),
            DeviceConfigurationOperation.SaveDialDisplay => r.TransferId is null && DeviceServiceProtocol.Id(r.ExpectedGeneration) &&
                emptyBody && r.TopologyRevision is >= 0 && DeviceDialDisplaySettings.Valid(r.DialDisplay),
            DeviceConfigurationOperation.SaveNotifications => r.TransferId is null && DeviceServiceProtocol.Id(r.ExpectedGeneration) &&
                emptyBody && r.TopologyRevision is null && DeviceNotifications.Validate(r.Notifications) is null,
            DeviceConfigurationOperation.SaveCoolingGroup => r.TransferId is null && DeviceServiceProtocol.Id(r.ExpectedGeneration) &&
                emptyBody && r.TopologyRevision is null && DeviceCoolingGroups.Valid(r.CoolingGroup),
            DeviceConfigurationOperation.SaveCoolingStartup => r.TransferId is null && DeviceServiceProtocol.Id(r.ExpectedGeneration) &&
                emptyBody && r.TopologyRevision is >= 0 && DeviceCoolingStartup.Valid(r.CoolingStartup),
            DeviceConfigurationOperation.RegisterMonitoring => r.TransferId is null && DeviceServiceProtocol.Id(r.ExpectedGeneration) &&
                emptyBody && r.TopologyRevision is >= 0 && DeviceMonitoringDraft.Valid(r.Monitoring),
            DeviceConfigurationOperation.SaveDashboard => r.TransferId is null && DeviceServiceProtocol.Id(r.ExpectedGeneration) &&
                emptyBody && r.TopologyRevision is null && DeviceDashboardDraft.Valid(r.Dashboard),
            DeviceConfigurationOperation.PrepareConnectionLighting or DeviceConfigurationOperation.Snapshot or DeviceConfigurationOperation.SnapshotWithCandidates => r.TransferId is null && r.ExpectedGeneration is null && emptyBody && r.TopologyRevision is null,
            DeviceConfigurationOperation.ReadSnapshot => transfer && r.Offset is >= 0 and < MaximumSnapshotBytes && r.Data is null &&
                r.TotalBytes is null && r.Sha256 is null && r.ExpectedGeneration is null && r.TopologyRevision is null,
            DeviceConfigurationOperation.BeginUpload => r.TransferId is null && DeviceServiceProtocol.Id(r.ExpectedGeneration) &&
                r.TotalBytes is >= 2 and <= MaximumBytes && Digest(r.Sha256) && r.Offset is null && r.Data is null && r.TopologyRevision is null,
            DeviceConfigurationOperation.Append => transfer && r.Offset is >= 0 and < MaximumBytes && r.Data is { Length: > 0 and <= ChunkBytes } &&
                r.ExpectedGeneration is null && r.TotalBytes is null && r.Sha256 is null && r.TopologyRevision is null,
            DeviceConfigurationOperation.Validate or DeviceConfigurationOperation.ValidateProviderRoute or DeviceConfigurationOperation.ValidateProviderSelection or DeviceConfigurationOperation.Discard => transfer && emptyBody && r.ExpectedGeneration is null && r.TopologyRevision is null,
            DeviceConfigurationOperation.Apply => transfer && emptyBody && r.ExpectedGeneration is null && r.TopologyRevision is >= 0,
            _ => false
        };
    }
    public static bool Valid(DeviceConfigurationReply r) => (r.PreparedLighting is null ||
        r.PreparedLighting is >= 0 and <= DeviceControlCapacity.MaximumLighting && r.Outcome == Outcome.Confirmed &&
        r.TransferId is null && r.TotalBytes is null && r.Sha256 is null && r.Offset is null && r.Data is null &&
        r.ChangedComponents is null && r.Bindings is null && r.Candidates is null && r.Setup is null && r.OpenRgbInspection is null &&
        r.ProviderRoute is null && r.OpenRgbRuntime is null && r.CacheRetirement is null) && Enum.IsDefined(r.Outcome) && DeviceServiceProtocol.Id(r.Generation) &&
        (r.CacheRetirement is null || DeviceCacheRetirementTarget.Valid(r.CacheRetirement) && r.Outcome == Outcome.Confirmed &&
            r.TransferId is null && r.TotalBytes is null && r.Sha256 is null && r.Offset is null && r.Data is null &&
            r.ChangedComponents is null && r.Bindings is null && r.Candidates is null && r.Setup is null && r.OpenRgbInspection is null && r.ProviderRoute is null && r.OpenRgbRuntime is null) &&
        (r.OpenRgbRuntime is null || DeviceOpenRgbRuntimeInspection.Valid(r.OpenRgbRuntime) && r.Outcome == Outcome.Confirmed &&
            r.TransferId is null && r.TotalBytes is null && r.Sha256 is null && r.Offset is null && r.Data is null &&
            r.ChangedComponents is null && r.Bindings is null && r.Candidates is null && r.Setup is null && r.OpenRgbInspection is null && r.ProviderRoute is null) &&
        (r.ProviderRoute is null || DeviceProviderRouteReview.Valid(r.ProviderRoute) && r.Outcome == Outcome.Confirmed &&
            DeviceServiceProtocol.Id(r.TransferId) && r.ChangedComponents is not null && r.Bindings is not null &&
            r.TotalBytes is null && r.Sha256 is null && r.Offset is null && r.Data is null && r.Candidates is null && r.Setup is null && r.OpenRgbInspection is null) &&
        (r.OpenRgbInspection is null || DeviceOpenRgbInspection.Valid(r.OpenRgbInspection) && r.Outcome == Outcome.Confirmed &&
            r.TransferId is null && r.TotalBytes is null && r.Sha256 is null && r.Offset is null && r.Data is null &&
            r.ChangedComponents is null && r.Bindings is null && r.Candidates is null && r.Setup is null) &&
        r.Message is { Length: <= 16384 } && (r.TransferId is null || DeviceServiceProtocol.Id(r.TransferId)) &&
        r.TotalBytes is null or >= 0 and <= MaximumSnapshotBytes && (r.Sha256 is null || Digest(r.Sha256)) &&
        r.Offset is null or >= 0 and <= MaximumSnapshotBytes && r.Data is null or { Length: <= ChunkBytes } &&
        (r.ChangedComponents is null || r.ChangedComponents.Length <= 7 && r.ChangedComponents.All(c => DeviceConfigurationRules.Text(c, 80))) &&
        (r.Candidates is null || r.Candidates.Length <= DeviceRegistry.MaximumDevices && r.Candidates.All(b => b is not null &&
            DeviceConfigurationRules.Key(b.DeviceId) && DeviceConfigurationRules.Text(b.ProviderIdentity, 2048) &&
            DeviceConfigurationRules.Key(b.DefinitionId) && b.Fingerprint is { Length: 64 } && b.Fingerprint.All(Uri.IsHexDigit)) &&
            r.Candidates.DistinctBy(b => b.DeviceId).Count() == r.Candidates.Length) &&
        (r.Setup is null || r.Setup.Length <= 256 && r.Setup.All(DeviceSetup.Valid) && r.Setup.DistinctBy(c => c.Target).Count() == r.Setup.Length) &&
        (r.Bindings is null || r.Bindings.Length <= DeviceRegistry.MaximumDevices && r.Bindings.All(b => b is not null &&
            DeviceConfigurationRules.Key(b.DeviceId) && DeviceConfigurationRules.Text(b.Reason, 512)) && r.Bindings.DistinctBy(b => b.DeviceId).Count() == r.Bindings.Length);
}
