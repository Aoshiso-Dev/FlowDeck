using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record CoolingSensorChoice(EndpointReference Target, string Name);

/// <summary>Editable cooling settings anchored to one configuration and physical inventory. Edits never actuate.</summary>
public sealed class DeviceCoolingSettingsDraft
{
    private readonly DeviceWorkspaceSnapshot original;
    private DeviceWorkspaceSnapshot latest;
    private readonly CoolingEndpointConfiguration saved;
    public EndpointReference Target { get; }
    public CoolingSafety Safety { get; private set; }
    public CoolingTemperatureProfile[] Profiles { get; private set; }
    public bool Confirmed { get; set; }
    public bool Conflict { get; private set; }
    public bool StopSupported { get; }
    public bool Pump { get; }
    public CoolingSensorChoice[] Sensors { get; }
    public DeviceCoolingSettingsDraft(DeviceWorkspaceSnapshot snapshot, EndpointReference target)
    {
        original = Contract.Copy(snapshot); latest = original; Target = target;
        var config = original.Configuration.Configuration;
        saved = config.Cooling.Endpoints.SingleOrDefault(e => e.Target == target) ?? throw new IOException("先に端子を設定してください");
        if (!saved.WiringConfirmed || !original.Configuration.Candidates.Any(b => b.DeviceId == target.DeviceId && config.Bindings.Devices.Contains(b)) ||
            !(original.Configuration.Setup ?? []).Any(s => s.Target == target && s.Options.CoolingExitActions.Length > 0))
            throw new IOException("先に端子の配線と出力を確認してください");
        Safety = Contract.Copy(saved.Safety); Profiles = Contract.Copy(saved.Profiles);
        StopSupported = original.Configuration.Setup!.Single(s=>s.Target==target).Options.CoolingStopSupported;
        Pump = config.Devices.Devices.Single(d=>d.Id==target.DeviceId).Endpoints.Single(e=>e.Id==target.EndpointId).Role=="pump" ||
            config.Dashboard.Items.Any(i=>i.Target==target&&i.Role=="pump");
        Sensors = original.State.Inventory.Devices.SelectMany(d => d.Device.Endpoints.Where(e => e.Kind == EndpointKind.Sensor &&
            e.Capabilities.Any(c => c.Feature == DeviceFeature.Temperature && c.Support == ImplementationSupport.Supported)).Select(e =>
            new CoolingSensorChoice(new(d.Device.Id,e.Id),
                DeviceMonitoringDraft.DisplayDeviceName(d.Device, original.State.Inventory) + " / " +
                (config.Devices.Devices.SingleOrDefault(r => r.Id == d.Device.Id)?.Endpoints.SingleOrDefault(r => r.Id == e.Id)?.Name ?? e.Name))))
            .ToArray();
        Update(snapshot);
    }
    public void Change(CoolingSafety safety, CoolingTemperatureProfile[] profiles)
    { Safety = Contract.Copy(safety); Profiles = Contract.Copy(profiles); Confirmed = false; }
    private EndpointReference[] Required => Safety.ManualProtection.Select(r => r.Sensor).Concat(Safety.Stop?.Temperatures.Select(t => t.Sensor) ?? [])
        .Concat(Profiles.SelectMany(p => p.Curves.Select(c => c.Sensor))).Distinct().ToArray();
    public void Update(DeviceWorkspaceSnapshot snapshot)
    {
        latest = snapshot;
        Conflict |= snapshot.Configuration.Generation != original.Configuration.Generation || snapshot.State.Inventory.Epoch != original.State.Inventory.Epoch ||
            snapshot.State.Inventory.TopologyRevision != original.State.Inventory.TopologyRevision;
        foreach (var id in Required.Select(t => t.DeviceId).Append(Target.DeviceId).Distinct())
        {
            var before = original.State.Inventory.Devices.SingleOrDefault(d => d.Device.Id == id);
            var after = snapshot.State.Inventory.Devices.SingleOrDefault(d => d.Device.Id == id);
            Conflict |= before is null || after is null || before.Generation != after.Generation ||
                original.Configuration.Candidates.SingleOrDefault(b => b.DeviceId == id) != snapshot.Configuration.Candidates.SingleOrDefault(b => b.DeviceId == id);
        }
    }
    public static double? Temperature(DeviceWorkspaceSnapshot snapshot, EndpointReference target)
    {
        var reading = DeviceDashboard.Read(new(target,DeviceFeature.Temperature,"温度","temperature"), snapshot.Configuration.Configuration.Devices,
            snapshot.State,snapshot.ElapsedSeconds,snapshot.Connected);
        return reading.Value is > 0 and < 150 ? reading.Value : null;
    }
    public string? Problem
    {
        get
        {
            if (Conflict) return "構成が変わりました。再取得してください";
            if (!latest.Connected || latest.ExitAttempted || latest.State.Releasing || latest.State.ConfigurationChanging ||
                !latest.State.Inventory.Devices.Any(d => d.Device.Id == Target.DeviceId && d.Connected)) return "機器へ接続されていません";
            if (DeviceConfigurationRules.SafetyReadiness(Safety) is { } error) return error;
            if (Safety.Stop is not null && !StopSupported) return "この端子は停止出力に対応していません。停止許可を外してください";
            if (Profiles.Length > 32 || Profiles.DistinctBy(p => p.Id).Count() != Profiles.Length ||
                Profiles.Any(p => DeviceConfigurationRules.ProfileReadiness(p, Safety) is not null)) return "カーブ・温度源・下降設定を確認してください";
            foreach (var target in Required)
                if (!Sensors.Any(s => s.Target == target) || !original.Configuration.Candidates.Any(b => b.DeviceId == target.DeviceId) || Temperature(latest,target) is null)
                    return "必要な温度を取得できません: " + (Sensors.SingleOrDefault(s => s.Target == target)?.Name ?? target.EndpointId);
            return Confirmed ? null : "温度カーブ・保護・停止条件の確認が必要です";
        }
    }
    public DeviceEndpointSetupEdit Capture()
    {
        Update(latest);
        if (Problem is { } problem) throw new InvalidOperationException(problem);
        var config = original.Configuration.Configuration;
        var devices = config.Devices.Devices.ToList(); var bindings = config.Bindings.Devices.ToList();
        var rebound = new HashSet<string>(); var changes = new List<string>();
        foreach (var group in Required.GroupBy(t => t.DeviceId))
        {
            var actual = original.State.Inventory.Devices.Single(d => d.Device.Id == group.Key).Device;
            var binding = original.Configuration.Candidates.Single(b => b.DeviceId == group.Key);
            var registered = devices.SingleOrDefault(d => d.Id == group.Key) ?? new(actual.Id, DeviceMonitoringDraft.DisplayDeviceName(actual,original.State.Inventory),actual.Provider,actual.Kind,[]);
            if (registered.Provider != actual.Provider || registered.Kind != actual.Kind) throw new IOException("温度源の種類が一致しません");
            var endpoints = registered.Endpoints.ToList();
            foreach (var target in group)
            {
                var endpoint = actual.Endpoints.Single(e => e.Id == target.EndpointId);
                if (endpoints.Any(e => e.Id == endpoint.Id && e.Kind != EndpointKind.Sensor)) throw new IOException("温度源の登録が一致しません");
                if (endpoints.All(e => e.Id != endpoint.Id)) endpoints.Add(new(endpoint.Id,endpoint.Name,EndpointKind.Sensor,endpoint.Role ?? ""));
            }
            if (!bindings.Contains(binding))
            {
                rebound.Add(group.Key); bindings.RemoveAll(b => b.DeviceId == group.Key); bindings.Add(binding);
                endpoints = endpoints.Select(e => e.Setup is { } setup ? e with { Setup=setup with { LedCountConfirmed=false } } : e).ToList();
                changes.Add("温度源の関連付け: " + registered.Name + " → 現在の個体・構成（同じ機器の制御端子は再確認）");
            }
            devices.RemoveAll(d => d.Id == group.Key); devices.Add(registered with { Endpoints=endpoints.ToArray() });
        }
        var profiles = Profiles.Select(p => {
            var old = saved.Profiles.SingleOrDefault(o => o.Id == p.Id);
            var same = old is not null && SettingsTransfer.Hash(p with { Revision=old.Revision }) == SettingsTransfer.Hash(old);
            return Contract.Copy(p with { Revision=same ? old!.Revision : checked((old?.Revision ?? 0)+1) });
        }).ToArray();
        var cooling = config.Cooling.Endpoints.Select(e => e.Target == Target ? e with { Safety=Safety,Profiles=profiles,SavedPolicy=null,ResumeOnStart=false } :
            rebound.Contains(e.Target.DeviceId) ? e with { WiringConfirmed=false,SavedPolicy=null,ResumeOnStart=false } : e).ToArray();
        var next = config with { Devices=config.Devices with { Devices=devices.ToArray() },Bindings=config.Bindings with { Devices=bindings.ToArray() },
            Cooling=config.Cooling with { Endpoints=cooling },Lighting=config.Lighting with { Endpoints=config.Lighting.Endpoints?.Select(e =>
                rebound.Contains(e.Target.DeviceId) ? e with { WiringConfirmed=false } : e).ToArray() } };
        DeviceConfigurationCodec.Validate(next);
        foreach (var old in saved.Profiles.Where(p => profiles.All(n => n.Id != p.Id))) changes.Add("カーブ削除: " + old.Name);
        string Sensor(EndpointReference target) => Sensors.Single(s => s.Target == target).Name;
        foreach (var p in profiles.Where(p => saved.Profiles.All(o => o.Id != p.Id || SettingsTransfer.Hash(o) != SettingsTransfer.Hash(p))))
        {
            changes.Add("カーブ: " + p.Name + $" / 下降待ち {p.DescentDelaySeconds:0.#}秒・毎秒 {p.DescentPerSecond:0.#}%");
            foreach (var c in p.Curves) changes.Add(Sensor(c.Sensor) + ": " + string.Join(" → ",c.Points.Select(v => $"{v.Temperature:0.#}°C / {v.Duty}%")) + $" / 過熱 {c.Overheat:0.#}°C・復帰 {c.ResumeBelow:0.#}°C未満");
        }
        if (!DeviceConfigurationRules.SameSafety(saved.Safety,Safety))
        {
            changes.Add("固定出力の温度保護: " + saved.Safety.ManualProtection.Length + " → " + Safety.ManualProtection.Length + "件");
            foreach (var rule in Safety.ManualProtection) changes.Add(Sensor(rule.Sensor) + $": 過熱 {rule.Overheat:0.#}°C・復帰 {rule.ResumeBelow:0.#}°C未満");
        }
        if (SettingsTransfer.Hash(saved.Safety.Stop)!=SettingsTransfer.Hash(Safety.Stop))
        {
            if (Safety.Stop is { } stop)
            {
                changes.Add((Pump?"ポンプ":"ファン")+$"の停止を許可 / 再始動 {stop.RestartDuty}%・{stop.RestartSeconds:0.###}秒");
                changes.Add("停止条件（すべて満たす）: "+string.Join(" / ",stop.Temperatures.Select(r=>Sensor(r.Sensor)+$" {r.Below:0.#}°C未満")));
            }
            else changes.Add("停止許可を解除 / 要求0%にも端子の最低出力を適用");
        }
        changes.Add("この端子の保存済み制御方針・自動再開を解除");
        changes.Add("変更する端子の制御を終了して保存。ほかのファンの制御は継続（実機の関連付けを更新する機器を除く）");
        return new(original.State.Inventory.Epoch,original.Configuration.Generation,original.State.Inventory.TopologyRevision,
            original.State.Inventory.Devices.Single(d => d.Device.Id == Target.DeviceId).Generation,Target,Contract.Copy(next),changes.ToArray());
    }
}
