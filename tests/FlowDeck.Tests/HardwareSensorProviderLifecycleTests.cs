using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class HardwareSensorProviderLifecycleTests
{
    private sealed class Backend : IHardwareSensorBackend
    {
        public int Reads, Disposals;
        public bool FailClose;
        public TaskCompletionSource? HoldRead, HoldClose;
        public readonly TaskCompletionSource ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource CloseEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<HardwareSensorDevice[]> ReadDevicesAsync(CancellationToken ct)
        { Reads++; ReadEntered.TrySetResult(); if (HoldRead is not null) await HoldRead.Task; return []; }
        public async ValueTask DisposeAsync()
        { Disposals++; CloseEntered.TrySetResult(); if (HoldClose is not null) await HoldClose.Task; if (FailClose) throw new IOException("close failed"); }
    }
    private static HardwareSensorProvider Provider(Backend backend) => new("0123456789abcdef0123456789abcdef", backend);

    [Fact]
    public async Task FailedBackendDisposalIsRetriedAndClosedProvidersDoNotReadAgain()
    {
        var backend = new Backend { FailClose = true }; var provider = Provider(backend);
        await provider.PollAsync(default); await Assert.ThrowsAsync<IOException>(async () => await provider.DisposeAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.PollAsync(default)); Assert.Equal(1, backend.Reads);
        backend.FailClose = false; await provider.DisposeAsync(); await provider.DisposeAsync(); Assert.Equal(2, backend.Disposals);
    }

    [Fact]
    public async Task ConcurrentDisposalsShareTheSuccessfulCloseWithoutOverlappingCalls()
    {
        var backend = new Backend { HoldClose = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var provider = Provider(backend);
        var first = provider.DisposeAsync().AsTask(); await backend.CloseEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = provider.DisposeAsync().AsTask();
        try { Assert.False(second.IsCompleted); Assert.Equal(1, backend.Disposals); }
        finally { backend.HoldClose.TrySetResult(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(1, backend.Disposals);
    }

    [Fact]
    public async Task DisposalWaitsForInFlightReadAndPreventsNewReadsFromEnteringTheBackend()
    {
        var backend = new Backend { HoldRead = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var provider = Provider(backend);
        var read = provider.PollAsync(default); await backend.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var close = provider.DisposeAsync().AsTask(); var second = provider.PollAsync(default);
        try { Assert.False(close.IsCompleted); Assert.Equal(0, backend.Disposals); Assert.Equal(1, backend.Reads); }
        finally { backend.HoldRead.TrySetResult(); }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => read);
        await close.WaitAsync(TimeSpan.FromSeconds(3)); await Assert.ThrowsAsync<ObjectDisposedException>(() => second);
        Assert.Equal(1, backend.Disposals); Assert.Equal(1, backend.Reads);
    }
}
