using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public class DeviceTemperatureEditor(CoolingSensorChoice sensor, double overheat, double resume, Action changed) : Observable
{
    public CoolingSensorChoice Sensor { get; } = sensor;
    private double overheat = overheat, resume = resume;
    protected Action Changed { get; } = changed;
    public double Overheat { get => overheat; set { if (Set(ref overheat,value)) Changed(); } }
    public double ResumeBelow { get => resume; set { if (Set(ref resume,value)) Changed(); } }
    public double? Temperature { get; private set; }
    public double? CurrentDuty { get; private set; }
    public TemperatureRule Rule => new(Sensor.Target,Overheat,ResumeBelow);
    public void Update(DeviceWorkspaceSnapshot snapshot, EndpointReference fan)
    {
        Temperature = DeviceCoolingSettingsDraft.Temperature(snapshot,Sensor.Target);
        CurrentDuty = DeviceDashboard.Read(new(fan,DeviceFeature.Rpm,"ファン","fan"),snapshot.Configuration.Configuration.Devices,
            snapshot.State,snapshot.ElapsedSeconds,snapshot.Connected).Duty;
        Notify(nameof(Temperature)); Notify(nameof(CurrentDuty));
    }
}
public sealed class DeviceCurveSettingsEditor : DeviceTemperatureEditor
{
    public ObservableCollection<CurvePointRow> Points { get; } = [];
    private readonly List<CurvePointRow> watched=[];
    public DeviceCurveSettingsEditor(CoolingSensorChoice sensor, TemperatureDemandCurve curve, Action changed) : base(sensor,curve.Overheat,curve.ResumeBelow,changed)
    {
        foreach(var point in curve.Points) Points.Add(new(point.Temperature,point.Duty));
        Watch(); Points.CollectionChanged+=(_,_)=> { Watch(); Changed(); };
    }
    private void Watch()
    { foreach(var point in watched) point.PropertyChanged-=PointChanged; watched.Clear(); foreach(var point in Points) { watched.Add(point); point.PropertyChanged+=PointChanged; } }
    private void PointChanged(object? sender,PropertyChangedEventArgs args) => Changed();
    public TemperatureDemandCurve Value => new(Sensor.Target,Points.Select(p=>new CurvePoint(p.Temperature,p.Duty)).ToArray(),Overheat,ResumeBelow);
}
public sealed class DeviceCoolingProfileEditor : Observable
{
    private readonly Action changed;
    private string name;
    private double delay, rate;
    public string Id { get; }
    public long Revision { get; }
    public ObservableCollection<DeviceCurveSettingsEditor> Curves { get; }=[];
    public string Name { get=>name; set { if(Set(ref name,value)) changed(); } }
    public double DescentDelay { get=>delay; set { if(Set(ref delay,value)) changed(); } }
    public double DescentRate { get=>rate; set { if(Set(ref rate,value)) changed(); } }
    public DeviceCoolingProfileEditor(CoolingTemperatureProfile profile, Func<EndpointReference,CoolingSensorChoice> sensor, Action changed)
    {
        this.changed=changed; Id=profile.Id; Revision=profile.Revision; name=profile.Name; delay=profile.DescentDelaySeconds??3; rate=profile.DescentPerSecond??2;
        foreach(var curve in profile.Curves) Curves.Add(new(sensor(curve.Sensor),curve,changed));
        Curves.CollectionChanged+=(_,_)=>changed();
    }
    public CoolingTemperatureProfile Value => new(Id,Name,Revision,Curves.Select(c=>c.Value).ToArray(),DescentDelay,DescentRate);
}
public sealed partial class DeviceCoolingSettingsEditor : Observable
{
    public DeviceCoolingSettingsDraft Draft { get; }
    public ObservableCollection<DeviceCoolingProfileEditor> Profiles { get; }=[];
    public ObservableCollection<DeviceTemperatureEditor> Protection { get; }=[];
    private DeviceCoolingProfileEditor? selectedProfile;
    private CoolingSensorChoice? selectedSensor, protectionSensor;
    private bool busy;
    public DeviceCoolingSettingsEditor(DeviceWorkspaceSnapshot snapshot, EndpointReference target)
    {
        Draft=new(snapshot,target);
        foreach(var p in Draft.Profiles) Profiles.Add(new(p,Sensor,Changed));
        foreach(var r in Draft.Safety.ManualProtection) Protection.Add(new(Sensor(r.Sensor),r.Overheat,r.ResumeBelow,Changed));
        InitializeStop();
        selectedProfile=Profiles.FirstOrDefault();
        Profiles.CollectionChanged+=(_,_)=>Changed(); Protection.CollectionChanged+=(_,_)=>Changed();
    }
    private CoolingSensorChoice Sensor(EndpointReference target)=>Draft.Sensors.SingleOrDefault(s=>s.Target==target)??new(target,"未接続 / "+target.EndpointId);
    public CoolingSensorChoice[] Sensors=>Draft.Sensors;
    public DeviceCoolingProfileEditor? SelectedProfile { get=>selectedProfile; set { if(Set(ref selectedProfile,value)) { Notify(nameof(HasProfile)); CommandManager.InvalidateRequerySuggested(); } } }
    public CoolingSensorChoice? SelectedSensor { get=>selectedSensor; set=>Set(ref selectedSensor,value); }
    public CoolingSensorChoice? ProtectionSensor { get=>protectionSensor; set=>Set(ref protectionSensor,value); }
    public bool HasProfile=>SelectedProfile is not null;
    public bool Editable=>!busy;
    public bool CanSave=>!busy&&Draft.Problem is null;
    public string Status=>busy?"処理中":Draft.Problem??"";
    public string MinimumLabel=>$"端子の最低出力 {Draft.Safety.Minimum}%";
    public ICommand AddProfile=>new AsyncCommand(_=>{ var p=new DeviceCoolingProfileEditor(new("curve-"+Guid.NewGuid().ToString("N"),"新しいカーブ",0,[],3,2),Sensor,Changed); Profiles.Add(p); SelectedProfile=p; return Task.CompletedTask; },_=>Editable&&Profiles.Count<32);
    public ICommand DeleteProfile=>new AsyncCommand(_=>{ if(SelectedProfile is { } p) Profiles.Remove(p); SelectedProfile=Profiles.FirstOrDefault(); return Task.CompletedTask; },_=>Editable&&SelectedProfile is not null);
    public ICommand AddCurve=>new AsyncCommand(_=>{ var s=SelectedSensor!; SelectedProfile!.Curves.Add(new(s,new(s.Target,[new(30,100),new(90,100)],95,85),Changed)); return Task.CompletedTask; },
        _=>Editable&&SelectedProfile is { } p&&SelectedSensor is { } s&&p.Curves.Count<32&&p.Curves.All(c=>c.Sensor.Target!=s.Target));
    public ICommand RemoveCurve=>new AsyncCommand(p=>{ SelectedProfile!.Curves.Remove((DeviceCurveSettingsEditor)p!); return Task.CompletedTask; },p=>Editable&&SelectedProfile is not null&&p is DeviceCurveSettingsEditor);
    public ICommand AddProtection=>new AsyncCommand(_=>{ var s=ProtectionSensor!; Protection.Add(new(s,95,85,Changed)); return Task.CompletedTask; },
        _=>Editable&&ProtectionSensor is { } s&&Protection.Count<32&&Protection.All(c=>c.Sensor.Target!=s.Target));
    public ICommand RemoveProtection=>new AsyncCommand(p=>{ Protection.Remove((DeviceTemperatureEditor)p!); return Task.CompletedTask; },p=>Editable&&p is DeviceTemperatureEditor);
    private void Changed()
    { Draft.Change(Draft.Safety with { ManualProtection=Protection.Select(p=>p.Rule).ToArray(),Stop=StopEnabled?new(StopConditions.Select(r=>r.Rule).ToArray(),RestartDuty,RestartSeconds):null },Profiles.Select(p=>p.Value).ToArray()); Publish(); }
    private void Publish()
    { foreach(var n in new[]{nameof(CanSave),nameof(Status),nameof(Editable)}) Notify(n); CommandManager.InvalidateRequerySuggested(); }
    public void Update(DeviceWorkspaceSnapshot snapshot,bool blocked)
    {
        busy=blocked; Draft.Update(snapshot);
        foreach(var r in Profiles.SelectMany(p=>p.Curves).Cast<DeviceTemperatureEditor>().Concat(Protection)) r.Update(snapshot,Draft.Target);
        foreach(var r in StopConditions) r.Update(snapshot);
        Publish();
    }
}

