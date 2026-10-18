using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceRegistrationRemovalEdit(string Epoch, string Generation, long Topology, long DeviceGeneration,
    string DeviceId, DeviceConfigurationPackage Configuration, string[] Changes)
{
    public bool Matches(DeviceWorkspaceSnapshot? snapshot) => snapshot is { Connected: true, ExitAttempted: false } &&
        !snapshot.State.Releasing && !snapshot.State.ConfigurationChanging && snapshot.State.Inventory.Epoch == Epoch &&
        snapshot.Configuration.Generation == Generation && snapshot.State.Inventory.TopologyRevision == Topology &&
        snapshot.Configuration.Configuration.Devices.Devices.Any(d => d.Id == DeviceId) &&
        (snapshot.State.Inventory.Devices.SingleOrDefault(d => d.Device.Id == DeviceId)?.Generation ?? 0) == DeviceGeneration;
}
public sealed record DeviceRegistrationRemovalPreview(DeviceRegistrationRemovalEdit Edit, ServiceConfigurationPreview Transfer);

/// <summary>Reviewable removal of saved registration and its references. Never deletes a driver's recovery records.</summary>
public static class DeviceRegistrationRemoval
{
    public static string? Problem(DeviceWorkspaceSnapshot snapshot, string deviceId)
    {
        if (!snapshot.Connected || snapshot.ExitAttempted || snapshot.State.Releasing || snapshot.State.ConfigurationChanging)
            return "接続と制御終了の状態を確認してください";
        var config=snapshot.Configuration.Configuration;
        if (!config.Devices.Devices.Any(d=>d.Id==deviceId)) return "解除する保存登録がありません";
        bool Uses(CoolingSafety? s) => s is not null && (s.ManualProtection.Any(t=>t.Sensor.DeviceId==deviceId) ||
            (s.Stop?.Temperatures.Any(t=>t.Sensor.DeviceId==deviceId) ?? false));
        bool Profile(CoolingTemperatureProfile? p) => p?.Curves.Any(t=>t.Sensor.DeviceId==deviceId)==true;
        var dependencies=config.Cooling.Endpoints.Where(e=>e.Target.DeviceId!=deviceId &&
            (Uses(e.Safety)||e.Profiles.Any(Profile)||Profile(e.SavedPolicy?.Profile)||Uses(e.SavedPolicy?.SafetySnapshot))).ToArray();
        if(dependencies.Length==0) return null;
        string Name(EndpointReference t)
        {
            var d=config.Devices.Devices.Single(d=>d.Id==t.DeviceId);
            return d.Name+" / "+d.Endpoints.Single(e=>e.Id==t.EndpointId).Name;
        }
        return "先に温度参照・保護条件を変更してください："+string.Join("、",dependencies.Select(e=>Name(e.Target)));
    }
    public static DeviceRegistrationRemovalEdit Capture(DeviceWorkspaceSnapshot snapshot,string deviceId)
    {
        var config=snapshot.Configuration.Configuration; DeviceConfigurationCodec.Validate(config);
        if(Problem(snapshot,deviceId) is { } error) throw new InvalidOperationException(error);
        var device=config.Devices.Devices.Single(d=>d.Id==deviceId);
        bool Keep(EndpointReference t)=>t.DeviceId!=deviceId;
        var changes=new List<string>{device.Name+" の保存登録を解除", "機器ID："+deviceId,
            "機器名・端子名・実機との関連付けを削除",
            $"冷却設定 {config.Cooling.Endpoints.Count(e=>!Keep(e.Target))}件、照明設定 {(config.Lighting.Endpoints??[]).Count(e=>!Keep(e.Target))}件を削除",
            $"モニター {(config.Dashboard.Items.Count(e=>!Keep(e.Target)))}項目、通知 {(config.Preferences.Notifications.Rules??[]).Count(e=>!Keep(e.Target))}件を削除"};
        EndpointGroup[] Groups(EndpointGroup[] groups,string kind) => groups.Select(g=> {
            var remaining=g.Targets.Where(Keep).ToArray();
            if(remaining.Length==g.Targets.Length) return g;
            changes.Add(kind+"グループ「"+g.Name+"」："+g.Targets.Length+" → "+remaining.Length+(remaining.Length==0?"（削除）":""));
            return remaining.Length==0?null:g with { Targets=remaining,Revision=checked(g.Revision+1) };
        }).OfType<EndpointGroup>().ToArray();
        DeviceLightingScene? Scene(DeviceLightingScene scene,bool describe)
        {
            var total=scene.Individual.Length+scene.Synchronized.Sum(s=>s.Targets.Length);
            var individual=scene.Individual.Where(i=>Keep(i.Target)).ToArray();
            var synchronized=scene.Synchronized.Select(s=>s with { Targets=s.Targets.Where(Keep).ToArray() }).Where(s=>s.Targets.Length>0).ToArray();
            var remaining=individual.Length+synchronized.Sum(s=>s.Targets.Length);
            if(total==remaining) return scene;
            if(describe) changes.Add("照明シーン「"+scene.Name+"」："+total+" → "+remaining+(remaining==0?"（削除）":""));
            return remaining==0?null:DeviceLightingSceneRequirements.Stamp(scene with { Individual=individual,Synchronized=synchronized,Revision=checked(scene.Revision+1) });
        }
        var coolingGroups=Groups(config.Cooling.Groups,"冷却"); var lightingGroups=Groups(config.Lighting.Groups,"照明");
        var scenes=config.Lighting.Scenes.Select(s=>Scene(s,true)).OfType<DeviceLightingScene>().ToArray();
        var adjustments=config.Lighting.Adjustments.Where(a=>a.GroupId=="all"||lightingGroups.Any(g=>g.Id==a.GroupId)).Select(a=> {
            if(a.Baseline is null) return a;
            var baseline=Scene(a.Baseline,false); return baseline is null?null:a with { Baseline=baseline };
        }).OfType<DeviceBrightness>().ToArray();
        var templates=config.Cooling.ProfileTemplates?.Where(p=>!p.Curves.Any(c=>!Keep(c.Sensor))).ToArray();
        if((config.Cooling.ProfileTemplates?.Length??0)!=(templates?.Length??0)) changes.Add("この機器の温度を参照するカーブのテンプレートを削除");
        var selection=config.Preferences.DialDisplay;
        if(selection is not null)
        {
            var nextSelection=selection with { Cpu=selection.Cpu is { } cpu&&!Keep(cpu)?null:selection.Cpu,
                Gpu=selection.Gpu is { } gpu&&!Keep(gpu)?null:selection.Gpu };
            if(selection!=nextSelection) changes.Add("ダイヤルのこの機器の温度表示を解除");
            selection=nextSelection;
        }
        var light=config.Lighting with {
            Scenes=scenes,Groups=lightingGroups,Names=config.Lighting.Names.Where(n=>Keep(n.Target)).ToArray(),
            SyncTargets=config.Lighting.SyncTargets.Where(Keep).ToArray(),Adjustments=adjustments,
            Endpoints=config.Lighting.Endpoints?.Where(e=>Keep(e.Target)).ToArray(),
            Preferences=config.Lighting.Preferences?.Where(e=>Keep(e.Target)).ToArray(),
            SavedSynchronization=config.Lighting.SavedSynchronization?.Select(s=>s with { Targets=s.Targets.Where(Keep).ToArray() }).Where(s=>s.Targets.Length>0).ToArray(),
            SavedFrameGroups=config.Lighting.SavedFrameGroups?.Select(g=>g.Where(Keep).ToArray()).Where(g=>g.Length>0).ToArray(),
            TargetAdjustments=config.Lighting.TargetAdjustments?.Where(a=>Keep(a.Target)).ToArray()
        };
        if(System.Text.Json.JsonSerializer.Serialize(light,Contract.Json)!=System.Text.Json.JsonSerializer.Serialize(config.Lighting,Contract.Json))
            light=light with { Revision=checked(light.Revision+1) };
        var next=config with {
            Devices=config.Devices with { Devices=config.Devices.Devices.Where(d=>d.Id!=deviceId).ToArray() },
            Bindings=config.Bindings with { Devices=config.Bindings.Devices.Where(b=>b.DeviceId!=deviceId).ToArray() },
            Cooling=config.Cooling with { Endpoints=config.Cooling.Endpoints.Where(e=>Keep(e.Target)).ToArray(),Groups=coolingGroups,ProfileTemplates=templates },
            Lighting=light,Dashboard=config.Dashboard with { Items=config.Dashboard.Items.Where(i=>Keep(i.Target)).ToArray() },
            Preferences=config.Preferences with { DialDisplay=selection,Notifications=config.Preferences.Notifications with {
                Rules=config.Preferences.Notifications.Rules?.Where(n=>Keep(n.Target)).ToArray() } },
            Migration=config.Migration with { References=config.Migration.References.Where(r=>Keep(r.Target)).ToArray() }
        };
        DeviceConfigurationCodec.Validate(next);
        changes.Add("実行中のすべての制御を保存済みの終了方法で終了し、監視状態で保存");
        changes.Add("接続中の機器は検出一覧に残ります。制御には端子の再設定が必要です");
        return new(snapshot.State.Inventory.Epoch,snapshot.Configuration.Generation,snapshot.State.Inventory.TopologyRevision,
            snapshot.State.Inventory.Devices.SingleOrDefault(d=>d.Device.Id==deviceId)?.Generation??0,deviceId,Contract.Copy(next),changes.ToArray());
    }
}