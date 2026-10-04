namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private bool BatchedLighting(DeviceServiceRequest request, DeviceTargetVersion target) =>
        request.Operation == DeviceOperation.SetLighting && DeviceLightingBatch.Program(request.Lighting) &&
        drivers[target.Target.DeviceId] is IDeviceLightingBatchDriver;

    private string? CheckStaticLightingBatches(DeviceServiceRequest request,
        IReadOnlyDictionary<EndpointReference, LightingProgram>? programs)
    {
        if (request.Operation != DeviceOperation.SetLighting) return null;
        foreach (var device in request.Targets!.Where(t => DeviceLightingBatch.Program(programs?.GetValueOrDefault(t.Target) ?? request.Lighting))
            .GroupBy(t => t.Target.DeviceId))
            if (drivers[device.Key] is IDeviceLightingBatchDriver driver &&
                driver.ValidateLightingBatch(device.Select(t => new EndpointLightingProgram(t.Target.EndpointId,
                    programs?.GetValueOrDefault(t.Target) ?? request.Lighting!)).ToArray()) is { } error) return error;
        return null;
    }

    private Task<DeviceTargetResult[]> ExecuteStaticLightingAsync(DeviceServiceRequest request, DeviceTargetVersion[] targets,
        IReadOnlyDictionary<EndpointReference, LightingProgram> programs, CancellationToken ct, Func<string?>? precondition = null)
    {
        var pending = new List<Task<DeviceTargetResult[]>>();
        lock (gate)
        {
            foreach (var device in targets.GroupBy(t => t.Target.DeviceId))
            {
                var batched = drivers[device.Key] is IDeviceLightingBatchDriver
                    ? device.Where(t => DeviceLightingBatch.Program(programs[t.Target])).ToArray() : [];
                if (batched.Length > 0)
                    pending.Add(ExecuteLightingBatchAsync(request with { Operation = DeviceOperation.SetLighting, Targets = batched },
                        batched, programs, ct, precondition));
                foreach (var target in device.Except(batched))
                    pending.Add(One(target));
            }
        }
        return Complete();
        async Task<DeviceTargetResult[]> One(DeviceTargetVersion target) =>
            [await ExecuteTargetAsync(AdjustmentPart(request, [target], programs[target.Target]), target, ct, precondition).ConfigureAwait(false)];
        async Task<DeviceTargetResult[]> Complete() => (await Task.WhenAll(pending).ConfigureAwait(false)).SelectMany(r => r).ToArray();
    }

    private async Task<DeviceTargetResult[]> ExecuteLightingBatchAsync(DeviceServiceRequest request, DeviceTargetVersion[] targets,
        IReadOnlyDictionary<EndpointReference, LightingProgram> programs, CancellationToken ct, Func<string?>? precondition)
    {
        DeviceDriverReleaseResult[] results; var issued = false;
        DeviceDriverReleaseResult[] Reject(Outcome outcome, string error) => targets.Select(t =>
            new DeviceDriverReleaseResult(t.Target.EndpointId, new(outcome, error))).ToArray();
        void Authorize()
        {
            lock (gate)
            {
                RefreshCore();
                if (ct.IsCancellationRequested || request.Session != session || stopping || releasing || configurationChanging ||
                    precondition?.Invoke() is not null || request.TopologyRevision != registry.Snapshot.TopologyRevision ||
                    targets.Any(t => !states.TryGetValue(t.Target, out var state) || state.Generation != t.Generation || state.PolicyRevision != t.ExpectedPolicyRevision))
                    throw new IOException("通信前に照明の対象・所有・設定が変更されました");
            }
        }
        try
        {
            string[] domains; IDeviceLightingBatchDriver driver;
            lock (gate)
            {
                domains = registry.Snapshot.Devices.Single(d => d.Device.Id == targets[0].Target.DeviceId).Device.ResourceDomains;
                driver = (IDeviceLightingBatchDriver)drivers[targets[0].Target.DeviceId];
            }
            var selection = targets.Select(t => new EndpointLightingProgram(t.Target.EndpointId, Contract.Copy(programs[t.Target]))).ToArray();
            results = await scheduler.RunPreparedAsync(domains, HardwarePriority.Manual, driver, DevicePreparationKind.Command, async token =>
            {
                lock (gate)
                {
                    Authorize();
                    if (CheckBatch(request, checkLightingStream: false, staticPrograms: programs) is { } error) return Reject(Outcome.Rejected, error);
                    foreach (var target in targets) states[target.Target] = states[target.Target] with { Owned = true };
                    issued = true;
                }
                try { return await driver.ApplyLightingBatchAsync(selection, token).ConfigureAwait(false); }
                catch (Exception e) { return Reject(Outcome.Unknown, e.Message); }
            }, ct, Authorize).ConfigureAwait(false);
        }
        catch (Exception e) { results = Reject(issued ? Outcome.Unknown : Outcome.Rejected, e.Message); }
        if (results is null || results.Length != targets.Length || results.Any(r => r is null || !ValidDriverResult(r.Result)) ||
            !results.Select(r => r.EndpointId).Order().SequenceEqual(targets.Select(t => t.Target.EndpointId).Order()))
            results = Reject(Outcome.Unknown, "照明の端子別結果が欠落または不正です");
        lock (gate)
        {
            RefreshCore();
            return targets.Select(target =>
            {
                var result = results.Single(r => r.EndpointId == target.Target.EndpointId).Result;
                if (request.Session != session || !states.TryGetValue(target.Target, out var state) ||
                    state.Generation != target.Generation || state.PolicyRevision != target.ExpectedPolicyRevision)
                    result = issued ? new(Outcome.Unknown, "結果が届く前に対象・所有・設定が変更されました") : result;
                else states[target.Target] = state with { Outcome = result.Outcome, ReportedLighting = result.ReportedLighting, Message = result.Message,
                    Lighting = result.Outcome is (Outcome.Confirmed or Outcome.WrittenUnverified) && !lightingOwners.ContainsKey(target.Target)
                        ? null : state.Lighting,
                    Owned = result.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified or Outcome.Unknown || state.Owned };
                return new DeviceTargetResult(target.Target, target.Generation, target.ExpectedPolicyRevision, result.Outcome,
                    result.Message, ReportedLighting: result.ReportedLighting);
            }).ToArray();
        }
    }
}
