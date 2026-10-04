namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private sealed record IdentificationTrial(DeviceServiceRequest Request, DeviceTargetVersion Target, DeviceExecutionState Baseline,
        IDeviceControlDriver Driver, string[] Domains, CancellationTokenSource Cancellation, TaskCompletionSource<DeviceServiceReply> Completion)
    { public bool Issued; }
    private readonly Dictionary<EndpointReference, IdentificationTrial> lightingIdentifications = [];
    private bool IdentificationConflict(DeviceServiceRequest request) => request.Operation is DeviceOperation.SetLighting or DeviceOperation.StopLighting or DeviceOperation.ApplyLightingPreset or DeviceOperation.AdjustLighting &&
        request.Targets!.Any(t => lightingIdentifications.Keys.Any(k => k.DeviceId == t.Target.DeviceId));
    private string? CheckIdentificationStart(DeviceServiceRequest request)
    {
        var target = request.Targets![0];
        if (request.TopologyRevision != registry.Snapshot.TopologyRevision || !states.TryGetValue(target.Target, out var current) ||
            current.Generation != target.Generation || current.PolicyRevision != target.ExpectedPolicyRevision) return "識別する照明の構成・設定が変更されました";
        if (DeviceLightingIdentification.Problem(State(), target.Target) is { } error) return error;
        if (lightingIdentifications.Keys.Any(t => t.DeviceId == target.Target.DeviceId) ||
            lightingSelectionsPending.Keys.Any(t => t.DeviceId == target.Target.DeviceId) ||
            states.Values.Any(e => e.Target.DeviceId == target.Target.DeviceId && e.Outcome == Outcome.Pending && e.RequestedLighting is not null))
            return "照明の処理が完了するまでお待ちください";
        var set = request with { Operation = DeviceOperation.SetLighting, Lighting = DeviceLightingIdentification.White };
        return CheckTarget(set, target) ?? CheckTarget(set with { Lighting = current.RequestedLighting }, target);
    }
    private void QueueIdentificationCore(DeviceServiceRequest request, TaskCompletionSource<DeviceServiceReply> completion)
    {
        try
        {
            if (CheckIdentificationStart(request) is { } error) { completion.SetResult(Reject(request, error)); return; }
            var before = request.Targets![0]; var baseline = Contract.Copy(states[before.Target]);
            var target = before with { ExpectedPolicyRevision = checked(baseline.PolicyRevision + 1) };
            var trial = new IdentificationTrial(request, target, baseline, drivers[target.Target.DeviceId],
                registry.Snapshot.Devices.Single(d => d.Device.Id == target.Target.DeviceId).Device.ResourceDomains,
                CancellationTokenSource.CreateLinkedTokenSource(sessionLifetime!.Token), completion);
            lightingIdentifications.Add(target.Target, trial);
            states[target.Target] = baseline with { PolicyRevision = target.ExpectedPolicyRevision, Outcome = Outcome.Pending,
                Identification = new(request.Id, LightingIdentificationPhase.Preparing, Message: "識別を準備中"), Message = "識別を準備中" };
            requests[request.Id] = requests[request.Id] with { Pending = new(request.Id, request.Source, Outcome.Pending, clock.GetUtcNow(),
                [new(target.Target, target.Generation, target.ExpectedPolicyRevision, Outcome.Pending, "識別を準備中", Identification: states[target.Target].Identification)], "識別を準備中") };
            pendingCount++;
            var work = Task.Run(() => ExecuteIdentificationAsync(trial)); commandWork.Add(work);
            _ = work.ContinueWith(t => { lock (gate) commandWork.Remove(t); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception e) { completion.TrySetResult(Reject(request, e.Message)); }
    }
    private string? CheckIdentificationCurrent(IdentificationTrial trial, bool restore)
    {
        try
        {
        ExpireCore(); RefreshCore(); var target = trial.Target;
        if (stopping || releasing || configurationChanging || session != trial.Request.Session ||
            lightingIdentifications.GetValueOrDefault(target.Target) != trial || !ReferenceEquals(drivers.GetValueOrDefault(target.Target.DeviceId), trial.Driver) ||
            !states.TryGetValue(target.Target, out var current) || current.Generation != target.Generation || current.PolicyRevision != target.ExpectedPolicyRevision)
            return "識別中に対象・所有・設定が変更されました";
        if (!restore && (trial.Cancellation.IsCancellationRequested || trial.Request.TopologyRevision != registry.Snapshot.TopologyRevision)) return "識別を中止しました";
        var set = trial.Request with { Operation = DeviceOperation.SetLighting, Targets = [target], Lighting = restore ? trial.Baseline.RequestedLighting : DeviceLightingIdentification.White };
        return CheckTarget(set, target, identification: true);
        }
        catch (Exception e) { return "識別対象を確認できません: " + e.Message; }
    }
    private void PublishIdentification(IdentificationTrial trial, LightingIdentificationPhase phase, LightingProgram? reported = null)
    {
        lock (gate)
            if (states.TryGetValue(trial.Target.Target, out var current) && current.Generation == trial.Target.Generation && current.PolicyRevision == trial.Target.ExpectedPolicyRevision)
            {
                var message = phase == LightingIdentificationPhase.Lighting ? "3秒間点灯中" : "元の照明へ戻しています";
                states[trial.Target.Target] = current with { Message = message, ReportedLighting = reported ?? current.ReportedLighting,
                    Identification = new(trial.Request.Id, phase, Message: message) };
            }
    }
    private async Task<DeviceDriverResult> WriteIdentificationAsync(IdentificationTrial trial, bool restore)
    {
        var program = restore ? trial.Baseline.RequestedLighting! : DeviceLightingIdentification.White;
        void Authorize() { lock (gate) if (CheckIdentificationCurrent(trial, restore) is { } error) throw new IOException(error); }
        try
        {
            var result = await scheduler.RunPreparedAsync(trial.Domains, restore ? HardwarePriority.Recovery : HardwarePriority.Manual,
                trial.Driver, DevicePreparationKind.Command, async token =>
                {
                    lock (gate)
                    {
                        if (CheckIdentificationCurrent(trial, restore) is { } error) return new DeviceDriverResult(Outcome.Rejected, error);
                        if (!restore) trial.Issued = true;
                    }
                    return await trial.Driver.ApplyAsync(new(trial.Target.Target.EndpointId, DeviceOperation.SetLighting, Lighting: program), token).ConfigureAwait(false);
                }, restore ? CancellationToken.None : trial.Cancellation.Token, Authorize).ConfigureAwait(false);
            return ValidDriverResult(result) ? result : new(Outcome.Unknown, "照明の送信結果が不正です");
        }
        catch (Exception e) { return new(trial.Issued ? Outcome.Unknown : Outcome.Rejected, e.Message.Length <= 16000 ? e.Message : e.Message[..16000]); }
    }
    private async Task ExecuteIdentificationAsync(IdentificationTrial trial)
    {
        var white = new DeviceDriverResult(Outcome.Rejected, "識別は未実行です"); DeviceDriverResult? restored = null;
        try
        {
            white = await WriteIdentificationAsync(trial, false).ConfigureAwait(false);
            if (white.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified)
            {
                PublishIdentification(trial, LightingIdentificationPhase.Lighting, white.ReportedLighting);
                // The delay holds no hardware resource. Polling and cooling can continue on this controller.
                try { await Task.Delay(DeviceLightingIdentification.Duration, clock, trial.Cancellation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
        catch (Exception e) { white = new(Outcome.Unknown, e.Message); }
        finally
        {
            if (trial.Issued)
            {
                PublishIdentification(trial, LightingIdentificationPhase.Restoring);
                restored = await WriteIdentificationAsync(trial, true).ConfigureAwait(false);
            }
            DeviceOperationResult result;
            lock (gate)
            {
                var problem = CheckIdentificationCurrent(trial, true);
                var current = problem is null;
                var back = current && (!trial.Issued || restored?.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified);
                var cancelled = trial.Cancellation.IsCancellationRequested;
                var outcome = !back ? trial.Issued ? Outcome.Unknown : Outcome.Rejected : !trial.Issued ? cancelled ? Outcome.Confirmed : Outcome.Rejected :
                    white.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified ? restored!.Outcome : Outcome.Partial;
                var message = back ? !trial.Issued ? "識別点灯は送信せず終了しました: " + white.Message :
                    (cancelled ? "識別を中止し、" : outcome == Outcome.Partial ? "識別点灯を確認できなかったため、" : "識別を終了し、") + "元の照明を送信しました（点灯未確認）" :
                    "元の照明の復元は未確認です。照明の制御終了を確認してください: " + (problem ?? restored?.Message ?? white.Message);
                if (message.Length > 16384) message = message[..16384];
                var status = new LightingIdentificationStatus(trial.Request.Id, LightingIdentificationPhase.Finished, back, cancelled, message);
                var target = trial.Target;
                if (trial.Issued && !back) lightingRecoveryRequired.Add(target.Target);
                if (states.TryGetValue(target.Target, out var state) && state.Generation == target.Generation && state.PolicyRevision == target.ExpectedPolicyRevision)
                    states[target.Target] = state with { Outcome = back ? restored?.Outcome ?? trial.Baseline.Outcome : outcome,
                        RequestedLighting = trial.Baseline.RequestedLighting, ReportedLighting = back ? restored?.ReportedLighting ?? trial.Baseline.ReportedLighting : null,
                        Identification = status, Message = message };
                result = new(trial.Request.Id, trial.Request.Source, outcome, clock.GetUtcNow(),
                    [new(target.Target, target.Generation, target.ExpectedPolicyRevision, outcome, message, ReportedLighting: back ? restored?.ReportedLighting ?? trial.Baseline.ReportedLighting : null, Identification: status)], message);
                lightingIdentifications.Remove(target.Target); pendingCount--; TrimRequests();
            }
            try { log.Write("lightingIdentification", result); } catch { }
            trial.Completion.TrySetResult(Reply(trial.Request, result)); trial.Cancellation.Dispose();
        }
    }
    private DeviceServiceReply CancelIdentificationCore(DeviceServiceRequest request)
    {
        var trial = lightingIdentifications.Values.SingleOrDefault(t => t.Request.Id == request.ResultId && t.Request.Session == request.Session);
        if (trial is null) return Reject(request, "実行中の識別が見つかりません");
        trial.Cancellation.Cancel();
        return Reply(request, new(request.Id, request.Source, Outcome.Accepted, clock.GetUtcNow(), [], "中止を受け付けました。元の照明へ戻します"));
    }
}
