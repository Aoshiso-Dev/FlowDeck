namespace FlowDeck.Core;

public sealed record DeviceNotificationRule(string Id, EndpointReference Target, DeviceFeature Measurement, string Name, double Threshold,
    double Hysteresis = 0, bool Enabled = true, bool Missing = true, bool IgnoreStopped = true);
public sealed record DeviceNotificationSettings(int Version = 2, bool Enabled = true, bool Desktop = true, bool Recovery = true,
    bool ControlFaults = true, bool Disconnects = true, bool OperationFailures = true,
    int DelaySeconds = 5, int RecoverySeconds = 5, int RepeatSeconds = 0, DeviceNotificationRule[]? Rules = null);
public sealed record DeviceNotificationSample(EndpointReference Target, DeviceFeature Measurement, string Name, double? Value, bool IntentionalStop = false);

public static class DeviceNotifications
{
    // Private adapter to the existing pure threshold engine. The "motherboard" tag here performs no device discovery or I/O.
    internal static NotificationAddress Address(EndpointReference target, DeviceFeature measurement) =>
        new("motherboard", target.DeviceId, target.EndpointId, measurement == DeviceFeature.Temperature ? "temperature" : "fan");
    internal static NotificationSettings Adapt(DeviceNotificationSettings s) => new(1, s.Enabled, s.Desktop, s.Recovery,
        s.ControlFaults, s.Disconnects, s.OperationFailures, s.DelaySeconds, s.RecoverySeconds, s.RepeatSeconds,
        (s.Rules ?? []).Select(r => new NotificationRule(r.Id, Address(r.Target, r.Measurement), r.Name, r.Threshold,
            r.Hysteresis, r.Enabled, r.Missing, r.IgnoreStopped)).ToArray());
    public static string? Validate(DeviceNotificationSettings? value)
    {
        if (value is null || value.Version != 2 || (value.Rules ?? []).Length > 256 || (value.Rules ?? []).Any(r =>
            r is null || !DeviceConfigurationRules.ValidReference(r.Target) || r.Measurement is not (DeviceFeature.Temperature or DeviceFeature.Rpm)))
            return "通知の形式または対象が不正です";
        return Notifications.Validate(Adapt(value));
    }
    public static DeviceNotificationSettings Migrate(NotificationSettings value, Func<NotificationAddress, EndpointReference> resolve) =>
        new(2, value.Enabled, value.Desktop, value.Recovery, value.ControlFaults, value.Disconnects, value.OperationFailures,
            value.DelaySeconds, value.RecoverySeconds, value.RepeatSeconds, (value.Rules ?? []).Select(r => new DeviceNotificationRule(r.Id,
                resolve(r.Address), r.Address.Kind == "temperature" ? DeviceFeature.Temperature : DeviceFeature.Rpm,
                r.Name, r.Threshold, r.Hysteresis, r.Enabled, r.Missing, r.IgnoreStopped)).ToArray());
}

/// <summary>Preserves notification hysteresis and missing-value behavior for any registered temperature or RPM endpoint.</summary>
public sealed class DeviceNotificationEngine(TimeProvider? time = null)
{
    private readonly NotificationEngine engine = new(time);
    public NotificationEvent[] Active => engine.Active;
    public void Reset() => engine.Reset();
    public void ResetRule(string id) { engine.Forget(id + ":limit"); engine.Forget(id + ":missing"); }
    public void ResetSignal(string key) => engine.Forget("system:" + key);
    public NotificationEvent[] Evaluate(DeviceNotificationSettings settings, DeviceNotificationSample[] samples, NotificationSignal[] signals)
    {
        if (DeviceNotifications.Validate(settings) is { } error) throw new ArgumentException(error);
        if (samples is null || samples.Length > DeviceRegistry.MaximumDevices * DeviceRegistry.MaximumEndpoints ||
            samples.Any(s => s is null || !DeviceConfigurationRules.ValidReference(s.Target) || s.Measurement is not (DeviceFeature.Temperature or DeviceFeature.Rpm)) ||
            samples.DistinctBy(s => (s.Target, s.Measurement)).Count() != samples.Length) throw new ArgumentException("通知の測定値が不正または重複しています");
        return engine.Evaluate(DeviceNotifications.Adapt(settings), samples.Select(s => new NotificationSample(
            DeviceNotifications.Address(s.Target, s.Measurement), s.Name, s.Value, s.IntentionalStop)).ToArray(), signals);
    }
}
