namespace FlowDeck.Infrastructure;

internal sealed partial class OpenRgbDeviceDriver
{
    private bool nativeShutdown;

    internal sealed class NativeShutdownLease(OpenRgbDeviceDriver owner, OpenRgbRecoveryRecord[] records,
        List<OpenRgbLightingRecovery.ReadLease> journals) : IDisposable
    {
        private bool released;
        internal OpenRgbRecoveryRecord[] Records => records.Select(FlowDeck.Core.Contract.Copy).ToArray();
        internal void Commit()
        {
            ObjectDisposedException.ThrowIf(released, this);
            foreach (var journal in journals) journal.Check();
            lock (owner.gate) owner.nativeShutdown = true;
        }
        public void Dispose()
        {
            if (released) return;
            released = true;
            foreach (var journal in journals) journal.Dispose();
            owner.io.Release();
        }
    }

    internal async Task<NativeShutdownLease> PrepareNativeShutdownAsync()
    {
        BeginShutdown();
        await io.WaitAsync().ConfigureAwait(false);
        var leases = new List<OpenRgbLightingRecovery.ReadLease>();
        try
        {
            if (journalError is not null) throw new IOException(journalError);
            var records = new List<OpenRgbRecoveryRecord>();
            foreach (var slot in slots.Values)
            {
                if (slot.Journal.Pending is { } record)
                {
                    leases.Add(slot.Journal.AcquireReadLease(record));
                    records.Add(record);
                }
                else slot.Journal.CheckAbsent();
            }
            return new(this, records.ToArray(), leases);
        }
        catch
        {
            foreach (var lease in leases) lease.Dispose();
            io.Release(); throw;
        }
    }

    internal async Task CompleteNativeShutdownAsync(Action checkNativeComplete)
    {
        await io.WaitAsync().ConfigureAwait(false);
        try
        {
            checkNativeComplete();
            lock (gate) if (disposed) return;
            if (!nativeShutdown || journalError is not null) throw new IOException("OpenRGBのNative引き渡しが未確認です");
            // Proof belongs to the owning native scope. File absence alone never authorizes this.
            foreach (var slot in slots.Values) slot.Journal.ForgetVerifiedNative(checkNativeComplete);
            checkNativeComplete();
            lock (gate)
            {
                foreach (var slot in slots.Values) { slot.Owned = slot.Faulted = false; slot.Executor = null; }
                streams.Clear(); shared = null; current = null; disposed = true;
            }
        }
        finally { io.Release(); }
    }
}
