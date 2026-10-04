using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceConfigurationAuthority
{
    private DeviceConfigurationReply SaveCoolingStartup(DeviceConfigurationRequest request,DeviceControlRuntime runtime)
    {
        if(request.ExpectedGeneration!=generation.Id) throw new IOException("適用済みの冷却方針が変わりました。再取得してください");
        var state=runtime.State(); var selection=request.CoolingStartup!;
        if(state.Inventory.TopologyRevision!=request.TopologyRevision||state.Releasing||state.ConfigurationChanging||coolingAttempts.Count!=0)
            throw new IOException("機器構成が変わったか、冷却の処理中です。再取得してください");
        var candidates=runtime.PhysicalCandidates();
        foreach(var target in selection.Targets)
        {
            var endpoint=configuration.Cooling.Endpoints.SingleOrDefault(e=>e.Target==target)??throw new IOException("設定済みの冷却端子を選択してください");
            if(DeviceCoolingStartup.Problem(endpoint,configuration.Bindings,state,candidates) is { } problem) throw new IOException(problem);
        }
        var next=configuration with { Preferences=configuration.Preferences with { Startup=configuration.Preferences.Startup with { ResumeControl=selection.Enabled } },
            Cooling=configuration.Cooling with { Endpoints=configuration.Cooling.Endpoints.Select(e=>e with { ResumeOnStart=selection.Targets.Contains(e.Target) }).ToArray() } };
        if(!Same(configuration.Cooling,next.Cooling)||!Same(configuration.Preferences,next.Preferences)) PersistConfiguration(next,"起動時の冷却");
        coolingResumeIntent=configuration.Cooling.Endpoints.ToDictionary(e=>e.Target,e=>e.ResumeOnStart);
        // An old preview must never replace a freshly selected startup policy.
        upload=null;
        return Reply("起動時の冷却設定を保存しました",changed:["cooling.json","preferences.json"]);
    }
}
