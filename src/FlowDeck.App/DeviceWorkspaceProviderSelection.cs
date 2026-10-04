using System.Collections.ObjectModel;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed class DeviceProviderSelectionRow(string id, Action<DeviceProviderSelectionRow, bool> select) : Observable
{
    private bool selected;
    public string Id { get; } = id;
    public string Name { get; private set; } = "";
    public bool Available { get; private set; }
    public bool CanSelect => selected || Available;
    public string Status => Available ? "接続中" : "未接続";
    public bool Selected { get => selected; set { if (selected != value) select(this, value); } }
    public void Update(string name, bool available, bool value) { Name = name; Available = available; selected = value; Refresh(); }
}

public sealed partial class DeviceWorkspaceViewModel
{
    private readonly HashSet<string> providerSelectedDevices = new(StringComparer.Ordinal);
    public ObservableCollection<DeviceProviderSelectionRow> ProviderDeviceChoices { get; } = [];
    public bool ProviderDeviceSelectionAvailable => client.SupportsProviderDeviceSelection && useOpenRgb;
    public bool ProviderDeviceSelectionIdle => ProviderRouteIdle && ProviderDeviceSelectionAvailable && ReadOpenRgbPort(out var port) &&
        client.Snapshot?.Configuration.Configuration.Preferences.Providers is { OpenRgbEnabled: true } current && current.OpenRgbPort == port;
    public string ProviderDeviceSelectionStatus => ProviderDeviceChoices.Count == 0 ? "OpenRGBへの切替後に機器を選択" : $"制御対象：{providerSelectedDevices.Count}台";
    private DeviceProviderPreferences DraftProviderSettings(int port) => new DeviceProviderPreferences(useOpenRgb, port,
        useOpenRgb ? providerSelectedDevices.ToArray() : []).Normalize();
    private void RenderProviderSelections(DeviceWorkspaceSnapshot snapshot)
    {
        var actual = snapshot.State.Inventory.Devices.Where(d => d.Device.Provider == "openrgb-sdk" &&
            DeviceProviderPreferences.IsOpenRgbDeviceId(d.Device.Id) && d.Device.Endpoints.Any(e => e.Kind == EndpointKind.Lighting)).ToDictionary(d => d.Device.Id);
        var names = snapshot.Configuration.Configuration.Devices.Devices.ToDictionary(d => d.Id, d => d.Name);
        var ids = actual.Keys.Union(providerSelectedDevices).Union(snapshot.Configuration.Configuration.Preferences.Providers?.OpenRgbDeviceIds ?? [])
            .Order(StringComparer.Ordinal).ToArray();
        var rows = ids.Select((id, index) =>
        {
            var row = ProviderDeviceChoices.SingleOrDefault(r => r.Id == id) ?? new(id, SelectProviderDevice);
            var device = actual.GetValueOrDefault(id);
            row.Update(names.GetValueOrDefault(id) ?? device?.Device.Name ?? $"未接続の照明 {index + 1}",
                snapshot.Connected && device?.Connected == true, providerSelectedDevices.Contains(id));
            return row;
        }).ToArray();
        Reconcile(ProviderDeviceChoices, rows);
        Notify(nameof(ProviderDeviceSelectionAvailable)); Notify(nameof(ProviderDeviceSelectionIdle)); Notify(nameof(ProviderDeviceSelectionStatus));
    }
    private void SelectProviderDevice(DeviceProviderSelectionRow row, bool selected)
    {
        if (!ProviderDeviceSelectionIdle || !ProviderDeviceChoices.Contains(row) || selected && !row.Available) return;
        if (selected && providerSelectedDevices.Count >= DeviceRegistry.MaximumDevices) { Notice = "選択機器数の上限です"; return; }
        if (selected) providerSelectedDevices.Add(row.Id); else providerSelectedDevices.Remove(row.Id);
        providerRoutePreview = null; PublishProviderRoutes();
    }
}
