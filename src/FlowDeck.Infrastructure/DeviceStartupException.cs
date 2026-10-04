using System.Runtime.ExceptionServices;

namespace FlowDeck.Infrastructure;

/// <summary>An unsuccessful start still owns its unfinished cleanup. The caller must retain it until closure or process exit.</summary>
public sealed class DeviceStartupException : IOException, IAsyncDisposable
{
    private sealed class IndependentCleanup(DeviceStartupException[] resources) : IAsyncDisposable
    {
        public bool ResourcesClosed => resources.All(r => r.ResourcesClosed);
        public async ValueTask DisposeAsync()
        {
            var errors = new List<Exception>();
            foreach (var resource in resources)
                try { await resource.DisposeAsync().ConfigureAwait(false); } catch (Exception e) { errors.Add(e); }
            if (errors.Count > 0) throw new AggregateException("起動途中の接続の終了が未確認です", errors);
        }
    }
    private readonly IAsyncDisposable owner;
    private readonly Func<bool>? closed;
    private readonly SemaphoreSlim gate = new(1);
    private volatile bool resourcesClosed;
    public bool ResourcesClosed => resourcesClosed || closed?.Invoke() == true;

    public DeviceStartupException(Exception failure, IAsyncDisposable owner, Func<bool>? resourcesClosed = null)
        : base("起動に失敗しました。未終了の接続を保持して後始末を再試行します", failure)
    { this.owner = owner; closed = resourcesClosed; }

    public static DeviceStartupException Combine(Exception failure, params DeviceStartupException[] resources)
    {
        var cleanup = new IndependentCleanup(resources.ToArray());
        return new(failure, cleanup, () => cleanup.ResourcesClosed);
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ResourcesClosed) return;
            try { await owner.DisposeAsync().ConfigureAwait(false); resourcesClosed = closed?.Invoke() ?? true; }
            finally { if (closed?.Invoke() == true) resourcesClosed = true; }
            if (!resourcesClosed) throw new IOException("起動時に取得した接続の終了が未確認です");
        }
        finally { gate.Release(); }
    }

    /// <summary>Preserves the original failure, all close errors and the retryable owner.</summary>
    public static async Task RethrowAfterCleanupAsync(Exception failure, IAsyncDisposable owner, Func<bool>? resourcesClosed = null)
    {
        var pending = owner as DeviceStartupException ?? new DeviceStartupException(failure, owner, resourcesClosed);
        try { await pending.DisposeAsync().ConfigureAwait(false); }
        catch (Exception close)
        {
            throw new DeviceStartupException(new AggregateException("起動と終了の両方に失敗しました", failure, close), pending,
                () => pending.ResourcesClosed);
        }
        ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
