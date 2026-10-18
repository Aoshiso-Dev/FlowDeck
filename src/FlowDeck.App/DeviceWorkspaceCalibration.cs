using System.Collections.ObjectModel;
using System.Windows.Input;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed class DeviceCalibrationEditor(DeviceCoolingCalibrationDraft draft,Action changed) : Observable
{
    public DeviceCoolingCalibrationDraft Draft { get; }=draft;
    public int Duty { get=>Draft.Duty; set { if(Draft.Busy||value!=100&&!Draft.CanReduce) return; Draft.ChangeDuty(Math.Clamp(value,1,100)); Refresh(); changed(); } }
    public bool CanReduce=>!Draft.Busy&&Draft.CanReduce;
    public string CurrentRpm=>Draft.Rpm is { } rpm?$"{rpm:0} RPM":"— RPM";
    public string CurrentDuty=>Draft.CurrentDuty is { } duty?$"{duty}%":"—";
    public string Restoration=>Draft.Restoration;
    public string Status=>Draft.Problem??"";
    public bool HasResult=>Draft.Result is not null;
    public string ResultSummary=>Draft.Result is { } r?$"{r.Duty}% · "+(r.Completed?"試験完了":"試験未完了")+" · "+(r.Restored?"復元済み":"復元未確認"):"";
    public string ResultRpm=>Draft.Result is { MinimumRpm:{ } min,MaximumRpm:{ } max,LastRpm:{ } last }?$"{min:0} ～ {max:0} RPM  /  最終 {last:0} RPM":"回転数の記録なし";
    public string Before=>Draft.Result?.Before is { } b?$"開始前 {b.Duty}% · {b.Rpm:0} RPM":"";
    public string ResultMessage=>Draft.Result?.Message??"";
    public bool Successful=>Draft.Result is { Completed:true,Restored:true,Outcome:Outcome.Confirmed }&&Draft.CanReduce;
    public void Update(DeviceWorkspaceSnapshot snapshot) { Draft.Update(snapshot); Refresh(); }
}

