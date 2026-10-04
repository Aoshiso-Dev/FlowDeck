namespace FlowDeck.Infrastructure;

/// <summary>Retains the service's exclusion lease while an unsuccessful factory still has live resources.</summary>
public sealed class DeviceStartupOwnership(DeviceStartupException? pending, IAsyncDisposable lease) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private volatile bool closed;
    public bool ResourcesClosed => closed;
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (closed) return;
            var errors = new List<Exception>();
            if (pending is not null)
                try { await pending.DisposeAsync().ConfigureAwait(false); } catch (Exception e) { errors.Add(e); }
            if (pending is null || pending.ResourcesClosed)
                try { await lease.DisposeAsync().ConfigureAwait(false); closed = true; } catch (Exception e) { errors.Add(e); }
            if (errors.Count > 0) throw new AggregateException("起動失敗後の機器所有権を解放できません", errors);
            if (!closed) throw new IOException("起動失敗後の機器接続が残っています");
        }
        finally { gate.Release(); }
    }
}
