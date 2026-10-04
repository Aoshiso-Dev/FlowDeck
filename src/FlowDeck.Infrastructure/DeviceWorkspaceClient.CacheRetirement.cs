using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceCacheRetirementEdit(string Epoch,string Session,string Generation,long Topology,DeviceCacheRetirementTarget Target,string Name)
{
    public bool Matches(DeviceWorkspaceSnapshot? s) => s is { Connected:true,ExitAttempted:false } && !s.State.Releasing && !s.State.ConfigurationChanging &&
        s.State.Inventory.Epoch==Epoch && s.Session==Session && s.Configuration.Generation==Generation && s.State.Inventory.TopologyRevision==Topology &&
        s.State.Inventory.Devices.Any(d=>d.Device.Id==Target.DeviceId && d.Generation==Target.Generation && !d.Connected);
    public static DeviceCacheRetirementEdit Capture(DeviceWorkspaceSnapshot s,string id)
    {
        var d=s.State.Inventory.Devices.SingleOrDefault(d=>d.Device.Id==id&&!d.Connected) ?? throw new IOException("切断済みの機器を選択してください");
        var name=s.Configuration.Configuration.Devices.Devices.SingleOrDefault(d=>d.Id==id)?.Name??d.Device.Name;
        var edit=new DeviceCacheRetirementEdit(s.State.Inventory.Epoch,s.Session??"",s.Configuration.Generation,s.State.Inventory.TopologyRevision,new(id,d.Generation),name);
        if(!edit.Matches(s)) throw new IOException("接続・機器・設定が変更されています");
        return edit;
    }
    public string Description => Name+Environment.NewLine+"機器ID："+Target.DeviceId+Environment.NewLine+
        "実行中のすべての制御を保存済みの終了方法で終了します。"+Environment.NewLine+
        "この機器の検出履歴と通信を解放します。保存した名前・設定は残ります。"+Environment.NewLine+
        "再検出後は端子設定を適用してから制御してください。";
}
public sealed partial class DeviceWorkspaceClient
{
    public bool SupportsCacheRetirement => client.Hello?.Features.Contains(DeviceCacheRetirementTarget.Feature)==true;
    public Task<DeviceCacheRetirementEdit> PreviewCacheRetirementAsync(DeviceCacheRetirementEdit edit,CancellationToken ct=default) => CacheRetirementAsync(edit,false,ct);
    public Task<DeviceCacheRetirementEdit> RetireDisconnectedAsync(DeviceCacheRetirementEdit edit,CancellationToken ct=default) => CacheRetirementAsync(edit,true,ct);
    private async Task<DeviceCacheRetirementEdit> CacheRetirementAsync(DeviceCacheRetirementEdit edit,bool apply,CancellationToken ct)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen(); await ReadStateAsync(ct).ConfigureAwait(false);
            if(!SupportsCacheRetirement||!edit.Matches(Snapshot)) throw new IOException("接続・機器・設定が変更されました。整理内容を確認し直してください");
            var op=apply?DeviceConfigurationOperation.RetireDisconnected:DeviceConfigurationOperation.PreviewCacheRetirement;
            var request=client.Request(DeviceOperation.Configuration,configuration:new(op,ExpectedGeneration:edit.Generation,TopologyRevision:edit.Topology,CacheRetirement:edit.Target));
            var reply=await client.SendAsync(request,ct).ConfigureAwait(false);
            if(!reply.Success||reply.Configuration is not { Outcome:Outcome.Confirmed } result) throw new IOException(reply.Error??"検出履歴を整理できません");
            if(result.Generation!=edit.Generation||result.CacheRetirement!=edit.Target) throw new IOException("整理結果の識別が一致しません");
            await ReadStateAsync(ct).ConfigureAwait(false);
            if(!apply&&!edit.Matches(Snapshot)) throw new IOException("機器の状態が変わりました。整理内容を確認し直してください");
            return Contract.Copy(edit);
        }
        catch(Exception e) { if(!client.Connected) RecordError(e); throw; }
        finally
        {
            try { if(apply&&client.Connected) { var next=await client.ReadConfigurationAsync(ct).ConfigureAwait(false); lock(gate) configuration=next; await ReadStateAsync(ct).ConfigureAwait(false); } }
            catch(Exception e) { RecordError(e); }
            operations.Release();
        }
    }
}
