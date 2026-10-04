namespace FlowDeck.Core;

public static class DeviceLightingSceneCapture
{
    // Capture chosen programs, never the instantaneous dimmed/rainbow frame reported by hardware.
    public static DeviceLightingScene Create(string id, string name, LightingConfiguration configuration, EndpointReference[] targets)
    {
        if (targets.Length is < 1 or > 256 || targets.Distinct().Count() != targets.Length) throw new ArgumentException("照明対象が不正です");
        var values = targets.Select(t => configuration.Preferences?.SingleOrDefault(p => p.Target == t) ?? throw new InvalidOperationException("対象に色・効果を適用してから保存してください")).ToArray();
        var sync = (configuration.SavedSynchronization ?? []).Select(s => new SynchronizedLighting(s.Targets.Where(targets.Contains).ToArray(), s.Program))
            .Where(s => s.Targets.Length > 1 && s.Targets.All(t => values.Any(v => v.Target == t && DeviceLighting.SameProgram(v.Program, s.Program)))).ToArray();
        var grouped = sync.SelectMany(s => s.Targets).ToHashSet();
        var scene = new DeviceLightingScene(id, name, values.Where(v => !grouped.Contains(v.Target)).ToArray(), sync);
        if (!DeviceLightingLibrary.ValidScene(scene)) throw new ArgumentException("照明シーンの名前・設定が不正です");
        return Contract.Copy(DeviceLightingSceneRequirements.Stamp(scene));
    }
}
