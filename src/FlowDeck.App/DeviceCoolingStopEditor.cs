using System.Collections.ObjectModel;
using System.Windows.Input;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed class DeviceStopTemperatureEditor(CoolingSensorChoice sensor,double below,Action changed) : Observable
{
    private double below=below;
    public CoolingSensorChoice Sensor { get; }=sensor;
    public double Below { get=>below; set { if(Set(ref below,value)) changed(); } }
    public double? Temperature { get; private set; }
    public StopTemperatureRule Rule=>new(Sensor.Target,Below);
    public void Update(DeviceWorkspaceSnapshot snapshot) { Temperature=DeviceCoolingSettingsDraft.Temperature(snapshot,Sensor.Target); Notify(nameof(Temperature)); }
}
public sealed partial class DeviceCoolingSettingsEditor
{
    public ObservableCollection<DeviceStopTemperatureEditor> StopConditions { get; }=[];
    private bool stopEnabled;
    private int restartDuty;
    private double restartSeconds;
    private CoolingSensorChoice? stopSensor;
    private void InitializeStop()
    {
        var saved=Draft.Safety.Stop; stopEnabled=saved is not null; restartDuty=saved?.RestartDuty??100; restartSeconds=saved?.RestartSeconds??1;
        foreach(var rule in saved?.Temperatures??[]) StopConditions.Add(new(Sensor(rule.Sensor),rule.Below,Changed));
        StopConditions.CollectionChanged+=(_,_)=>Changed();
    }
    public bool StopSectionVisible=>Draft.StopSupported||StopEnabled;
    public string StopLabel=>Draft.Pump?"ポンプの停止を許可":"ファンの停止を許可";
    public bool StopEnabled { get=>stopEnabled; set { if(value&&!Draft.StopSupported) return; if(Set(ref stopEnabled,value)) { Changed(); Notify(nameof(StopSectionVisible)); } } }
    public CoolingSensorChoice? StopSensor { get=>stopSensor; set=>Set(ref stopSensor,value); }
    public int RestartDuty { get=>restartDuty; set { if(Set(ref restartDuty,value)) Changed(); } }
    public int RestartDutyMinimum=>Draft.Safety.Minimum??1;
    public double RestartSeconds { get=>restartSeconds; set { if(Set(ref restartSeconds,value)) Changed(); } }
    public double RestartSecondsMinimum=>Math.Min(0.1,Draft.Safety.Stop?.RestartSeconds??0.1);
    public double RestartSecondsMaximum=>Math.Max(30,Draft.Safety.Stop?.RestartSeconds??30);
    public ICommand AddStopCondition=>new AsyncCommand(_=>{ var sensor=StopSensor!; StopConditions.Add(new(sensor,40,Changed)); return Task.CompletedTask; },
        _=>Editable&&StopEnabled&&StopSensor is { } sensor&&StopConditions.Count<32&&StopConditions.All(r=>r.Sensor.Target!=sensor.Target));
    public ICommand RemoveStopCondition=>new AsyncCommand(p=>{ StopConditions.Remove((DeviceStopTemperatureEditor)p!); return Task.CompletedTask; },p=>Editable&&p is DeviceStopTemperatureEditor);
}
