using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceNotificationTarget(EndpointReference Target, DeviceFeature Measurement, string Name, string DeviceName)
{
    public string Key => DeviceDashboard.Key(Target, Measurement);
    public string Label => Name + (Name == DeviceName ? "" : " · " + DeviceName) + (Measurement == DeviceFeature.Temperature ? " · °C" : " · RPM");
}
public sealed class DeviceNotificationDraft
{
    private DeviceWorkspaceSnapshot? baseline, latest;
    private string original = "";
    private (string Epoch, string Generation, long Topology)? targetVersion;
    private DeviceNotificationTarget[]? targets;
    public DeviceNotificationSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public bool Conflict { get; private set; }
    public bool Connected => latest is { Connected: true, ExitAttempted: false };
    public string? Problem => !Connected ? "サービス未接続" : Conflict ? "別の操作で変更あり。再取得してください" :
        DeviceNotifications.Validate(Settings) ?? ((Settings.Rules ?? []).Any(r => !Registered(r.Target, r.Measurement, latest!)) ? "通知する対象を登録してください" : null);
    public bool CanSave => Dirty && Problem is null;
    private static string Signature(DeviceNotificationSettings settings) => JsonSerializer.Serialize(settings, Contract.Json);
    public void Update(DeviceWorkspaceSnapshot snapshot)
    {
        var version = (snapshot.State.Inventory.Epoch, snapshot.Configuration.Generation, snapshot.State.Inventory.TopologyRevision);
        if (targetVersion != version) { targetVersion = version; targets = null; }
        latest = snapshot;
        if (baseline is null || !Dirty) Reset(snapshot);
        else if (baseline.State.Inventory.Epoch != snapshot.State.Inventory.Epoch || baseline.Session != snapshot.Session ||
            baseline.Configuration.Generation != snapshot.Configuration.Generation) Conflict = true;
    }
    public void Reset(DeviceWorkspaceSnapshot snapshot)
    {
        targets = null;
        baseline = latest = snapshot; Settings = Contract.Copy(snapshot.Configuration.Configuration.Preferences.Notifications);
        original = Signature(Settings); Dirty = Conflict = false;
    }
    public void Change(DeviceNotificationSettings settings)
    { Settings = Contract.Copy(settings); targets = null; Dirty = Signature(Settings) != original; }
    public void ChangeRule(DeviceNotificationRule rule)
    {
        if (!(Settings.Rules ?? []).Any(r => r.Id == rule.Id)) throw new ArgumentException("編集する通知条件がありません");
        Change(Settings with { Rules = Settings.Rules!.Select(r => r.Id == rule.Id ? rule : r).ToArray() });
    }
    public DeviceNotificationRule Add(DeviceNotificationTarget target)
    {
        if (latest is null || !Registered(target.Target, target.Measurement, latest) ||
            (Settings.Rules ?? []).Length >= 256 || (Settings.Rules ?? []).Any(r => r.Target == target.Target && r.Measurement == target.Measurement))
            throw new ArgumentException("追加できる通知対象を選択してください");
        var rule = new DeviceNotificationRule(Guid.NewGuid().ToString("N"), target.Target, target.Measurement,
            target.Name.Length <= 64 ? target.Name : target.Name[..64], target.Measurement == DeviceFeature.Temperature ? 85 : 300,
            target.Measurement == DeviceFeature.Temperature ? 2 : 50);
        Change(Settings with { Rules = [.. Settings.Rules ?? [], rule] }); return rule;
    }
    public void Remove(string id) => Change(Settings with { Rules = (Settings.Rules ?? []).Where(r => r.Id != id).ToArray() });
    public DeviceNotificationEdit Capture()
    {
        if (Problem is { } error) throw new InvalidOperationException(error);
        if (baseline?.Session is not { } session) throw new InvalidOperationException("現在の設定を再取得してください");
        return new(baseline.State.Inventory.Epoch, session, baseline.Configuration.Generation, Contract.Copy(Settings));
    }
    private static bool Registered(EndpointReference target, DeviceFeature feature, DeviceWorkspaceSnapshot snapshot)
    {
        var endpoint = snapshot.Configuration.Configuration.Devices.Devices.SingleOrDefault(d => d.Id == target.DeviceId)?.Endpoints.SingleOrDefault(e => e.Id == target.EndpointId);
        return endpoint is not null && (feature == DeviceFeature.Temperature ? endpoint.Kind == EndpointKind.Sensor :
            feature == DeviceFeature.Rpm && endpoint.Kind is EndpointKind.Sensor or EndpointKind.Cooling);
    }
    public DeviceNotificationTarget[] Targets()
    {
        if (latest is null) return [];
        if (targets is not null) return targets;
        var config = latest.Configuration.Configuration;
        var registered = config.Devices.Devices.SelectMany(d => d.Endpoints.Select(e => (Target: new EndpointReference(d.Id, e.Id), Device: d, Endpoint: e)))
            .ToDictionary(e => e.Target);
        var names = config.Dashboard.Items.ToDictionary(i => (i.Target, i.Measurement), i => i.Name);
        var result = new Dictionary<(EndpointReference, DeviceFeature), DeviceNotificationTarget>();
        void Add(EndpointReference target, DeviceFeature feature, string? fallback = null)
        {
            if (!registered.TryGetValue(target, out var value) || !(feature == DeviceFeature.Temperature ? value.Endpoint.Kind == EndpointKind.Sensor :
                feature == DeviceFeature.Rpm && value.Endpoint.Kind is EndpointKind.Sensor or EndpointKind.Cooling)) return;
            var name = names.GetValueOrDefault((target, feature)) ?? fallback ?? value.Endpoint.Name;
            result.TryAdd((target, feature), new(target, feature, name, value.Device.Name));
        }
        foreach (var device in latest.State.Inventory.Devices)
        foreach (var endpoint in device.Device.Endpoints)
        foreach (var capability in endpoint.Capabilities.Where(c => c.Feature is DeviceFeature.Temperature or DeviceFeature.Rpm && c.Support == ImplementationSupport.Supported))
            Add(new(device.Device.Id, endpoint.Id), capability.Feature);
        foreach (var item in config.Dashboard.Items) Add(item.Target, item.Measurement, item.Name);
        foreach (var rule in Settings.Rules ?? [])
        {
            Add(rule.Target, rule.Measurement, rule.Name);
            result.TryAdd((rule.Target, rule.Measurement), new(rule.Target, rule.Measurement, rule.Name, "未登録の対象"));
        }
        return targets = result.Values.ToArray();
    }
}
