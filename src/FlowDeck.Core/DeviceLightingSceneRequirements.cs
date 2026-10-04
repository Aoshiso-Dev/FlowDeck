namespace FlowDeck.Core;

public sealed record DeviceLightingRequirement(EndpointReference Target, DeviceFeature Feature, string EffectId,
    int ColorCount, int Speed, bool Reverse, bool RequiresBrightness, int LedCount);

public static class DeviceLightingSceneRequirements
{
    public static DeviceLightingRequirement For(EndpointReference target, LightingProgram program) => new(target,
        program.EffectId is "fixed" or "off" ? DeviceFeature.FixedColor : DeviceLighting.Animated(program) ? DeviceFeature.ColorFrames : DeviceFeature.NativeEffect,
        program.EffectId, program.Colors.Length, program.Speed, program.Reverse, program.Brightness != 100, program.LedColors?.Length ?? 0);
    public static DeviceLightingRequirement[] Describe(DeviceLightingScene scene) => scene.Individual.Select(p => For(p.Target, p.Program))
        .Concat(scene.Synchronized.SelectMany(p => p.Targets.Select(t => For(t, p.Program))))
        .OrderBy(r => r.Target.DeviceId, StringComparer.Ordinal).ThenBy(r => r.Target.EndpointId, StringComparer.Ordinal).ToArray();
    public static DeviceLightingScene Stamp(DeviceLightingScene scene) => scene with { Requirements = Describe(scene) };
    // Missing requirements are permitted for earlier v2 drafts. Every library save stamps them.
    // A supplied list must describe the exact programs; it cannot weaken or retarget their requirements.
    public static bool Matches(DeviceLightingScene scene) => scene.Requirements is null || scene.Requirements is { Length: <= 256 } actual &&
        Matches(actual, Describe(scene));
    private static bool Matches(DeviceLightingRequirement[] actual, DeviceLightingRequirement[] expected) =>
        actual.Length == expected.Length && actual.Distinct().Count() == actual.Length && actual.All(expected.Contains);
}
