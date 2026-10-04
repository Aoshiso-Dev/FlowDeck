using System.Text.Json;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceM5SessionTests
{
    [Fact] public async Task FailedExitRetainsTheLinkAndExplicitRetryClosesItWithoutChangingPcCooling()
    {
        await using var f = new Fixture(); await f.Initialize(); await f.Fan(55); await f.Session.ConnectAsync();
        var link = Assert.Single(f.Factory.Links); var writes = f.Demo.Hubs[0].Writes;
        f.Factory.BeforeDispose = () => throw new IOException("demo close failure");
        await Assert.ThrowsAsync<IOException>(() => f.Session.DisposeAsync().AsTask());
        Assert.True(link.Closed); Assert.False(link.Disposed); Assert.True(f.Session.State.Running);
        Assert.False(f.Session.State.Wanted); Assert.Contains("demo close failure", f.Session.State.Message);
        await f.Session.PollAsync(); await f.Session.StartupAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => f.Session.ConnectAsync());
        foreach (var route in new[] { "usb", "wifi" })
            link.Router.Receive(route, JsonSerializer.Serialize(new { v = 1, type = "hello", deviceType = "M5GO",
                deviceId = f.Factory.DeviceId, bootId = "late", firmwareVersion = "1.3.0" }));
        Assert.False(link.Router.Protocol.Connected);
        f.Factory.BeforeDispose = null; await f.Session.DisposeAsync(); await f.Session.DisposeAsync();
        Assert.True(link.Disposed); Assert.Equal(2, link.DisposeCalls); Assert.Single(f.Factory.Links);
        Assert.False(f.Session.State.Running); Assert.False(f.Session.State.Link.Connected);
        Assert.Equal(writes, f.Demo.Hubs[0].Writes); Assert.Equal(55, (await f.Demo.Hubs[0].ReadAsync()).Channels[0].Duty);
    }

    [Fact] public async Task ConcurrentExitSharesItsAttemptAndLaterRetryRunsOnlyOnce()
    {
        await using var f = new Fixture(); await f.Initialize(); await f.Session.ConnectAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Factory.BeforeDispose = async () => { entered.TrySetResult(); await finish.Task; };
        var first = f.Session.DisposeAsync().AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var second = f.Session.DisposeAsync().AsTask(); Assert.Same(first, second);
            Assert.Equal(1, f.Factory.Links[0].DisposeCalls);
        }
        finally { finish.TrySetException(new IOException("close interrupted")); }
        await Assert.ThrowsAsync<IOException>(() => first);
        f.Factory.BeforeDispose = null; await f.Session.DisposeAsync();
        Assert.Equal(2, f.Factory.Links[0].DisposeCalls); Assert.True(f.Factory.Links[0].Disposed);
    }

    [Fact] public async Task FailedDisconnectCannotCreateAReplacementUntilTheOldLinkCloses()
    {
        await using var f = new Fixture(); await f.Initialize(); await f.Session.ConnectAsync();
        var old = Assert.Single(f.Factory.Links);
        f.Factory.BeforeDispose = () => throw new IOException("still open");
        await Assert.ThrowsAsync<IOException>(() => f.Session.DisconnectAsync());
        Assert.False(f.Session.State.Wanted); Assert.False(f.Session.State.Link.Connected); Assert.True(f.Session.State.Running);
        await f.Session.PollAsync(); Assert.Single(f.Factory.Links);
        await Assert.ThrowsAsync<IOException>(() => f.Session.ConnectAsync()); Assert.Single(f.Factory.Links);
        f.Factory.BeforeDispose = null; await f.Session.ConnectAsync();
        Assert.True(old.Disposed); Assert.Equal(2, f.Factory.Links.Count); Assert.True(f.Session.State.Link.Connected);
        Assert.All(f.Demo.Hubs, hub => Assert.Equal(0, hub.Writes));
    }

    [Fact] public async Task InterruptedExitIsRetryableButNeverAllowsAReopenedSession()
    {
        await using var f = new Fixture(); await f.Initialize(); await f.Session.ConnectAsync();
        f.Factory.BeforeDispose = () => Task.FromCanceled(new CancellationToken(true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Session.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => f.Session.ConnectAsync());
        f.Factory.BeforeDispose = null; await f.Session.DisposeAsync();
        Assert.True(f.Factory.Links[0].Disposed); Assert.Single(f.Factory.Links);
    }
}

public class M5ShutdownTests
{
    [Fact] public async Task SynchronousReentrantCloseObservesThePublishedAttempt()
    {
        var shutdown = new M5Shutdown(); Task? reentrant = null; var calls = 0;
        var first = shutdown.Run(() => { calls++; reentrant = shutdown.Run(() => throw new InvalidOperationException()); return Task.CompletedTask; });
        await first; Assert.Same(first, reentrant); Assert.Equal(1, calls); Assert.True(shutdown.Requested);
        Assert.Same(first, shutdown.Run(() => throw new InvalidOperationException()));
    }
}
