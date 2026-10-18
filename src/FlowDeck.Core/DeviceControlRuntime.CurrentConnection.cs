namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private readonly Dictionary<string, string> currentPreparationErrors = [];

    private void PrepareCurrentControlCore(IDeviceControlDriver driver)
    {
        if (stopping || releasing || configurationChanging || lifecycleError is not null ||
            configurationHandler is not IDeviceCurrentConnectionConfiguration configuration ||
            driver is not IDeviceCurrentConnectionControlDriver current || current.BindingCandidate is not { } candidate ||
            current.CurrentControlBinding == candidate) return;
        try
        {
            configuration.PrepareCurrentConnection(current);
            currentPreparationErrors.Remove(driver.DeviceId);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or ObjectDisposedException)
        {
            // Missing settings/recovery keep the driver's own capability state. A failed
            // no-output preparation must not masquerade as physical disconnection.
            if (currentPreparationErrors.GetValueOrDefault(driver.DeviceId) != e.Message)
            {
                currentPreparationErrors[driver.DeviceId] = e.Message;
                try { log.Write("currentControlPreparation", new { driver.DeviceId, error = e.Message }); } catch { }
            }
        }
    }

    private PhysicalDeviceBinding? CurrentControlBinding(EndpointReference target, DeviceFeature feature)
    {
        lock (gate)
        {
            if (stopping || releasing || configurationChanging || lifecycleError is not null ||
                configurationHandler is IDeviceCurrentConnectionConfiguration { CurrentControlError: not null } ||
                drivers.GetValueOrDefault(target.DeviceId) is not IDeviceCurrentConnectionControlDriver current ||
                current.CurrentControlBinding is not { } binding || binding != current.BindingCandidate ||
                !current.AllowsCurrentConnectionControl(target.EndpointId, feature)) return null;
            return Contract.Copy(binding);
        }
    }

    private string? PhysicalBindingError(EndpointReference target, DeviceFeature feature, bool savedOnly = false)
    {
        if (configurationHandler is IDeviceCurrentConnectionConfiguration configuration)
        {
            if (configuration.CurrentControlError is { } error) return error;
            if (!savedOnly && drivers.GetValueOrDefault(target.DeviceId) is IDeviceCurrentConnectionControlDriver &&
                configuration.CurrentControlCapacityProblem(target, feature, drivers.Values.OfType<IDeviceCurrentConnectionControlDriver>().ToArray()) is { } capacity)
                return capacity;
        }
        if (!savedOnly && CurrentControlBinding(target, feature) is not null) return null;
        if (!savedOnly && feature is (DeviceFeature.FixedColor or DeviceFeature.NativeEffect or DeviceFeature.ColorFrames) &&
            connectionLighting.TryGetValue(target.DeviceId, out var permission) && permission.Target == target &&
            ConnectionLightingBinding(target.DeviceId) is not null) return null;
        return PhysicalBindingError(target.DeviceId);
    }

    private PhysicalDeviceBinding OperationBinding(EndpointReference target, DeviceFeature feature) =>
        CurrentControlBinding(target, feature) ?? physicalBindings?.GetValueOrDefault(target.DeviceId) ??
        throw new IOException("操作対象の現在の関連付けがありません");

    private static DeviceFeature LightingFeature(LightingProgram program) => program.EffectId switch
    {
        "fixed" or "off" => DeviceFeature.FixedColor,
        "breathing" or "color-cycle" => DeviceFeature.ColorFrames,
        _ => DeviceFeature.NativeEffect
    };
}
