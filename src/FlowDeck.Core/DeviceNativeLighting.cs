namespace FlowDeck.Core;

// Driver-advertised bounds; effect IDs are provider-qualified and never matched by display name.
public sealed record DeviceNativeLightingEffect(string Id, string Name, bool Color, int MinimumColors, int MaximumColors,
    int MinimumSpeed, int MaximumSpeed, int DefaultSpeed, bool Speed = true, bool Direction = false, bool Brightness = true, int LedCount = 0);

public static class DeviceNativeLighting
{
    public static bool Valid(DeviceNativeLightingEffect? e) => e is not null && DeviceConfigurationRules.Text(e.Id, 128) &&
        e.Id.StartsWith("native/", StringComparison.Ordinal) && e.Id.Split('/').Length == 3 && e.Id.Split('/').All(p => p.Length > 0) &&
        DeviceConfigurationRules.Text(e.Name, 80) && e.MinimumColors >= 0 && e.MaximumColors >= e.MinimumColors && e.MaximumColors <= 32 &&
        (e.Color ? e.MinimumColors >= 1 : e.MinimumColors == 0 && e.MaximumColors <= 1) &&
        e.MinimumSpeed >= 0 && e.MaximumSpeed <= 255 && e.MinimumSpeed <= e.DefaultSpeed && e.DefaultSpeed <= e.MaximumSpeed &&
        e.LedCount is >= 0 and <= 4096 && (e.LedCount == 0 || e.Color);
    public static bool Accepts(DeviceNativeLightingEffect? e, LightingProgram? p) => Valid(e) && DeviceConfigurationRules.ValidProgram(p) &&
        p!.EffectId == e!.Id && p.Colors.Length >= e.MinimumColors && p.Colors.Length <= e.MaximumColors &&
        p.Speed >= e.MinimumSpeed && p.Speed <= e.MaximumSpeed && (!p.Reverse || e.Direction) && (e.Brightness || p.Brightness == 100) &&
        (p.LedColors is null || e.LedCount > 0 && p.LedColors.Length == e.LedCount);
    public static DeviceNativeLightingEffect[] Common(IEnumerable<DeviceEndpoint?> endpoints)
    {
        var values = endpoints.ToArray();
        if (values.Length == 0 || values.Any(e => e?.Capabilities.Any(c => c.Feature == DeviceFeature.NativeEffect && c.Support == ImplementationSupport.Supported) != true)) return [];
        var common = new List<DeviceNativeLightingEffect>();
        foreach (var first in values[0]!.NativeLightingEffects ?? [])
        {
            var matches = values.Select(v => v!.NativeLightingEffects?.SingleOrDefault(e => e.Id == first.Id)).ToArray();
            if (matches.Any(e => !Valid(e) || e!.Color != first.Color)) continue;
            var effects = matches.Select(e => e!).ToArray();
            var minSpeed = effects.Max(e => e.MinimumSpeed); var maxSpeed = effects.Min(e => e.MaximumSpeed);
            if (minSpeed > maxSpeed) continue;
            var combined = first with {
                MinimumColors = effects.Max(e => e.MinimumColors), MaximumColors = effects.Min(e => e.MaximumColors),
                MinimumSpeed = minSpeed, MaximumSpeed = maxSpeed, DefaultSpeed = Math.Clamp(first.DefaultSpeed, minSpeed, maxSpeed),
                Speed = effects.All(e => e.Speed), Direction = effects.All(e => e.Direction), Brightness = effects.All(e => e.Brightness),
                LedCount = effects.All(e => e.LedCount == first.LedCount) ? first.LedCount : 0
            };
            if (Valid(combined)) common.Add(combined);
        }
        return common.ToArray();
    }
    public static LightingProgram Create(DeviceNativeLightingEffect effect, LightingProgram previous)
    {
        if (!Valid(effect)) throw new ArgumentException("照明効果の能力情報が不正です");
        var colors = effect.Color ? previous.Colors.Take(effect.MaximumColors).ToList() : [];
        while (colors.Count < effect.MinimumColors) colors.Add(new(52, 137, 255));
        var sameProvider = previous.EffectId.StartsWith(effect.Id[..(effect.Id.LastIndexOf('/') + 1)], StringComparison.Ordinal);
        return new(effect.Id, colors.ToArray(), effect.Speed && sameProvider ? Math.Clamp(previous.Speed, effect.MinimumSpeed, effect.MaximumSpeed) : effect.DefaultSpeed,
            effect.Direction && previous.Reverse, effect.Brightness ? previous.Brightness : 100,
            effect.LedCount > 0 && previous.LedColors?.Length == effect.LedCount ? previous.LedColors.ToArray() : null);
    }
}
