using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record CoolingGroupMember(EndpointReference Target,string Name);
public sealed class DeviceCoolingGroupEditorDraft
{
    private readonly DeviceWorkspaceSnapshot original;
    private DeviceWorkspaceSnapshot latest;
    private readonly EndpointGroup? saved;
    private readonly HashSet<EndpointReference> selected;
    public string Id { get; }
    public string Name { get; set; }
    public CoolingGroupMember[] Candidates { get; }
    public bool Existing=>saved is not null;
    public bool Conflict { get; private set; }
    public DeviceCoolingGroupEditorDraft(DeviceWorkspaceSnapshot snapshot,string? id=null)
    {
        original=Contract.Copy(snapshot); latest=original; saved=id is null?null:original.Configuration.Configuration.Cooling.Groups.Single(g=>g.Id==id);
        Id=saved?.Id??Guid.NewGuid().ToString("N"); Name=saved?.Name??""; selected=(saved?.Targets??[]).ToHashSet();
        Candidates=original.Configuration.Configuration.Devices.Devices.SelectMany(d=>d.Endpoints.Where(e=>e.Kind==EndpointKind.Cooling)
            .Select(e=>new CoolingGroupMember(new(d.Id,e.Id),d.Name+" / "+e.Name+" · "+d.Id[^Math.Min(8,d.Id.Length)..]))).ToArray();
    }
    public bool Selected(EndpointReference target)=>selected.Contains(target);
    public void Select(EndpointReference target,bool value)
    {
        if(!Candidates.Any(c=>c.Target==target)) throw new ArgumentException("登録済みの冷却端子を選んでください");
        if(value) selected.Add(target); else selected.Remove(target);
    }
    public void Update(DeviceWorkspaceSnapshot snapshot)
    {
        latest=snapshot;
        Conflict|=snapshot.State.Inventory.Epoch!=original.State.Inventory.Epoch||snapshot.Configuration.Generation!=original.Configuration.Generation;
    }
    public string? Problem=>Conflict?"設定が変わりました。再取得してください":!latest.Connected||latest.ExitAttempted?"サービスへ接続されていません":
        !Existing&&latest.Configuration.Configuration.Cooling.Groups.Length>=32?"冷却グループは32個までです":
        !DeviceConfigurationRules.Text(Name,80)?"グループ名を入力してください":selected.Count is <1 or >64?"1〜64端子を選択してください":null;
    public CoolingGroupSaveEdit Capture(bool delete=false)
    {
        if(delete?Conflict||!latest.Connected||!Existing:Problem is not null) throw new InvalidOperationException(Problem??"このグループを削除できません");
        return new(original.State.Inventory.Epoch,original.Configuration.Generation,new(Id,saved?.Revision??0,
            delete?null:new(Id,Name,EndpointKind.Cooling,Candidates.Where(c=>selected.Contains(c.Target)).Select(c=>c.Target).ToArray(),saved?.Revision??1)));
    }
}
public sealed class DeviceCoolingGroupDraft
{
    private DeviceWorkspaceSnapshot latest;
    private DeviceWorkspaceSnapshot? editing;
    public EndpointGroup Group { get; }
    public bool Dirty=>editing is not null;
    public bool Conflict { get; private set; }
    public int Duty { get; private set; }=50;
    public CoolingTemperatureProfile? Profile { get; private set; }
    public bool Automatic { get; private set; }
    public DeviceCoolingGroupDraft(DeviceWorkspaceSnapshot snapshot,string id)
    {
        latest=Contract.Copy(snapshot); Group=Contract.Copy(snapshot.Configuration.Configuration.Cooling.Groups.Single(g=>g.Id==id));
        var rows=Group.Targets.Select(t=>snapshot.State.Endpoints.SingleOrDefault(e=>e.Target==t)).ToArray();
        var requested=rows.All(r=>r?.Owned==true)?rows.Select(r=>r!.RequestedDuty).Distinct().ToArray():[];
        if(requested is [{ } duty]) Duty=duty;
        else if(rows.All(r=>r?.Owned==false))
        {
            var reported=Group.Targets.Select(t=>DeviceDashboard.Sample(snapshot.State,t,DeviceFeature.DutyRead)).ToArray();
            if(reported.All(s=>s is not null&&DeviceDashboard.Fresh(s,snapshot.ElapsedSeconds))&&reported.Select(s=>s!.Value).Distinct().ToArray() is [{ } value]) Duty=(int)Math.Clamp(Math.Round(value),0,100);
        }
        var saved=Group.Targets.Select(t=>snapshot.Configuration.Configuration.Cooling.Endpoints.SingleOrDefault(e=>e.Target==t)?.SavedPolicy?.Profile).ToArray();
        if(rows.All(r=>r?.Owned==true&&r.Cooling?.Mode==FanMode.Auto)&&saved.All(p=>p is not null)&&saved.Select(SettingsTransfer.Canonical).Distinct().Count()==1)
        { Automatic=true; Profile=Contract.Copy(saved[0]); }
    }
    public void Change(int duty,bool automatic,CoolingTemperatureProfile? profile)
    { editing??=Contract.Copy(latest); Duty=Math.Clamp(duty,0,100); Automatic=automatic; Profile=Contract.Copy(profile); }
    public void Update(DeviceWorkspaceSnapshot snapshot)
    {
        latest=snapshot;
        if(editing is not { } before) return;
        Conflict|=before.Configuration.Generation!=snapshot.Configuration.Generation||before.State.Inventory.Epoch!=snapshot.State.Inventory.Epoch||
            before.State.Inventory.TopologyRevision!=snapshot.State.Inventory.TopologyRevision||Group.Targets.Any(t=>
            before.State.Endpoints.SingleOrDefault(e=>e.Target==t)?.PolicyRevision!=snapshot.State.Endpoints.SingleOrDefault(e=>e.Target==t)?.PolicyRevision||
            before.State.Endpoints.SingleOrDefault(e=>e.Target==t)?.Generation!=snapshot.State.Endpoints.SingleOrDefault(e=>e.Target==t)?.Generation);
    }
    public string? Problem(DeviceOperation? operation=null)
    {
        if(Conflict) return "対象または設定が変わりました。再取得してください";
        if(!latest.Connected||latest.ExitAttempted||latest.ElapsedSeconds is <0 or >=3||!double.IsFinite(latest.ElapsedSeconds)) return "サービスの状態を更新してください";
        var current=latest.Configuration.Configuration.Cooling.Groups.SingleOrDefault(g=>g.Id==Group.Id);
        if(current?.Revision!=Group.Revision||current is null||!current.Targets.ToHashSet().SetEquals(Group.Targets)) return "冷却グループが変わりました。再取得してください";
        if(latest.State.Releasing||latest.State.ConfigurationChanging) return "処理中です";
        foreach(var target in Group.Targets)
        {
            var config=latest.Configuration.Configuration.Cooling.Endpoints.SingleOrDefault(e=>e.Target==target);
            if(operation==DeviceOperation.ReleaseCooling)
            {
                var row=new DeviceCoolingDraft(target); row.Update(latest.State,latest.Configuration.Generation,config,latest.ElapsedSeconds,latest.Connected);
                if(!row.CanRelease) return "制御を終了できない端子があります";
                continue;
            }
            if(config is null) return "端子の設定が必要です";
            if(Automatic&&Profile is null) return "カーブを選択してください";
            var policy=new CoolingSavedPolicy(Automatic?FanMode.Auto:FanMode.Manual,Automatic?null:Duty,Automatic?Profile:null,config.Safety);
            if(DeviceCoolingStartup.Problem(config with { SavedPolicy=policy },latest.Configuration.Configuration.Bindings,latest.State,latest.Configuration.Candidates,latest.ElapsedSeconds) is { } error)
                return Name(target)+": "+error;
        }
        return null;
    }
    public string Name(EndpointReference target)
    {
        var device=latest.Configuration.Configuration.Devices.Devices.Single(d=>d.Id==target.DeviceId);
        return device.Name+" / "+device.Endpoints.Single(e=>e.Id==target.EndpointId).Name;
    }
    public CoolingGroupControlEdit Capture(DeviceOperation? operation=null)
    {
        var kind=operation??(Automatic?DeviceOperation.SetAutomatic:DeviceOperation.SetDuty);
        if(Problem(kind) is { } problem) throw new InvalidOperationException(problem);
        var snapshot=editing??latest;
        return new(snapshot.State.Inventory.Epoch,snapshot.State.Inventory.TopologyRevision,new(snapshot.Configuration.Generation,Group.Id,Group.Revision),
            Group.Targets.Select(t=>{ var row=snapshot.State.Endpoints.Single(e=>e.Target==t); return new DeviceTargetVersion(t,row.Generation,row.PolicyRevision); }).ToArray(),
            kind,kind==DeviceOperation.SetDuty?Duty:null,kind==DeviceOperation.SetAutomatic?Profile:null);
    }
}
