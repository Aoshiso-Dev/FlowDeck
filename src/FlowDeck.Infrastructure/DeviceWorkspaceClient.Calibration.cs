using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceWorkspaceClient
{
    private sealed record CalibrationCall(DeviceServicePipeClient Client,DeviceServiceRequest Request,Task<DeviceServiceReply> Pending);
    private CalibrationCall? calibrationCall;
    public bool SupportsCoolingCalibration=>client.Hello?.Features.Contains("cooling-calibration-v2")==true;
    public bool CalibrationRunning { get { lock(gate) return calibrationCall is not null; } }
    public async Task<DeviceOperationResult> StartCoolingCalibrationAsync(DeviceCoolingCalibrationEdit edit,CancellationToken ct=default)
    {
        CalibrationCall call;
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen(); if(!SupportsCoolingCalibration||CalibrationRunning) throw new IOException("端子試験を開始できません");
            await ReadStateAsync(ct).ConfigureAwait(false);
            var version=state!.Endpoints.SingleOrDefault(e=>e.Target==edit.Target.Target);
            if(client.Hello!.Epoch!=edit.Epoch||client.Hello.Session!=edit.Session||configuration!.Generation!=edit.Generation||state.Inventory.TopologyRevision!=edit.Topology||
                version?.Generation!=edit.Target.Generation||version.PolicyRevision!=edit.Target.ExpectedPolicyRevision||
                !CoolingCalibrationRules.Valid(edit.Trial)||edit.Trial.Binding.DeviceId!=edit.Target.Target.DeviceId)
                throw new IOException("確認後に接続・対象・設定が変更されました。再取得してください");
            var request=client.Request(DeviceOperation.CalibrateCooling,topology:edit.Topology,targets:[edit.Target]) with { Calibration=edit.Trial };
            call=new(client,request,client.SendAsync(request,ct)); lock(gate) calibrationCall=call;
        }
        finally { operations.Release(); }
        try
        {
            var reply=await call.Pending.ConfigureAwait(false);
            return reply.Result??throw new IOException(reply.Error??"試験結果を取得できません");
        }
        catch(Exception e) { if(!call.Client.Connected) RecordError(e); throw; }
        finally
        {
            await operations.WaitAsync().ConfigureAwait(false);
            try
            {
                if(ReferenceEquals(client,call.Client)&&!disposed&&!released&&!exitAttempted&&client.Connected)
                {
                    var next=await client.ReadConfigurationAsync(CancellationToken.None).ConfigureAwait(false); lock(gate) configuration=next;
                    await ReadStateAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch(Exception e) { RecordError(e); }
            finally { lock(gate) if(ReferenceEquals(calibrationCall,call)) calibrationCall=null; operations.Release(); }
        }
    }
    public async Task CancelCoolingCalibrationAsync(CancellationToken ct=default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen(); CalibrationCall? call; lock(gate) call=calibrationCall;
            if(call is null||call.Pending.IsCompleted) return;
            if(!ReferenceEquals(client,call.Client)) throw new IOException("試験中の接続が変更されました");
            var reply=await client.SendAsync(client.Request(DeviceOperation.CancelCoolingCalibration,resultId:call.Request.Id),ct).ConfigureAwait(false);
            if(!reply.Success&&!call.Pending.IsCompleted) throw new IOException(reply.Error??"中止要求を確認できません");
        }
        finally { operations.Release(); }
    }
}
