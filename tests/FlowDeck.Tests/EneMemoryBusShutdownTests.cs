using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class EneMemoryBusShutdownTests
{
    [Theory] [InlineData("handle")] [InlineData("release")] [InlineData("mutex")]
    public void FailedCloseKeepsTheRemainingResourceAndNeverRepeatsSuccessfulSteps(string failure)
    {
        var events = new List<string>(); var fail = true;
        void Step(string name) { events.Add(name); if (fail && name == failure) throw new IOException(name + " failed"); }
        var lifetime = new EneMemoryBusLifetime(() => Step("handle"), () => Step("release"), () => Step("mutex"));
        lifetime.AcquiredMutex(); Assert.Throws<IOException>(lifetime.Dispose); Assert.False(lifetime.Closed);
        Assert.Throws<ObjectDisposedException>(lifetime.CheckAccess);
        Assert.Equal(failure switch { "handle" => ["handle"], "release" => ["handle", "release"], _ => new[] { "handle", "release", "mutex" } }, events);
        fail = false; lifetime.Dispose(); lifetime.Dispose(); Assert.True(lifetime.Closed);
        Assert.Equal(2, events.Count(e => e == failure));
        Assert.All(new[] { "handle", "release", "mutex" }.Where(e => e != failure), e => Assert.Equal(1, events.Count(v => v == e)));
    }
    [Fact] public void ActualMutexRemainsOwnedUntilTheModuleHandleCloses()
    {
        using var mutex = new Mutex(false); Assert.True(mutex.WaitOne(0)); var fail = true;
        var lifetime = new EneMemoryBusLifetime(() => { if (fail) throw new IOException("module close failed"); }, mutex.ReleaseMutex, () => { });
        lifetime.AcquiredMutex(); Assert.Throws<IOException>(lifetime.Dispose);
        bool? acquired = null; Exception? wrongThread = null;
        var contender = new Thread(() => {
            try { lifetime.Dispose(); } catch (Exception e) { wrongThread = e; }
            acquired = mutex.WaitOne(0); if (acquired == true) mutex.ReleaseMutex();
        }) { IsBackground = true };
        contender.Start(); Assert.True(contender.Join(TimeSpan.FromSeconds(3)));
        Assert.IsType<IOException>(wrongThread); Assert.False(acquired); Assert.False(lifetime.Closed);
        fail = false; lifetime.Dispose(); Assert.True(lifetime.Closed);
        var after = new Thread(() => { acquired = mutex.WaitOne(0); if (acquired == true) mutex.ReleaseMutex(); }) { IsBackground = true };
        after.Start(); Assert.True(after.Join(TimeSpan.FromSeconds(3))); Assert.True(acquired);
    }
    [Fact] public void ClosedOrWrongThreadBusNeverCallsTheNativeTransfer()
    {
        var calls = 0; var bus = new EneMemoryRgbBus((_, _, count) => { calls++; return new long[count]; });
        Exception? failure = null;
        var other = new Thread(() => { try { bus.Identify(0x71); } catch (Exception e) { failure = e; } }) { IsBackground = true };
        other.Start(); Assert.True(other.Join(TimeSpan.FromSeconds(3))); Assert.IsType<IOException>(failure); Assert.Equal(0, calls);
        bus.Dispose(); Assert.Throws<ObjectDisposedException>(() => bus.Identify(0x71));
        Assert.Throws<ObjectDisposedException>(() => bus.VerifyPreviouslyIdentified(0x71)); Assert.Equal(0, calls);
    }

    private sealed class Factory
    {
        public readonly EneMemoryBackendTests.Rack Rack = new(0x71);
        public readonly List<Lease> Leases = [];
        public bool FailNewClose, FailInitialization;
        public EneMemoryTransport Transport(string id = "recording-bus") => Rack.Transport(id) with { Open = Open };
        private IEneMemoryIo Open()
        {
            Assert.All(Leases, lease => Assert.True(lease.Closed));
            var bus = new Lease(Rack.Open()) { FailClose = FailNewClose }; Leases.Add(bus);
            if (FailInitialization)
            {
                var opening = new IOException("initialization failed");
                try { bus.Dispose(); } catch (Exception closing) { throw new EneMemoryBusOpenException(opening, closing, bus); }
                throw opening;
            }
            return bus;
        }
        public void AllowClosing() { FailNewClose = false; foreach (var lease in Leases) lease.FailClose = false; }
    }
    private sealed class Lease(IEneMemoryIo inner) : IEneMemoryIo, IEneMemoryIdentifyingIo
    {
        private readonly int thread = Environment.CurrentManagedThreadId;
        public bool FailClose, Closed;
        public int Closes;
        public bool Identify(byte a) => inner.Identify(a);
        public EneMemoryIdentification? IdentifyController(byte a) => ((IEneMemoryIdentifyingIo)inner).IdentifyController(a);
        public byte Read(byte a, ushort r) => inner.Read(a, r);
        public void Write(byte a, ushort r, byte value) => inner.Write(a, r, value);
        public void Dispose()
        {
            Assert.Equal(thread, Environment.CurrentManagedThreadId); Closes++;
            if (FailClose) throw new IOException("lease close failed");
            inner.Dispose(); Closed = true;
        }
    }
    [Fact] public async Task FailedDiscoveryCloseBlocksNewBusesUntilTheOriginalHandleIsClosed()
    {
        var factory = new Factory { FailNewClose = true }; var second = new EneMemoryBackendTests.Rack(0x71);
        await using var backend = new EneMemoryDeviceBackend([factory.Transport(), second.Transport("second")]);
        try
        {
            Assert.Empty(await backend.DiscoverAsync(default)); var original = Assert.Single(factory.Leases);
            Assert.False(original.Closed); Assert.Equal(1, factory.Rack.Active); Assert.Equal(0, second.Opens);
            Assert.Empty(await backend.DiscoverAsync(default)); Assert.Single(factory.Leases); Assert.Equal(2, original.Closes);
            Assert.Equal(0, second.Opens); Assert.NotEmpty(backend.Problems);
            factory.AllowClosing(); Assert.Equal(2, (await backend.DiscoverAsync(default)).Length);
            Assert.True(original.Closed); Assert.Equal(3, original.Closes); Assert.Equal(2, factory.Leases.Count);
            Assert.Equal(1, factory.Rack.MaximumActive); Assert.Equal(0, factory.Rack.Active); Assert.Empty(backend.Problems);
        }
        finally { factory.AllowClosing(); }
    }
    [Fact] public async Task OperationAndCloseFailuresAreBothPreservedAndOldSessionsCannotResume()
    {
        var factory = new Factory(); await using var backend = new EneMemoryDeviceBackend([factory.Transport()]);
        var previous = Assert.Single(await backend.DiscoverAsync(default)); factory.FailNewClose = true; factory.Rack.FailRead = true;
        try
        {
            IEneMemorySession? escaped = null;
            var error = await Assert.ThrowsAsync<AggregateException>(() => backend.ExecuteAsync(previous, true, session => {
                escaped = session; return session.Read(0x8020);
            }, default));
            Assert.Contains("read failed", error.ToString()); Assert.Contains("lease close failed", error.ToString());
            Assert.Throws<IOException>(() => backend.Check(previous)); Assert.Throws<IOException>(() => escaped!.Read(0x8020));
            var unclosed = factory.Leases[1]; Assert.False(unclosed.Closed);
            factory.Rack.FailRead = false; factory.AllowClosing(); var current = Assert.Single(await backend.DiscoverAsync(default));
            Assert.NotEqual(previous.ConnectionId, current.ConnectionId); Assert.True(unclosed.Closed);
            var opened = factory.Leases.Count;
            await Assert.ThrowsAsync<IOException>(() => backend.ExecuteAsync(previous, true, session => session.Read(0x8020), default));
            Assert.Equal(opened, factory.Leases.Count); Assert.Empty(factory.Rack.Writes);
        }
        finally { factory.AllowClosing(); }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ShutdownRetainsTheOwnerThreadAfterOperationOrPartialInitializationCloseFailure(bool partialInitialization)
    {
        var factory = new Factory { FailNewClose = true, FailInitialization = partialInitialization };
        await using var backend = new EneMemoryDeviceBackend([factory.Transport()]);
        try
        {
            Assert.Empty(await backend.DiscoverAsync(default)); var unclosed = Assert.Single(factory.Leases);
            if (partialInitialization) Assert.Contains("初期化と終了", Assert.Single(backend.Problems));
            await Assert.ThrowsAsync<AggregateException>(() => backend.DisposeAsync().AsTask());
            Assert.False(unclosed.Closed); Assert.Equal(1, factory.Rack.Active);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => backend.DiscoverAsync(default));
            factory.AllowClosing(); await backend.DisposeAsync(); await backend.DisposeAsync();
            Assert.True(unclosed.Closed); Assert.Equal(3, unclosed.Closes); Assert.Equal(0, factory.Rack.Active);
        }
        finally { factory.AllowClosing(); }
    }
    [Fact] public async Task ConcurrentShutdownWaitsForInFlightIoAndSharesItsRetryableCloseResult()
    {
        var factory = new Factory(); await using var backend = new EneMemoryDeviceBackend([factory.Transport()]);
        var connection = Assert.Single(await backend.DiscoverAsync(default)); factory.FailNewClose = true;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var operation = backend.ExecuteAsync(connection, true, session => {
            var value = session.Read(0x8020); entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); return value;
        }, default);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3))); var current = factory.Leases[1];
            var first = backend.DisposeAsync().AsTask(); var second = backend.DisposeAsync().AsTask();
            Assert.Same(first, second); Assert.False(first.IsCompleted); Assert.Equal(0, current.Closes);
            release.Set(); await Assert.ThrowsAsync<IOException>(() => operation);
            await Assert.ThrowsAsync<AggregateException>(() => first); Assert.False(current.Closed); Assert.Equal(2, current.Closes);
            factory.AllowClosing(); await backend.DisposeAsync(); Assert.True(current.Closed); Assert.Equal(3, current.Closes);
        }
        finally { release.Set(); factory.AllowClosing(); }
    }
}
