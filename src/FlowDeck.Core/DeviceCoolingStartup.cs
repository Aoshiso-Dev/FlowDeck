namespace FlowDeck.Core;

public sealed record CoolingStartupSelection(bool Enabled,EndpointReference[] Targets);
public sealed record CoolingStartupEdit(string Epoch,string Generation,long Topology,CoolingStartupSelection Selection);
public static class DeviceCoolingStartup
{
    public static bool Valid(CoolingStartupSelection? value)=>value is { Targets.Length: <= 256 }&&
        (value.Enabled?value.Targets.Length>0:value.Targets.Length==0)&&value.Targets.All(DeviceConfigurationRules.ValidReference)&&
        value.Targets.Distinct().Count()==value.Targets.Length;
    public static string PolicyName(CoolingSavedPolicy? policy)=>policy?.Mode switch {
        FanMode.Manual=>$"固定 {policy.Duty}%",FanMode.Auto=>"自動 / "+policy.Profile?.Name,_=>"適用済みの制御なし" };
    // Cached state only. Both GUI and service use this preflight; actual restoration repeats driver checks immediately before I/O.
    public static string? Problem(CoolingEndpointConfiguration endpoint,DeviceBindings bindings,DeviceServiceState state,PhysicalDeviceBinding[] candidates,double elapsed=0)
    {
        if(!double.IsFinite(elapsed)||elapsed is < 0 or >= 3||state.Releasing||state.ConfigurationChanging) return "状態を更新してください";
        if(state.LifecycleError is { } lifecycle) return lifecycle;
        if(!endpoint.WiringConfirmed) return "配線と出力の確認が必要です";
        if(DeviceConfigurationRules.SavedPolicyReadiness(endpoint) is { } error) return error;
        var policy=endpoint.SavedPolicy!;
        var instance=state.Inventory.Devices.SingleOrDefault(d=>d.Device.Id==endpoint.Target.DeviceId&&d.Connected);
        var port=instance?.Device.Endpoints.SingleOrDefault(e=>e.Id==endpoint.Target.EndpointId&&e.Kind==EndpointKind.Cooling);
        var execution=state.Endpoints.SingleOrDefault(e=>e.Target==endpoint.Target&&e.Generation==instance?.Generation);
        if(port is null||execution is null||!port.Capabilities.Any(c=>c.Feature==DeviceFeature.FixedDuty&&c.Usable)||
            policy.Mode==FanMode.Auto&&!port.Capabilities.Any(c=>c.Feature==DeviceFeature.AutomaticDuty&&c.Usable)) return "この端子を操作できません";
        if(execution.Outcome==Outcome.Pending) return "端子の操作・試験が完了してから起動時再開を設定してください";
        if(execution.Cooling?.Faulted==true||execution.Persistence is { State:not DevicePolicySaveState.Saved }) return "最後の操作・保存を確認してください";
        var sensors=DeviceCoolingPolicy.Required(policy,endpoint.Safety);
        foreach(var id in sensors.Select(s=>s.DeviceId).Append(endpoint.Target.DeviceId).Distinct())
            if(bindings.Devices.SingleOrDefault(b=>b.DeviceId==id) is not { } saved||!candidates.Contains(saved)||
                !state.Inventory.Devices.Any(d=>d.Connected&&d.Device.Id==id)) return "機器または温度源の個体・構成が一致しません";
        double? Value(EndpointReference target,DeviceFeature feature)
        { var sample=DeviceDashboard.Sample(state,target,feature); return sample is not null&&DeviceDashboard.Fresh(sample,elapsed)?sample.Value:null; }
        var duty=Value(endpoint.Target,DeviceFeature.DutyRead); var rpm=Value(endpoint.Target,DeviceFeature.Rpm);
        if(duty is not (>=0 and <=100)||rpm is not (>=0)) return "現在の出力と回転数を取得できません";
        var temperatures=new Dictionary<EndpointReference,double>();
        foreach(var sensor in sensors)
        {
            var found=state.Inventory.Devices.Single(d=>d.Device.Id==sensor.DeviceId).Device.Endpoints.SingleOrDefault(e=>e.Id==sensor.EndpointId);
            if(found is not { Kind:EndpointKind.Sensor }||!found.Capabilities.Any(c=>c.Feature==DeviceFeature.Temperature&&c.Usable)||Value(sensor,DeviceFeature.Temperature) is not { } value)
                return "必要な温度を取得できません";
            temperatures[sensor]=value;
        }
        return new DeviceCoolingPolicy(policy,endpoint.Safety,(int)Math.Round(duty.Value)).InputError(temperatures);
    }
}
