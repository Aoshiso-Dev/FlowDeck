using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceImportChoice(EndpointReference? Target,string Name);
public sealed record DeviceImportEndpoint(EndpointReference Source,string Name,EndpointKind Kind,DeviceImportChoice[] Choices);
public sealed record DeviceSettingsImportEdit(string Epoch,string Generation,long Topology,DeviceConfigurationPackage Configuration,string[] Changes);
public sealed record DeviceSettingsImportPreview(DeviceSettingsImportEdit Edit,ServiceConfigurationPreview Transfer);

/// <summary>Every remap is explicit. Unmapped references stay registered under an isolated identity and cannot own hardware.</summary>
public sealed class DeviceSettingsImportDraft
{
    private readonly DeviceWorkspaceSnapshot original;
    private readonly DeviceSettingsArchive archive;
    private readonly Dictionary<EndpointReference,EndpointReference?> mappings=[];
    private bool available;
    public bool Conflict { get; private set; }
    public bool Confirmed { get; set; }
    public bool SameInstallation { get; }
    public DeviceImportEndpoint[] Endpoints { get; }
    public int MappedCount=>mappings.Values.Count(t=>t is not null);
    public DateTimeOffset CreatedUtc=>archive.CreatedUtc;
    public DeviceSettingsImportDraft(DeviceWorkspaceSnapshot snapshot,DeviceSettingsArchive source)
    {
        original=Contract.Copy(snapshot); archive=DeviceSettingsArchives.Read(DeviceSettingsArchives.Write(source));
        SameInstallation=archive.Purpose=="backup"&&archive.Configuration.Bindings.InstallationId is { } installation&&installation==original.Configuration.Configuration.Bindings.InstallationId;
        var required=new Dictionary<EndpointReference,HashSet<DeviceFeature>>();
        void Need(EndpointReference target,DeviceFeature feature) { if(!required.TryGetValue(target,out var set)) required[target]=set=[]; set.Add(feature); }
        foreach(var item in archive.Configuration.Dashboard.Items) Need(item.Target,item.Measurement);
        foreach(var rule in archive.Configuration.Preferences.Notifications.Rules??[]) Need(rule.Target,rule.Measurement);
        var panelKinds = new Dictionary<EndpointReference,DeviceKind>();
        var panel = archive.Configuration.Preferences.M5Selection;
        if(panel?.Cpu is { } panelCpu) { Need(panelCpu,DeviceFeature.Temperature); panelKinds[panelCpu]=DeviceKind.Processor; }
        if(panel?.Gpu is { } panelGpu) { Need(panelGpu,DeviceFeature.Temperature); panelKinds[panelGpu]=DeviceKind.Graphics; }
        foreach(var item in panel?.Motherboard??[]) { Need(item.Target,item.Measurement); panelKinds[item.Target]=DeviceKind.Motherboard; }
        foreach(var endpoint in archive.Configuration.Cooling.Endpoints)
        {
            foreach(var rule in endpoint.Safety.ManualProtection) Need(rule.Sensor,DeviceFeature.Temperature);
            foreach(var rule in endpoint.Safety.Stop?.Temperatures??[]) Need(rule.Sensor,DeviceFeature.Temperature);
            foreach(var curve in endpoint.Profiles.SelectMany(p=>p.Curves)) Need(curve.Sensor,DeviceFeature.Temperature);
        }
        foreach(var curve in (archive.Configuration.Cooling.ProfileTemplates??[]).SelectMany(p=>p.Curves)) Need(curve.Sensor,DeviceFeature.Temperature);
        var choiceCache=new Dictionary<(EndpointKind,string),DeviceImportChoice[]>();
        var detected=original.State.Inventory.Devices.Where(d=>d.Connected).SelectMany(d=>d.Device.Endpoints.Select(e=>(Device:d.Device,Endpoint:e,
            Choice:new DeviceImportChoice(new(d.Device.Id,e.Id),DeviceMonitoringDraft.DisplayDeviceName(d.Device,original.State.Inventory)+" / "+e.Name)))).ToArray();
        Endpoints=archive.Configuration.Devices.Devices.SelectMany(d=>d.Endpoints.Select(e=>
        {
            var reference=new EndpointReference(d.Id,e.Id);
            var features=required.GetValueOrDefault(reference)??[]; var panelKind=panelKinds.TryGetValue(reference,out var selectedKind)?(DeviceKind?)selectedKind:null;
            var key=(e.Kind,string.Join(",",features.Order())+"/"+panelKind);
            if(!choiceCache.TryGetValue(key,out var choices)) choiceCache[key]=choices=[new(null,"未関連として保持"),..detected.Where(p=>p.Endpoint.Kind==e.Kind&&
                (panelKind is null||p.Device.Kind==panelKind)&&
                features.All(f=>p.Endpoint.Capabilities.Any(c=>c.Feature==f&&c.Support==ImplementationSupport.Supported))).Select(p=>p.Choice)];
            // Stable IDs may restore monitoring references; only a matching stored physical binding can retain control confirmations.
            var identity=original.State.Inventory.Devices.Any(a=>a.Connected&&a.Device.Id==d.Id&&a.Device.Provider==d.Provider&&a.Device.Kind==d.Kind);
            mappings[reference]=SameInstallation&&identity&&choices.Any(c=>c.Target==reference)?reference:null;
            return new DeviceImportEndpoint(reference,d.Name+" / "+e.Name,e.Kind,choices);
        })).ToArray();
        Update(snapshot);
    }
    private bool DeviceMatches(string id)=>archive.Configuration.Bindings.Devices.SingleOrDefault(b=>b.DeviceId==id) is { } binding&&
        original.Configuration.Candidates.Contains(binding)&&original.State.Inventory.Devices.Any(d=>d.Device.Id==id&&d.Connected);
    public EndpointReference? Mapping(EndpointReference source)=>mappings.TryGetValue(source,out var target)?target:throw new ArgumentException("復元元の端子がありません");
    public void Map(EndpointReference source,EndpointReference? target)
    {
        var endpoint=Endpoints.SingleOrDefault(e=>e.Source==source)??throw new ArgumentException("復元元の端子がありません");
        if(!endpoint.Choices.Any(c=>c.Target==target)) throw new InvalidOperationException("対応する種類の端子を選択してください");
        if(target is not null&&mappings.Any(p=>p.Key!=source&&p.Value==target)) throw new InvalidOperationException("その端子は別の項目に割り当てられています");
        if(mappings[source]==target) return; mappings[source]=target; Confirmed=false;
    }
    private bool TrustedDevice(string id)=>SameInstallation&&DeviceMatches(id)&&
        Endpoints.Where(e=>e.Source.DeviceId==id).All(e=>Mapping(e.Source)==e.Source);
    public string MappingStatus(EndpointReference source)=>Mapping(source) is null?"未関連・操作無効":
        TrustedDevice(source.DeviceId)?"同じ個体・構成と一致":"参照先あり・制御には再設定が必要";
    public void Update(DeviceWorkspaceSnapshot snapshot)
    {
        Conflict|=snapshot.State.Inventory.Epoch!=original.State.Inventory.Epoch||snapshot.State.Inventory.TopologyRevision!=original.State.Inventory.TopologyRevision||
            snapshot.Configuration.Generation!=original.Configuration.Generation;
        foreach(var id in mappings.Values.OfType<EndpointReference>().Select(t=>t.DeviceId).Distinct())
        {
            var before=original.State.Inventory.Devices.Single(d=>d.Device.Id==id); var now=snapshot.State.Inventory.Devices.SingleOrDefault(d=>d.Device.Id==id);
            Conflict|=now is null||now.Generation!=before.Generation||original.Configuration.Candidates.SingleOrDefault(b=>b.DeviceId==id)!=snapshot.Configuration.Candidates.SingleOrDefault(b=>b.DeviceId==id);
        }
        available=snapshot.Connected&&!snapshot.ExitAttempted&&!snapshot.State.Releasing&&!snapshot.State.ConfigurationChanging;
    }
    public string? Problem=>Conflict?"構成が変わりました。復元ファイルを再取得してください":!available?"サービスへ接続されていません":
        !Confirmed?"機器との対応と復元内容の確認が必要です":null;
    public DeviceSettingsImportEdit Capture()
    {
        if(Problem is { } problem) throw new InvalidOperationException(problem);
        var source=archive.Configuration; var local=original.Configuration.Configuration;
        var installation=local.Bindings.InstallationId??throw new IOException("現在のインストール識別がありません");
        EndpointReference Map(EndpointReference old)=>mappings[old]??new(DeviceIdentity.Create(installation,"import-unbound",archive.Sha256+"/"+old.DeviceId),old.EndpointId);
        bool Trusted(EndpointReference old)=>TrustedDevice(old.DeviceId)&&Map(old)==old;
        var records=source.Devices.Devices.SelectMany(d=>d.Endpoints.Select(e=>(Device:d,Endpoint:e,Old:new EndpointReference(d.Id,e.Id),Target:Map(new(d.Id,e.Id))))).ToArray();
        if(records.DistinctBy(r=>r.Target).Count()!=records.Length) throw new IOException("復元先の端子が重複しています");
        var devices=records.GroupBy(r=>r.Target.DeviceId).Select(group=>
        {
            var first=group.First(); var actual=original.State.Inventory.Devices.SingleOrDefault(d=>d.Device.Id==group.Key)?.Device;
            var trusted=actual is not null&&TrustedDevice(first.Device.Id)&&group.All(r=>r.Device.Id==first.Device.Id);
            return new RegisteredDevice(group.Key,trusted||actual is null?first.Device.Name:DeviceMonitoringDraft.DisplayDeviceName(actual,original.State.Inventory),
                actual?.Provider??first.Device.Provider,actual?.Kind??first.Device.Kind,
                group.Select(r=>r.Endpoint with { Id=r.Target.EndpointId,Setup=r.Endpoint.Setup is { } setup&&!Trusted(r.Old)?setup with { LedCountConfirmed=false }:r.Endpoint.Setup }).ToArray());
        }).ToArray();
        CoolingSafety Safety(CoolingSafety safety)=>safety with { ManualProtection=safety.ManualProtection.Select(r=>r with { Sensor=Map(r.Sensor) }).ToArray(),
            Stop=safety.Stop is { } stop?stop with { Temperatures=stop.Temperatures.Select(r=>r with { Sensor=Map(r.Sensor) }).ToArray() }:null };
        CoolingTemperatureProfile Profile(CoolingTemperatureProfile profile)=>profile with { Curves=profile.Curves.Select(c=>c with { Sensor=Map(c.Sensor) }).ToArray() };
        EndpointGroup Group(EndpointGroup group)=>group with { Targets=group.Targets.Select(Map).ToArray() };
        EndpointLighting Light(EndpointLighting light)=>light with { Target=Map(light.Target) };
        SynchronizedLighting Sync(SynchronizedLighting sync)=>sync with { Targets=sync.Targets.Select(Map).ToArray() };
        DeviceLightingScene Scene(DeviceLightingScene scene)=>scene with { Individual=scene.Individual.Select(Light).ToArray(),Synchronized=scene.Synchronized.Select(Sync).ToArray(),
            Requirements=scene.Requirements?.Select(r=>r with { Target=Map(r.Target) }).ToArray() };
        var cooling=source.Cooling with { Endpoints=source.Cooling.Endpoints.Select(e=>e with { Target=Map(e.Target),WiringConfirmed=Trusted(e.Target)&&e.WiringConfirmed,
            Safety=Safety(e.Safety),Profiles=e.Profiles.Select(Profile).ToArray(),SavedPolicy=null,ResumeOnStart=false }).ToArray(),Groups=source.Cooling.Groups.Select(Group).ToArray(),
            ProfileTemplates=source.Cooling.ProfileTemplates?.Select(Profile).ToArray() };
        var light=source.Lighting with { Scenes=source.Lighting.Scenes.Select(Scene).ToArray(),Groups=source.Lighting.Groups.Select(Group).ToArray(),
            Names=source.Lighting.Names.Select(n=>n with { Target=Map(n.Target) }).ToArray(),SyncTargets=source.Lighting.SyncTargets.Select(Map).ToArray(),
            Adjustments=source.Lighting.Adjustments.Select(a=>a with { Baseline=a.Baseline is { } baseline?Scene(baseline):null }).ToArray(),
            Preferences=source.Lighting.Preferences?.Select(Light).ToArray(),Endpoints=source.Lighting.Endpoints?.Select(e=>e with { Target=Map(e.Target),WiringConfirmed=Trusted(e.Target)&&e.WiringConfirmed }).ToArray(),
            SavedSynchronization=source.Lighting.SavedSynchronization?.Select(Sync).ToArray(),SavedFrameGroups=source.Lighting.SavedFrameGroups?.Select(g=>g.Select(Map).ToArray()).ToArray(),
            TargetAdjustments=source.Lighting.TargetAdjustments?.Select(a=>a with { Target=Map(a.Target) }).ToArray() };
        var preferences=(SameInstallation?source:DeviceConfigurationCodec.Portable(source)).Preferences;
        if(preferences.M5Selection is { } selectedPanel)
        {
            var keepHub = selectedPanel.Hub is { } hub && TrustedDevice(hub.DeviceId) && source.Bindings.Devices.Contains(hub);
            if(!keepHub) selectedPanel=selectedPanel.WithoutControl();
            selectedPanel=selectedPanel with { Cpu=selectedPanel.Cpu is { } cpu?Map(cpu):null, Gpu=selectedPanel.Gpu is { } gpu?Map(gpu):null,
                Motherboard=selectedPanel.Motherboard.Select(i=>i with { Target=Map(i.Target) }).ToArray() };
            preferences=preferences with { M5Selection=selectedPanel };
        }
        preferences=preferences with { M5ConnectOnStart=false };
        preferences=preferences with { Startup=preferences.Startup with { ResumeControl=false },Notifications=preferences.Notifications with {
            Rules=preferences.Notifications.Rules?.Select(r=>r with { Target=Map(r.Target) }).ToArray() } };
        var next=new DeviceConfigurationPackage(new(2,devices),new(2,installation,source.Bindings.Devices.Where(b=>TrustedDevice(b.DeviceId)&&devices.Any(d=>d.Id==b.DeviceId)).ToArray()),
            cooling,light,source.Dashboard with { Items=source.Dashboard.Items.Select(i=>i with { Target=Map(i.Target) }).ToArray() },preferences,
            new(2,"native-v2",archive.Sha256,[],[]));
        _=DeviceConfigurationCodec.Read(DeviceConfigurationCodec.Write(next));
        var changes=DeviceSettingsDifferences.Describe(local,next).ToList();
        changes.Add($"機器との対応: {MappedCount} / {Endpoints.Length}端子（未関連 {Endpoints.Length-MappedCount}）");
        changes.Add("実行中のすべての制御を変更前の終了設定で終了し、監視状態で復元");
        changes.Add("保存済み冷却方針・起動時の制御再開を解除");
        return new(original.State.Inventory.Epoch,original.Configuration.Generation,original.State.Inventory.TopologyRevision,Contract.Copy(next),changes.ToArray());
    }
}
