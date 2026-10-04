using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record CoolingGroupSaveEdit(string Epoch,string Generation,CoolingGroupChange Change);
public sealed record CoolingGroupControlEdit(string Epoch,long Topology,CoolingGroupSelection Group,DeviceTargetVersion[] Targets,
    DeviceOperation Operation,int? Duty=null,CoolingTemperatureProfile? Profile=null);
public sealed partial class DeviceWorkspaceClient
{
    public bool SupportsCoolingGroups=>client.Hello?.Features.Contains("cooling-groups-v2")==true;
    public async Task<DeviceConfigurationReply> SaveCoolingGroupAsync(CoolingGroupSaveEdit edit,CancellationToken ct=default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            if(!SupportsCoolingGroups||configuration?.Generation!=edit.Generation||client.Hello?.Epoch!=edit.Epoch) throw new IOException("冷却設定が変わりました。再取得してください");
            var reply=await client.SendAsync(client.Request(DeviceOperation.Configuration,configuration:new(DeviceConfigurationOperation.SaveCoolingGroup,
                ExpectedGeneration:edit.Generation,CoolingGroup:edit.Change)),ct).ConfigureAwait(false);
            return reply.Configuration??throw new IOException(reply.Error??"冷却グループの保存結果を取得できません");
        }
        finally { await RefreshGroupOperationAsync(ct).ConfigureAwait(false); operations.Release(); }
    }
    public async Task<DeviceOperationResult> ApplyCoolingGroupAsync(CoolingGroupControlEdit edit,CancellationToken ct=default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            if(!SupportsCoolingGroups||configuration?.Generation!=edit.Group.ExpectedGeneration||client.Hello?.Epoch!=edit.Epoch) throw new IOException("冷却設定が変わりました。再取得してください");
            if(!DeviceCoolingGroups.Operation(edit.Operation)) throw new ArgumentException("冷却グループの操作が不正です");
            var request=client.Request(edit.Operation,topology:edit.Topology,targets:edit.Targets,duty:edit.Duty,profile:edit.Profile) with { CoolingGroup=edit.Group };
            var reply=await client.SendAsync(request,ct).ConfigureAwait(false);
            return reply.Result??throw new IOException(reply.Error??"冷却グループの操作結果を取得できません");
        }
        finally { await RefreshGroupOperationAsync(ct).ConfigureAwait(false); operations.Release(); }
    }
    private async Task RefreshGroupOperationAsync(CancellationToken ct)
    {
        try { if(client.Connected) { var next=await client.ReadConfigurationAsync(ct).ConfigureAwait(false); lock(gate) configuration=next; await ReadStateAsync(ct).ConfigureAwait(false); } }
        catch(Exception e) { RecordError(e); }
    }
}
