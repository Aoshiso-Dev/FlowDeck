namespace FlowDeck.Core;

public sealed record DeviceNotificationConfiguration(DeviceNotificationSettings Settings, DeviceRegistrations Devices);
// The compiled configuration authority publishes one coherent, read-only, no-I/O snapshot.
public interface IDeviceNotificationConfiguration : IDeviceConfigurationHandler
{
    DeviceNotificationConfiguration ReadNotificationConfiguration();
}
public sealed record DeviceNotificationQuery(long After = 0, int ActiveOffset = 0, int Limit = 32, long? ActiveRevision = null);
public sealed record DeviceNotificationEntry(long Sequence, NotificationEvent Event, bool Desktop);
public sealed record DeviceNotificationFeed(long LatestSequence, long OldestSequence, long NextSequence, bool HistoryGap,
    DeviceNotificationEntry[] Events, NotificationEvent[] Active, int ActiveOffset, int ActiveTotal, long ActiveRevision,
    bool Enabled, bool Desktop, string? Error = null);

public static class DeviceNotificationProtocol
{
    public const int HistoryCapacity = 256, PageSize = 32, MaximumActive = 512 + DeviceRegistry.MaximumDevices + DeviceRegistry.MaximumDevices * DeviceRegistry.MaximumEndpoints + 2;
    public static bool Valid(DeviceNotificationQuery? q) => q is { After: >= 0, ActiveOffset: >= 0 and <= MaximumActive, Limit: > 0 and <= PageSize } &&
        q.ActiveRevision is null or >= 0 && (q.ActiveOffset == 0 || q.ActiveRevision is not null);
    private static bool ValidEvent(NotificationEvent? e) => e is not null && DeviceConfigurationRules.Text(e.Key, 4096) &&
        DeviceConfigurationRules.Text(e.Name, 160) && DeviceConfigurationRules.Text(e.Message, 512) &&
        e.Kind is "raised" or "recovered" or "reminder" or "test" or "operation-failed";
    public static bool Valid(DeviceNotificationFeed f) => f is { LatestSequence: >= 0, OldestSequence: >= 1, NextSequence: >= 0,
        ActiveOffset: >= 0, ActiveTotal: >= 0 and <= MaximumActive, ActiveRevision: >= 0, Events.Length: <= PageSize, Active.Length: <= PageSize } &&
        f.OldestSequence - 1 <= f.LatestSequence && f.NextSequence <= f.LatestSequence && f.ActiveOffset <= f.ActiveTotal &&
        f.Active.Length <= f.ActiveTotal - f.ActiveOffset && f.Error is null or { Length: <= 512 } &&
        f.Events.All(e => e is not null && e.Sequence >= f.OldestSequence && e.Sequence <= f.LatestSequence && ValidEvent(e.Event)) &&
        f.Events.Zip(f.Events.Skip(1)).All(p => p.First.Sequence < p.Second.Sequence) &&
        (f.Events.Length == 0 || f.NextSequence == f.Events[^1].Sequence) &&
        f.Active.All(ValidEvent) && f.Active.DistinctBy(e => e.Key).Count() == f.Active.Length;
}

