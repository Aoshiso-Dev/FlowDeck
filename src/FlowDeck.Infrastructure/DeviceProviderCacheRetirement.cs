using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal static class DeviceCacheRetirement
{
    internal static async Task CloseAsync(IDeviceControlDriver driver, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (driver.Observe() is not null) throw new IOException("接続中の機器の履歴は整理できません");
        await driver.DisposeAsync().ConfigureAwait(false);
        // Once closed, the caller removes this exact object even if cancellation just arrived.
    }
}

public sealed partial class HubDeviceProvider
{
    private HashSet<IDeviceControlDriver> confirmedAbsent = [];
    public bool IsConfirmedAbsent(IDeviceControlDriver driver) { lock (gate) return confirmedAbsent.Contains(driver); }
    public async Task RetireDisconnectedAsync(IDeviceControlDriver driver, CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            KeyValuePair<string, HubDeviceDriver> entry;
            lock (gate) entry = drivers.SingleOrDefault(p => ReferenceEquals(p.Value, driver));
            if (entry.Value is null) throw new IOException("Hubのキャッシュが変更されています");
            if (!IsConfirmedAbsent(driver)) throw new IOException("機器が検出結果から外れたことを確認できません");
            await DeviceCacheRetirement.CloseAsync(driver, ct).ConfigureAwait(false);
            lock (gate)
            {
                drivers.Remove(entry.Key); confirmedAbsent.Remove(driver); rememberedIds = rememberedIds.Where(id => id != entry.Key).ToArray();
                if (driverAdapters.Remove(entry.Value, out var adapter)) adapters.Remove(adapter);
                closedDrivers.Remove(driver);
            }
        }
        finally { lifecycle.Release(); }
    }
}

public sealed partial class AsrockDeviceProvider
{
    private HashSet<IDeviceControlDriver> confirmedAbsent = [];
    public bool IsConfirmedAbsent(IDeviceControlDriver driver) { lock (gate) return confirmedAbsent.Contains(driver); }
    public async Task RetireDisconnectedAsync(IDeviceControlDriver driver, CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(closing, this);
            lock (gate) if (!ReferenceEquals(drivers.GetValueOrDefault(driver.DeviceId), driver)) throw new IOException("照明のキャッシュが変更されています");
            // Successful discovery already closes sessions belonging to absent paths. Keep the shared backend.
            if (!IsConfirmedAbsent(driver)) throw new IOException("機器が検出結果から外れたことを確認できません");
            await DeviceCacheRetirement.CloseAsync(driver, ct).ConfigureAwait(false);
            lock (gate) { drivers.Remove(driver.DeviceId); confirmedAbsent.Remove(driver); }
        }
        finally { lifecycle.Release(); }
    }
}

public sealed partial class HardwareSensorProvider
{
    private HashSet<IDeviceControlDriver> confirmedAbsent = [];
    public bool IsConfirmedAbsent(IDeviceControlDriver driver) { lock (gate) return confirmedAbsent.Contains(driver); }
    public async Task RetireDisconnectedAsync(IDeviceControlDriver driver, CancellationToken ct)
    {
        await backendAccess.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!ReferenceEquals(drivers.GetValueOrDefault(driver.DeviceId), driver) || current.ContainsKey(driver.DeviceId))
                    throw new IOException("センサーの接続またはキャッシュが変更されています");
            }
            if (!IsConfirmedAbsent(driver)) throw new IOException("機器が検出結果から外れたことを確認できません");
            await DeviceCacheRetirement.CloseAsync(driver, ct).ConfigureAwait(false);
            lock (gate) { drivers.Remove(driver.DeviceId); confirmedAbsent.Remove(driver); }
        }
        finally { backendAccess.Release(); }
    }
}

public sealed partial class EneMemoryDeviceProvider
{
    private HashSet<IDeviceControlDriver> confirmedAbsent = [];
    public bool IsConfirmedAbsent(IDeviceControlDriver driver) { lock (gate) return confirmedAbsent.Contains(driver); }
    public async Task RetireDisconnectedAsync(IDeviceControlDriver driver, CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(closing, this);
            lock (gate) if (!ReferenceEquals(drivers.GetValueOrDefault(driver.DeviceId), driver)) throw new IOException("メモリ照明のキャッシュが変更されています");
            if (!IsConfirmedAbsent(driver)) throw new IOException("機器が検出結果から外れたことを確認できません");
            await DeviceCacheRetirement.CloseAsync(driver, ct).ConfigureAwait(false);
            lock (gate) { drivers.Remove(driver.DeviceId); confirmedAbsent.Remove(driver); }
        }
        finally { lifecycle.Release(); }
    }
}

internal sealed partial class OpenRgbDeviceProvider
{
    private HashSet<IDeviceControlDriver> confirmedAbsent = [];
    public bool IsConfirmedAbsent(IDeviceControlDriver driver) { lock (gate) return confirmedAbsent.Contains(driver); }
    public async Task RetireDisconnectedAsync(IDeviceControlDriver driver, CancellationToken ct)
    {
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closing || disposed, this);
                if (!ReferenceEquals(drivers.GetValueOrDefault(driver.DeviceId), driver)) throw new IOException("OpenRGBのキャッシュが変更されています");
            }
            if (!IsConfirmedAbsent(driver)) throw new IOException("機器が検出結果から外れたことを確認できません");
            await DeviceCacheRetirement.CloseAsync(driver, ct).ConfigureAwait(false);
            lock (gate) { drivers.Remove(driver.DeviceId); confirmedAbsent.Remove(driver); }
        }
        finally { io.Release(); }
    }
}

internal sealed partial class OpenRgbManagedDeviceProvider
{
    public bool IsConfirmedAbsent(IDeviceControlDriver driver) => sdk.IsConfirmedAbsent(driver);
    public async Task RetireDisconnectedAsync(IDeviceControlDriver driver, CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(closing || closed || startFailed, this);
            await sdk.RetireDisconnectedAsync(driver, ct).ConfigureAwait(false);
        }
        finally { lifecycle.Release(); }
    }
}
