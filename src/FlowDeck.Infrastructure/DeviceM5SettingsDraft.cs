using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceM5HubChoice(PhysicalDeviceBinding? Binding, string Name);
public sealed record DeviceM5SensorChoice(EndpointReference? Target, string Name);
public sealed record DeviceM5PortChoice(string Id, string Name, bool Available);
public sealed class DeviceM5SettingsDraft
{
    private DeviceWorkspaceSnapshot? baseline, latest;
    private string original = "";
    public DeviceM5Settings Settings { get; private set; } = new(new(), new());
    public bool Dirty { get; private set; }
    public bool Conflict { get; private set; }
    public string? Problem => latest is not { Connected: true, ExitAttempted: false } s || !double.IsFinite(s.ElapsedSeconds) || s.ElapsedSeconds is < 0 or >= 3 ||
        s.State.Releasing || s.State.ConfigurationChanging ? "サービスの状態を更新してください" : Conflict ? "別の操作で変更あり。再取得してください" :
        DeviceM5Configuration.LiveProblem(Settings, s.Configuration.Configuration, s.State, s.Configuration.Candidates);
    public bool CanSave => Dirty && Problem is null;
    public void Update(DeviceWorkspaceSnapshot snapshot)
    {
        latest = snapshot;
        if (baseline is null || !Dirty) Reset(snapshot);
        else if (baseline.State.Inventory.Epoch != snapshot.State.Inventory.Epoch || baseline.Session != snapshot.Session ||
            baseline.Configuration.Generation != snapshot.Configuration.Generation || baseline.State.Inventory.TopologyRevision != snapshot.State.Inventory.TopologyRevision ||
            !baseline.Configuration.Candidates.OrderBy(b=>b.DeviceId).SequenceEqual(snapshot.Configuration.Candidates.OrderBy(b=>b.DeviceId))) Conflict = true;
    }
    public void Reset(DeviceWorkspaceSnapshot snapshot)
    {
        latest = baseline = snapshot; Settings = Contract.Copy(DeviceM5Configuration.Read(snapshot.Configuration.Configuration.Preferences));
        original = SettingsTransfer.Hash(Settings); Dirty = Conflict = false;
    }
    public void Change(DeviceM5Settings next) { Settings = Contract.Copy(next); Dirty = SettingsTransfer.Hash(Settings) != original; }
    public void SelectHub(PhysicalDeviceBinding? binding)
    {
        if (Settings.Selection.Hub == binding) return;
        Change(Settings with { Selection = Settings.Selection.WithoutControl() with { Hub = binding } });
    }
    public DeviceM5SettingsEdit Capture()
    {
        if (Problem is { } error) throw new InvalidOperationException(error);
        if (baseline?.Session is not { } session) throw new InvalidOperationException("接続を更新してください");
        return new(baseline.State.Inventory.Epoch, session, baseline.Configuration.Generation, baseline.State.Inventory.TopologyRevision, Contract.Copy(Settings));
    }
    public DeviceM5HubChoice[] Hubs()
    {
        var result = new List<DeviceM5HubChoice> { new(null, "未選択") };
        if (latest is { } s)
            foreach (var registered in s.Configuration.Configuration.Devices.Devices.Where(d => d.Provider == DeviceM5Selection.HubProvider && d.Kind == DeviceKind.Controller))
            {
                // A display-only selection needs the observed identity, not permission to control it.
                var binding = s.Configuration.Candidates.SingleOrDefault(b=>b.DeviceId==registered.Id);
                var instance = s.State.Inventory.Devices.SingleOrDefault(d=>d.Device.Id==registered.Id && d.Connected);
                if (binding?.DefinitionId == DeviceM5Selection.HubDefinition && instance?.Device is
                    { Provider: DeviceM5Selection.HubProvider, DefinitionId: DeviceM5Selection.HubDefinition })
                    result.Add(new(binding, DeviceMonitoringDraft.DisplayDeviceName(instance.Device with { Name=registered.Name }, s.State.Inventory)));
            }
        if (Settings.Selection.Hub is { } saved && result.All(c=>c.Binding!=saved)) result.Add(new(saved, "保存済みHub · 再確認が必要"));
        return result.ToArray();
    }
    public DeviceM5SensorChoice[] Sensors(DeviceKind kind)
    {
        var result = new List<DeviceM5SensorChoice> { new(null, "未選択") };
        if (latest is { } s)
            foreach (var device in s.Configuration.Configuration.Devices.Devices.Where(d=>d.Kind==kind))
            foreach (var endpoint in device.Endpoints.Where(e=>e.Kind==EndpointKind.Sensor))
            {
                var target = new EndpointReference(device.Id,endpoint.Id);
                var instance = s.State.Inventory.Devices.SingleOrDefault(d=>d.Device.Id==device.Id&&d.Connected);
                if (instance?.Device.Endpoints.SingleOrDefault(e=>e.Id==endpoint.Id)?.Capabilities.Any(c=>c.Feature==DeviceFeature.Temperature&&c.Support==ImplementationSupport.Supported)==true)
                    result.Add(new(target, endpoint.Name+" · "+device.Name));
            }
        var saved = kind==DeviceKind.Processor?Settings.Selection.Cpu:Settings.Selection.Gpu;
        if (saved is not null && result.All(c=>c.Target!=saved)) result.Add(new(saved,"保存済み温度 · 未検出"));
        return result.ToArray();
    }
    public DeviceM5PortChoice[] Ports(bool lighting)
    {
        if (latest is not { } s || Settings.Selection.Hub is not { } hub) return [];
        var device = s.Configuration.Configuration.Devices.Devices.SingleOrDefault(d=>d.Id==hub.DeviceId);
        var current = s.State.Inventory.Devices.SingleOrDefault(d=>d.Device.Id==hub.DeviceId&&d.Connected);
        var feature = lighting?DeviceFeature.FixedColor:DeviceFeature.FixedDuty; var kind = lighting?EndpointKind.Lighting:EndpointKind.Cooling;
        return (device?.Endpoints??[]).Where(e=>e.Kind==kind && (lighting?DeviceM5Selection.Led(e.Id):DeviceM5Selection.Fan(e.Id)))
            .Select(e=>new DeviceM5PortChoice(e.Id,e.Name,current?.Device.Endpoints.SingleOrDefault(a=>a.Id==e.Id)?.Capabilities.Any(c=>c.Feature==feature&&c.Usable)==true)).ToArray();
    }
    public CoolingTemperatureProfile[] Profiles()
    {
        if (latest is not { } s || Settings.Selection.Hub is not { } hub || Settings.Selection.Fans.Length==0) return [];
        var groups = Settings.Selection.Fans.Select(id=>s.Configuration.Configuration.Cooling.Endpoints.SingleOrDefault(e=>e.Target==new EndpointReference(hub.DeviceId,id))?.Profiles??[]).ToArray();
        return groups[0].Where(p=>groups.All(g=>g.Any(other=>SettingsTransfer.Hash(p)==SettingsTransfer.Hash(other)))).ToArray();
    }
    public DeviceDashboardItem[] MotherboardItems()
    {
        if(latest is not { } s) return Settings.Selection.Motherboard;
        var items = new Dictionary<string,DeviceDashboardItem>();
        foreach(var device in s.Configuration.Configuration.Devices.Devices.Where(d=>d.Kind==DeviceKind.Motherboard))
        foreach(var endpoint in device.Endpoints)
        {
            var current=s.State.Inventory.Devices.SingleOrDefault(d=>d.Device.Id==device.Id&&d.Connected)?.Device.Endpoints.SingleOrDefault(e=>e.Id==endpoint.Id);
            foreach(var cap in current?.Capabilities.Where(c=>c.Feature is DeviceFeature.Temperature or DeviceFeature.Rpm && c.Support==ImplementationSupport.Supported)??[])
            {
                var item = new DeviceDashboardItem(new(device.Id,endpoint.Id),cap.Feature,endpoint.Name,
                    cap.Feature==DeviceFeature.Temperature?"temperature":endpoint.Role=="pump"?"pump":"fan");
                items[DeviceDashboard.Key(item.Target,item.Measurement)]=item;
            }
        }
        foreach(var saved in Settings.Selection.Motherboard) items[DeviceDashboard.Key(saved.Target,saved.Measurement)]=saved;
        return items.Values.ToArray();
    }
}