/// <summary>No hardware or disk I/O. The runtime serializes evaluations, history and queries with its state lock.</summary>
public sealed class DeviceNotificationMonitor(TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly DeviceNotificationEngine engine = new(time);
    private readonly Queue<DeviceNotificationEntry> history = [];
    private readonly HashSet<string> seenDevices = [];
    private readonly Dictionary<string, long?> ruleGenerations = [];
    private readonly HashSet<EndpointReference> monitoredControls = [];
    private DeviceNotificationSettings settings = new(Enabled: false);
    private NotificationEvent[] active = [];
    private long sequence, activeRevision;
    public string? Error { get; private set; }
    private static string Short(string text, int max) => new(text.Where(c => !char.IsControl(c)).Take(max).ToArray());
    private static string Name(string? name) => string.IsNullOrWhiteSpace(name) ? "機器" : Short(name, 160);
    private static NotificationEvent Bounded(NotificationEvent e) => e with { Name = Name(e.Name), Message = Short(e.Message, 512) };
    private void Append(NotificationEvent e)
    {
        history.Enqueue(new(checked(++sequence), Bounded(e), settings.Enabled && settings.Desktop));
        while (history.Count > DeviceNotificationProtocol.HistoryCapacity) history.Dequeue();
    }
    public void EvaluationFailed(Exception error) => Error = Short("通知の評価を確認できません: " + error.Message, 512);
    public void Evaluate(DeviceNotificationConfiguration configuration, DeviceServiceState state)
    {
        if (DeviceNotifications.Validate(configuration.Settings) is { } error) throw new ArgumentException(error);
        settings = configuration.Settings; Error = null;
        var samples = new List<DeviceNotificationSample>(); var signals = new List<NotificationSignal>();
        var inventory = state.Inventory.Devices.ToDictionary(d => d.Device.Id);
        var execution = state.Endpoints.ToDictionary(e => e.Target);
        var telemetry = (state.Telemetry ?? []).GroupBy(s => (s.Target, s.Feature))
            .ToDictionary(g => g.Key, g => g.Take(2).Count() == 1 ? g.First() : null);
        DeviceTelemetry? Sample(EndpointReference target, DeviceFeature feature) => telemetry.GetValueOrDefault((target, feature));
        var rules = settings.Rules ?? [];
        foreach (var obsolete in ruleGenerations.Keys.Except(rules.Select(r => r.Id)).ToArray()) ruleGenerations.Remove(obsolete);
        foreach (var rule in rules)
        {
            var device = inventory.GetValueOrDefault(rule.Target.DeviceId);
            // A replacement/reconnection cannot complete a threshold timer begun on another device generation.
            if (ruleGenerations.TryGetValue(rule.Id, out var old) && old != device?.Generation) engine.ResetRule(rule.Id);
            ruleGenerations[rule.Id] = device?.Generation;
            var endpoint = device?.Device.Endpoints.SingleOrDefault(e => e.Id == rule.Target.EndpointId);
            var sample = Sample(rule.Target, rule.Measurement);
            var value = device?.Connected == true && endpoint?.Capabilities.Any(c => c.Feature == rule.Measurement && c.Usable) == true &&
                sample is not null && DeviceDashboard.Fresh(sample, 0) ? sample.Value : null;
            var current = execution.GetValueOrDefault(rule.Target);
            var duty = Sample(rule.Target, DeviceFeature.DutyRead);
            var stopped = current is { Owned: true, Outcome: Outcome.Confirmed, ReportedDuty: 0,
                Cooling: { TargetDuty: 0, Faulted: false, Restarting: false } } && current.Generation == device?.Generation &&
                duty is { Value: 0 } && DeviceDashboard.Fresh(duty, 0);
            samples.Add(new(rule.Target, rule.Measurement, rule.Name, value, stopped));
        }
        var registered = configuration.Devices.Devices.ToDictionary(d => d.Id);
        seenDevices.IntersectWith(registered.Keys);
        if (!settings.Enabled) seenDevices.Clear();
        else if (settings.Disconnects)
        {
            foreach (var saved in registered.Values)
            {
                var device = inventory.GetValueOrDefault(saved.Id);
                var measured = device?.Device.Endpoints.SelectMany(e => e.Capabilities.Where(c => c.Feature is DeviceFeature.Temperature or DeviceFeature.Rpm)
                    .Select(c => (Target: new EndpointReference(saved.Id, e.Id), c.Feature))).ToArray() ?? [];
                var healthy = device?.Connected == true && (measured.Length == 0 || measured.Any(m =>
                    Sample(m.Target, m.Feature) is { } s && DeviceDashboard.Fresh(s, 0)));
                if (healthy) seenDevices.Add(saved.Id);
                if (seenDevices.Contains(saved.Id)) signals.Add(new("device:" + saved.Id, Name(saved.Name), !healthy,
                    Name(saved.Name) + " · 接続または更新が停止しています"));
            }
        }
        if (settings.ControlFaults)
        {
            monitoredControls.IntersectWith(execution.Keys);
            foreach (var current in state.Endpoints)
            {
                if (current.Owned || current.Cooling is not null || current.Lighting is not null || current.Persistence?.State == DevicePolicySaveState.Failed)
                    monitoredControls.Add(current.Target);
                if (!monitoredControls.Contains(current.Target)) continue;
                var instance = inventory.GetValueOrDefault(current.Target.DeviceId);
                var endpoint = registered.GetValueOrDefault(current.Target.DeviceId)?.Endpoints.SingleOrDefault(e => e.Id == current.Target.EndpointId);
                var label = Name(endpoint?.Name ?? instance?.Device.Endpoints.SingleOrDefault(e => e.Id == current.Target.EndpointId)?.Name ?? current.Target.EndpointId);
                var name = Name((registered.GetValueOrDefault(current.Target.DeviceId)?.Name ?? instance?.Device.Name ?? "機器") + " · " + label);
                var fault = current.Cooling?.Faulted == true || current.Lighting?.Fault is not null || current.Persistence?.State == DevicePolicySaveState.Failed ||
                    current.Owned && current.Outcome is Outcome.Unknown or Outcome.Failed;
                signals.Add(new("control:" + DeviceDashboard.Key(current.Target, DeviceFeature.FixedDuty), name, fault,
                    name + " · " + (current.Cooling?.Fault ?? current.Lighting?.Fault ?? current.Persistence?.Message ?? current.Message ?? "制御の状態を確認してください"), true));
            }
            signals.Add(new("lifecycle", "機器の復旧", state.LifecycleError is not null, state.LifecycleError ?? "機器の復旧が完了しました", true));
        }
        var events = engine.Evaluate(settings, samples.ToArray(), signals.ToArray());
        foreach (var e in events) Append(e);
        RefreshActive();
    }
    private void RefreshActive()
    {
        var next = engine.Active.Select(Bounded).OrderBy(e => e.Key, StringComparer.Ordinal).ToArray();
        if (!active.SequenceEqual(next)) { active = next; activeRevision = checked(activeRevision + 1); }
    }
    public void RecordOperation(DeviceServiceRequest request, DeviceServiceReply reply)
    {
        if (!settings.Enabled || !settings.OperationFailures || !Failed(reply) ||
            !(DeviceServiceProtocol.IsWrite(request.Operation) || request.Operation is DeviceOperation.ReleaseSession or DeviceOperation.LightingLibrary ||
                request.Configuration?.Operation is DeviceConfigurationOperation.SaveNotifications or DeviceConfigurationOperation.SaveDashboard or DeviceConfigurationOperation.SaveDeviceName or
                    DeviceConfigurationOperation.SaveCoolingGroup or DeviceConfigurationOperation.SaveCoolingStartup or DeviceConfigurationOperation.SaveDialDisplay or DeviceConfigurationOperation.RegisterMonitoring or DeviceConfigurationOperation.Apply)) return;
        var failures = reply.Result?.Targets.Where(t => t.Outcome is not (Outcome.Confirmed or Outcome.WrittenUnverified) || t.Persistence?.State == DevicePolicySaveState.Failed)
            .Select(t => t.Target.EndpointId + " · " + t.Message).ToArray() ?? [];
        Append(new("operation:" + request.Id, "操作結果", "operation-failed",
            failures.Length > 0 ? string.Join(" / ", failures) : reply.Error ?? reply.Result?.Message ?? "操作を完了できませんでした", clock.GetUtcNow()));
    }
    // Success is the IPC acknowledgement; a completed hardware operation may still be partial/failed.
    public static bool Failed(DeviceServiceReply reply) => !reply.Success || reply.Result?.Outcome is Outcome.Partial or Outcome.Failed or Outcome.Rejected or Outcome.Unknown;
    public void Test()
    {
        if (!settings.Enabled || !settings.Desktop) throw new InvalidOperationException("通知とWindows通知を有効にしてください");
        Append(new("test", "通知テスト", "test", "FlowDeckの通知テストです", clock.GetUtcNow()));
    }
    public DeviceNotificationFeed Read(DeviceNotificationQuery query)
    {
        if (!DeviceNotificationProtocol.Valid(query) || query.After > sequence || query.ActiveOffset > active.Length ||
            query.ActiveRevision is { } revision && revision != activeRevision) throw new IOException("通知の取得位置が変わりました。先頭から再取得してください");
        var oldest = history.TryPeek(out var first) ? first.Sequence : sequence + 1;
        var events = history.Where(e => e.Sequence > query.After).Take(query.Limit).ToArray();
        return new(sequence, oldest, events.LastOrDefault()?.Sequence ?? query.After, query.After < oldest - 1,
            events, active.Skip(query.ActiveOffset).Take(query.Limit).ToArray(), query.ActiveOffset, active.Length, activeRevision, settings.Enabled, settings.Desktop, Error);
    }
}