public sealed partial class DeviceWorkspaceViewModel
{
    public ObservableCollection<DeviceSetupChoice> CalibrationChoices { get; }=[];
    private DeviceSetupChoice? selectedCalibration;
    private DeviceCalibrationEditor? calibrationEditor;
    private bool calibrationBusy,calibrationCancelBusy,updatingCalibration,calibrationExpanded,setupExpanded;
    public bool CalibrationSupported=>client.SupportsCoolingCalibration;
    public bool CalibrationIdle=>!calibrationBusy&&!closing&&!setupBusy&&!dashboardBusy&&!monitoringBusy&&!lightingBusy&&Cooling.All(r=>!r.Busy)&&client.Snapshot is { Connected:true,ExitAttempted:false };
    public bool CalibrationExpanded { get=>calibrationExpanded; set=>Set(ref calibrationExpanded,value); }
    public bool SetupExpanded { get=>setupExpanded; set=>Set(ref setupExpanded,value); }
    public DeviceCalibrationEditor? CalibrationEditor=>SelectedCalibration is not null?calibrationEditor:null;
    public bool HasCalibrationEditor=>CalibrationEditor is not null;
    public bool HasCalibrationResult=>CalibrationEditor?.HasResult==true;
    public DeviceSetupChoice? SelectedCalibration
    {
        get=>selectedCalibration is not null&&CalibrationChoices.Any(c=>c.Target==selectedCalibration.Target)?selectedCalibration:null;
        set
        {
            if(updatingCalibration||!CalibrationIdle||value?.Target==selectedCalibration?.Target) return;
            selectedCalibration=value; calibrationEditor=null;
            if(value is not null&&client.Snapshot is { } snapshot)
                try { calibrationEditor=new(new(snapshot,value.Target),PublishCalibration); } catch(Exception e) { Notice=e.Message; }
            PublishCalibration();
        }
    }
    public bool CanStartCalibration=>CalibrationIdle&&calibrationEditor?.Draft.Problem is null&&calibrationEditor is not null;
    public bool CanCancelCalibration=>calibrationBusy&&!calibrationCancelBusy&&client.CalibrationRunning;
    public bool CanAdoptCalibration=>CalibrationIdle&&calibrationEditor?.Draft.CanAdopt==true;
    public ICommand StartCalibration=>new AsyncCommand(_=>StartCalibrationAsync(),_=>CanStartCalibration,e=>Notice=e.Message);
    public ICommand CancelCalibration=>new AsyncCommand(_=>CancelCalibrationAsync(),_=>CanCancelCalibration,e=>Notice=e.Message);
    public ICommand ReloadCalibration=>new AsyncCommand(_=>ReloadCalibrationAsync(),_=>CalibrationIdle,e=>Notice=e.Message);
    public ICommand AdoptCalibration=>new AsyncCommand(_=>{ AdoptCalibrationMinimum(); return Task.CompletedTask; },_=>CanAdoptCalibration,e=>Notice=e.Message);
    private void PublishCalibration()
    {
        foreach(var name in new[]{nameof(CalibrationSupported),nameof(CalibrationIdle),nameof(CalibrationEditor),nameof(SelectedCalibration),nameof(HasCalibrationEditor),nameof(HasCalibrationResult),
            nameof(CanStartCalibration),nameof(CanCancelCalibration),nameof(CanAdoptCalibration)}) Notify(name);
        CommandManager.InvalidateRequerySuggested();
    }
    private void RenderCalibration(DeviceWorkspaceSnapshot snapshot)
    {
        updatingCalibration=true;
        try
        {
            var targets=(snapshot.Configuration.Setup??[]).Where(c=>c.Options.CoolingCalibrationSupported).Select(c=>c.Target).ToHashSet();
            var choices=SetupChoices.Where(c=>targets.Contains(c.Target)).Select(c=>c.Target==selectedCalibration?.Target?selectedCalibration!:c).ToList();
            Reconcile(CalibrationChoices,choices.ToArray()); calibrationEditor?.Update(snapshot); PublishCalibration();
        }
        finally { updatingCalibration=false; }
    }
    public async Task ReloadCalibrationAsync()
    {
        if(!CalibrationIdle) return; calibrationBusy=true; Render();
        try
        {
            await client.RefreshAsync(true); RenderSetup(client.Snapshot!);
            var target=selectedCalibration?.Target;
            selectedCalibration=SetupChoices.SingleOrDefault(c=>c.Target==target)??selectedCalibration;
            calibrationEditor=selectedCalibration is null?null:new(new(client.Snapshot!,selectedCalibration.Target),PublishCalibration);
        }
        finally { calibrationBusy=false; Render(); }
    }
    public async Task StartCalibrationAsync()
    {
        Render(); if(!CanStartCalibration) return;
        var editor=calibrationEditor!; var edit=editor.Draft.Capture(); DeviceOperationResult? result=null;
        calibrationBusy=true; editor.Draft.Begin(); ClearConfigurationPreviews(); Render();
        using var status = BeginStatus("校正試験・復元を確認中");
        try { result=await client.StartCoolingCalibrationAsync(edit); ReportResult(result); }
        finally
        {
            if(client.Snapshot is { } snapshot) editor.Draft.Complete(result,snapshot);
            calibrationBusy=false; Render();
        }
    }
    public async Task CancelCalibrationAsync()
    {
        if(!CanCancelCalibration) return; calibrationCancelBusy=true; PublishCalibration();
        using var status = BeginStatus("校正試験の中止を要求中");
        try { await client.CancelCoolingCalibrationAsync(); ReportStatus("中止受付済み · 復元確認中"); }
        finally { calibrationCancelBusy=false; Render(); }
    }
    public void AdoptCalibrationMinimum()
    {
        Render(); if(!CanAdoptCalibration) return;
        var draft=calibrationEditor!.Draft.AdoptMinimum(); ClearConfigurationPreviews();
        selectedSetup=SetupChoices.Single(c=>c.Target==draft.Candidate.Target); setupEditor=new(draft,SetupChanged);
        SetupExpanded=true; SelectedPage=3; ReportStatus("最低出力を反映済み · 未保存"); Render();
    }
}
