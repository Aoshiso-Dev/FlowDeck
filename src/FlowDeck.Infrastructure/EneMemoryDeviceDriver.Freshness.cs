using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed partial class EneMemoryDeviceDriver
{
    private sealed class FreshnessRetry
    {
        private bool used;
        public bool Take() { if (used) return false; used = true; return true; }
    }

    // The caller holds logical I/O ownership, never an SMBus reservation here.
    // One budget covers every phase of the command. Only evidence-age failures
    // before the first data write can repeat; transport or identity errors cannot.
    private async Task<T> ExecuteWithFreshnessRecoveryAsync<T>(EneMemoryConnection connection, bool full,
        Func<IEneMemorySession, T> operation, FreshnessRetry retry, Func<bool> unwritten, Action guard, CancellationToken ct)
    {
        try { return await ExecuteHardwareAsync(connection, full, operation, ct).ConfigureAwait(false); }
        catch (EneMemoryFreshnessException) when (unwritten() && retry.Take())
        {
            guard();
            return await backend.PrepareFreshOperationAsync(async token =>
            {
                guard();
                if (!full)
                {
                    // Refresh only this expected connection, with no data writes
                    // or cached-state publication. Release the bus before retrying
                    // the original short session, whose per-data TTL still applies.
                    await ExecuteHardwareAsync(connection, true, _ => { guard(); return true; }, token).ConfigureAwait(false);
                }
                guard();
                return await ExecuteHardwareAsync(connection, full, operation, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
    }
}
