using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class AsrockProviderLifecycleTests
{
    private sealed class Backend : IAsrockDeviceBackend
    {
        public int Reads, Closes;
        public bool FailClose;
        public TaskCompletionSource? HoldRead, HoldClose;
        public readonly TaskCompletionSource ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource CloseEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AsrockDeviceConnection[]> DiscoverAsync(CancellationToken ct)
        { Reads++; ReadEntered.TrySetResult(); if (HoldRead is not null) await HoldRead.Task; return []; }
        public void Check(AsrockDeviceConnection expected) => throw new InvalidOperationException();
        public IAsrockRgbSession Borrow(AsrockDeviceConnection expected) => throw new InvalidOperationException();
        public void CheckConflicts() { }
        public async ValueTask DisposeAsync()
        { Closes++; CloseEntered.TrySetResult(); if (HoldClose is not null) await HoldClose.Task; if (FailClose) throw new IOException("close failed"); }
    }
    private static AsrockDeviceProvider New(Backend backend,string directory) => new("0123456789abcdef0123456789abcdef",directory,"boot",backend);
    [Fact]
    public async Task ClosureIsNotPublishedOnFailureAndAClosedProviderCannotDiscoverAgain()
    {
        using var dir = new ServiceTestDirectory(); var backend = new Backend { FailClose = true }; var provider = New(backend,dir.Path);
        await provider.PollAsync(default); await Assert.ThrowsAsync<IOException>(() => provider.DisposeAsync().AsTask());
        Assert.False(provider.ResourcesClosed); await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.PollAsync(default));
        backend.FailClose = false; await provider.DisposeAsync(); await provider.DisposeAsync();
        Assert.True(provider.ResourcesClosed); Assert.Equal(2,backend.Closes); Assert.Equal(1,backend.Reads);
    }
    [Fact]
    public async Task ClosingWaitsForDiscoveryAndPreventsQueuedDiscoveryFromStarting()
    {
        using var dir = new ServiceTestDirectory(); var backend = new Backend { HoldRead = new() }; var provider = New(backend,dir.Path);
        var read = provider.PollAsync(default); await backend.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var close = provider.DisposeAsync().AsTask(); var queued = provider.PollAsync(default);
        try { Assert.False(close.IsCompleted); Assert.False(provider.ResourcesClosed); Assert.Equal(0,backend.Closes); }
        finally { backend.HoldRead.TrySetResult(); }
        await read; await close; await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
        Assert.True(provider.ResourcesClosed); Assert.Equal(1,backend.Reads); Assert.Equal(1,backend.Closes);
    }
    [Fact]
    public async Task ConcurrentClosesShareOneSuccessfulTransportClose()
    {
        using var dir = new ServiceTestDirectory(); var backend = new Backend { HoldClose = new() }; var provider = New(backend,dir.Path);
        var first = provider.DisposeAsync().AsTask(); await backend.CloseEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = provider.DisposeAsync().AsTask();
        try { Assert.False(second.IsCompleted); Assert.False(provider.ResourcesClosed); Assert.Equal(1,backend.Closes); }
        finally { backend.HoldClose.TrySetResult(); }
        await Task.WhenAll(first,second); Assert.True(provider.ResourcesClosed); Assert.Equal(1,backend.Closes);
    }
}
