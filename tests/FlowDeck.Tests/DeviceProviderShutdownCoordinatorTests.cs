using System.Collections.Concurrent;
using FlowDeck.Core;

namespace FlowDeck.Tests;

public sealed partial class DeviceProviderReplacementTests
{
    private sealed class Coordinator(Provider inner) : IDeviceProviderShutdownCoordinator
    {
        public bool Allow = true, CompleteChild = true, Fail;
        public int Attempts;
        public string ProviderId => inner.ProviderId;
        public string[] ResourceDomains => inner.ResourceDomains;
        public IDeviceControlDriver[] CachedDrivers => inner.CachedDrivers;
        public Task PollAsync(CancellationToken ct) => inner.PollAsync(ct);
        public bool CanCompleteDriverShutdown(IDeviceControlDriver[] pending) => Allow && pending.Length > 0 &&
            pending.All(driver => ReferenceEquals(driver, inner.Driver));
        public async ValueTask DisposeAsync()
        {
            Attempts++;
            if (Fail) throw new IOException("native fallback pending");
            if (CompleteChild) inner.Driver.FailDispose = false;
            await inner.DisposeAsync();
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CompiledCoordinatorMayCompleteOnlyItsFailedChildren(bool retired)
    {
        var events = new ConcurrentQueue<string>(); var inner = new Provider("coordinated", "rgb", events);
        var coordinator = new Coordinator(inner); var independent = new Provider("other", "cooler", events);
        await using var runtime = new DeviceControlRuntime([], ["user"], new TestLog(), new TestClock(), providers: [coordinator, independent]);
        Assert.False(coordinator.CanCompleteDriverShutdown([new Driver("rgb", "coordinated", events)]));
        inner.Driver.FailDispose = true;
        if (retired)
        {
            var hello = runtime.OpenSession("user");
            await runtime.ReconfigureAsync(hello.Session, runtime.State().Inventory.TopologyRevision, async (_, ct) =>
            { await runtime.ReplaceProvidersAsync(hello.Session, ["coordinated"], () => [], ct); return []; });
            Assert.False(independent.Disposed); Assert.False(independent.Driver.Disposed);
        }
        else await runtime.DisposeAsync();
        Assert.True(inner.Disposed); Assert.True(inner.Driver.Disposed); Assert.Equal(2, inner.Driver.Disposes); Assert.Equal(1, coordinator.Attempts);
        await runtime.DisposeAsync(); Assert.True(runtime.ResourcesClosed); Assert.Equal(1, coordinator.Attempts);
    }
    [Fact] public async Task SuccessfulParentClosureIsNotAcceptedAsProofOfChildClosure()
    {
        var inner = new Provider("coordinated", "rgb", new()); var coordinator = new Coordinator(inner) { CompleteChild = false };
        await using var runtime = new DeviceControlRuntime([], ["user"], new TestLog(), new TestClock(), providers: [coordinator]);
        inner.Driver.FailDispose = true;
        try
        {
            await Assert.ThrowsAsync<AggregateException>(() => runtime.DisposeAsync().AsTask());
            Assert.False(runtime.ResourcesClosed); Assert.False(inner.Driver.Disposed); Assert.True(inner.Disposed);
            Assert.Equal(2, inner.Driver.Disposes); Assert.Equal(1, coordinator.Attempts);
        }
        finally { inner.Driver.FailDispose = false; }
        await runtime.DisposeAsync(); Assert.True(runtime.ResourcesClosed); Assert.Equal(1, coordinator.Attempts);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FailedOrRefusedCoordinatorRetainsUnfinishedChildrenForRetry(bool refused)
    {
        var inner = new Provider("coordinated", "rgb", new()); var coordinator = new Coordinator(inner) { Allow = !refused, Fail = !refused };
        var independent = new Provider("other", "cooler", new());
        await using var runtime = new DeviceControlRuntime([], ["user"], new TestLog(), new TestClock(), providers: [coordinator, independent]);
        inner.Driver.FailDispose = true;
        try
        {
            await Assert.ThrowsAsync<AggregateException>(() => runtime.DisposeAsync().AsTask());
            Assert.False(runtime.ResourcesClosed); Assert.False(inner.Disposed); Assert.False(inner.Driver.Disposed);
            Assert.True(independent.Disposed); Assert.Equal(refused ? 0 : 1, coordinator.Attempts);
        }
        finally { coordinator.Allow = true; coordinator.Fail = false; }
        await runtime.DisposeAsync(); Assert.True(runtime.ResourcesClosed); Assert.Equal(1, independent.Disposes);
    }
}
