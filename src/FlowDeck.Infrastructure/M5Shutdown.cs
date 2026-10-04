namespace FlowDeck.Infrastructure;

/// <summary>One in-flight close attempt; a failed attempt can be explicitly retried.</summary>
internal sealed class M5Shutdown
{
    private readonly object gate = new();
    private Task? pending;
    public bool Requested { get { lock (gate) return pending is not null; } }
    public Task Run(Func<Task> cleanup)
    {
        TaskCompletionSource completion;
        lock (gate)
        {
            if (pending is { IsCompleted: false } or { IsCompletedSuccessfully: true }) return pending;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = completion.Task;
        }
        // Publish before running callbacks, including callbacks which complete synchronously.
        _ = CompleteAsync(completion, cleanup);
        return completion.Task;
    }
    private static async Task CompleteAsync(TaskCompletionSource completion, Func<Task> cleanup)
    {
        try { await cleanup().ConfigureAwait(false); completion.TrySetResult(); }
        catch (OperationCanceledException e) { completion.TrySetCanceled(e.CancellationToken); }
        catch (Exception e) { completion.TrySetException(e); }
    }
}
