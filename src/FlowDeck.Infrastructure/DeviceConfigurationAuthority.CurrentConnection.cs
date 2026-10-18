using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceConfigurationAuthority : IDeviceCurrentConnectionConfiguration
{
    private Func<EndpointReference, DeviceFeature, PhysicalDeviceBinding?>? currentControlResolver;
    private volatile bool currentConfigurationInvalid;
    public string? CurrentControlError => failure ?? (configuration.Bindings.InstallationId != installation ?
        "設定の導入先が現在のサービスと一致しません" : currentConfigurationInvalid ?
        "保存された設定世代を確認できません。サービスを再起動して再取得してください" : null);
    public string? CurrentControlCapacityProblem(EndpointReference target, DeviceFeature feature,
        IDeviceCurrentConnectionControlDriver[] drivers)
    {
        if (feature is not (DeviceFeature.FixedColor or DeviceFeature.NativeEffect or DeviceFeature.ColorFrames)) return null;
        var basis = configuration;
        var counts = basis.Devices.Devices.SelectMany(d => d.Endpoints.Select(e =>
            (Target: new EndpointReference(d.Id, e.Id), Count: e.Setup?.LedCount ?? 0))).ToDictionary(e => e.Target, e => e.Count);
        var saved = (basis.Lighting.Endpoints ?? []).Select(e => e.Target).ToHashSet();
        var usage = DeviceControlCapacity.Count(basis.Devices, basis.Cooling, basis.Lighting);
        var live = drivers.Where(d => d.CurrentControlBinding is { } binding && binding == d.BindingCandidate)
            .OfType<IDeviceSetupDriver>().SelectMany(d => d.DescribeSetup().Where(e => e.LightingExitActions.Length > 0)
                .Select(e => (Target: new EndpointReference(((IDeviceControlDriver)d).DeviceId, e.EndpointId),
                    Count: e.DetectedLeds ?? (e.MinimumLeds == e.MaximumLeds ? e.MinimumLeds ?? 0 : counts.GetValueOrDefault(new(((IDeviceControlDriver)d).DeviceId, e.EndpointId))))))
            .OrderBy(e => e.Target.DeviceId, StringComparer.Ordinal).ThenBy(e => e.Target.EndpointId, StringComparer.Ordinal).ToArray();
        // Registered reservations are retained even when disconnected. Known controller extents
        // replace saved counts; registering a live endpoint never charges it twice.
        foreach (var item in live.Where(e => saved.Contains(e.Target)))
            usage = usage with { Leds = usage.Leds - counts.GetValueOrDefault(item.Target) + item.Count };
        if (DeviceControlCapacity.Problem(usage) is { } savedProblem) return savedProblem;
        foreach (var item in live.Where(e => !saved.Contains(e.Target)))
        {
            var next = usage with { Lighting = usage.Lighting + 1, Leds = usage.Leds + item.Count };
            var problem = DeviceControlCapacity.Problem(next);
            if (item.Target == target) return problem;
            if (problem is null) usage = next;
        }
        return null;
    }
    public void AttachCurrentControlResolver(Func<EndpointReference, DeviceFeature, PhysicalDeviceBinding?> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        if (currentControlResolver is not null) throw new InvalidOperationException("現在接続の照合は起動時に登録してください");
        currentControlResolver = resolve;
    }
    public void PrepareCurrentConnection(IDeviceCurrentConnectionControlDriver driver)
    {
        var basis = configuration;
        if (CurrentControlError is not null || applyingConfiguration || basis.Bindings.InstallationId != installation ||
            driver.BindingCandidate is not { } candidate || driver.CurrentControlBinding == candidate) return;
        _ = ConfigureDrivers(basis, installation, [driver]);
    }
    private bool CurrentBindingMatches(EndpointReference target, DeviceFeature feature, PhysicalDeviceBinding binding) =>
        currentControlResolver?.Invoke(target, feature) == binding;
    private static DeviceFeature SelectedLightingFeature(LightingProgram program) => program.EffectId switch
    {
        "fixed" or "off" => DeviceFeature.FixedColor,
        "breathing" or "color-cycle" => DeviceFeature.ColorFrames,
        _ => DeviceFeature.NativeEffect
    };
}
