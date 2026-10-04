using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>SCM lifetime survives power transitions; each resumed runtime gets a new epoch and transports.</summary>
public sealed class DeviceServiceLifecycle : IAsyncDisposable
{
    private readonly Func<Task<DeviceServiceHost>> start;
    private readonly IEventLog log;
    private readonly SemaphoreSlim transition = new(1);
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DeviceServiceHost? current;
    private DeviceServiceHost? pendingClose;
    private DeviceStartupException? pendingStartup;
    private Exception? releaseFailure;
    private bool disposed, stopping;
    private volatile bool resourcesClosed = true;
    public bool ResourcesClosed => resourcesClosed;
    public Task Completion => completion.Task;
    public DeviceControlRuntime? Runtime => Volatile.Read(ref current)?.Runtime;
    private DeviceServiceLifecycle(Func<Task<DeviceServiceHost>> start, IEventLog log) { this.start = start; this.log = log; }
    public static async Task<DeviceServiceLifecycle> StartAsync(Func<Task<DeviceServiceHost>> start, IEventLog log)
    {
        var owner = new DeviceServiceLifecycle(start, log);
        try { await owner.PowerAsync(true).ConfigureAwait(false); return owner; }
        catch (Exception e)
        {
            await DeviceStartupException.RethrowAfterCleanupAsync(e, owner, () => owner.ResourcesClosed).ConfigureAwait(false);
            throw;
        }
    }
    public async Task PowerAsync(bool resume)
    {
        await transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed || stopping) return;
            if (resume)
            {
                if (current is not null) return;
                await ClosePendingAsync().ConfigureAwait(false);
                resourcesClosed = false;
                DeviceServiceHost host;
                try { host = await start().ConfigureAwait(false); }
                catch (Exception e)
                {
                    pendingStartup = e as DeviceStartupException;
                    resourcesClosed = pendingStartup?.ResourcesClosed ?? true;
                    if (resourcesClosed) pendingStartup = null;
                    throw;
                }
                Volatile.Write(ref current, host); releaseFailure = null;
                _ = ObserveAsync(host);
                log.Write("deviceServicePower", new { resume, host.Runtime.Epoch });
            }
            else
            {
                pendingClose ??= Interlocked.Exchange(ref current, null);
                if (pendingClose is null && pendingStartup is null) return;
                var epoch = pendingClose?.Runtime.Epoch;
                await ClosePendingAsync().ConfigureAwait(false);
                log.Write("deviceServicePower", new { resume, epoch });
            }
        }
        catch (Exception e)
        {
            try { log.Write("deviceServicePowerFailure", new { resume, error = e.ToString() }); }
            catch { /* The original exception carries unfinished resources; logging must not replace it. */ }
            throw;
        }
        finally { transition.Release(); }
    }
    private async Task ClosePendingAsync()
    {
        if (pendingStartup is { } startup)
        {
            try { await startup.DisposeAsync().ConfigureAwait(false); }
            catch (Exception e) { releaseFailure = e; throw; }
            finally
            {
                if (startup.ResourcesClosed) { pendingStartup = null; resourcesClosed = true; }
            }
            releaseFailure = null;
        }
        if (pendingClose is not { } host) return;
        try { await host.DisposeAsync().ConfigureAwait(false); pendingClose = null; releaseFailure = null; resourcesClosed = true; }
        catch (Exception e)
        {
            releaseFailure = e;
            // An uncertain restoration with confirmed handle closure can be recovered on a fresh host.
            // An unfinished handle closure must be retried before any new factory can run.
            if (host.Runtime.ResourcesClosed) { pendingClose = null; resourcesClosed = true; }
            throw;
        }
    }
    private async Task ObserveAsync(DeviceServiceHost host)
    {
        Exception? failure = null;
        try { await host.Completion.ConfigureAwait(false); } catch (Exception e) { failure = e; }
        // Intentional suspend/stop clears current before closing transport. It must not complete the SCM lifetime.
        await transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!disposed && ReferenceEquals(current, host))
                completion.TrySetException(failure ?? new IOException("汎用サービスの通信が予期せず終了しました"));
        }
        finally { transition.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            stopping = true;
            pendingClose ??= Interlocked.Exchange(ref current, null);
            try
            {
                if (pendingClose is not null || pendingStartup is not null) await ClosePendingAsync().ConfigureAwait(false);
                else if (releaseFailure is not null) throw new IOException("スリープ前の解除が未確認です", releaseFailure);
                disposed = true;
                completion.TrySetResult();
            }
            catch (Exception e)
            {
                if (pendingClose is null && pendingStartup is null) { disposed = true; completion.TrySetException(e); }
                throw;
            }
        }
        finally { transition.Release(); }
    }
}
