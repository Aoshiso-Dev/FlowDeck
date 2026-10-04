using System.Reflection;
using System.Security.Principal;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class DeviceProviderReplacementTests
{
    private static ConfigurationGenerationStore StartupStore(ServiceTestDirectory directory)
    {
        var store = DeviceConfigurationCodec.Store(directory.Path);
        store.Activate(store.Stage(DeviceConfigurationCodec.Write(ServiceRuntimeBootstrap.MonitoringConfiguration(JournalRig.Installation))));
        return store;
    }
    private static IEnumerable<Exception> Causes(Exception error) => new[] { error }.Concat(error is AggregateException aggregate
        ? aggregate.InnerExceptions.SelectMany(Causes) : error.InnerException is { } inner ? Causes(inner) : []);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task InvalidRuntimeRegistrationRetainsFailedChildrenAndClosesIndependentFactories(bool getterFails)
    {
        using var directory = new ServiceTestDirectory(); var store = StartupStore(directory);
        var first = new Provider("same", "first", new()) { FailEnumeration = getterFails };
        var other = new Provider("same", "other", new()); // Duplicate provider IDs fail before runtime ownership transfers.
        first.Driver.FailDispose = !getterFails;
        var error = await Assert.ThrowsAsync<DeviceStartupException>(() => DeviceRuntimeStartup.CreateAsync(store, JournalRig.Installation,
            () => new([], [first, other]), ["user"], new TestLog(), demo: true));
        Assert.Contains(Causes(error), e => e is ArgumentException);
        Assert.False(error.ResourcesClosed); Assert.False(first.Disposed); Assert.True(other.Disposed); Assert.True(other.Driver.Disposed);
        var reads = other.Enumerations;
        await Assert.ThrowsAsync<AggregateException>(() => error.DisposeAsync().AsTask());
        Assert.Equal(1, other.Disposes); Assert.Equal(1, other.Driver.Disposes); Assert.Equal(reads, other.Enumerations);
        first.FailEnumeration = first.Driver.FailDispose = false;
        await error.DisposeAsync(); Assert.True(error.ResourcesClosed); Assert.True(first.Disposed); Assert.True(first.Driver.Disposed);
        await error.DisposeAsync(); Assert.Equal(1, first.Disposes);
    }

    [Fact]
    public async Task SuccessfulCleanupPreservesTheOriginalConstructorFailure()
    {
        using var directory = new ServiceTestDirectory(); var store = StartupStore(directory);
        var first = new Provider("same", "first", new()); var other = new Provider("same", "other", new());
        await Assert.ThrowsAsync<ArgumentException>(() => DeviceRuntimeStartup.CreateAsync(store, JournalRig.Installation,
            () => new([], [first, other]), ["user"], new TestLog(), demo: true));
        Assert.True(first.Disposed); Assert.True(other.Disposed);
    }

    [Fact]
    public async Task CancelledColdStartupKeepsTheRuntimeUntilDriverClosureSucceeds()
    {
        using var directory = new ServiceTestDirectory(); var store = StartupStore(directory); using var cancel = new CancellationTokenSource();
        var provider = new Provider("test", "first", new()); provider.Driver.FailDispose = true;
        var error = await Assert.ThrowsAsync<DeviceStartupException>(() => DeviceRuntimeStartup.CreateAsync(store, JournalRig.Installation,
            () => { cancel.Cancel(); return new([], [provider]); }, ["user"], new TestLog(), demo: true, ct: cancel.Token));
        Assert.Contains(Causes(error), e => e is OperationCanceledException); Assert.False(error.ResourcesClosed);
        Assert.False(provider.Disposed); Assert.Equal(0, provider.Driver.Writes);
        provider.Driver.FailDispose = false; await error.DisposeAsync(); Assert.True(error.ResourcesClosed); Assert.True(provider.Disposed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PackagedConstructionTracksEarlierAndNestedPartialFactoryResults(bool nestedFailure)
    {
        using var directory = new ServiceTestDirectory(); var store = StartupStore(directory);
        var first = new Provider("first", "first", new()); var partial = new Provider("partial", "partial", new()) { FailDispose = true };
        var laterCalled = false;
        var error = await Assert.ThrowsAsync<DeviceStartupException>(() => DeviceRuntimeStartup.CreateAsync(store, JournalRig.Installation,
            () => DeviceRuntimeComponents.ConstructProviders(() => first,
                () => throw (nestedFailure ? new DeviceStartupException(new IOException("nested factory failed"), partial) : new IOException("factory failed")),
                () => { laterCalled = true; return new Provider("late", "late", new()); }), ["user"], new TestLog(), demo: true));
        Assert.True(first.Disposed); Assert.True(first.Driver.Disposed); Assert.False(laterCalled);
        Assert.Equal(!nestedFailure, error.ResourcesClosed);
        if (nestedFailure)
        {
            Assert.False(partial.Disposed); partial.FailDispose = false; await error.DisposeAsync(); Assert.True(partial.Disposed);
        }
        Assert.Equal(1, first.Disposes); Assert.True(error.ResourcesClosed);
    }

    [Fact]
    public async Task FailedPipeStartRetainsItsRuntimeAndPreservesTheTransportFailure()
    {
        using var directory = new ServiceTestDirectory(); var store = StartupStore(directory);
        using var user = WindowsIdentity.GetCurrent(); var sid = user.User!.Value;
        var provider = new Provider("test", "first", new());
        var startup = await DeviceRuntimeStartup.CreateAsync(store, JournalRig.Installation, () => new([], [provider]), [sid], new TestLog(), demo: true);
        provider.Driver.FailDispose = true;
        var error = await Assert.ThrowsAsync<DeviceStartupException>(() => DeviceServiceHost.StartAsync(startup, [sid], new TestLog(), pipe: ""));
        Assert.Contains(Causes(error), e => e is ArgumentException); Assert.False(error.ResourcesClosed); Assert.False(startup.Runtime.ResourcesClosed);
        provider.Driver.FailDispose = false;
        var closing = await Assert.ThrowsAsync<AggregateException>(() => error.DisposeAsync().AsTask());
        Assert.Contains(Causes(closing), e => e is ArgumentException);
        Assert.True(error.ResourcesClosed); Assert.True(provider.Disposed);
        await error.DisposeAsync(); Assert.Equal(1, provider.Disposes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedResumeMustCloseItsPartialFactoryBeforeCallingANewFactory(bool initialFailure)
    {
        await using var f = new ShutdownHostFixture(); var partial = new Provider("partial", "partial", new()) { FailDispose = true };
        var calls = 0;
        Task<DeviceServiceHost> Start()
        {
            calls++;
            if (calls == (initialFailure ? 1 : 2))
                throw new DeviceStartupException(new IOException("factory failed after allocation"), partial);
            return f.Start();
        }
        if (initialFailure)
        {
            var error = await Assert.ThrowsAsync<DeviceStartupException>(() => DeviceServiceLifecycle.StartAsync(Start, new TestLog()));
            Assert.False(error.ResourcesClosed); Assert.False(partial.Disposed); Assert.Equal(1, calls);
            partial.FailDispose = false; await error.DisposeAsync(); Assert.True(error.ResourcesClosed); Assert.True(partial.Disposed);
            return;
        }
        f.Lifecycle = await DeviceServiceLifecycle.StartAsync(Start, new TestLog()); await f.Lifecycle.PowerAsync(false);
        await Assert.ThrowsAsync<DeviceStartupException>(() => f.Lifecycle.PowerAsync(true)); Assert.False(f.Lifecycle.ResourcesClosed);
        await Assert.ThrowsAsync<IOException>(() => f.Lifecycle.PowerAsync(true)); Assert.Equal(2, calls); Assert.Null(f.Lifecycle.Runtime);
        partial.FailDispose = false; await f.Lifecycle.PowerAsync(true); Assert.Equal(3, calls); Assert.True(partial.Disposed);
    }

    private sealed class FailingStartupLog : IEventLog
    {
        public void Write(string eventName, object? data) => throw new IOException("log unavailable");
    }

    [Fact]
    public async Task LoggingFailureCannotDiscardTheFailedFactoryOwner()
    {
        var partial = new Provider("partial", "partial", new()) { FailDispose = true };
        var original = new DeviceStartupException(new IOException("original failure"), partial);
        var error = await Assert.ThrowsAsync<DeviceStartupException>(() => DeviceServiceLifecycle.StartAsync(() => throw original, new FailingStartupLog()));
        Assert.Contains(Causes(error), e => ReferenceEquals(e, original)); Assert.False(error.ResourcesClosed);
        partial.FailDispose = false; await error.DisposeAsync(); Assert.True(error.ResourcesClosed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RealServiceStartupFailureKeepsItsLeaseEvenWhenFailureLoggingAlsoFails(bool loggingFails)
    {
        var prefix = "FlowDeck.Service.Test." + Guid.NewGuid().ToString("N");
        await using var lease = await ServiceHardwareLease.AcquireAsync(prefix);
        var partial = new Provider("partial", "partial", new()) { FailDispose = true };
        var original = new DeviceStartupException(new IOException("allocated factory failure"), partial);
        var configuration = typeof(ServiceProcessTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var assembly = Assembly.LoadFrom(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/FlowDeck.Service/bin",
            configuration, "net10.0-windows/FlowDeck.Service.dll")));
        var method = assembly.GetType("FlowDeck.Service.DeviceServiceProcess", true)!.GetMethod("CloseFailedStartupAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        // This compiled production failure handler only sees a fake provider and test-prefixed lease, never StartRealAsync.
        var task = (Task)method.Invoke(null, [original, lease, loggingFails ? new FailingStartupLog() : new TestLog()])!;
        var error = await Assert.ThrowsAsync<DeviceStartupException>(() => task);
        Assert.False(error.ResourcesClosed);
        if (loggingFails) Assert.Contains(Causes(error), e => e.Message == "log unavailable");
        await Task.Run(() => { using var competing = new ApplicationInstance(false, prefix); Assert.False(competing.Acquired); });
        partial.FailDispose = false; await error.DisposeAsync(); Assert.True(error.ResourcesClosed);
        await Task.Run(() => { using var competing = new ApplicationInstance(false, prefix); Assert.True(competing.Acquired); });
    }

    [Fact]
    public async Task FailedRouteRecoveryReturnsItsCleanupAndNeverConstructsTheNextRoute()
    {
        await using var r = new Rig(); using var j = new JournalRig(); await j.Switch(r, () => [r.New()]); await r.DisposeAsync();
        var calls = 0; Provider? opened = null;
        var error = await Assert.ThrowsAsync<DeviceStartupException>(() => j.Startup(r, target =>
        {
            Assert.True(target); calls++; opened = r.New(); opened.Driver.FailDispose = true; return opened;
        }));
        Assert.Equal(1, calls); Assert.False(error.ResourcesClosed); Assert.False(opened!.Disposed);
        Assert.Equal(ProviderTransitionRecovery.TargetThenSource, j.Reload().Recovery());
        opened.Driver.FailDispose = false; await error.DisposeAsync(); Assert.True(error.ResourcesClosed); Assert.True(opened.Disposed);
        Assert.Equal(ProviderTransitionRecovery.TargetThenSource, j.Reload().Recovery());
    }
}
