using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class RuntimeHost
{
    public string SettingsRestoreMessage { get; private set; } = "";
    public bool SettingsRestorePending => new SettingsTransferStore(Store.DirectoryPath).HasPending;
    private TransferDevices TransferDevices() => new(Motherboard.Board.Name, Coordinator.Snapshot.Sensors.Select(s => s.Id)
        .Concat(Motherboard.Board.Sensors.Select(s => s.Id)).Distinct().ToArray(),
        Rgb.Devices.Where(d => !string.IsNullOrWhiteSpace(d.ControlIdentity)).SelectMany(d => d.Zones.Where(z => z.HardwareZone is not null)
            .Select(z => new BoardLightAddress(d.ControlIdentity!, z.HardwareZone!.Value))).Distinct().ToArray());
    private async Task<T> WithSettingsSnapshot<T>(Func<SettingsTransferStore, T> action)
    {
        await lightingSceneGate.WaitAsync();
        try
        {
            await notificationStoreGate.WaitAsync();
            try { await FlushSettingsAsync(); return action(new SettingsTransferStore(Store.DirectoryPath)); }
            finally { notificationStoreGate.Release(); }
        }
        finally { lightingSceneGate.Release(); }
    }
    public Task<SettingsArchive> ExportSettingsAsync(string destination) => WithSettingsSnapshot(store => store.Export(destination, TransferDevices()));
    public Task<SettingsRestorePreview> PreviewSettingsAsync(string source) => WithSettingsSnapshot(store => store.Preview(source, TransferDevices()));
    public Task ScheduleSettingsAsync(SettingsRestorePreview preview) => WithSettingsSnapshot(store => { store.Schedule(preview); return true; });
    public Task CancelSettingsRestoreAsync() => WithSettingsSnapshot(store => { store.Cancel(); return true; });
}
