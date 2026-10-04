using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record CoolingStartupResult(EndpointReference Target,Outcome Outcome,string Message);
public sealed partial class DeviceWorkspaceClient
{
    private bool startupAttempted;
    private volatile bool startupStopRequested;
    public bool SupportsCoolingStartup=>client.Hello?.Features.Contains("cooling-startup-settings-v2")==true;
    public async Task<DeviceConfigurationReply> SaveCoolingStartupAsync(CoolingStartupEdit edit,CancellationToken ct=default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            if(configuration?.Generation!=edit.Generation||client.Hello?.Epoch!=edit.Epoch) throw new IOException("冷却の方針が変わりました。再取得してください");
            return await client.SaveCoolingStartupAsync(edit,ct).ConfigureAwait(false);
        }
        finally
        {
            try { if(client.Connected) { var next=await client.ReadConfigurationAsync(ct).ConfigureAwait(false); lock(gate) configuration=next; await ReadStateAsync(ct).ConfigureAwait(false); } }
            catch(Exception e) { RecordError(e); }
            operations.Release();
        }
    }
    // Called only by the normal application launch. Polling/reconnect never calls this, and an uncertain attempt is never replayed.
    public async Task<CoolingStartupResult[]> RestoreStartupAsync(CancellationToken ct=default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(startupAttempted||startupStopRequested) return []; startupAttempted=true; EnsureOpen();
            if(!SupportsCoolingStartup||client.Hello?.Features.Contains("restore-saved-cooling-v2")!=true) return [];
            var saved=await client.ReadConfigurationAsync(ct).ConfigureAwait(false); lock(gate) configuration=saved;
            await ReadStateAsync(ct).ConfigureAwait(false);
            if(!saved.Configuration.Preferences.Startup.ResumeControl) return [];
            var epoch=state!.Inventory.Epoch; var topology=state.Inventory.TopologyRevision; var results=new List<CoolingStartupResult>();
            foreach(var endpoint in saved.Configuration.Cooling.Endpoints.Where(e=>e.ResumeOnStart))
            {
                if(startupStopRequested) break;
                var issued=false;
                try
                {
                    EnsureOpen(); await ReadStateAsync(ct).ConfigureAwait(false);
                    if(state!.Inventory.Epoch!=epoch||state.Inventory.TopologyRevision!=topology) throw new IOException("起動処理中に接続・機器構成が変わりました");
                    if(DeviceCoolingStartup.Problem(endpoint,saved.Configuration.Bindings,state,saved.Candidates) is { } problem) throw new IOException(problem);
                    var row=state.Endpoints.Single(e=>e.Target==endpoint.Target);
                    if(row.Owned) throw new IOException("現在の制御を優先しました");
                    if(startupStopRequested) break;
                    issued=true;
                    var reply=await client.SendAsync(client.Request(DeviceOperation.RestoreSavedCooling,topology:topology,
                        targets:[new(endpoint.Target,row.Generation,row.PolicyRevision)]),ct).ConfigureAwait(false);
                    var result=reply.Result?.Targets.SingleOrDefault(t=>t.Target==endpoint.Target);
                    var persistenceFailed=result?.Persistence is { State:not DevicePolicySaveState.Saved };
                    results.Add(new(endpoint.Target,persistenceFailed?Outcome.Partial:result?.Outcome??Outcome.Rejected,
                        persistenceFailed?result!.Persistence!.Message:result?.Message??reply.Error??"再開結果を取得できません"));
                }
                catch(Exception e) when(e is not OperationCanceledException) { results.Add(new(endpoint.Target,issued?Outcome.Unknown:Outcome.Rejected,e.Message)); }
            }
            if(client.Connected) { var next=await client.ReadConfigurationAsync(ct).ConfigureAwait(false); lock(gate) configuration=next; await ReadStateAsync(ct).ConfigureAwait(false); }
            return results.ToArray();
        }
        catch(Exception e) { if(!client.Connected) RecordError(e); throw; }
        finally { operations.Release(); }
    }
}
