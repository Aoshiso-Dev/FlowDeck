namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private sealed record CalibrationProof(string Owner,long Generation,PhysicalDeviceBinding Binding);
    private sealed record CalibrationTrial(DeviceServiceRequest Request,DeviceTargetVersion Target,IDeviceCoolingCalibrationDriver Driver,
        string[] Domains,CancellationTokenSource Cancellation,TaskCompletionSource<DeviceServiceReply> Completion);
    private readonly Dictionary<EndpointReference,CalibrationTrial> calibrationTrials=[];
    private readonly Dictionary<EndpointReference,CalibrationProof> calibrationProofs=[];
    private bool CalibrationReadyCore(DeviceExecutionState state)=>session is not null&&!releasing&&!stopping&&!configurationChanging&&
        !calibrationTrials.ContainsKey(state.Target)&&calibrationProofs.GetValueOrDefault(state.Target) is { } proof&&proof.Owner==session&&
        proof.Generation==state.Generation&&drivers.GetValueOrDefault(state.Target.DeviceId) is IDeviceCoolingCalibrationDriver driver&&driver.BindingCandidate==proof.Binding;

    private string? CheckCalibrationStart(DeviceServiceRequest request)
    {
        var target=request.Targets![0]; var trial=request.Calibration!;
        if(configurationChanging||lifecycleError is not null) return lifecycleError??"設定の反映中です";
        if(request.TopologyRevision!=registry.Snapshot.TopologyRevision) return "機器構成が変更されています";
        if(!states.TryGetValue(target.Target,out var current)||current.Generation!=target.Generation||current.PolicyRevision!=target.ExpectedPolicyRevision)
            return "試験対象・設定が変更されています";
        if(calibrationTrials.ContainsKey(target.Target)||current.Owned||current.Outcome==Outcome.Pending||coolingPolicies.ContainsKey(target.Target)||
            recoveryRequired.Any(t=>t.DeviceId==target.Target.DeviceId)) return "端子の制御・復旧を終了してから試験してください";
        if(drivers.GetValueOrDefault(target.Target.DeviceId) is not IDeviceCoolingCalibrationDriver driver||driver.BindingCandidate!=trial.Binding)
            return "試験可能な実機の関連付けを確認してください";
        var instance=registry.Snapshot.Devices.SingleOrDefault(d=>d.Device.Id==target.Target.DeviceId);
        var endpoint=instance?.Device.Endpoints.SingleOrDefault(e=>e.Id==target.Target.EndpointId);
        if(instance?.Connected!=true||endpoint?.Kind!=EndpointKind.Cooling||
            endpoint.Capabilities.Any(c=>c.Feature==DeviceFeature.FixedDuty&&c.Support==ImplementationSupport.Supported)!=true)
            return "この端子の出力試験は未対応です";
        if(registry.Check(new(Epoch,target.Target.DeviceId,target.Target.EndpointId,target.Generation,DeviceFeature.Rpm)) is { } rpmError) return rpmError;
        if(registry.Check(new(Epoch,target.Target.DeviceId,target.Target.EndpointId,target.Generation,DeviceFeature.DutyRead)) is { } dutyError) return dutyError;
        var inputs=ReadCoolingInputs(target.Target,[]);
        if(!inputs.RpmFresh||inputs.Rpm is not >0||inputs.Duty!=trial.BeforeDuty) return "新しい回転数と試験前の出力を確認してください";
        if(trial.Duty!=100&&calibrationProofs.GetValueOrDefault(target.Target)!=new CalibrationProof(request.Session,target.Generation,trial.Binding))
            return "この接続で100%の端子試験を完了してから出力を下げてください";
        if(configurationHandler is not null&&configurationHandler is not IDeviceCoolingCalibrationPersistence)
            return "端子試験中の起動時再開を保留できません";
        return driver.ValidateCalibrationStart(target.Target.EndpointId,trial.Binding);
    }
    private void QueueCalibrationCore(DeviceServiceRequest request,TaskCompletionSource<DeviceServiceReply> completion)
    {
        try
        {
            if(CheckCalibrationStart(request) is { } error) { completion.SetResult(Reject(request,error)); return; }
            var original=request.Targets![0]; var old=states[original.Target]; var target=original with { ExpectedPolicyRevision=checked(old.PolicyRevision+1) };
            var trial=new CalibrationTrial(request,target,(IDeviceCoolingCalibrationDriver)drivers[target.Target.DeviceId],
                registry.Snapshot.Devices.Single(d=>d.Device.Id==target.Target.DeviceId).Device.ResourceDomains,
                CancellationTokenSource.CreateLinkedTokenSource(sessionLifetime!.Token),completion);
            calibrationTrials.Add(target.Target,trial);
            states[target.Target]=old with { PolicyRevision=target.ExpectedPolicyRevision,Outcome=Outcome.Pending,RequestedDuty=request.Calibration!.Duty,
                Calibration=null,Message="端子試験を準備中",Persistence=null };
            requests[request.Id]=requests[request.Id] with { Pending=new(request.Id,request.Source,Outcome.Pending,clock.GetUtcNow(),
                [new(target.Target,target.Generation,target.ExpectedPolicyRevision,Outcome.Pending,"端子試験中",request.Calibration.Duty)],"端子試験中") };
            pendingCount++;
            var work=Task.Run(()=>ExecuteCalibrationAsync(trial)); commandWork.Add(work);
            _=work.ContinueWith(t=>{ lock(gate) commandWork.Remove(t); },CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        }
        catch(Exception e) { completion.TrySetResult(Reject(request,e.Message)); }
    }
    private string? CheckCalibrationCurrent(CalibrationTrial trial)
    {
        ExpireCore(); RefreshCore();
        var request=trial.Request; var target=trial.Target;
        if(trial.Cancellation.IsCancellationRequested||stopping||releasing||configurationChanging||request.Session!=session||
            calibrationTrials.GetValueOrDefault(target.Target)!=trial||request.TopologyRevision!=registry.Snapshot.TopologyRevision||
            !states.TryGetValue(target.Target,out var state)||state.Generation!=target.Generation||state.PolicyRevision!=target.ExpectedPolicyRevision||
            trial.Driver.BindingCandidate!=request.Calibration!.Binding)
            return "試験対象・所有・設定が変更されたため試験を中止しました";
        return null;
    }
    private async Task ExecuteCalibrationAsync(CalibrationTrial trial)
    {
        var request=trial.Request; var target=trial.Target; var input=request.Calibration!; var ct=trial.Cancellation.Token;
        CoolingCalibrationResult report; DeviceDriverResult? restored=null;
        string? Guard() { lock(gate) return CheckCalibrationCurrent(trial); }
        void Authorize() { if(Guard() is { } error) throw new IOException(error); }
        try
        {
            Authorize();
            if(configurationHandler is IDeviceCoolingCalibrationPersistence persistence)
                await persistence.PauseCoolingForCalibrationAsync(target.Target,ct).ConfigureAwait(false);
            report=await CoolingCalibrationRunner.RunAsync(input.Duty,
                token=>scheduler.RunPreparedAsync(trial.Domains,HardwarePriority.Manual,trial.Driver,DevicePreparationKind.Command,async ioToken=>
                {
                    lock(gate)
                    {
                        if(CheckCalibrationCurrent(trial) is { } changed) return new CoolingCalibrationStart(new(Outcome.Rejected,changed));
                        // Reserve the exit obligation before any driver phase can write. The driver journals the original state.
                        states[target.Target]=states[target.Target] with { Owned=true,Message="端子試験中" };
                    }
                    var actuation=new CoolingCalibrationActuation(target.Target.EndpointId,input.Binding,input.Duty,input.BeforeDuty,()=>new(Guard()));
                    return await trial.Driver.BeginCalibrationAsync(actuation,ioToken).ConfigureAwait(false);
                },token,Authorize),
                token=>scheduler.RunPreparedAsync(trial.Domains,HardwarePriority.Manual,trial.Driver,DevicePreparationKind.Polling,
                    ioToken=>{ Authorize(); return trial.Driver.ReadCalibrationAsync(target.Target.EndpointId,input.Binding,ioToken); },token,Authorize),
                async ()=>restored=await scheduler.RunPreparedAsync(trial.Domains,HardwarePriority.Recovery,trial.Driver,DevicePreparationKind.Release,
                    token=>trial.Driver.EndCalibrationAsync(target.Target.EndpointId,token)).ConfigureAwait(false),Guard,ct,clock).ConfigureAwait(false);
        }
        catch(Exception e) { report=new(input.Duty,null,null,null,null,0,false,true,Outcome.Rejected,e.Message); }
        lock(gate)
        {
            RefreshCore();
            calibrationProofs.Remove(target.Target);
            if(report is { Completed:true,Restored:true,Outcome:Outcome.Confirmed }&&CheckCalibrationCurrent(trial) is null)
                calibrationProofs[target.Target]=new(request.Session,target.Generation,input.Binding);
            if(states.TryGetValue(target.Target,out var state))
            {
                if(report.Restored) recoveryRequired.Remove(target.Target); else recoveryRequired.Add(target.Target);
                if(state.Generation==target.Generation&&state.PolicyRevision==target.ExpectedPolicyRevision)
                    states[target.Target]=state with { Outcome=report.Outcome,RequestedDuty=null,ReportedDuty=restored?.ReportedDuty,
                        Owned=!report.Restored,Message=report.Message,Calibration=report };
                else if(report.Restored) states[target.Target]=state with { Owned=false };
            }
            calibrationTrials.Remove(target.Target); pendingCount--; TrimRequests();
        }
        var result=new DeviceOperationResult(request.Id,request.Source,report.Outcome,clock.GetUtcNow(),
            [new(target.Target,target.Generation,target.ExpectedPolicyRevision,report.Outcome,report.Message,input.Duty,restored?.ReportedDuty,Calibration:report)],report.Message);
        try { log.Write("coolingCalibration",result); } catch { }
        trial.Completion.TrySetResult(Reply(request,result)); trial.Cancellation.Dispose();
    }
    private DeviceServiceReply CancelCalibrationCore(DeviceServiceRequest request)
    {
        var trial=calibrationTrials.Values.SingleOrDefault(t=>t.Request.Id==request.ResultId&&t.Request.Session==request.Session);
        if(trial is null) return Reject(request,"実行中の端子試験が見つかりません");
        trial.Cancellation.Cancel();
        return Reply(request,result:new(request.Id,request.Source,Outcome.Accepted,clock.GetUtcNow(),[],"中止を受け付けました。元の制御を復元します"));
    }
}
