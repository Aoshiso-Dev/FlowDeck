namespace FlowDeck.Infrastructure;

// PawnIO module teardown can still touch the controller. Keep the bus mutex until
// its handle is closed, and release/dispose each resource only after that step succeeds.
internal sealed class EneMemoryBusLifetime(Action closeHandle, Action releaseMutex, Action closeMutex)
{
    private readonly int thread = Environment.CurrentManagedThreadId;
    private bool stopping, handleClosed, held, mutexClosed;
    public bool Closed => handleClosed && !held && mutexClosed;
    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != thread) throw new IOException("SMBusの取得と解放は同じスレッドで実行してください");
    }
    public void CheckAccess() { CheckThread(); ObjectDisposedException.ThrowIf(stopping, this); }
    public void AcquiredMutex() { CheckAccess(); held = true; }
    public void Dispose()
    {
        CheckThread(); if (Closed) return; stopping = true;
        if (!handleClosed) { closeHandle(); handleClosed = true; }
        if (held) { releaseMutex(); held = false; }
        if (!mutexClosed) { closeMutex(); mutexClosed = true; }
    }
}

// A factory that failed after acquiring a resource must return its unfinished owner
// with the error. The backend keeps it on the original bus thread for disposal retry.
internal sealed class EneMemoryBusOpenException(Exception opening, Exception closing, IEneMemoryIo pendingBus)
    : IOException("SMBusの初期化と終了に失敗しました", new AggregateException(opening, closing))
{
    public IEneMemoryIo PendingBus { get; } = pendingBus;
}
