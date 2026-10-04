using FlowDeck.Core;

namespace FlowDeck.Tests;

public sealed partial class DeviceProviderReplacementTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ShutdownRetriesOnlyFailedHandlesAndKeepsTheirParentTransport(bool providerFails)
    {
        await using var r = new Rig();
        r.Direct.FailDispose = providerFails; r.Direct.Driver.FailDispose = !providerFails;
        try
        {
            await Assert.ThrowsAsync<AggregateException>(() => r.Runtime.DisposeAsync().AsTask());
            Assert.False(r.Runtime.ResourcesClosed);
            Assert.Equal(providerFails ? 1 : 0, r.Direct.Disposes);
            Assert.True(r.Independent.Disposed); Assert.True(r.Independent.Driver.Disposed);
            var driverObservations = r.Independent.Driver.Observes; var enumerations = r.Independent.Enumerations;
            var releases = r.Direct.Driver.Releases;
            r.Direct.FailDispose = r.Direct.Driver.FailDispose = false;
            await r.Runtime.DisposeAsync();
            Assert.True(r.Runtime.ResourcesClosed); Assert.Null(r.Runtime.State().LifecycleError);
            Assert.Equal(releases, r.Direct.Driver.Releases);
            Assert.Equal(providerFails ? 1 : 2, r.Direct.Driver.Disposes);
            Assert.Equal(providerFails ? 2 : 1, r.Direct.Disposes);
            Assert.Equal(1, r.Independent.Disposes); Assert.Equal(1, r.Independent.Driver.Disposes);
            Assert.Equal(driverObservations, r.Independent.Driver.Observes); Assert.Equal(enumerations, r.Independent.Enumerations);
            await r.Runtime.DisposeAsync(); Assert.Equal(1, r.Independent.Disposes);
        }
        finally { r.Direct.FailDispose = r.Direct.Driver.FailDispose = false; }
    }

    [Fact]
    public async Task ConcurrentShutdownSharesItsAttemptAndFailureBeforeAnExplicitRetry()
    {
        await using var r = new Rig(); var entered = new TaskCompletionSource(); var resume = new TaskCompletionSource();
        r.Direct.Driver.FailDispose = true;
        r.Direct.Driver.Closing = async () => { entered.TrySetResult(); await resume.Task; };
        var first = r.Runtime.DisposeAsync().AsTask(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = r.Runtime.DisposeAsync().AsTask();
        try { Assert.Same(first, second); Assert.False(first.IsCompleted); Assert.Equal(0, r.Direct.Disposes); }
        finally { resume.TrySetResult(); }
        try
        {
            await Assert.ThrowsAsync<AggregateException>(() => first);
            await Assert.ThrowsAsync<AggregateException>(() => second);
            Assert.Equal(1, r.Direct.Driver.Disposes);
        }
        finally { r.Direct.Driver.FailDispose = false; }
        await r.Runtime.DisposeAsync(); Assert.True(r.Runtime.ResourcesClosed); Assert.Equal(2, r.Direct.Driver.Disposes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ShutdownWaitsForAnInFlightPollEvenIfItsFinalReadFails(bool failPoll)
    {
        await using var r = new Rig(); var entered = new TaskCompletionSource(); var resume = new TaskCompletionSource();
        r.Direct.Reading = async () => { entered.TrySetResult(); await resume.Task; };
        r.Direct.FailPoll = failPoll;
        var polling = r.Runtime.PollAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = r.Runtime.DisposeAsync().AsTask();
        try
        {
            Assert.False(closing.IsCompleted); Assert.False(r.Runtime.ResourcesClosed);
            Assert.Equal(0, r.Direct.Disposes); Assert.Equal(0, r.Direct.Driver.Disposes);
            var reads = r.Direct.Polls; await r.Runtime.PollAsync(); Assert.Equal(reads, r.Direct.Polls);
        }
        finally { resume.TrySetResult(); }
        await polling; await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(r.Runtime.ResourcesClosed); Assert.True(r.Direct.Disposed);
    }

    [Fact]
    public async Task ShutdownReleasesADeviceDiscoveredAfterItsFirstReleaseSnapshot()
    {
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var provider = new Provider("late", "device", events) { HideDriver = true };
        var entered = new TaskCompletionSource(); var resume = new TaskCompletionSource();
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var anchor = new Driver("anchor", "test", events)
        { RejectRelease = reason => { if (reason == DeviceReleaseReason.ServiceStopping) firstRelease.TrySetResult(); return false; } };
        provider.Reading = async () => { entered.TrySetResult(); await resume.Task; provider.HideDriver = false; };
        await using var runtime = new DeviceControlRuntime([anchor], ["user"], new TestLog(), new TestClock(), providers: [provider]);
        var polling = runtime.PollAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = runtime.DisposeAsync().AsTask();
        try
        {
            await firstRelease.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(closing.IsCompleted); Assert.Equal(0, provider.Disposes);
        }
        finally { resume.TrySetResult(); }
        await polling; await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(runtime.ResourcesClosed); Assert.Equal(1, provider.Driver.Releases);
        var history = events.ToArray(); Assert.True(Array.IndexOf(history, "device:release") < Array.IndexOf(history, "device:closed"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ShutdownRetainsRetiredDependenciesAndRetriesThemBeforeReportingClosed(bool providerFails)
    {
        await using var r = new Rig(); r.Direct.FailDispose = providerFails; r.Direct.Driver.FailDispose = !providerFails;
        try
        {
            await Assert.ThrowsAsync<IOException>(() => r.Change(["direct"], () => [r.New()]));
            await Assert.ThrowsAsync<AggregateException>(() => r.Runtime.DisposeAsync().AsTask());
            Assert.False(r.Runtime.ResourcesClosed);
            Assert.Equal(providerFails ? 2 : 0, r.Direct.Disposes);
            Assert.True(r.Independent.Disposed);
        }
        finally { r.Direct.FailDispose = r.Direct.Driver.FailDispose = false; }
        await r.Runtime.DisposeAsync(); Assert.True(r.Runtime.ResourcesClosed);
        Assert.True(r.Direct.Disposed); Assert.True(r.Direct.Driver.Disposed);
        Assert.Equal(1, r.Independent.Disposes);
    }

    private sealed class ShutdownConfiguration(bool throwsOnCancellation = false) : IDeviceConfigurationHandler
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Reconfigure;
        public async Task<DeviceConfigurationReply> ExecuteAsync(DeviceConfigurationRequest request, string requestId, string owner,
            DeviceControlRuntime runtime, CancellationToken ct)
        {
            using var registration = throwsOnCancellation ? ct.Register(() => throw new IOException("cancel callback failed")) : default;
            if (Reconfigure)
                await runtime.ReconfigureAsync(owner, runtime.State().Inventory.TopologyRevision, async (_, _) =>
                { Entered.TrySetResult(); await Continue.Task; return []; });
            else { Entered.TrySetResult(); await Continue.Task; }
            return new(Outcome.Confirmed, "done", new('a', 32));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ShutdownWaitsForConfigurationRequestsWithoutIncludingTheirOwnTaskInReconfiguration(bool reconfigure)
    {
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>(); var driver = new Driver("device", "test", events);
        var handler = new ShutdownConfiguration { Reconfigure = reconfigure };
        await using var runtime = new DeviceControlRuntime([driver], ["user"], new TestLog(), new TestClock());
        runtime.InstallConfigurationHandler(handler); var hello = runtime.OpenSession("user");
        var request = new DeviceServiceRequest(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "test",
            DeviceOperation.Configuration, Configuration: new(DeviceConfigurationOperation.Snapshot));
        var configuring = runtime.ExecuteAsync(request);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = runtime.DisposeAsync().AsTask();
        try { Assert.False(closing.IsCompleted); Assert.Equal(0, driver.Disposes); }
        finally { handler.Continue.TrySetResult(); }
        await configuring.WaitAsync(TimeSpan.FromSeconds(5)); await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(runtime.ResourcesClosed); Assert.True(driver.Disposed);
    }

    [Fact]
    public async Task FailedSessionCancellationReturnsAnErrorAndStillReleasesDevices()
    {
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>(); var driver = new Driver("device", "test", events);
        var handler = new ShutdownConfiguration(throwsOnCancellation: true);
        await using var runtime = new DeviceControlRuntime([driver], ["user"], new TestLog(), new TestClock());
        runtime.InstallConfigurationHandler(handler); var hello = runtime.OpenSession("user");
        var configuring = runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "test",
            DeviceOperation.Configuration, Configuration: new(DeviceConfigurationOperation.Snapshot)));
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var reply = await runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 2, "test",
                DeviceOperation.ReleaseSession)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(reply.Success); Assert.Contains("cancel callback failed", reply.Error);
            Assert.Equal(1, driver.Releases);
            Assert.NotNull(runtime.OpenSession("user")); // Failure did not leave releasing permanently set.
        }
        finally { handler.Continue.TrySetResult(); await configuring; }
    }
}
