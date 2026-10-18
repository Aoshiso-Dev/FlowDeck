using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class AsrockDeviceDriverTests
{
    [Theory, InlineData(false), InlineData(true)]
    public async Task CurrentAsrockConnectionUsesFirmwareExtentWithoutUserApprovalOrStartupOutput(bool serial)
    {
        var a = new Cell(1, serial); await using var r = await new Rig(new(a)).Open(configure: false);
        var driver = r.Driver(a);
        driver.ConfigureCurrentConnection(driver.BindingCandidate!, null, [], [], []);
        r.Runtime.Refresh();
        Assert.All(driver.DescribeSetup(), option => Assert.Null(option.DetectedLeds));
        Assert.True(driver.AllowsCurrentConnectionControl("zone:5", DeviceFeature.FixedColor));
        Assert.True(driver.AllowsCurrentConnectionControl("zone:5", DeviceFeature.NativeEffect));
        Assert.False(driver.AllowsCurrentConnectionControl("zone:5", DeviceFeature.FixedDuty));
        Assert.False(driver.AllowsCurrentConnectionControl("zone:5", DeviceFeature.Temperature));
        Assert.False(driver.AllowsCurrentConnectionControl("zone:7", DeviceFeature.FixedColor));
        Assert.Empty(a.Device.Writes);
        var before = Setting(a, 5);
        Assert.Equal(Outcome.Confirmed, (await Fixed(r, a, 5)).Result!.Outcome);
        Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.StopLighting, [(a, 5)])).Result!.Outcome);
        Assert.Equal(before, Setting(a, 5));
    }

    [Fact]
    public async Task CurrentAsrockConnectionReusesActualExitWithBothLegacyConfirmationFlagsFalse()
    {
        var a = new Cell(1, false); await using var r = await new Rig(new(a)).Open(configure: false);
        var driver = r.Driver(a); var target = new EndpointReference(a.Id, "zone:5");
        var saved = new LightingEndpointConfiguration(target, false, new(LightingExitAction.FixedColor, new(9, 8, 7)));
        var setup = new EndpointSetup(LedCount: a.Device.Counts[5], LedCountConfirmed: false);
        driver.ConfigureCurrentConnection(driver.BindingCandidate!, driver.BindingCandidate, [], [saved],
            [new("zone:5", "Header", EndpointKind.Lighting, Setup: setup)]);
        r.Runtime.Refresh(); Assert.Empty(a.Device.Writes);
        Assert.Equal(Outcome.Confirmed, (await Fixed(r, a, 5)).Result!.Outcome);
        // Poll/reconfiguration during ownership must preserve the selected exit and first original.
        var journal = File.ReadAllText(NativePath(r, a)); var writes = a.Device.Writes.Count;
        driver.ConfigureCurrentConnection(driver.BindingCandidate!, null, [], [], []);
        Assert.Equal(journal, File.ReadAllText(NativePath(r, a))); Assert.Equal(writes, a.Device.Writes.Count);
        Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.StopLighting, [(a, 5)])).Result!.Outcome);
        Assert.Equal(new BoardLightSetting(1, 9, 8, 7), Setting(a, 5));
        Assert.False(saved.WiringConfirmed); Assert.False(setup.LedCountConfirmed);
    }

    [Fact]
    public async Task AutomaticAsrockPreparationKeepsDirectBackgroundAndFreshConnectionGuards()
    {
        var a = new Cell(1, false); a.Device.Set(2, new(7, 10, 20, 30));
        await using var r = await new Rig(new(a)).Open(configure: false); var driver = r.Driver(a);
        var before = driver.BindingCandidate!;
        driver.ConfigureCurrentConnection(before, null, [], [], []);
        Assert.NotNull(driver.ValidateLightingStream(["zone:5"], new("color-cycle", [new(1, 2, 3)])));
        Assert.Empty(a.Device.Writes);
        a.Connection = a.Connection with { ConnectionId = Guid.NewGuid().ToString("N") };
        await r.Runtime.PollAsync(); await r.Runtime.PollAsync();
        Assert.Throws<IOException>(() => driver.ConfigureCurrentConnection(before, null, [], [], []));
        Assert.False(driver.AllowsCurrentConnectionControl("zone:5", DeviceFeature.FixedColor));
        Assert.Empty(a.Device.Writes);
    }

    [Fact]
    public async Task CurrentAsrockPreparationDoesNotInheritAnAbandonedNativeOwner()
    {
        var a = new Cell(1, true); var backend = new Backend(a);
        var directory = Path.Combine(Path.GetTempPath(), "FlowDeck-Asrock-Current-Recovery-" + Guid.NewGuid().ToString("N"));
        await PrepareAbandonedNative(a, backend, directory, written: true);
        await using var r = await new Rig(backend, directory).Open(configure: false); var driver = r.Driver(a);
        var before = File.ReadAllText(NativePath(r, a)); var writes = a.Device.Writes.Count;
        driver.ConfigureCurrentConnection(driver.BindingCandidate!, null, [], [], []);
        Assert.False(driver.AllowsCurrentConnectionControl("zone:5", DeviceFeature.FixedColor));
        Assert.False(driver.AllowsCurrentConnectionControl("zone:6", DeviceFeature.ColorFrames));
        Assert.True(driver.AllowsCurrentConnectionControl("zone:5", DeviceFeature.RestoreLighting));
        Assert.False(driver.AllowsCurrentConnectionControl("zone:7", DeviceFeature.RestoreLighting));
        Assert.All(driver.Observe()!.Endpoints, endpoint => {
            Assert.Equal(DeviceAvailability.Failed, endpoint.Capabilities.Single(c => c.Feature == DeviceFeature.FixedColor).Availability);
            Assert.Equal(DeviceAvailability.Ready, endpoint.Capabilities.Single(c => c.Feature == DeviceFeature.RestoreLighting).Availability);
        });
        Assert.Equal(before, File.ReadAllText(NativePath(r, a))); Assert.Equal(writes, a.Device.Writes.Count);
        Assert.Equal(Outcome.Confirmed, Assert.Single(await driver.ReleaseAsync(DeviceReleaseReason.StartupRecovery, default)).Result.Outcome);
    }

    [Fact]
    public async Task AsrockFailedFreshReadIsTemporaryAndDoesNotRequireSetupAgain()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(configure: false); var driver = r.Driver(a);
        driver.ConfigureCurrentConnection(driver.BindingCandidate!, null, [], [], []);
        r.Backend.BeforeTransport = _ => throw new IOException("temporary query failure");
        await driver.ApplyAsync(new("zone:5", DeviceOperation.SetLighting, Lighting: new("fixed", [new(1, 2, 3)])), default);
        Assert.All(driver.Observe()!.Endpoints, endpoint =>
            Assert.Equal(DeviceAvailability.TemporarilyUnavailable, endpoint.Capabilities.Single(c => c.Feature == DeviceFeature.FixedColor).Availability));
        Assert.Empty(a.Device.Writes);
        r.Backend.BeforeTransport = null; await driver.PollAsync(default);
        Assert.True(driver.AllowsCurrentConnectionControl("zone:5", DeviceFeature.FixedColor));
        Assert.Empty(a.Device.Writes);
    }
}
