using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>Owns a prepared v2 runtime and its authenticated transport, independently of any GUI process.</summary>
public sealed class DeviceServiceHost : IAsyncDisposable
{
    private readonly DeviceRuntimeStartup startup;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim lifecycle = new(1);
    private bool disposed;
    public DeviceControlRuntime Runtime => startup.Runtime;
    public Task Completion { get; private set; } = Task.CompletedTask;
    private DeviceServiceHost(DeviceRuntimeStartup startup) => this.startup = startup;
    private async Task ServeAsync(string[] users, IEventLog log, string pipe, TaskCompletionSource ready, Func<bool>? allowsClients)
    {
        try { await new DeviceServicePipeServer(Runtime, users, log, pipe, allowsClients).RunAsync(stop.Token, () => ready.TrySetResult()).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    public static async Task<DeviceServiceHost> StartAsync(DeviceRuntimeStartup startup, string[] users, IEventLog log,
        string pipe = DeviceServiceProtocol.PipeName, CancellationToken ct = default, Func<bool>? allowsClients = null)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new DeviceServiceHost(startup);
        try
        {
            startup.Runtime.Start();
            host.Completion = host.ServeAsync(users.ToArray(), log, pipe, ready, allowsClients);
            var first = await Task.WhenAny(ready.Task, host.Completion).WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            if (first == host.Completion) { await host.Completion.ConfigureAwait(false); throw new IOException("汎用サービスの通信が起動前に終了しました"); }
            await ready.Task.ConfigureAwait(false); ct.ThrowIfCancellationRequested(); return host;
        }
        catch (Exception e)
        {
            await DeviceStartupException.RethrowAfterCleanupAsync(e, host, () => host.Runtime.ResourcesClosed && host.Completion.IsCompleted).ConfigureAwait(false);
            throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            var errors = new List<Exception>();
            try { if (!stop.IsCancellationRequested) await stop.CancelAsync().ConfigureAwait(false); } catch (Exception e) { errors.Add(e); }
            try { await Completion.ConfigureAwait(false); } catch (Exception e) { errors.Add(e); }
            try { await startup.DisposeAsync().ConfigureAwait(false); } catch (Exception e) { errors.Add(e); }
            try { if (Runtime.State().LifecycleError is { } failure) errors.Add(new IOException(failure)); }
            catch (Exception e) { errors.Add(e); }
            if (Runtime.ResourcesClosed) { disposed = true; stop.Dispose(); }
            if (errors.Count > 0) throw new AggregateException("汎用サービスの終了を確認できません", errors);
        }
        finally { lifecycle.Release(); }
    }
}
