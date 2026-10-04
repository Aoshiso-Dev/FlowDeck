using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Device = FlowDeck.Tests.AsrockVolatileTransactionTests.Device;
using Detected = FlowDeck.Infrastructure.AsrockHidDeviceBackend.Detected;

namespace FlowDeck.Tests;

public sealed partial class AsrockHidBackendTests
{
    private sealed class ClosingSession : IAsrockRgbSession
    {
        private readonly int owner = Environment.CurrentManagedThreadId;
        public readonly Device Inner = new() { OwnerThread = Environment.CurrentManagedThreadId };
        public volatile bool FailClose, FailExchange;
        public int Closes;
        public Action? Exchanging;
        public byte[] Exchange(byte[] request)
        {
            Assert.Equal(owner, Environment.CurrentManagedThreadId);
            Exchanging?.Invoke();
            if (FailExchange) throw new IOException("exchange failed");
            return Inner.Exchange(request);
        }
        public void Dispose()
        {
            Assert.Equal(owner, Environment.CurrentManagedThreadId); Closes++;
            if (FailClose) throw new IOException("session close failed");
            Inner.Dispose();
        }
    }

    [Fact]
    public async Task FailedCloseRetainsItsOwnerThreadAndDoesNotRepeatIndependentSuccessfulClosures()
    {
        var sessions = new Dictionary<string, ClosingSession>();
        await using var backend = new AsrockHidDeviceBackend(() => [new("a", null), new("b", null)], path =>
        { var session = new ClosingSession(); sessions.Add(path, session); return session; }, () => { });
        await backend.DiscoverAsync(default); sessions["a"].FailClose = true;
        try
        {
            await Assert.ThrowsAsync<AggregateException>(() => backend.DisposeAsync().AsTask());
            Assert.False(sessions["a"].Inner.Disposed); Assert.True(sessions["b"].Inner.Disposed);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => backend.DiscoverAsync(default));
            sessions["a"].FailClose = false; await backend.DisposeAsync(); await backend.DisposeAsync();
            Assert.Equal(2, sessions["a"].Closes); Assert.Equal(1, sessions["b"].Closes);
        }
        finally { sessions["a"].FailClose = false; }
    }

    [Fact]
    public async Task ExchangeFailureRetainsTheOldHandleAndPreventsReplacementUntilCleanupSucceeds()
    {
        var sessions = new List<ClosingSession>();
        await using var backend = new AsrockHidDeviceBackend(() => [new("a", null)], _ =>
        { var session = new ClosingSession(); sessions.Add(session); return session; }, () => { });
        var first = Assert.Single(await backend.DiscoverAsync(default)); using var old = backend.Borrow(first);
        sessions[0].FailExchange = sessions[0].FailClose = true;
        try
        {
            var failure = Assert.Throws<AggregateException>(() => old.Exchange(AsrockRgbMonitor.Query(0x14, 1)));
            Assert.Contains("exchange failed", failure.ToString()); Assert.Contains("session close failed", failure.ToString());
            Assert.Throws<IOException>(() => backend.Check(first));
            await Assert.ThrowsAsync<AggregateException>(() => backend.DiscoverAsync(default)); Assert.Single(sessions);
            sessions[0].FailClose = false;
            var next = Assert.Single(await backend.DiscoverAsync(default)); Assert.NotEqual(first.ConnectionId, next.ConnectionId);
            Assert.True(sessions[0].Inner.Disposed); Assert.Equal(3, sessions[0].Closes); Assert.Equal(2, sessions.Count);
            Assert.Throws<IOException>(() => old.Exchange(AsrockRgbMonitor.Query(0x14, 1)));
            using var current = backend.Borrow(next); current.Exchange(AsrockRgbMonitor.Query(0x14, 1));
        }
        finally { sessions[0].FailClose = false; }
    }

    [Fact]
    public async Task ADisappearingPathCannotBeReopenedWhileItsRetiredSessionRemainsOpen()
    {
        Detected[] inventory = [new("a", null), new("b", null)]; var sessions = new List<ClosingSession>();
        await using var backend = new AsrockHidDeviceBackend(() => inventory, _ =>
        { var session = new ClosingSession(); sessions.Add(session); return session; }, () => { });
        var first = (await backend.DiscoverAsync(default)).Single(c => c.Path == "a"); sessions[0].FailClose = true;
        try
        {
            inventory = [new("b", null)];
            await Assert.ThrowsAsync<AggregateException>(() => backend.DiscoverAsync(default));
            Assert.Throws<IOException>(() => backend.Check(first)); Assert.False(sessions[1].Inner.Disposed);
            inventory = [new("a", null), new("b", null)];
            await Assert.ThrowsAsync<AggregateException>(() => backend.DiscoverAsync(default)); Assert.Equal(2, sessions.Count);
            sessions[0].FailClose = false;
            var next = (await backend.DiscoverAsync(default)).Single(c => c.Path == "a");
            Assert.NotEqual(first.ConnectionId, next.ConnectionId); Assert.Equal(3, sessions.Count); Assert.True(sessions[0].Inner.Disposed);
        }
        finally { sessions[0].FailClose = false; }
    }

    [Fact]
    public async Task ConstructorCleanupFailureIsAdoptedByTheBackendInsteadOfLosingItsSession()
    {
        var sessions = new List<ClosingSession>();
        await using var backend = new AsrockHidDeviceBackend(() => [new("a", null)], _ =>
        {
            var session = new ClosingSession(); sessions.Add(session);
            if (sessions.Count > 1) return session;
            session.FailClose = true;
            try { session.Dispose(); }
            catch (Exception closing) { throw new AsrockSessionOpenException(new IOException("open failed"), closing, session); }
            throw new InvalidOperationException("The injected close must fail");
        }, () => { });
        await Assert.ThrowsAsync<AsrockSessionOpenException>(() => backend.DiscoverAsync(default));
        try
        {
            await Assert.ThrowsAsync<AggregateException>(() => backend.DiscoverAsync(default)); Assert.Single(sessions);
            Assert.Equal(2, sessions[0].Closes);
            sessions[0].FailClose = false; await backend.DiscoverAsync(default);
            Assert.True(sessions[0].Inner.Disposed); Assert.Equal(2, sessions.Count);
        }
        finally { sessions[0].FailClose = false; }
    }

    [Fact]
    public async Task ShutdownWaitsForTheCurrentExchangeBeforeClosingItsSession()
    {
        ClosingSession? session = null; var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        await using var backend = new AsrockHidDeviceBackend(() => [new("a", null)], _ => session = new(), () => { });
        var connection = Assert.Single(await backend.DiscoverAsync(default)); using var borrowed = backend.Borrow(connection);
        session!.Exchanging = () => { entered.TrySetResult(); Assert.True(resume.Wait(TimeSpan.FromSeconds(5))); };
        var exchange = Task.Run(() => borrowed.Exchange(AsrockRgbMonitor.Query(0x14, 1)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var closing = backend.DisposeAsync().AsTask();
        try
        {
            Assert.False(closing.IsCompleted); Assert.Equal(0, session.Closes);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => backend.DiscoverAsync(default));
        }
        finally { resume.Set(); }
        await exchange; await closing.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, session.Closes);
    }

    [Fact]
    public async Task ShutdownRequestedFromAnExchangeCallbackIsQueuedUntilThatExchangeReturns()
    {
        ClosingSession? session = null; Task? closing = null;
        await using var backend = new AsrockHidDeviceBackend(() => [new("a", null)], _ => session = new(), () => { });
        var connection = Assert.Single(await backend.DiscoverAsync(default)); using var borrowed = backend.Borrow(connection);
        session!.Exchanging = () =>
        {
            closing = backend.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted); Assert.Equal(0, session.Closes);
        };
        await Task.Run(() => borrowed.Exchange(AsrockRgbMonitor.Query(0x14, 1))).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(closing); await closing.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, session.Closes);
    }

    private sealed class ClosingTransport : IDisposable
    {
        private readonly int owner = Environment.CurrentManagedThreadId;
        public volatile bool FailClose = true;
        public int Closes;
        public void Dispose()
        {
            Assert.Equal(owner, Environment.CurrentManagedThreadId); Closes++;
            if (FailClose) throw new IOException("transport close failed");
        }
    }

    [Fact]
    public async Task NativeSessionKeepsItsMutexWhenTransportDisposalFailsAndRetriesOnTheOwnerThread()
    {
        var mutexName = "FlowDeck.Asrock.Test." + Guid.NewGuid().ToString("N"); ClosingTransport? transport = null;
        await using var backend = new AsrockHidDeviceBackend(() => [new("a", null)], _ =>
        {
            transport = new(); var ownership = new Mutex(true, mutexName, out var created); Assert.True(created);
            return new AsrockRgbSession(transport, ownership);
        }, () => { });
        await backend.DiscoverAsync(default); using var contender = new Mutex(false, mutexName);
        static void Check(Mutex contender, bool allowed)
        { var acquired = contender.WaitOne(0); try { Assert.Equal(allowed, acquired); } finally { if (acquired) contender.ReleaseMutex(); } }
        try
        {
            await Assert.ThrowsAsync<AggregateException>(() => backend.DisposeAsync().AsTask());
            await Task.Run(() => Check(contender, false)); Assert.Equal(1, transport!.Closes);
            transport.FailClose = false; await backend.DisposeAsync();
            await Task.Run(() => Check(contender, true)); Assert.Equal(2, transport.Closes);
            await backend.DisposeAsync(); Assert.Equal(2, transport.Closes);
        }
        finally { transport!.FailClose = false; }
    }
}
