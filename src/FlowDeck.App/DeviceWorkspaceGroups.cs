using System.Collections.ObjectModel;
using System.Windows.Input;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed class CoolingGroupChoiceRow(DeviceCoolingGroupEditorDraft draft,CoolingGroupMember candidate,Action changed) : Observable
{
    public EndpointReference Target=>candidate.Target;
    public string Name=>candidate.Name;
    public bool Selected { get=>draft.Selected(Target); set { draft.Select(Target,value); Refresh(); changed(); } }
}
public sealed class CoolingGroupEditor : Observable
{
    private readonly Action changed;
    public DeviceCoolingGroupEditorDraft Draft { get; }
    public ObservableCollection<CoolingGroupChoiceRow> Members { get; }=[];
    public string Name { get=>Draft.Name; set { Draft.Name=value; Refresh(); changed(); } }
    public string Status=>Draft.Problem??"保存できます";
    public CoolingGroupEditor(DeviceCoolingGroupEditorDraft draft,Action changed)
    { Draft=draft; this.changed=changed; foreach(var candidate in draft.Candidates) Members.Add(new(draft,candidate,changed)); }
    public void Update(DeviceWorkspaceSnapshot snapshot) { Draft.Update(snapshot); Notify(nameof(Status)); }
}
public sealed class CoolingGroupReading(EndpointReference target) : Observable
{
    public EndpointReference Target { get; }=target;
    public string Name { get; private set; }="";
    public string Rpm { get; private set; }="—";
    public string Duty { get; private set; }="—";
    public string Minimum { get; private set; }="";
    public string Status { get; private set; }="";
    public string Result { get; private set; }="";
    public void Update(DeviceWorkspaceSnapshot snapshot,DeviceTargetResult? result)
    {
        var config=snapshot.Configuration.Configuration; var device=config.Devices.Devices.SingleOrDefault(d=>d.Id==Target.DeviceId);
        var endpoint=device?.Endpoints.SingleOrDefault(e=>e.Id==Target.EndpointId);
        if(endpoint is null) { Rpm="—"; Duty="—"; Minimum="未設定"; Status="未登録"; Result=""; Refresh(); return; }
        Name=device!.Name+" / "+endpoint.Name;
        var reading=DeviceDashboard.Read(new(Target,DeviceFeature.Rpm,Name,endpoint.Role=="pump"?"pump":"fan"),config.Devices,snapshot.State,snapshot.ElapsedSeconds,snapshot.Connected);
        Rpm=reading.DisplayValue+" RPM"; Duty=reading.DisplayDuty; Status=reading.Control;
        Minimum=config.Cooling.Endpoints.SingleOrDefault(e=>e.Target==Target)?.Safety.Minimum is { } value?$"最低 {value}%":"未設定";
        Result=result is null?"":"前回: "+(result.Persistence is { State:not DevicePolicySaveState.Saved }?result.Persistence.Message:result.Message);
        Refresh();
    }
}
public sealed partial class DeviceWorkspaceViewModel
{
    private DeviceProfileChoice? selectedCoolingGroup,groupProfile;
    private DeviceCoolingGroupDraft? coolingGroupDraft;
    private readonly Dictionary<EndpointReference,DeviceTargetResult> coolingGroupResults=[];
    private CoolingTemperatureProfile[] groupProfiles=[];
    private bool updatingCoolingGroups;
    public ObservableCollection<DeviceProfileChoice> CoolingGroups { get; }=[];
    public ObservableCollection<DeviceProfileChoice> CoolingGroupProfiles { get; }=[];
    public ObservableCollection<CoolingGroupReading> CoolingGroupReadings { get; }=[];
    public CoolingGroupEditor? GroupEditor { get; private set; }
    public bool CoolingGroupsSupported=>client.SupportsCoolingGroups;
    public bool HasCoolingGroup=>coolingGroupDraft is not null;
    public bool HasCoolingGroupEditor=>GroupEditor is not null;
    public bool CoolingGroupsIdle=>SetupIdle;
    public string[] CoolingGroupModes { get; }=["固定","自動"];
    public DeviceProfileChoice? SelectedCoolingGroup { get=>selectedCoolingGroup; set {
        if(updatingCoolingGroups||selectedCoolingGroup?.Id==value?.Id) return; selectedCoolingGroup=value; coolingGroupDraft=null; groupProfile=null; coolingGroupResults.Clear(); Render(); } }
    public int CoolingGroupDuty { get=>coolingGroupDraft?.Duty??50; set { if(updatingCoolingGroups||value==CoolingGroupDuty) return; coolingGroupDraft?.Change(value,CoolingGroupAutomatic,SelectedGroupProfile()); PublishCoolingGroups(); } }
    public string CoolingGroupMode { get=>CoolingGroupAutomatic?"自動":"固定"; set { if(updatingCoolingGroups||value is not ("固定" or "自動")||value==CoolingGroupMode) return; coolingGroupDraft?.Change(CoolingGroupDuty,value=="自動",SelectedGroupProfile()); PublishCoolingGroups(); } }
    public bool CoolingGroupAutomatic=>coolingGroupDraft?.Automatic==true;
    public bool CoolingGroupFixed=>!CoolingGroupAutomatic;
    public DeviceProfileChoice? CoolingGroupProfile { get=>groupProfile; set { if(updatingCoolingGroups||Equals(groupProfile,value)) return; groupProfile=value; coolingGroupDraft?.Change(CoolingGroupDuty,CoolingGroupAutomatic,SelectedGroupProfile()); PublishCoolingGroups(); } }
    private CoolingTemperatureProfile? SelectedGroupProfile()=>groupProfile is null?null:groupProfiles[int.Parse(groupProfile.Id)];
    public string CoolingGroupStatus=>coolingGroupDraft?.Problem()??(coolingGroupDraft?.Dirty==true?"未適用":"");
    public bool CanApplyCoolingGroup=>SetupIdle&&coolingGroupDraft is not null&&coolingGroupDraft.Problem() is null;
    public bool CanReleaseCoolingGroup=>SetupIdle&&coolingGroupDraft is not null&&coolingGroupDraft.Problem(DeviceOperation.ReleaseCooling) is null;
    public bool ShowReleaseCoolingGroup=>coolingGroupDraft is { } draft&&client.Snapshot is { } s&&draft.Group.Targets.All(t=>
        s.State.Inventory.Devices.SingleOrDefault(d=>d.Device.Id==t.DeviceId)?.Device.Endpoints.SingleOrDefault(e=>e.Id==t.EndpointId)?.Capabilities.Any(c=>c.Feature==DeviceFeature.ReleaseCooling&&c.Support==ImplementationSupport.Supported)==true);
    public bool CanSaveCoolingGroup=>SetupIdle&&GroupEditor?.Draft.Problem is null&&GroupEditor is not null;
    public bool CanDeleteCoolingGroup=>SetupIdle&&GroupEditor?.Draft is { Existing:true,Conflict:false };
    public ICommand ApplyCoolingGroup=>new AsyncCommand(_=>OperateCoolingGroupAsync(),_=>CanApplyCoolingGroup,e=>Notice=e.Message);
    public ICommand ReleaseCoolingGroup=>new AsyncCommand(_=>OperateCoolingGroupAsync(DeviceOperation.ReleaseCooling),_=>CanReleaseCoolingGroup,e=>Notice=e.Message);
    public ICommand NewCoolingGroup=>new AsyncCommand(_=>{ OpenCoolingGroupEditor(); return Task.CompletedTask; },_=>SetupIdle,e=>Notice=e.Message);
    public ICommand EditCoolingGroup=>new AsyncCommand(_=>{ OpenCoolingGroupEditor(SelectedCoolingGroup?.Id); return Task.CompletedTask; },_=>SetupIdle&&SelectedCoolingGroup is not null,e=>Notice=e.Message);
    public ICommand SaveCoolingGroup=>new AsyncCommand(_=>SaveCoolingGroupAsync(),_=>CanSaveCoolingGroup,e=>Notice=e.Message);
    public ICommand DeleteCoolingGroup=>new AsyncCommand(_=>SaveCoolingGroupAsync(true),_=>CanDeleteCoolingGroup,e=>Notice=e.Message);
    public ICommand CloseCoolingGroupEditor=>new AsyncCommand(_=>{ GroupEditor=null; PublishCoolingGroups(); return Task.CompletedTask; },_=>SetupIdle);
    public ICommand ReloadCoolingGroups=>new AsyncCommand(_=>ReloadCoolingGroupsAsync(),_=>SetupIdle,e=>Notice=e.Message);
    private void PublishCoolingGroups()
    {
        foreach(var name in new[]{nameof(SelectedCoolingGroup),nameof(HasCoolingGroup),nameof(HasCoolingGroupEditor),nameof(GroupEditor),nameof(CoolingGroupsSupported),
            nameof(CoolingGroupsIdle),nameof(CoolingGroupDuty),nameof(CoolingGroupMode),nameof(CoolingGroupAutomatic),nameof(CoolingGroupFixed),nameof(CoolingGroupProfile),
            nameof(CoolingGroupStatus),nameof(CanApplyCoolingGroup),nameof(CanReleaseCoolingGroup),nameof(ShowReleaseCoolingGroup),nameof(CanSaveCoolingGroup),nameof(CanDeleteCoolingGroup)}) Notify(name);
        GroupEditor?.Refresh(); CommandManager.InvalidateRequerySuggested();
    }
    private void RenderCoolingGroups(DeviceWorkspaceSnapshot snapshot)
    {
        updatingCoolingGroups=true;
        try
        {
        var config=snapshot.Configuration.Configuration;
        Reconcile(CoolingGroups,config.Cooling.Groups.Select(g=>new DeviceProfileChoice(g.Id,g.Name)).ToArray());
        if(selectedCoolingGroup is not null&&!CoolingGroups.Any(g=>g.Id==selectedCoolingGroup.Id)&&coolingGroupDraft?.Dirty!=true) { selectedCoolingGroup=null; coolingGroupDraft=null; }
        if(selectedCoolingGroup is not null&&coolingGroupDraft is null) coolingGroupDraft=new(snapshot,selectedCoolingGroup.Id);
        coolingGroupDraft?.Update(snapshot);
        // Refresh an untouched selection after a saved policy/group edit; retain explicit slider edits as conflicts.
        if(coolingGroupDraft is { Dirty:false } draft&&config.Cooling.Groups.Any(g=>g.Id==draft.Group.Id)) coolingGroupDraft=new(snapshot,draft.Group.Id);
        var targets=coolingGroupDraft?.Group.Targets??[];
        foreach(var old in CoolingGroupReadings.Where(r=>!targets.Contains(r.Target)).ToArray()) CoolingGroupReadings.Remove(old);
        foreach(var target in targets)
        {
            var row=CoolingGroupReadings.SingleOrDefault(r=>r.Target==target);
            if(row is null) { row=new(target); CoolingGroupReadings.Add(row); }
            row.Update(snapshot,coolingGroupResults.GetValueOrDefault(target));
        }
        if(coolingGroupDraft?.Dirty!=true)
        {
            var choices=config.Cooling.Endpoints.Where(e=>targets.Contains(e.Target)).SelectMany(e=>e.Profiles.Select(p=>(Profile:p,Source:coolingGroupDraft!.Name(e.Target))))
                .Concat((config.Cooling.ProfileTemplates??[]).Select(p=>(Profile:p,Source:"共通"))).DistinctBy(p=>SettingsTransfer.Canonical(p.Profile)).ToArray();
            groupProfiles=choices.Select(p=>p.Profile).ToArray();
            Reconcile(CoolingGroupProfiles,choices.Select((p,i)=>new DeviceProfileChoice(i.ToString(),p.Profile.Name+" / "+p.Source)).ToArray());
            if(groupProfile is not null&&!CoolingGroupProfiles.Contains(groupProfile)) groupProfile=null;
            if(coolingGroupDraft?.Profile is { } selectedProfile)
                groupProfile=CoolingGroupProfiles.ElementAtOrDefault(Array.FindIndex(groupProfiles,p=>SettingsTransfer.Canonical(p)==SettingsTransfer.Canonical(selectedProfile)));
        }
        GroupEditor?.Update(snapshot); PublishCoolingGroups();
        }
        finally { updatingCoolingGroups=false; }
    }
    public void OpenCoolingGroupEditor(string? id=null)
    {
        if(!SetupIdle||client.Snapshot is not { } snapshot) return;
        GroupEditor=new(new(snapshot,id),PublishCoolingGroups); PublishCoolingGroups();
    }
    public async Task SaveCoolingGroupAsync(bool delete=false)
    {
        Render(); if(delete?!CanDeleteCoolingGroup:!CanSaveCoolingGroup) return;
        var edit=GroupEditor!.Draft.Capture(delete); setupBusy=true; ClearConfigurationPreviews(); Render();
        try
        {
            var reply=await client.SaveCoolingGroupAsync(edit); Notice=reply.Message; GroupEditor=null;
            if(delete&&selectedCoolingGroup?.Id==edit.Change.Id) selectedCoolingGroup=null;
            else if(!delete) selectedCoolingGroup=new(edit.Change.Id,edit.Change.Group!.Name);
            coolingGroupDraft=null; groupProfile=null;
        }
        finally { setupBusy=false; Render(); }
    }
    public async Task ReloadCoolingGroupsAsync()
    {
        if(!SetupIdle) return; setupBusy=true; Render();
        try
        {
            await client.RefreshAsync(true); coolingGroupDraft=null; groupProfile=null; coolingGroupResults.Clear();
            if(GroupEditor is { } editor) GroupEditor=new(new(client.Snapshot!,editor.Draft.Existing&&client.Snapshot!.Configuration.Configuration.Cooling.Groups.Any(g=>g.Id==editor.Draft.Id)?editor.Draft.Id:null),PublishCoolingGroups);
        }
        finally { setupBusy=false; Render(); }
    }
    public async Task OperateCoolingGroupAsync(DeviceOperation? operation=null)
    {
        Render(); if(!SetupIdle||coolingGroupDraft is null) return; var edit=coolingGroupDraft.Capture(operation); setupBusy=true; Render();
        try
        {
            var result=await client.ApplyCoolingGroupAsync(edit); coolingGroupResults.Clear();
            foreach(var target in result.Targets) coolingGroupResults[target.Target]=target;
            Notice=result.Message; coolingGroupDraft=null; groupProfile=null;
        }
        finally { setupBusy=false; Render(); }
    }
}
