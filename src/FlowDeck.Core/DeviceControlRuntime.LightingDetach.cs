namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private LightingGroup[] SelectedLightingGroups(IEnumerable<DeviceTargetVersion> targets) => targets
        .Select(t => lightingOwners.GetValueOrDefault(t.Target)).Where(id => id is not null).Distinct().Select(id => lightingGroups[id!]).ToArray();
    private string? CheckSelectedLightingStop(DeviceServiceRequest request)
    {
        var selected = request.Targets!.Select(t => t.Target).ToHashSet();
        foreach (var group in SelectedLightingGroups(request.Targets!))
        {
            if (group.Programs.Keys.All(selected.Contains)) continue;
            if (group.Stopping || group.Reconfiguring || group.Fault is not null || group.Frames == 0 || group.Owner != request.Session ||
                group.Participants.Any(p => !CurrentLighting(group,p)) || group.Programs.Keys.Any(lightingSelectionsPending.ContainsKey))
                return "同期の処理が終了するまでお待ちください。復旧が必要な場合はグループ全体を選択してください";
            foreach (var participant in group.Participants)
                if (participant.Targets.Any(t => selected.Contains(t.Target)) && !participant.Targets.All(t => selected.Contains(t.Target)) &&
                    participant.Stream is not ISelectiveDeviceLightingStream)
                    return "この機器は同期中の端子を個別に終了できません。同じ機器の同期端子をまとめて選択してください";
        }
        return null;
    }
    // Selected revisions have already advanced. Reserve the affected groups before releasing the gate.
    private Task<DeviceTargetResult[]>? QueueSelectedLightingStopCore(DeviceServiceRequest request, DeviceTargetVersion[] targets, CancellationToken ct)
    {
        var groups=SelectedLightingGroups(targets); var selected=targets.ToDictionary(t=>t.Target); var endingTargets=selected.Keys.ToHashSet();
        if(groups.Length==0 || groups.All(g=>g.Programs.Keys.All(selected.ContainsKey))) return null;
        var reserved=groups.SelectMany(g=>g.Programs.Keys).Concat(selected.Keys).Distinct().ToArray();
        var previous=groups.Select(g=>g.FrameWork).ToArray();
        var failed=new HashSet<LightingGroup>();
        var detached=new Dictionary<LightingGroup,Dictionary<EndpointReference,DeviceDriverResult>>();
        foreach(var group in groups)
        {
            group.Reconfiguring=true; group.CompositionRevision++; detached[group]=[];
            foreach(var p in group.Participants)
                for(var i=0;i<p.Targets.Length;i++) if(selected.TryGetValue(p.Targets[i].Target,out var version)) p.Targets[i]=version;
        }
        foreach(var t in reserved) lightingTransitionTargets.Add(t);
        var work=Task.Run(async () =>
        {
            await Task.WhenAll(previous).ConfigureAwait(false);
            var active=groups.SelectMany(g=>g.Programs.Keys).ToHashSet();
            var native=targets.Where(t=>!active.Contains(t.Target)).Select(t=>ExecuteTargetAsync(request,t,ct)).ToArray();
            var streamResults=await Task.WhenAll(groups.SelectMany(group=>group.Participants
                .Where(p=>p.Targets.Any(t=>selected.ContainsKey(t.Target))).Select(async participant=>
                {
                    var ending=participant.Targets.Where(t=>selected.ContainsKey(t.Target)).ToArray();
                    DeviceDriverReleaseResult[] rows; var issued=false;
                    void Check()
                    {
                        lock(gate)
                        {
                            ExpireCore(); RefreshCore();
                            if(ct.IsCancellationRequested || stopping || releasing || configurationChanging || session!=request.Session ||
                                request.TopologyRevision!=registry.Snapshot.TopologyRevision || group.Stopping || group.Fault is not null ||
                                !lightingGroups.TryGetValue(group.Id,out var current) || current!=group || group.Participants.Any(p=>!CurrentLighting(group,p,endingTargets)))
                                throw new IOException("終了前に照明の対象・所有・構成が変更されました");
                        }
                    }
                    try
                    {
                        rows=await scheduler.RunPreparedAsync(participant.Domains,HardwarePriority.Recovery,participant.Driver,DevicePreparationKind.Release,async token=>
                        {
                            Check(); issued=true;
                            var result=ending.Length==participant.Targets.Length
                                ? await participant.Stream!.EndAsync(DeviceLightingStopReason.Normal,token).ConfigureAwait(false)
                                : await ((ISelectiveDeviceLightingStream)participant.Stream!).EndSelectedAsync(ending.Select(t=>t.Target.EndpointId).ToArray(),token).ConfigureAwait(false);
                            return LightingResults(participant with {Targets=ending},result);
                        },ct,Check).ConfigureAwait(false);
                    }
                    catch(Exception e) { rows=ending.Select(t=>new DeviceDriverReleaseResult(t.Target.EndpointId,new(issued?Outcome.Unknown:Outcome.Rejected,e.Message))).ToArray(); }
                    lock(gate)
                    {
                        var removed=new HashSet<EndpointReference>();
                        foreach(var target in ending)
                        {
                            var result=rows.Single(r=>r.EndpointId==target.Target.EndpointId).Result;
                            var ok=result.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified;
                            if(issued && !ok) failed.Add(group);
                            if(ok)
                            {
                                removed.Add(target.Target); detached[group][target.Target]=result;
                                if(lightingOwners.GetValueOrDefault(target.Target)==group.Id) lightingOwners.Remove(target.Target);
                                group.Programs.Remove(target.Target); group.Origins.Remove(target.Target);
                                group.AppliedPrograms.Remove(target.Target); group.AppliedOrigins.Remove(target.Target);
                                lightingRecoveryRequired.Remove(target.Target); recoveryRequired.Remove(target.Target);
                            }
                            if(states.TryGetValue(target.Target,out var state) && state.PolicyRevision==target.ExpectedPolicyRevision)
                                states[target.Target]=state with { Outcome=state.Generation==target.Generation?result.Outcome:Outcome.Unknown,
                                    Owned=ok?false:state.Owned, RequestedLighting=ok?null:group.Programs.GetValueOrDefault(target.Target),
                                    ReportedLighting=ok && state.Generation==target.Generation?result.ReportedLighting:state.ReportedLighting,
                                    Lighting=ok?LightingStatus(group) with {Running=false,LastColor=result.ReportedLighting?.Colors.FirstOrDefault()}:state.Lighting,
                                    Message=result.Message };
                        }
                        var remaining=participant.Targets.Where(t=>!removed.Contains(t.Target)).ToArray();
                        group.Participants=group.Participants.Where(p=>p!=participant || remaining.Length>0)
                            .Select(p=>p==participant?p with {Targets=remaining,PreviouslyOwned=p.PreviouslyOwned.Where(e=>remaining.Any(t=>t.Target.EndpointId==e)).ToArray()}:p).ToArray();
                        if(group.Participants.Length==0)
                        {
                            lightingGroups.Remove(group.Id);
                            if(group.Ending is null)
                            { group.Stopping=true; group.Stop.Cancel(); group.Stop.Dispose(); group.Ending=Task.FromResult(detached[group].ToDictionary()); }
                        }
                    }
                    return ending.Select(t=>{ var result=rows.Single(r=>r.EndpointId==t.Target.EndpointId).Result;
                        return new DeviceTargetResult(t.Target,t.Generation,t.ExpectedPolicyRevision,result.Outcome,result.Message,ReportedLighting:result.ReportedLighting); }).ToArray();
                }))).ConfigureAwait(false);
            return streamResults.SelectMany(r=>r).Concat(await Task.WhenAll(native).ConfigureAwait(false)).ToArray();
        });
        foreach(var group in groups) group.FrameWork=work;
        return Complete();
        async Task<DeviceTargetResult[]> Complete()
        {
            try
            {
                var result=await work.ConfigureAwait(false); LightingGroup[] restore;
                lock(gate) restore=failed.ToArray();
                await Task.WhenAll(restore.Select(g=>StopLightingGroupAsync(g,DeviceLightingStopReason.Failure))).ConfigureAwait(false);
                return result;
            }
            finally
            {
                lock(gate)
                {
                    foreach(var t in reserved) lightingTransitionTargets.Remove(t);
                    foreach(var group in groups) group.Reconfiguring=false;
                }
            }
        }
    }
}
