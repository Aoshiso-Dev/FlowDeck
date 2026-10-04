using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class HubDeviceProviderTests
{
    [Fact]
    public async Task DriverRetriesFailedAdapterCloseWithoutRepeatingRestorationOrAcceptingCommands()
    {
        await using var f = new Fixture([A]); await f.DiscoverAndOpen(); var driver = f.Driver(A.Id); var adapter = f.Adapters[A.Id];
        Configure(driver, 70); await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 60), default);
        adapter.FailDispose = true;
        try
        {
            await Assert.ThrowsAsync<AggregateException>(() => driver.DisposeAsync().AsTask());
            Assert.Equal(70, (await adapter.Inner.ReadAsync()).Channels[0].Duty);
            Assert.Empty(new HubEndpointRecoveryJournal(f.Directory.Path, driver.DeviceId).Pending);
            Assert.Null(driver.Observe()); Assert.Empty(driver.ReadCachedSamples());
            Assert.Equal(Outcome.Rejected, (await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 65), default)).Outcome);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => driver.PollAsync(default));
            var writes = adapter.Inner.Writes; var reads = adapter.Reads;
            adapter.FailDispose = false; await driver.DisposeAsync();
            Assert.Equal(2, adapter.Disposes); Assert.True(adapter.Closed);
            Assert.Equal(writes, adapter.Inner.Writes); Assert.Equal(reads, adapter.Reads);
            Assert.Empty(await driver.ReleaseAsync(DeviceReleaseReason.ServiceStopping, default));
            await driver.DisposeAsync(); Assert.Equal(2, adapter.Disposes);
        }
        finally { adapter.FailDispose = false; }
    }

    [Fact]
    public async Task DriverShutdownSharesAnInProgressAttemptAndWaitsForAdapterCompletion()
    {
        await using var f = new Fixture([A]); await f.DiscoverAndOpen(); var driver = f.Driver(A.Id); var adapter = f.Adapters[A.Id];
        var entered = new TaskCompletionSource(); var resume = new TaskCompletionSource();
        adapter.DuringDispose = async () => { entered.TrySetResult(); await resume.Task; };
        var first = driver.DisposeAsync().AsTask(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var second = driver.DisposeAsync().AsTask(); Assert.Same(first, second);
            Assert.False(second.IsCompleted); Assert.False(adapter.Closed); Assert.Equal(1, adapter.Disposes);
        }
        finally { resume.TrySetResult(); }
        await first; Assert.True(adapter.Closed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ReadsQueuedBeforeShutdownCannotReachTheAdapterAfterItsCurrentRead(bool initialize)
    {
        await using var f = new Fixture([A]); await f.DiscoverAndOpen(); var driver = f.Driver(A.Id); var adapter = f.Adapters[A.Id];
        var entered = new TaskCompletionSource(); var resume = new TaskCompletionSource();
        adapter.DuringRead = async () => { entered.TrySetResult(); await resume.Task; };
        var read = driver.RefreshAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = initialize ? driver.InitializeAsync() : driver.RefreshAsync();
        var closed = driver.DisposeAsync().AsTask();
        try { Assert.False(closed.IsCompleted); Assert.False(queued.IsCompleted); Assert.Equal(0, adapter.Disposes); }
        finally { resume.TrySetResult(); }
        await read; await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
        await closed.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, adapter.Reads); Assert.Single(adapter.Opened);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ProviderRetriesOnlyUnfinishedAdaptersAndStillClosesIndependentWorkers(bool discoveryFailure)
    {
        await using var f = new Fixture([A, B]); await f.DiscoverAndOpen();
        var a = f.Adapters[A.Id]; var b = f.Adapters[B.Id];
        f.Discovery.FailDispose = discoveryFailure; a.FailDispose = !discoveryFailure;
        try
        {
            await Assert.ThrowsAsync<AggregateException>(() => f.Provider.DisposeAsync().AsTask());
            Assert.True(b.Closed); Assert.Equal(1, b.Disposes);
            var probes = f.Discovery.Probes;
            await Assert.ThrowsAsync<ObjectDisposedException>(() => f.Provider.PollAsync(default));
            Assert.Equal(probes, f.Discovery.Probes);
            f.Discovery.FailDispose = a.FailDispose = false;
            await f.Provider.DisposeAsync(); await f.Provider.DisposeAsync();
            Assert.Equal(discoveryFailure ? 1 : 2, a.Disposes); Assert.Equal(1, b.Disposes);
            Assert.Equal(discoveryFailure ? 2 : 1, f.Discovery.Disposes);
        }
        finally { f.Discovery.FailDispose = a.FailDispose = false; }
    }

    [Fact]
    public async Task ProviderWaitsForDiscoveryAndRefusesAnotherQueuedDiscoveryDuringShutdown()
    {
        await using var f = new Fixture([A]); var entered = new TaskCompletionSource(); var resume = new TaskCompletionSource();
        f.Discovery.DuringProbe = async () => { entered.TrySetResult(); await resume.Task; };
        var polling = f.Provider.PollAsync(default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = f.Provider.PollAsync(default); var closing = f.Provider.DisposeAsync().AsTask();
        try { Assert.False(closing.IsCompleted); Assert.Equal(0, f.Discovery.Disposes); }
        finally { resume.TrySetResult(); }
        await polling; await Assert.ThrowsAsync<ObjectDisposedException>(() => queued); await closing;
        Assert.Equal(1, f.Discovery.Probes); Assert.Equal(1, f.Adapters[A.Id].Disposes); Assert.Equal(1, f.Discovery.Disposes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedConstructionKeepsAnAdapterWhoseCleanupFailedUntilProviderShutdown(bool lightingJournal)
    {
        using var directory = new ServiceTestDirectory(); var discovery = new Discovery { Found = [A] };
        var adapter = new Adapter(discovery, A) { FailDispose = true }; var factories = 0;
        var id = DeviceIdentity.Create(Installation, HubDeviceDriver.Provider, A.Id);
        var filename = (lightingJournal ? "hub-lighting-" : "hub-endpoints-") + SettingsTransfer.Hash(id).ToLowerInvariant() + ".json";
        File.WriteAllText(Path.Combine(directory.Path, filename), "{broken");
        var provider = new HubDeviceProvider(Installation, directory.Path, discovery, _ => { factories++; return adapter; });
        try
        {
            var failure = await Assert.ThrowsAsync<AggregateException>(() => provider.PollAsync(default));
            Assert.Equal(2, failure.InnerExceptions.Count); Assert.Contains("adapter close failed", failure.ToString());
            Assert.Empty(provider.CachedDrivers); Assert.Equal(1, adapter.Disposes);
            await Assert.ThrowsAsync<IOException>(() => provider.PollAsync(default)); Assert.Equal(1, factories);
            await Assert.ThrowsAsync<AggregateException>(() => provider.DisposeAsync().AsTask());
            Assert.Equal(2, adapter.Disposes); Assert.Equal(1, discovery.Disposes);
            adapter.FailDispose = false; await provider.DisposeAsync(); await provider.DisposeAsync();
            Assert.Equal(3, adapter.Disposes); Assert.True(adapter.Closed); Assert.Equal(1, discovery.Disposes);
        }
        finally { adapter.FailDispose = false; await provider.DisposeAsync(); }
    }

    [Fact]
    public async Task RuntimeRetainsARealHubProviderUntilItsActualAdapterHasClosed()
    {
        await using var f = new Fixture([A]); await f.DiscoverAndOpen(); var adapter = f.Adapters[A.Id];
        await using var runtime = new DeviceControlRuntime([], ["user"], new TestLog(), providers: [f.Provider]);
        adapter.FailDispose = true;
        try
        {
            await Assert.ThrowsAsync<AggregateException>(() => runtime.DisposeAsync().AsTask());
            Assert.False(runtime.ResourcesClosed); Assert.Equal(0, f.Discovery.Disposes);
            adapter.FailDispose = false; await runtime.DisposeAsync();
            Assert.True(runtime.ResourcesClosed); Assert.True(adapter.Closed); Assert.Equal(2, adapter.Disposes);
            Assert.Equal(1, f.Discovery.Disposes);
        }
        finally { adapter.FailDispose = false; }
    }
}
