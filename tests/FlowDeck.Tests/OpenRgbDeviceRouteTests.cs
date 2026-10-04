using System.Text;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class OpenRgbDeviceDriverTests
{
    [Fact]
    public async Task ProviderRequiresEnvironmentSupportBeforeQueryingDevices()
    {
        await using var r = new Rig(); r.Server.Wire.Flags = 1;
        await Assert.ThrowsAsync<IOException>(() => r.Provider.PollAsync(default));
        Assert.Empty(r.Provider.CachedDrivers);
        Assert.DoesNotContain(r.Server.Wire.Requests, p => p.Command is 0 or 1 or 1050 or 1051 or 1101 or 1103);
    }

    [Fact]
    public async Task EnvironmentPolicyRejectsBeforeDiscoveryAndCannotBeReplacedByLocalRouteApproval()
    {
        await using var r = new Rig(); r.Server.ServerSettings = "{\"all_controllers\":true}";
        await Assert.ThrowsAsync<IOException>(() => r.Provider.PollAsync(default));
        Assert.Empty(r.Provider.CachedDrivers); Assert.DoesNotContain(r.Server.Wire.Requests, p => p.Command is 0 or 1);
    }

    [Fact]
    public async Task InventoryPolicySeesEveryControllerAndCanRejectAnUnselectedConflict()
    {
        await using var r = new Rig(SelectDevices(Device()), Device(), Device(700, "CONFLICT"));
        r.Policy.InventoryCheck = devices =>
        {
            Assert.Equal(new uint[] { 41, 700 }, devices.Select(d => d.Id));
            throw new IOException("conflicting controller opened by server");
        };
        await Assert.ThrowsAsync<IOException>(() => r.Provider.PollAsync(default));
        Assert.Empty(r.Provider.CachedDrivers); Assert.Equal(0, r.Server.WriteCount);
    }

    [Fact]
    public async Task CompiledEnvironmentCheckCanRejectAChangedDetectorBeforeDiscovery()
    {
        await using var r = new Rig(); r.Server.Detectors = "{\"detectors\":{\"Fixture controller\":false}}";
        r.Policy.EnvironmentCheck = value =>
        {
            if (!value.Detectors.GetProperty("detectors").GetProperty("Fixture controller").GetBoolean())
                throw new IOException("fixture detector changed");
        };
        await Assert.ThrowsAsync<IOException>(() => r.Provider.PollAsync(default));
        Assert.DoesNotContain(r.Server.Wire.Requests, p => p.Command is 0 or 1); Assert.Empty(r.Provider.CachedDrivers);
    }

    [Fact]
    public async Task InventoryPolicyCannotMutateTheDriversControllerDescriptions()
    {
        await using var r = new Rig();
        r.Policy.InventoryCheck = devices => devices[0].Zones[0] = devices[0].Zones[0] with { LedCount = 99 };
        await r.Open(); Assert.Equal(1, r.Driver().DescribeSetup().Single(s => s.EndpointId == r.Zone(0)).DetectedLeds);
        Written(await r.Command(DeviceOperation.SetLighting, [r.Target(r.Zone(0))], Blue));
    }

    [Fact]
    public async Task SettingsChangingAcrossDiscoveryCannotPublishDrivers()
    {
        await using var r = new Rig();
        r.Server.DuringRead = _ => { r.Server.Detectors = "{\"detectors\":{\"Fixture controller\":false}}"; return Task.CompletedTask; };
        await Assert.ThrowsAsync<OpenRgbSdkObservationChangedException>(() => r.Provider.PollAsync(default));
        Assert.Empty(r.Provider.CachedDrivers); Assert.Equal(0, r.Server.WriteCount);
    }

    [Fact]
    public async Task LocalExclusionIsRecheckedBeforeEachDiscoveryPacket()
    {
        await using var r = new Rig(Device(), Device(700, "SECOND"));
        r.Server.DuringRead = _ => { r.RouteAvailable = false; return Task.CompletedTask; };
        await Assert.ThrowsAsync<IOException>(() => r.Provider.PollAsync(default));
        Assert.Single(r.Server.Wire.Requests, p => p.Command == 1); Assert.Empty(r.Provider.CachedDrivers);
    }

    [Fact]
    public async Task LocalExclusionIsRecheckedBeforeEachEnvironmentPacket()
    {
        await using var r = new Rig(); var original = r.Server.Wire.Intercept!;
        r.Server.Wire.Intercept = async p =>
        {
            if (p.Command == 250) r.RouteAvailable = false;
            return await original(p);
        };
        await Assert.ThrowsAsync<IOException>(() => r.Provider.PollAsync(default));
        Assert.Single(r.Server.Wire.Requests, p => p.Command is 250 or 251); Assert.Empty(r.Provider.CachedDrivers);
    }

    [Fact]
    public async Task ExpiredEnvironmentPreventsReadsAndWritesUntilProviderRefreshes()
    {
        await using var r = await new Rig().Open(); var driver = r.Driver(); var target = r.Target("device");
        r.EnvironmentClock.Advance(2.001); var requests = r.Server.Wire.Requests.Count;
        await Assert.ThrowsAsync<IOException>(() => driver.PollAsync(default));
        Assert.Equal(requests, r.Server.Wire.Requests.Count);
        var result = await driver.ApplyAsync(new("device", DeviceOperation.SetLighting, Lighting: Blue), default);
        Assert.Equal(Outcome.Rejected, result.Outcome); Assert.Equal(0, r.Server.WriteCount);
        await r.Runtime.PollAsync();
        Written(await r.Command(DeviceOperation.SetLighting, [target], Blue));
    }

    [Fact]
    public async Task IndependentEnvironmentRefreshCannotAuthorizeAnExistingProviderPermit()
    {
        await using var r = await new Rig().Open(); var target = r.Target("device");
        await r.Connection!.ReadEnvironmentAsync();
        Assert.Equal(Outcome.Rejected, (await r.Command(DeviceOperation.SetLighting, [target], Blue)).Result!.Outcome);
        Assert.Equal(0, r.Server.WriteCount);
    }

    [Fact]
    public async Task ExpiryWhileQueuedIsCheckedImmediatelyBeforeDeviceRead()
    {
        await using var r = await new Rig().Open(); var driver = r.Driver();
        var captured = (await r.Connection!.ScanAsync()).Single();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        r.Server.DuringRead = async _ => { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(4)); };
        var blocking = r.Connection.ReadAsync(captured.Target);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
        var waiting = driver.PollAsync(default);
        r.EnvironmentClock.Advance(3); release.SetResult(); await blocking;
        var count = r.Server.Wire.Requests.Count;
        await Assert.ThrowsAsync<IOException>(() => waiting);
        Assert.Equal(count, r.Server.Wire.Requests.Count); Assert.Equal(0, r.Server.WriteCount);
    }

    [Fact]
    public async Task ExpiryDuringFirstWritePreservesRecoveryAndStopsFurtherPackets()
    {
        await using var r = await new Rig().Open(); var driver = r.Driver(); var target = r.Target(r.Zone(0));
        r.Server.BeforeWrite = _ => { r.EnvironmentClock.Advance(3); return Task.CompletedTask; };
        var result = await r.Command(DeviceOperation.SetLighting, [target], Blue);
        Assert.Equal(Outcome.Unknown, result.Result!.Outcome); Assert.Equal(1, r.Server.WriteCount);
        Assert.NotEmpty(System.IO.Directory.GetFiles(r.Directory));
        var expired = await driver.ReleaseAsync(DeviceReleaseReason.StartupRecovery, default);
        Assert.Contains(expired, item => item.Result.Outcome == Outcome.Unknown); Assert.Equal(1, r.Server.WriteCount);
        r.Server.BeforeWrite = null; await r.Runtime.PollAsync();
        var recovered = await driver.ReleaseAsync(DeviceReleaseReason.StartupRecovery, default);
        Assert.All(recovered, item => Assert.Contains(item.Result.Outcome, new[] { Outcome.Confirmed, Outcome.WrittenUnverified }));
        Assert.Empty(System.IO.Directory.GetFiles(r.Directory));
    }

    [Fact]
    public async Task FailedRefreshRevokesTheOldPermitAndRetainsUnfinishedOutput()
    {
        await using var r = await new Rig().Open(); var driver = r.Driver();
        Written(await r.Command(DeviceOperation.SetLighting, [r.Target("device")], Blue));
        var writes = r.Server.WriteCount; var files = System.IO.Directory.GetFiles(r.Directory); Assert.NotEmpty(files);
        r.Server.ServerSettings = "{\"all_controllers\":true}";
        await Assert.ThrowsAsync<IOException>(() => r.Provider.PollAsync(default));
        var stopped = await driver.ReleaseAsync(DeviceReleaseReason.StartupRecovery, default);
        Assert.Contains(stopped, item => item.Result.Outcome == Outcome.Unknown);
        Assert.Equal(writes, r.Server.WriteCount); Assert.All(files, f => Assert.True(File.Exists(f)));
        r.Server.ServerSettings = "{\"all_controllers\":false}"; await r.Runtime.PollAsync();
        var recovered = await driver.ReleaseAsync(DeviceReleaseReason.StartupRecovery, default);
        Assert.All(recovered, item => Assert.Contains(item.Result.Outcome, new[] { Outcome.Confirmed, Outcome.WrittenUnverified }));
        Assert.Empty(System.IO.Directory.GetFiles(r.Directory));
    }

    [Fact]
    public async Task SlowCompiledInventoryCheckCannotPublishAnExpiredPermit()
    {
        await using var r = new Rig(); r.Policy.InventoryCheck = _ => r.EnvironmentClock.Advance(3);
        await Assert.ThrowsAsync<IOException>(() => r.Provider.PollAsync(default));
        Assert.Empty(r.Provider.CachedDrivers); Assert.Equal(0, r.Server.WriteCount);
    }

    [Fact]
    public async Task ListChangeDuringFinalPolicyCheckCannotPublishMismatchedTargets()
    {
        await using var r = new Rig();
        r.Policy.InventoryCheck = _ =>
        {
            var prior = r.Connection!.InventoryRevision;
            r.Server.Wire.SendAsync(0, 100, []).GetAwaiter().GetResult();
            Assert.True(SpinWait.SpinUntil(() => r.Connection.InventoryRevision != prior, TimeSpan.FromSeconds(3)));
        };
        await Assert.ThrowsAsync<IOException>(() => r.Provider.PollAsync(default));
        Assert.Empty(r.Provider.CachedDrivers); Assert.Equal(0, r.Server.WriteCount);
    }

    [Fact]
    public async Task EnvironmentRefreshDoesNotAskTheSdkToConfigureOrRescanHardware()
    {
        await using var r = await new Rig().Open();
        Assert.All(r.Server.Wire.Requests, p => Assert.Contains(p.Command, new uint[] { 40, 50, 52, 250, 251, 200, 0, 1 }));
        Assert.All(r.Server.Wire.Requests.Where(p => p.Command == 250), p => Assert.Contains(Encoding.UTF8.GetString(p.Data), new[] { "Detectors\0", "Drivers\0", "Server\0" }));
        Assert.True(r.Server.Wire.Requests.Count(p => p.Command == 200) >= 4);
        Assert.Equal(0, r.Server.WriteCount);
    }

    [Fact]
    public async Task FixedRequirementsRejectDriverChangesBeforeAnotherControllerRequest()
    {
        await using var r = await new Rig().Open(); var driver = r.Driver(); var count = r.Server.Wire.Requests.Count(p => p.Command is 0 or 1);
        r.Server.Drivers = "{\"shared_smbus_access\":false,\"smbus_sleep_mode\":2}";
        var error = await Assert.ThrowsAsync<IOException>(() => r.Provider.PollAsync(default));
        Assert.Contains("共有SMBus", error.Message); Assert.Equal(count, r.Server.Wire.Requests.Count(p => p.Command is 0 or 1));
        Assert.Null(driver.Observe()); Assert.Equal(0, r.Server.WriteCount);
        r.Server.Drivers = "{\"shared_smbus_access\":true,\"smbus_sleep_mode\":2}"; await r.Runtime.PollAsync();
        Written(await r.Command(DeviceOperation.SetLighting, [r.Target("device")], Blue));
    }
}
