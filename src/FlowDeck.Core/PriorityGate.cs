namespace FlowDeck.Core;

/// <summary>One USB operation; waiting shutdown/fallback precede users, automatic work and telemetry.</summary>
public sealed class PriorityGate
{
    private readonly object sync = new();
    private readonly PriorityQueue<TaskCompletionSource<IDisposable>, (int, long)> queue = new();
    private long sequence;
    private bool held;
    public IDisposable? TryEnter()
    {
        lock (sync)
        {
            if (held) return null;
            held = true; return new Lease(this);
        }
    }
    public Task<IDisposable> EnterAsync(int priority)
    {
        lock (sync)
        {
            if (!held) { held = true; return Task.FromResult<IDisposable>(new Lease(this)); }
            var waiter = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Enqueue(waiter, (priority, sequence++));
            return waiter.Task;
        }
    }
    private void Release()
    {
        lock (sync)
        {
            if (queue.TryDequeue(out var waiter, out _)) waiter.SetResult(new Lease(this));
            else held = false;
        }
    }
    private sealed class Lease(PriorityGate owner) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Release(); }
    }
}
