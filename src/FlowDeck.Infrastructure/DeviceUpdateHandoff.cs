namespace FlowDeck.Infrastructure;

/// <summary>Keep the old owner until release and client cleanup are both confirmed.</summary>
public sealed class DeviceUpdateHandoff(Func<Task<bool>> release, Func<ValueTask> disconnect)
{
    private Task<bool>? pending;
    private readonly object gate = new();
    private bool released, prepared;
    public bool Prepared { get { lock (gate) return prepared; } }
    public Task<bool> PrepareAsync()
    {
        TaskCompletionSource<bool> completion;
        lock (gate)
        {
            if (prepared) return Task.FromResult(true);
            if (pending is { IsCompleted: false }) return pending;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); pending = completion.Task;
        }
        _ = CompleteAsync(completion); return completion.Task;
    }
    private async Task CompleteAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            if (!released)
            {
                if (!await release()) { completion.SetResult(false); return; }
                released = true;
            }
            await disconnect(); lock (gate) prepared = true;
            completion.SetResult(true);
        }
        catch (Exception) { completion.TrySetResult(false); }
    }
}
