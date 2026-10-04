using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceCoolingCalibrationEdit(string Epoch,string Session,string Generation,long Topology,DeviceTargetVersion Target,CoolingCalibrationRequest Trial);
public sealed class DeviceCoolingCalibrationDraft
{
    private DeviceWorkspaceSnapshot original,latest;
    public EndpointReference Target { get; }
    public PhysicalDeviceBinding Binding { get; }
    public DeviceSetupCandidate Candidate { get; }
    public int Duty { get; private set; }=100;
    public bool Confirmed { get; set; }
    public bool MinimumConfirmed { get; set; }
    public bool Conflict { get; private set; }
    public bool Busy { get; private set; }
    private int? trialBeforeDuty;
    public CoolingCalibrationResult? Result { get; private set; }
    private DeviceExecutionState? Execution=>latest.State.Endpoints.SingleOrDefault(e=>e.Target==Target);
    public bool CanReduce=>!Conflict&&Execution?.CalibrationReady==true;
    public int? CurrentDuty=>Sample(DeviceFeature.DutyRead) is { } value?(int)Math.Round(value):null;
    public double? Rpm=>Sample(DeviceFeature.Rpm);
    public bool DeviceManagedExit=>Candidate.Options.CoolingExitActions.Contains(CoolingExitAction.ReleaseToDevice);
    public string Restoration=>DeviceManagedExit?"元の機器管理・出力設定へ戻す":$"試験前の出力 {(Busy?trialBeforeDuty:CurrentDuty)?.ToString()??"—"}% へ戻す";
    public DeviceCoolingCalibrationDraft(DeviceWorkspaceSnapshot snapshot,EndpointReference target)
    {
        Target=target; original=latest=Contract.Copy(snapshot);
        Candidate=snapshot.Configuration.Setup?.SingleOrDefault(c=>c.Target==target&&c.Options.CoolingCalibrationSupported)??throw new IOException("端子試験に対応していません");
        Binding=snapshot.Configuration.Candidates.SingleOrDefault(b=>b.DeviceId==target.DeviceId)??throw new IOException("個体を再取得してください");
        if(!DeviceSetup.Valid(Candidate)||!DeviceServiceProtocol.Id(snapshot.Session)) throw new IOException("試験対象・接続が不正です");
        Result=Execution?.Calibration; Update(snapshot);
    }
    private double? Sample(DeviceFeature feature)
    {
        var sample=DeviceDashboard.Sample(latest.State,Target,feature);
        return latest.Connected&&sample is not null&&DeviceDashboard.Fresh(sample,latest.ElapsedSeconds)?sample.Value:null;
    }
    public void ChangeDuty(int value)
    {
        if(Busy) return;
        if(value is <1 or >100||value!=100&&!CanReduce) throw new ArgumentOutOfRangeException(nameof(value));
        if(Duty==value) return; Duty=value; Confirmed=false; MinimumConfirmed=false;
    }
    public void Update(DeviceWorkspaceSnapshot snapshot)
    {
        var previousDuty=CurrentDuty; latest=Contract.Copy(snapshot);
        if(Busy) return;
        var before=original.State.Endpoints.SingleOrDefault(e=>e.Target==Target);
        var current=snapshot.State.Inventory.Devices.SingleOrDefault(d=>d.Device.Id==Target.DeviceId);
        Conflict|=snapshot.Session!=original.Session||snapshot.State.Inventory.Epoch!=original.State.Inventory.Epoch||
            snapshot.Configuration.Generation!=original.Configuration.Generation||snapshot.State.Inventory.TopologyRevision!=original.State.Inventory.TopologyRevision||
            current is null||current.Generation!=Candidate.Generation||Execution?.PolicyRevision!=before?.PolicyRevision||
            !snapshot.Configuration.Candidates.Contains(Binding)||snapshot.Configuration.Setup?.Any(c=>c.Target==Target&&c.Generation==Candidate.Generation&&c.Options.CoolingCalibrationSupported)!=true;
        if(Conflict||!DeviceManagedExit&&previousDuty!=CurrentDuty) Confirmed=false;
        if(!CanReduce&&Duty!=100) { Duty=100; Confirmed=false; }
    }
    public string? Problem=>Busy?"試験中・復元待ち":Conflict?"対象・設定が変わりました。再取得してください":
        !latest.Connected||latest.ExitAttempted||latest.State.Releasing||latest.State.ConfigurationChanging?"サービスへ接続してください":
        latest.State.LifecycleError is { } error?error:
        !latest.State.Inventory.Devices.Any(d=>d.Connected&&d.Device.Id==Target.DeviceId&&d.Generation==Candidate.Generation)?"機器へ接続してください":
        Execution is null||Execution.Owned||Execution.Outcome==Outcome.Pending?"この端子の制御を終了してください":
        Rpm is not >0||CurrentDuty is null?"回転中の端子と新しい測定値を確認してください":
        !Confirmed?"対象・試験内容を確認してください":null;
    public bool CanAdopt=>!Busy&&!Conflict&&MinimumConfirmed&&latest.Connected&&CanReduce&&
        Result is { Completed:true,Restored:true,Outcome:Outcome.Confirmed }&&Equals(Execution?.Calibration,Result);
    public DeviceCoolingCalibrationEdit Capture()
    {
        if(Problem is { } error) throw new InvalidOperationException(error);
        if(Duty!=100&&!CanReduce) throw new InvalidOperationException("100%の端子試験を先に完了してください");
        return new(latest.State.Inventory.Epoch,latest.Session!,latest.Configuration.Generation,latest.State.Inventory.TopologyRevision,
            new(Target,Candidate.Generation,Execution!.PolicyRevision),new(Binding,Duty,CurrentDuty!.Value,true));
    }
    public void Begin() { trialBeforeDuty=CurrentDuty; Busy=true; Confirmed=false; MinimumConfirmed=false; }
    public void Complete(DeviceOperationResult? result,DeviceWorkspaceSnapshot snapshot)
    {
        Busy=false; latest=Contract.Copy(snapshot);
        Result=result?.Targets.SingleOrDefault(t=>t.Target==Target)?.Calibration;
        // Only a completed operation on the same physical connection may rebase our own policy revision/pause of startup.
        if(snapshot.Session==original.Session&&snapshot.State.Inventory.Epoch==original.State.Inventory.Epoch&&
            snapshot.State.Inventory.Devices.Any(d=>d.Connected&&d.Device.Id==Target.DeviceId&&d.Generation==Candidate.Generation)&&snapshot.Configuration.Candidates.Contains(Binding))
            original=Contract.Copy(snapshot);
        Update(snapshot); if(!CanReduce) Duty=100;
    }
    public DeviceEndpointSetupDraft AdoptMinimum()
    {
        if(!CanAdopt) throw new InvalidOperationException("成功した試験と安定した回転を確認してください");
        var draft=new DeviceEndpointSetupDraft(latest,Target);
        draft.Change(draft.Values with { Safety=draft.Values.Safety! with { Minimum=Result!.Duty },Confirmed=false });
        return draft;
    }
}
