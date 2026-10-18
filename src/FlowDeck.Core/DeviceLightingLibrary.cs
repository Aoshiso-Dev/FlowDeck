namespace FlowDeck.Core;

public enum LightingLibraryOperation { SaveGroup, DeleteGroup, SaveScene, DeleteScene, SaveAlias, ToggleFavorite, RememberColors }
public enum LightingPresetKind { Group, Scene }
public sealed record LightingLibraryRequest(LightingLibraryOperation Operation, string ExpectedGeneration, string Id, long ExpectedRevision,
    EndpointGroup? Group = null, DeviceLightingScene? Scene = null, EndpointAlias? Alias = null, RgbColor[]? Colors = null);
public sealed record LightingLibraryReply(Outcome Outcome, string Message, string Generation, string Id, long Revision);
public sealed record LightingPresetRequest(LightingPresetKind Kind, string ExpectedGeneration, string Id, long ExpectedRevision, LightingProgram? Program = null);
// Service-resolved immutable copies; never accepted as client-provided expanded plans.
public sealed record LightingPresetPlan(long LibraryRevision, LightingPresetRequest Selection, SynchronizedLighting[] Parts);
public interface IDeviceLightingLibrary
{
    Task<LightingLibraryReply> SaveLightingLibraryAsync(LightingLibraryRequest request, CancellationToken ct);
    LightingPresetPlan ResolveLightingPreset(LightingPresetRequest request);
    string? CheckLightingPreset(LightingPresetPlan plan);
}
public interface IDeviceLightingPersonalization : IDeviceLightingLibrary { }
public static class DeviceLightingLibrary
{
    public const int MaximumGroups = 32, MaximumScenes = 64;
    public const string PersonalizationFeature = "lighting-personalization-v2";
    public static bool Personalization(LightingLibraryOperation operation) => operation is LightingLibraryOperation.SaveAlias or LightingLibraryOperation.ToggleFavorite or LightingLibraryOperation.RememberColors;
    private static bool References(EndpointReference[]? targets) => targets is { Length: > 0 and <= 256 } &&
        targets.All(DeviceConfigurationRules.ValidReference) && targets.Distinct().Count() == targets.Length;
    public static bool ValidGroup(EndpointGroup? g) => g is not null && DeviceConfigurationRules.ProfileId(g.Id) &&
        DeviceConfigurationRules.Text(g.Name, 80) && g.Kind == EndpointKind.Lighting && g.Revision >= 1 && References(g.Targets);
    public static bool ValidParts(SynchronizedLighting[]? parts) => parts is { Length: > 0 and <= 256 } &&
        parts.All(p => p is not null && References(p.Targets) && DeviceConfigurationRules.ValidProgram(p.Program)) &&
        References(parts.SelectMany(p => p.Targets).ToArray());
    public static bool ValidScene(DeviceLightingScene? s)
    {
        if (s is null || !DeviceConfigurationRules.ProfileId(s.Id) || !DeviceConfigurationRules.Text(s.Name, 80) || s.Revision < 1 ||
            s.Individual is not { Length: <= 256 } || s.Synchronized is not { Length: <= 32 } ||
            s.Individual.Any(i => i is null || !DeviceConfigurationRules.ValidReference(i.Target) || !DeviceConfigurationRules.ValidProgram(i.Program)) ||
            s.Synchronized.Any(g => g is null || !References(g.Targets) || !DeviceConfigurationRules.ValidProgram(g.Program))) return false;
        return References(s.Individual.Select(i => i.Target).Concat(s.Synchronized.SelectMany(g => g.Targets)).ToArray()) && DeviceLightingSceneRequirements.Matches(s);
    }
    public static bool Valid(LightingLibraryRequest? r) => r is not null && Enum.IsDefined(r.Operation) &&
        DeviceServiceProtocol.Id(r.ExpectedGeneration) && DeviceConfigurationRules.ProfileId(r.Id) && r.ExpectedRevision >= 0 && (r.Operation switch
        {
            LightingLibraryOperation.SaveGroup => r.Alias is null && r.Colors is null && r.Scene is null && ValidGroup(r.Group) && r.Group!.Id == r.Id,
            LightingLibraryOperation.SaveScene => r.Alias is null && r.Colors is null && r.Group is null && ValidScene(r.Scene) && r.Scene!.Id == r.Id,
            LightingLibraryOperation.SaveAlias => r.Id == "alias" && r.Group is null && r.Scene is null && r.Colors is null && r.Alias is { } a &&
                DeviceConfigurationRules.ValidReference(a.Target) && a.Name is not null && (a.Name.Length == 0 || DeviceConfigurationRules.Text(a.Name, 80)),
            LightingLibraryOperation.ToggleFavorite or LightingLibraryOperation.RememberColors => r.Id == "palette" && r.Group is null && r.Scene is null && r.Alias is null &&
                r.Colors is { Length: > 0 and <= 32 } && r.Colors.All(c => c?.Valid == true) && (r.Operation != LightingLibraryOperation.ToggleFavorite || r.Colors.Length == 1),
            _ => r.ExpectedRevision >= 1 && r.Group is null && r.Scene is null && r.Alias is null && r.Colors is null
        });
    public static bool Valid(LightingPresetRequest? r) => r is not null && Enum.IsDefined(r.Kind) &&
        DeviceServiceProtocol.Id(r.ExpectedGeneration) && DeviceConfigurationRules.ProfileId(r.Id) && r.ExpectedRevision >= 1 &&
        (r.Kind == LightingPresetKind.Group ? DeviceConfigurationRules.ValidProgram(r.Program) : r.Program is null);
    public static bool Valid(LightingLibraryReply? r) => r is not null && Enum.IsDefined(r.Outcome) && r.Message is { Length: <= 16384 } &&
        DeviceServiceProtocol.Id(r.Generation) && DeviceConfigurationRules.ProfileId(r.Id) && r.Revision >= 1;
}
