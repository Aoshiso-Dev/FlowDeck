using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record CoolingStartupCandidate(EndpointReference Target,string Name,string Policy);
public sealed class DeviceCoolingStartupDraft
{
    private readonly DeviceWorkspaceSnapshot original;
    private DeviceWorkspaceSnapshot latest;
    private readonly HashSet<EndpointReference> selected;
    private readonly bool originalEnabled;
    private readonly HashSet<EndpointReference> originalSelected;
    public bool Enabled { get; private set; }
    public bool Conflict { get; private set; }
    public string Generation=>original.Configuration.Generation;
    public bool Dirty=>Enabled!=originalEnabled||!selected.SetEquals(originalSelected);
    public CoolingStartupCandidate[] Candidates { get; }
    public DeviceCoolingStartupDraft(DeviceWorkspaceSnapshot snapshot)
    {
        original=Contract.Copy(snapshot); latest=original;
        var config=original.Configuration.Configuration;
        originalEnabled=Enabled=config.Preferences.Startup.ResumeControl;
        originalSelected=config.Cooling.Endpoints.Where(e=>e.ResumeOnStart).Select(e=>e.Target).ToHashSet(); selected=new(originalSelected);
        Candidates=config.Cooling.Endpoints.Where(e=>e.SavedPolicy is not null||e.ResumeOnStart).Select(e=> {
            var device=config.Devices.Devices.Single(d=>d.Id==e.Target.DeviceId); var endpoint=device.Endpoints.Single(p=>p.Id==e.Target.EndpointId);
            return new CoolingStartupCandidate(e.Target,device.Name+" / "+endpoint.Name,DeviceCoolingStartup.PolicyName(e.SavedPolicy)+$" · 最低 {e.Safety.Minimum}%");
        }).ToArray();
    }
    public void SetEnabled(bool value)=>Enabled=value;
    public bool Selected(EndpointReference target)=>selected.Contains(target);
    public void Select(EndpointReference target,bool value)
    {
        if(!Candidates.Any(c=>c.Target==target)) throw new ArgumentException("冷却端子がありません");
        if(value&&TargetProblem(target) is { } problem) throw new InvalidOperationException(problem);
        if(value) selected.Add(target); else selected.Remove(target);
    }
    public string? TargetProblem(EndpointReference target)=>DeviceCoolingStartup.Problem(
        original.Configuration.Configuration.Cooling.Endpoints.Single(e=>e.Target==target),original.Configuration.Configuration.Bindings,
        latest.State,latest.Configuration.Candidates,latest.ElapsedSeconds);
    public void Update(DeviceWorkspaceSnapshot snapshot)
    {
        latest=snapshot;
        Conflict|=snapshot.State.Inventory.Epoch!=original.State.Inventory.Epoch||snapshot.State.Inventory.TopologyRevision!=original.State.Inventory.TopologyRevision||snapshot.Configuration.Generation!=Generation;
        foreach(var endpoint in original.Configuration.Configuration.Cooling.Endpoints.Where(e=>selected.Contains(e.Target)))
        {
            var ids=(endpoint.SavedPolicy is { } p?DeviceCoolingPolicy.Required(p,endpoint.Safety).Select(s=>s.DeviceId):[]).Append(endpoint.Target.DeviceId).Distinct();
            foreach(var id in ids)
                Conflict|=original.State.Inventory.Devices.SingleOrDefault(d=>d.Device.Id==id)?.Generation!=snapshot.State.Inventory.Devices.SingleOrDefault(d=>d.Device.Id==id)?.Generation||
                    original.Configuration.Candidates.SingleOrDefault(b=>b.DeviceId==id)!=snapshot.Configuration.Candidates.SingleOrDefault(b=>b.DeviceId==id);
        }
    }
    public string? Problem=>Conflict?"構成または適用済み方針が変わりました。再取得してください":!latest.Connected||latest.ExitAttempted?"サービスへ接続されていません":
        latest.State.Releasing||latest.State.ConfigurationChanging?"処理中です":Enabled&&selected.Count==0?(Candidates.Length==0?"先に冷却を一度適用してください":"再開する端子を選択してください"):
        Enabled?selected.Select(TargetProblem).FirstOrDefault(p=>p is not null):null;
    public CoolingStartupEdit Capture()
    {
        if(Problem is { } problem) throw new InvalidOperationException(problem);
        return new(original.State.Inventory.Epoch,Generation,original.State.Inventory.TopologyRevision,new(Enabled,Enabled?Candidates.Where(c=>selected.Contains(c.Target)).Select(c=>c.Target).ToArray():[]));
    }
}
