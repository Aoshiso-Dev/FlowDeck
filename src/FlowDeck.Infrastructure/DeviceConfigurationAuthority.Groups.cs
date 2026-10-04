using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceConfigurationAuthority
{
    private sealed record CoolingGroupsView(string Generation,long Revision,EndpointGroup[] Groups);
    private CoolingGroupsView? coolingGroups;
    private void PublishCoolingGroups()
    {
        var previous=Volatile.Read(ref coolingGroups); var groups=configuration.Cooling.Groups;
        var revision=previous is null?0:Same(previous.Groups,groups)?previous.Revision:checked(previous.Revision+1);
        Volatile.Write(ref coolingGroups,new(generation.Id,revision,Contract.Copy(groups)));
    }
    private DeviceConfigurationReply SaveCoolingGroup(DeviceConfigurationRequest request,DeviceControlRuntime runtime)
    {
        if(request.ExpectedGeneration!=generation.Id) throw new IOException("冷却設定が変わりました。再取得してください");
        if(runtime.State() is { Releasing:true } or { ConfigurationChanging:true }) throw new IOException("制御の終了・設定の反映中です");
        var change=request.CoolingGroup!; var old=configuration.Cooling.Groups.SingleOrDefault(g=>g.Id==change.Id);
        if(change.ExpectedRevision!=(old?.Revision??0)) throw new IOException("冷却グループの改訂が変わりました");
        var groups=configuration.Cooling.Groups.Where(g=>g.Id!=change.Id).ToList();
        if(change.Group is { } group) groups.Add(group with { Revision=checked((old?.Revision??0)+1) });
        PersistConfiguration(configuration with { Cooling=configuration.Cooling with { Groups=groups.ToArray() } },"冷却グループ");
        upload=null;
        return Reply(change.Group is null?"冷却グループを削除しました":"冷却グループを保存しました",changed:["cooling.json"]);
    }
    public CoolingGroupPlan ResolveCoolingGroup(CoolingGroupSelection selection)
    {
        var view=Volatile.Read(ref coolingGroups)??throw new IOException("冷却グループを取得できません");
        if(failure is not null||selection.ExpectedGeneration!=view.Generation) throw new IOException(failure??"冷却設定が変わりました。再取得してください");
        var group=view.Groups.SingleOrDefault(g=>g.Id==selection.Id);
        if(group is null||group.Revision!=selection.ExpectedRevision) throw new IOException("冷却グループの改訂が変わりました");
        return new(view.Revision,Contract.Copy(group));
    }
    public string? CheckCoolingGroup(CoolingGroupPlan plan)=>failure??
        (Volatile.Read(ref coolingGroups)?.Revision!=plan.CatalogRevision?"冷却グループが編集されました。再取得してください":null);
}
