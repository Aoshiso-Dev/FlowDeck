using System.Collections.ObjectModel;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed class DeviceCatalogCapabilityRow(CatalogCapability value) : Observable
{
    public CatalogCapability Value { get; private set; } = value;
    public void Update(CatalogCapability next) { if (Value == next) return; Value = next; Notify(nameof(Value)); }
}
public sealed class DeviceCatalogEndpointRow(string id) : Observable
{
    public string Id { get; } = id;
    public string Name { get; private set; } = "";
    public string Kind { get; private set; } = "";
    public bool Empty => Capabilities.Count == 0;
    public ObservableCollection<DeviceCatalogCapabilityRow> Capabilities { get; } = [];
    public void Update(CatalogEndpoint endpoint)
    {
        if (Name != endpoint.Name) { Name = endpoint.Name; Notify(nameof(Name)); }
        if (Kind != endpoint.Kind) { Kind = endpoint.Kind; Notify(nameof(Kind)); }
        var rows = endpoint.Capabilities.Select(c => {
            var row = Capabilities.SingleOrDefault(r => r.Value.Feature == c.Feature) ?? new(c);
            row.Update(c); return row;
        }).ToArray();
        DeviceWorkspaceViewModel.Reconcile(Capabilities, rows); Notify(nameof(Empty));
    }
}
public sealed class DeviceCatalogRow(string id) : Observable
{
    private bool expanded;
    public string Id { get; } = id;
    public string Name { get; private set; } = "";
    public string Status { get; private set; } = "";
    public string Details { get; private set; } = "";
    public string IdentityDetails { get; private set; } = "";
    public ObservableCollection<DeviceCatalogEndpointRow> Endpoints { get; } = [];
    public IEnumerable<DeviceCatalogEndpointRow> VisibleEndpoints => Expanded ? Endpoints : Array.Empty<DeviceCatalogEndpointRow>();
    public bool Expanded { get => expanded; set { if (Set(ref expanded, value)) Notify(nameof(VisibleEndpoints)); } }
    public void Update(DeviceInstance instance) => Update(DeviceCatalogPresentation.Describe(instance));
    public void Update(CatalogDevice device)
    {
        if (device.Id != Id) throw new ArgumentException("機器の識別が一致しません");
        var rows = device.Endpoints.Select(e => {
            var row = Endpoints.SingleOrDefault(r => r.Id == e.Id) ?? new(e.Id);
            row.Update(e); return row;
        }).ToArray();
        DeviceWorkspaceViewModel.Reconcile(Endpoints, rows);
        var details = string.Join("\n", device.Endpoints.SelectMany(e => e.Capabilities.Select(c =>
            e.Name + " / " + c.Name + "：" + c.Availability + " · " + c.Support + " · " + c.Evidence + (c.HasReason ? "\n" + c.Reason : ""))));
        if (Name == device.Name && Status == device.Status && Details == details && IdentityDetails == device.IdentityDetails) return;
        Name = device.Name; Status = device.Status; Details = details; IdentityDetails = device.IdentityDetails; Refresh();
    }
}

public sealed partial class FlowDeckViewModel
{
    public ObservableCollection<DeviceCatalogRow> DeviceCatalog { get; } = [];
    public string DeviceCatalogStatus { get; private set; } = "";
    private void RefreshDeviceCatalog()
    {
        var inventory = host.ObserveDevices();
        var ids = inventory.Devices.Select(d => d.Device.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var row in DeviceCatalog.Where(r => !ids.Contains(r.Id)).ToArray()) DeviceCatalog.Remove(row);
        foreach (var instance in inventory.Devices)
        {
            var row = DeviceCatalog.FirstOrDefault(r => r.Id == instance.Device.Id);
            if (row is null) { row = new(instance.Device.Id); DeviceCatalog.Add(row); }
            row.Update(instance);
        }
        DeviceCatalogStatus = host.DeviceInventoryError ?? $"接続中 {inventory.Devices.Count(d => d.Connected)} / 登録 {inventory.Devices.Length}";
        Notify(nameof(DeviceCatalogStatus));
    }
}