public sealed partial class DeviceCoolingRow
{
    public DeviceCoolingSettingsEditor? SettingsEditor { get; private set; }
    private bool detailsExpanded;
    public bool DetailsExpanded { get=>detailsExpanded; set=>Set(ref detailsExpanded,value); }
    public bool HasSettingsEditor=>SettingsEditor is not null;
    public bool ShowSettingsEditor { get; private set; }
    public void ToggleSettingsEditor() { ShowSettingsEditor=!ShowSettingsEditor; Notify(nameof(ShowSettingsEditor)); }
    public void SetSettingsEditor(DeviceCoolingSettingsEditor? editor) { SettingsEditor=editor; ShowSettingsEditor=editor is not null; Notify(nameof(SettingsEditor)); Notify(nameof(HasSettingsEditor)); Notify(nameof(ShowSettingsEditor)); }
}
public sealed partial class DeviceWorkspaceViewModel
{
    public ICommand EditCoolingSettings=>new AsyncCommand(p=> { OpenCoolingSettings((DeviceCoolingRow)p!); return Task.CompletedTask; },p=>SetupIdle&&SetupSupported&&p is DeviceCoolingRow, e=>Notice=e.Message);
    public ICommand SaveCoolingSettings=>new AsyncCommand(p=>SaveCoolingSettingsAsync((DeviceCoolingSettingsEditor)p!),p=>SetupIdle&&p is DeviceCoolingSettingsEditor { CanSave:true },e=>Notice=e.Message);
    public ICommand ReloadCoolingSettings=>new AsyncCommand(p=>ReloadCoolingSettingsAsync((DeviceCoolingSettingsEditor)p!),_=>SetupIdle,e=>Notice=e.Message);
    public void OpenCoolingSettings(DeviceCoolingRow row)
    { if(!SetupIdle||!SetupSupported) return; row.DetailsExpanded=true; if(row.SettingsEditor is null) row.SetSettingsEditor(new(client.Snapshot!,row.Draft.Target)); else row.ToggleSettingsEditor(); Render(); }
    public async Task ReloadCoolingSettingsAsync(DeviceCoolingSettingsEditor editor)
    {
        if(!SetupIdle) return; setupBusy=true; Render();
        try { await client.RefreshAsync(true); Cooling.Single(r=>r.Draft.Target==editor.Draft.Target).SetSettingsEditor(new(client.Snapshot!,editor.Draft.Target)); }
        finally { setupBusy=false; Render(); }
    }
    public async Task SaveCoolingSettingsAsync(DeviceCoolingSettingsEditor editor)
    {
        Render(); if(!SetupIdle||!editor.CanSave) return;
        var edit=editor.Draft.Capture(); setupBusy=true; ClearConfigurationPreviews(); Render();
        try { var preview=await client.PreviewSetupAsync(edit); var result=await client.ApplySetupAsync(preview); Notice=result.Message;
            if(result.Outcome==Outcome.Confirmed) Cooling.Single(r=>r.Draft.Target==editor.Draft.Target).SetSettingsEditor(null); }
        finally { setupBusy=false; Render(); }
    }
}
