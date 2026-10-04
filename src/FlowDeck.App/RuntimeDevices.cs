using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class RuntimeHost
{
    // Observation only until the generic service command path owns the registry.
    private DeviceRegistry deviceRegistry = new();
    private string? deviceInstallation;
    public string? DeviceInventoryError { get; private set; }
    private void InitializeDeviceCatalog(string directory)
    {
        deviceRegistry = new(); deviceInstallation = null; DeviceInventoryError = null;
        try { deviceInstallation = DeviceInstallationIdentity.LoadOrCreate(directory); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { DeviceInventoryError = e.Message; Log.Write("deviceCatalog", new { error = e.Message }); }
    }
    public DeviceInventory ObserveDevices()
    {
        if (deviceInstallation is null) return deviceRegistry.Snapshot;
        try
        {
            var observations = LegacyDeviceCatalog.Build(deviceInstallation, Coordinator.Settings, Coordinator.Snapshot,
                Motherboard.Board, Rgb, Demo || (Coordinator is RemoteControlCoordinator remote ? remote.Connected : HardwareTemperatures.IsAdministrator));
            var inventory = deviceRegistry.Reconcile(observations);
            DeviceInventoryError = null;
            return inventory;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            if (DeviceInventoryError != e.Message) Log.Write("deviceCatalog", new { error = e.Message });
            DeviceInventoryError = e.Message;
            return deviceRegistry.Reconcile([]);
        }
    }
}
