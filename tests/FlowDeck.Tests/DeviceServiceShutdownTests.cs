using FlowDeck.Core;
using FlowDeck.Infrastructure;
using System.Security.Principal;
using System.Reflection;

namespace FlowDeck.Tests;

public sealed partial class DeviceProviderReplacementTests
{
    private sealed class ShutdownHostFixture : IAsyncDisposable
    {
        public readonly ServiceTestDirectory Directory = new();
        public readonly List<Provider> Opened = [];
        public readonly string Pipe = "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N");
        public DeviceServiceLifecycle? Lifecycle;
        public async Task<DeviceServiceHost> Start()
        {
            using var user = WindowsIdentity.GetCurrent(); var sid = user.User!.Value;
            var store = DeviceConfigurationCodec.Store(System.IO.Path.Combine(Directory.Path, "configuration"));
            if (store.ReadActive() is null)
                store.Activate(store.Stage(DeviceConfigurationCodec.Write(ServiceRuntimeBootstrap.MonitoringConfiguration(JournalRig.Installation))));
            var provider = new Provider("test", "device", new()); Opened.Add(provider);
            var startup = await DeviceRuntimeStartup.CreateAsync(store, JournalRig.Installation, () => new([], [provider]),
                [sid], new TestLog(), new TestClock(), demo: true);
            return await DeviceServiceHost.StartAsync(startup, [sid], new TestLog(), Pipe);
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var provider in Opened) provider.FailDispose = provider.Driver.FailDispose = false;
            try { if (Lifecycle is not null) await Lifecycle.DisposeAsync(); }
            finally { Directory.Dispose(); }
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ServiceHostRetriesFailedClosuresWithoutDisposingCompletedChildrenTwice(bool providerFails)
    {
        await using var f = new ShutdownHostFixture(); await using var host = await f.Start(); var provider = f.Opened[0];
        provider.FailDispose = providerFails; provider.Driver.FailDispose = !providerFails;
        try
        {
            await Assert.ThrowsAsync<AggregateException>(() => host.DisposeAsync().AsTask());
            Assert.True(host.Completion.IsCompleted); Assert.False(host.Runtime.ResourcesClosed);
        }
        finally { provider.FailDispose = provider.Driver.FailDispose = false; }
        await host.DisposeAsync(); Assert.True(host.Runtime.ResourcesClosed);
        Assert.Equal(providerFails ? 1 : 2, provider.Driver.Disposes);
        Assert.Equal(providerFails ? 2 : 1, provider.Disposes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ResumeCannotInvokeANewFactoryUntilTheSuspendedHostCloses(bool providerFails)
    {
        await using var f = new ShutdownHostFixture(); f.Lifecycle = await DeviceServiceLifecycle.StartAsync(f.Start, new TestLog());
        var runtime = f.Lifecycle.Runtime!; var provider = f.Opened[0];
        provider.FailDispose = providerFails; provider.Driver.FailDispose = !providerFails;
        await Assert.ThrowsAsync<AggregateException>(() => f.Lifecycle.PowerAsync(false));
        Assert.Null(f.Lifecycle.Runtime); Assert.False(f.Lifecycle.Completion.IsCompleted);
        await Assert.ThrowsAsync<AggregateException>(() => f.Lifecycle.PowerAsync(true));
        Assert.Single(f.Opened); Assert.False(runtime.ResourcesClosed);
        provider.FailDispose = provider.Driver.FailDispose = false;
        await f.Lifecycle.PowerAsync(true);
        Assert.Equal(2, f.Opened.Count); Assert.True(runtime.ResourcesClosed);
        Assert.NotEqual(runtime.Epoch, f.Lifecycle.Runtime!.Epoch);
        Assert.Equal(0, f.Opened[1].Driver.Writes);
    }

    [Fact]
    public async Task FailedServiceStopRemainsRetryableAndDoesNotPermitPowerResume()
    {
        await using var f = new ShutdownHostFixture(); f.Lifecycle = await DeviceServiceLifecycle.StartAsync(f.Start, new TestLog());
        var runtime = f.Lifecycle.Runtime!; f.Opened[0].Driver.FailDispose = true;
        await Assert.ThrowsAsync<AggregateException>(() => f.Lifecycle.DisposeAsync().AsTask());
        Assert.False(runtime.ResourcesClosed); Assert.False(f.Lifecycle.Completion.IsCompleted);
        await f.Lifecycle.PowerAsync(true); Assert.Single(f.Opened); Assert.Null(f.Lifecycle.Runtime);
        f.Opened[0].Driver.FailDispose = false;
        await f.Lifecycle.DisposeAsync(); Assert.True(runtime.ResourcesClosed); Assert.True(f.Lifecycle.Completion.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ProcessKeepsHardwareOwnershipUntilFailedHandlesActuallyClose(bool providerFails)
    {
        await using var f = new ShutdownHostFixture(); f.Lifecycle = await DeviceServiceLifecycle.StartAsync(f.Start, new TestLog());
        var prefix = "FlowDeck.Service.Test." + Guid.NewGuid().ToString("N");
        await using var lease = await ServiceHardwareLease.AcquireAsync(prefix);
        var configuration = typeof(ServiceProcessTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var assembly = Assembly.LoadFrom(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/FlowDeck.Service/bin",
            configuration, "net10.0-windows/FlowDeck.Service.dll")));
        // Construct the real process owner around a demo lifecycle; StartRealAsync and hardware factories are never invoked.
        var process = (IAsyncDisposable)Activator.CreateInstance(assembly.GetType("FlowDeck.Service.DeviceServiceProcess", true)!, f.Lifecycle, lease)!;
        var provider = f.Opened[0]; provider.FailDispose = providerFails; provider.Driver.FailDispose = !providerFails;
        try
        {
            await Assert.ThrowsAsync<AggregateException>(() => process.DisposeAsync().AsTask());
            Assert.False(f.Lifecycle.ResourcesClosed);
            await Task.Run(() => { using var another = new ApplicationInstance(false, prefix); Assert.False(another.Acquired); });
        }
        finally { provider.FailDispose = provider.Driver.FailDispose = false; }
        await process.DisposeAsync(); Assert.True(f.Lifecycle.ResourcesClosed);
        await Task.Run(() => { using var another = new ApplicationInstance(false, prefix); Assert.True(another.Acquired); });
    }
}
