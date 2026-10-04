using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class OpenRgbDeviceDriverTests
{
    private static async Task SetForShutdown(OpenRgbDeviceDriver driver, string endpoint = "device")
    {
        var result = await driver.ApplyAsync(new(endpoint, DeviceOperation.SetLighting, Lighting: Blue), default);
        Assert.Equal(Outcome.WrittenUnverified, result.Outcome);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DriverShutdownRetainsFailedRestorationAndCanRetryEvenAfterThePermitAges(bool agePermit)
    {
        await using var rig = await new Rig().Open(); var driver = rig.Driver(); var original = OpenRgbLightingPlan.State(rig.Server.Devices[41]);
        await SetForShutdown(driver); rig.Server.IgnoreWriteNumber = rig.Server.WriteCount + 1;
        if (agePermit) rig.EnvironmentClock.Advance(5);
        await Assert.ThrowsAnyAsync<IOException>(() => driver.DisposeAsync().AsTask());
        Assert.False(driver.ResourcesClosed); Assert.True(rig.Connection!.IsConnected); Assert.NotEmpty(System.IO.Directory.GetFiles(rig.Directory));
        Assert.NotNull(driver.Validate(new("device", DeviceOperation.SetLighting, Lighting: Blue)));
        var writes = rig.Server.WriteCount;
        Assert.Equal(Outcome.Rejected, (await driver.ApplyAsync(new("device", DeviceOperation.SetLighting, Lighting: Blue), default)).Outcome);
        Assert.Equal(writes, rig.Server.WriteCount);
        rig.Server.IgnoreWriteNumber = 0; rig.EnvironmentClock.Advance(5);
        await driver.DisposeAsync(); Assert.True(driver.ResourcesClosed); Assert.Null(driver.Observe());
        Assert.True(OpenRgbLightingPlan.Same(original, OpenRgbLightingPlan.State(rig.Server.Devices[41])));
        Assert.Empty(System.IO.Directory.GetFiles(rig.Directory));
        writes = rig.Server.WriteCount; await driver.DisposeAsync(); Assert.Equal(writes, rig.Server.WriteCount);
    }
    [Fact] public async Task ProviderShutdownTriesEveryDeviceAndKeepsTheConnectionUntilFailedRecoveryCanFinish()
    {
        await using var rig = await new Rig(Device(), Device(700, "FD-UNIT-002")).Open();
        var a = rig.Driver(); var b = rig.Driver(700); var originalB = OpenRgbLightingPlan.State(rig.Server.Devices[700]);
        await SetForShutdown(a); await SetForShutdown(b);
        rig.Server.IgnoreWriteNumber = rig.Server.WriteCount + 1;
        await Assert.ThrowsAsync<AggregateException>(() => rig.Provider.DisposeAsync().AsTask());
        Assert.False(a.ResourcesClosed); Assert.True(b.ResourcesClosed); Assert.False(rig.Provider.ResourcesClosed); Assert.True(rig.Connection!.IsConnected);
        Assert.True(OpenRgbLightingPlan.Same(originalB, OpenRgbLightingPlan.State(rig.Server.Devices[700])));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => rig.Provider.PollAsync(default));
        Assert.NotNull(a.BindingCandidate); // A rejected post-shutdown poll must not discard the recovery connection.
        rig.Server.IgnoreWriteNumber = 0; rig.EnvironmentClock.Advance(5);
        await Task.WhenAll(rig.Provider.DisposeAsync().AsTask(), rig.Provider.DisposeAsync().AsTask());
        Assert.True(a.ResourcesClosed); Assert.True(rig.Provider.ResourcesClosed); Assert.False(rig.Connection.IsConnected);
        Assert.Empty(System.IO.Directory.GetFiles(rig.Directory));
    }
    [Fact] public async Task ProviderShutdownWithoutDiscoveryDoesNotOpenAnSdkConnection()
    {
        using var directory = new ServiceTestDirectory(); var connections = 0;
        await using var provider = new OpenRgbDeviceProvider(Installation, directory.Path, Boot,
            _ => { connections++; throw new InvalidOperationException("Shutdown must stay inert"); }, new TestRoutePolicy(() => { }), []);
        await provider.DisposeAsync(); await provider.DisposeAsync();
        Assert.True(provider.ResourcesClosed); Assert.Equal(0, connections);
    }
    [Fact] public async Task DriverShutdownDoesNotAdoptChangedSdkSettingsForRecovery()
    {
        await using var rig = await new Rig().Open(); var driver = rig.Driver(); await SetForShutdown(driver);
        var settings = rig.Server.Drivers; rig.Server.Drivers = "{\"shared_smbus_access\":true,\"smbus_sleep_mode\":2,\"changed\":true}";
        var writes = rig.Server.WriteCount;
        await Assert.ThrowsAnyAsync<IOException>(() => driver.DisposeAsync().AsTask());
        Assert.False(driver.ResourcesClosed); Assert.Equal(writes, rig.Server.WriteCount); Assert.NotEmpty(System.IO.Directory.GetFiles(rig.Directory));
        rig.Server.Drivers = settings; await driver.DisposeAsync(); Assert.True(driver.ResourcesClosed);
    }
    [Fact] public async Task ConcurrentShutdownWaitsForAnAlreadyStartedWriteThenRestoresExactlyOnce()
    {
        await using var rig = await new Rig().Open(); var driver = rig.Driver(); var original = OpenRgbLightingPlan.State(rig.Server.Devices[41]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Server.BeforeWrite = async _ => { entered.TrySetResult(); await finish.Task; };
        var write = SetForShutdown(driver); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var closeA = driver.DisposeAsync().AsTask(); var closeB = driver.DisposeAsync().AsTask();
        try
        {
            Assert.False(closeA.IsCompleted); Assert.False(closeB.IsCompleted);
            Assert.NotNull(driver.Validate(new("device", DeviceOperation.SetLighting, Lighting: Blue)));
        }
        finally { rig.Server.BeforeWrite = null; finish.TrySetResult(); }
        await Task.WhenAll(write, closeA, closeB).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(driver.ResourcesClosed); Assert.Empty(System.IO.Directory.GetFiles(rig.Directory));
        Assert.True(OpenRgbLightingPlan.Same(original, OpenRgbLightingPlan.State(rig.Server.Devices[41])));
        var writes = rig.Server.WriteCount; await driver.DisposeAsync(); Assert.Equal(writes, rig.Server.WriteCount);
    }
    [Fact] public async Task UnsentStreamShutdownDoesNotReadTheSdkOrInventAnUnfinishedOperation()
    {
        await using var rig = await new Rig().Open(); var driver = rig.Driver();
        var stream = await driver.OpenLightingStreamAsync([rig.Zone(0)], Animation, default); var requests = rig.Server.Wire.Requests.Count;
        await driver.DisposeAsync(); Assert.True(driver.ResourcesClosed); Assert.Equal(requests, rig.Server.Wire.Requests.Count);
        Assert.False(System.IO.Directory.Exists(rig.Directory));
        var result = await stream.WriteAsync(new(1, new(0, 0, 255), () => new(ValidFor: TimeSpan.FromMilliseconds(500))), default);
        Assert.All(result, r => Assert.Equal(Outcome.Rejected, r.Result.Outcome)); Assert.Equal(0, rig.Server.WriteCount);
    }
    [Fact] public async Task RuntimeShutdownCannotReleaseTheOwningProviderUntilTheSdkDriverHasRecovered()
    {
        await using var rig = await new Rig().Open(); var driver = rig.Driver();
        Written(await rig.Command(DeviceOperation.SetLighting, [rig.Target("device")], Blue));
        rig.RouteAvailable = false;
        await Assert.ThrowsAsync<AggregateException>(() => rig.Runtime.DisposeAsync().AsTask());
        Assert.False(rig.Runtime.ResourcesClosed); Assert.False(driver.ResourcesClosed); Assert.False(rig.Provider.ResourcesClosed);
        Assert.True(rig.Connection!.IsConnected); Assert.NotEmpty(System.IO.Directory.GetFiles(rig.Directory));
        rig.RouteAvailable = true; rig.EnvironmentClock.Advance(5);
        await rig.Runtime.DisposeAsync();
        Assert.True(driver.ResourcesClosed); Assert.True(rig.Provider.ResourcesClosed); Assert.True(rig.Runtime.ResourcesClosed);
        Assert.False(rig.Connection.IsConnected); Assert.Empty(System.IO.Directory.GetFiles(rig.Directory));
    }
}
