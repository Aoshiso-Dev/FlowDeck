using System.Collections.ObjectModel;
using System.Windows.Input;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed class DeviceCoolingStartupRow(DeviceCoolingStartupDraft draft,CoolingStartupCandidate candidate,Action changed,Action<string> failed) : Observable
{
    public EndpointReference Target=>candidate.Target;
    public string Name=>candidate.Name;
    public string Policy=>candidate.Policy;
    public bool Selected { get=>draft.Selected(Target); set { try { draft.Select(Target,value); changed(); } catch(Exception e) { failed(e.Message); } Refresh(); } }
    public bool CanSelect=>Selected||draft.TargetProblem(Target) is null;
    public string Status=>draft.TargetProblem(Target)??"再開可能";
}
public sealed partial class DeviceWorkspaceViewModel
{
    private DeviceCoolingStartupDraft? startupDraft;
    public ObservableCollection<DeviceCoolingStartupRow> StartupRows { get; }=[];
    public bool StartupSupported=>client.SupportsCoolingStartup;
    public bool StartupIdle=>SetupIdle;
    public bool StartupEnabled { get=>startupDraft?.Enabled==true; set { startupDraft?.SetEnabled(value); PublishStartup(); } }
    public bool CanSaveStartup=>StartupIdle&&startupDraft is { Dirty:true,Problem:null };
    public string StartupStatus=>startupDraft?.Problem??(startupDraft?.Dirty==true?"未保存":"保存済み");
    public ICommand SaveStartup=>new AsyncCommand(_=>SaveStartupAsync(),_=>CanSaveStartup,e=>Notice=e.Message);
    public ICommand ReloadStartup=>new AsyncCommand(_=>ReloadStartupAsync(),_=>StartupIdle,e=>Notice=e.Message);
    private void PublishStartup()
    {
        foreach(var name in new[]{nameof(StartupSupported),nameof(StartupIdle),nameof(StartupEnabled),nameof(CanSaveStartup),nameof(StartupStatus)}) Notify(name);
        foreach(var row in StartupRows) row.Refresh(); CommandManager.InvalidateRequerySuggested();
    }
    private void RenderStartup(DeviceWorkspaceSnapshot snapshot)
    {
        if(startupDraft is null||!startupDraft.Dirty&&(startupDraft.Generation!=snapshot.Configuration.Generation||startupDraft.Conflict))
        {
            startupDraft=new(snapshot); StartupRows.Clear();
            foreach(var candidate in startupDraft.Candidates) StartupRows.Add(new(startupDraft,candidate,PublishStartup,e=>Notice=e));
        }
        startupDraft.Update(snapshot); PublishStartup();
    }
    public async Task SaveStartupAsync()
    {
        Render(); if(!CanSaveStartup) return; var edit=startupDraft!.Capture(); setupBusy=true; ClearConfigurationPreviews(); Render();
        try { var result=await client.SaveCoolingStartupAsync(edit); Notice=result.Message; if(result.Outcome==Outcome.Confirmed) startupDraft=null; }
        finally { setupBusy=false; Render(); }
    }
    public async Task ReloadStartupAsync()
    {
        if(!StartupIdle) return; setupBusy=true; Render();
        try { await client.RefreshAsync(true); startupDraft=null; }
        finally { setupBusy=false; Render(); }
    }
    public async Task RestoreStartupAsync()
    {
        var previousNotice=Notice;
        if(client.Snapshot?.Configuration.Configuration.Preferences.Startup.ResumeControl==true) Notice="起動時の冷却を確認しています";
        setupBusy=true; Render();
        try
        {
            var results=await client.RestoreStartupAsync();
            if(results.Length>0&&!closing)
            {
                var failures=results.Where(r=>r.Outcome!=Outcome.Confirmed).ToArray();
                Notice=$"起動時の冷却: {results.Length-failures.Length}端子を再開"+(failures.Length==0?"":$" / {failures.Length}端子は未再開: "+string.Join(" / ",failures.Select(f=>StartupRows.FirstOrDefault(r=>r.Target==f.Target)?.Name+"「"+f.Message+"」")));
            }
            else if(!closing) Notice=previousNotice;
        }
        catch(Exception e) { if(!closing) Notice="起動時の冷却を再開できません: "+e.Message; }
        finally { setupBusy=false; Render(); }
    }
}
