using System.Collections.ObjectModel;
using System.Windows.Input;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed record DeviceDialSensorChoice(EndpointReference? Target, string Name);

public sealed partial class DeviceWorkspaceViewModel
{
    private DeviceDialDisplaySettings dialDisplay = new(), savedDialDisplay = new();
    private string? dialDisplayEpoch, dialDisplaySession, dialDisplayGeneration;
    private long dialDisplayTopology;
    private bool dialDisplayUpdating, dialDisplayConflict;
    public ObservableCollection<DeviceDialSensorChoice> DialCpuSensors { get; } = [];
    public ObservableCollection<DeviceDialSensorChoice> DialGpuSensors { get; } = [];
    public bool DialDisplaySupported => client.SupportsDialDisplay;
    public bool DialDisplayIdle => SetupIdle && DialDisplaySupported;
    public bool DialDisplayDirty => dialDisplay != savedDialDisplay;
    public DeviceDialSensorChoice? DialCpuSensor
    {
        get => DialCpuSensors.FirstOrDefault(c => c.Target == dialDisplay.Cpu);
        set { if (dialDisplayUpdating || !DialDisplayIdle || value is null) return; dialDisplay = dialDisplay with { Cpu = value.Target }; PublishDialDisplay(); }
    }
    public DeviceDialSensorChoice? DialGpuSensor
    {
        get => DialGpuSensors.FirstOrDefault(c => c.Target == dialDisplay.Gpu);
        set { if (dialDisplayUpdating || !DialDisplayIdle || value is null) return; dialDisplay = dialDisplay with { Gpu = value.Target }; PublishDialDisplay(); }
    }
    private bool DialDisplayAvailable => DialCpuSensor is not null && DialGpuSensor is not null;
    public bool CanSaveDialDisplay => DialDisplayIdle && DialDisplayDirty && !dialDisplayConflict && DialDisplayAvailable;
    public string DialDisplayStatus => dialDisplayConflict ? "別の操作で変更あり・再読込してください" :
        !DialDisplayAvailable ? "選択した温度センサーは未接続です" : DialDisplayDirty ? "未保存" : "保存済み";
    public ICommand SaveDialDisplay => new AsyncCommand(_ => SaveDialDisplayAsync(), _ => CanSaveDialDisplay, e => Notice = e.Message);
    public ICommand ReloadDialDisplay => new AsyncCommand(_ => ReloadDialDisplayAsync(), _ => DialDisplayIdle, e => Notice = e.Message);
    private void RenderDialDisplay(DeviceWorkspaceSnapshot snapshot)
    {
        dialDisplayUpdating = true;
        try
        {
            if (dialDisplayGeneration is null || !DialDisplayDirty)
            {
                dialDisplay = savedDialDisplay = snapshot.Configuration.Configuration.Preferences.DialDisplay ?? new();
                dialDisplayEpoch = snapshot.State.Inventory.Epoch; dialDisplaySession = snapshot.Session;
                dialDisplayGeneration = snapshot.Configuration.Generation; dialDisplayTopology = snapshot.State.Inventory.TopologyRevision;
                dialDisplayConflict = false;
            }
            else dialDisplayConflict = dialDisplayEpoch != snapshot.State.Inventory.Epoch || dialDisplaySession != snapshot.Session ||
                dialDisplayGeneration != snapshot.Configuration.Generation || dialDisplayTopology != snapshot.State.Inventory.TopologyRevision;
            DeviceDialSensorChoice[] Choices(DeviceKind kind) => new[] { new DeviceDialSensorChoice(null, "表示しない") }.Concat(
                snapshot.Configuration.Configuration.Devices.Devices.Where(d => d.Kind == kind).SelectMany(saved =>
                {
                    var live = snapshot.Connected ? snapshot.State.Inventory.Devices.SingleOrDefault(d =>
                        d.Connected && d.Device.Id == saved.Id && d.Device.Provider == saved.Provider) : null;
                    return saved.Endpoints.Where(e => e.Kind == EndpointKind.Sensor && live?.Device.Endpoints.Any(observed =>
                        observed.Id == e.Id && observed.Kind == EndpointKind.Sensor &&
                        observed.Capabilities.Any(c => c.Feature == DeviceFeature.Temperature && c.Usable)) == true)
                        .Select(e => new DeviceDialSensorChoice(new(saved.Id, e.Id), saved.Name + " / " + e.Name));
                })).ToArray();
            Reconcile(DialCpuSensors, Choices(DeviceKind.Processor)); Reconcile(DialGpuSensors, Choices(DeviceKind.Graphics));
            PublishDialDisplay();
        }
        finally { dialDisplayUpdating = false; }
    }
    private void PublishDialDisplay()
    {
        foreach (var name in new[] { nameof(DialDisplaySupported), nameof(DialDisplayIdle), nameof(DialDisplayDirty),
            nameof(DialCpuSensor), nameof(DialGpuSensor), nameof(CanSaveDialDisplay), nameof(DialDisplayStatus) }) Notify(name);
        CommandManager.InvalidateRequerySuggested();
    }
    public async Task SaveDialDisplayAsync()
    {
        Render(); if (!CanSaveDialDisplay) return;
        var edit = new DeviceDialDisplayEdit(dialDisplayEpoch!, dialDisplaySession!, dialDisplayGeneration!, dialDisplayTopology, dialDisplay);
        setupBusy = true; ClearConfigurationPreviews(); Render();
        using var status = BeginStatus("ダイヤルの温度表示を保存中");
        try
        {
            var result = await client.SaveDialDisplayAsync(edit); ReportResult(result.Message, result.Outcome);
            if (result.Outcome == Outcome.Confirmed) dialDisplayGeneration = null;
        }
        finally { setupBusy = false; Render(); }
    }
    public async Task ReloadDialDisplayAsync()
    {
        if (!DialDisplayIdle) return; setupBusy = true; Render();
        try { await client.RefreshAsync(true); dialDisplayGeneration = null; }
        finally { setupBusy = false; Render(); }
    }
}
