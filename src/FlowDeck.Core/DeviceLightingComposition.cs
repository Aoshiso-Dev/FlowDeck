namespace FlowDeck.Core;

public sealed record EndpointLightingProgram(string EndpointId, LightingProgram Program);

// Opt-in contract: the driver can preserve its exact endpoint ownership and recovery while the
// service supplies independently sampled endpoint colors. It does not promise native-effect emulation.
public interface IDeviceLightingCompositionDriver : IDeviceLightingDriver
{
    string? ValidateLightingComposition(EndpointLightingProgram[] programs) => DeviceLightingComposition.Validate(this, programs);
    Task<IDeviceLightingStream> OpenLightingCompositionAsync(EndpointLightingProgram[] programs, CancellationToken ct) =>
        DeviceLightingComposition.OpenAsync(this, programs, ct);
}

// Creating a replacement is side-effect free: no I/O, durable ownership change, or mutation of
// the current stream. The service transfers ownership before using the returned stream. Its
// first authorized frame must persist the expanded recovery scope before touching new outputs.
public interface IDeviceLightingExpansionDriver : IDeviceLightingCompositionDriver
{
    string? ValidateLightingExpansion(IDeviceLightingStream current, EndpointLightingProgram[] programs);
    IDeviceLightingStream CreateLightingExpansion(IDeviceLightingStream current, EndpointLightingProgram[] programs);
}

public static class DeviceLightingComposition
{
    public static bool Valid(EndpointLightingProgram[]? programs) => programs is { Length: > 0 and <= 256 } &&
        programs.All(p => p is not null && DeviceConfigurationRules.Key(p.EndpointId) && DeviceLighting.FrameProgram(p.Program)) &&
        programs.DistinctBy(p => p.EndpointId).Count() == programs.Length;
    // Existing transports use this only to open a frame channel. Every transmitted frame supplies
    // the complete per-endpoint colors and stop colors; no representative effect is sent to hardware.
    public static LightingProgram TransportProgram(IEnumerable<LightingProgram> programs) => programs.FirstOrDefault(DeviceLighting.ValidAnimation) ?? new("breathing", [new(0, 0, 0)]);
    public static string? Validate(IDeviceLightingDriver driver, EndpointLightingProgram[] programs) =>
        !Valid(programs) || driver.LightingLimits is not { EndpointColors: true } ? "端子別の照明効果を合成できません" :
        driver.ValidateLightingStream(programs.Select(p => p.EndpointId).ToArray(), TransportProgram(programs.Select(p => p.Program)));
    public static Task<IDeviceLightingStream> OpenAsync(IDeviceLightingDriver driver, EndpointLightingProgram[] programs, CancellationToken ct)
    {
        var selected = Contract.Copy(programs);
        if (Validate(driver, selected) is { } error) throw new ArgumentException(error);
        return driver.OpenLightingStreamAsync(selected.Select(p => p.EndpointId).ToArray(), TransportProgram(selected.Select(p => p.Program)), ct);
    }
    public static EndpointReference[][] Components(IEnumerable<EndpointReference[]> parts)
    {
        var groups = parts.Where(p => p.Length > 0).Select(p => p.ToHashSet()).ToList();
        for (var i = 0; i < groups.Count; i++)
            for (var j = i + 1; j < groups.Count; j++)
                if (groups[i].Select(t => t.DeviceId).Intersect(groups[j].Select(t => t.DeviceId)).Any())
                { groups[i].UnionWith(groups[j]); groups.RemoveAt(j); i = -1; break; }
        return groups.Select(g => g.ToArray()).ToArray();
    }
}
