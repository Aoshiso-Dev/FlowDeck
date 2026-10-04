namespace FlowDeck.Infrastructure;

internal sealed partial class OpenRgbDeviceProvider
{
    private volatile bool nativeShutdownStarted, nativeConnectionsClosed;
    internal bool NativeShutdownStarted => nativeShutdownStarted;
    internal bool NativeConnectionsClosed => nativeShutdownStarted && nativeConnectionsClosed;

    internal async Task QuiesceNativeAsync(Action<OpenRgbRecoveryRecord[]> checkNativeRecovery)
    {
        lock (gate) closing = true;
        await io.WaitAsync().ConfigureAwait(false);
        var leases = new List<OpenRgbDeviceDriver.NativeShutdownLease>();
        try
        {
            if (!nativeShutdownStarted)
            {
                var pending = CachedDrivers.Cast<OpenRgbDeviceDriver>().ToArray();
                foreach (var driver in pending) driver.BeginShutdown();
                // Hold every driver's operation lock and every pending journal's read lease until
                // native coverage is checked and SDK transport ownership is irrevocably revoked.
                foreach (var driver in pending) leases.Add(await driver.PrepareNativeShutdownAsync().ConfigureAwait(false));
                checkNativeRecovery(leases.SelectMany(l => l.Records).ToArray());
                foreach (var lease in leases) lease.Commit();
                nativeShutdownStarted = true;
                Volatile.Write(ref permit, null);
            }
            if (nativeConnectionsClosed) return;
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            lock (gate) connection = null;
            nativeConnectionsClosed = true;
        }
        finally
        {
            foreach (var lease in Enumerable.Reverse(leases)) lease.Dispose();
            io.Release();
        }
    }

    internal async Task CompleteNativeShutdownAsync(Action checkNativeComplete)
    {
        await io.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!NativeConnectionsClosed) throw new IOException("OpenRGBのNative引き渡し前にSDK記録を終了できません");
            checkNativeComplete();
            foreach (var driver in CachedDrivers.Cast<OpenRgbDeviceDriver>())
                await driver.CompleteNativeShutdownAsync(checkNativeComplete).ConfigureAwait(false);
            checkNativeComplete();
            lock (gate) disposed = true;
        }
        finally { io.Release(); }
    }
}
