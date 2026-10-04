using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class HubDeviceDriverTests
{
    [Theory]
    [InlineData(true, 0, 0, 0)] [InlineData(true, 0, 100, 0)]
    [InlineData(true, 0, 101, 1)] [InlineData(true, 101, 0, 1)]
    [InlineData(false, 0, 0, 1)] [InlineData(true, 0, -1, 1)]
    public async Task JournalPreflightOnlyReusesAgeAwareRecentSamples(bool smooth, int sampleAge, int elapsed, int reads)
    {
        using var dir = new ServiceTestDirectory(); var clock = new TestClock();
        var hub = new Hub { Smooth = smooth, Transform = s => s with { SampleAgeMilliseconds = sampleAge } };
        await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path, clock);
        await driver.InitializeAsync(); driver.Configure(driver.BindingCandidate, [Config(driver, "fan1")]);
        clock.Advance(elapsed / 1000d);
        Assert.Equal(Outcome.Confirmed, (await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 61), default)).Outcome);
        Assert.Equal(reads, hub.Reads); Assert.Equal(1, hub.Inner.Writes);
        Assert.Single(Journal(driver, dir.Path).Pending);
    }

    [Fact]
    public async Task ReusedJournalPreflightStillRejectsExternalDutyAndDoesNotOverwriteItOnRelease()
    {
        using var dir = new ServiceTestDirectory(); var clock = new TestClock(); var hub = new Hub { Smooth = true };
        await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path, clock);
        await driver.InitializeAsync(); driver.Configure(driver.BindingCandidate, [Config(driver, "fan1")]);
        hub.BeforeWrite = () => { hub.BeforeWrite = null; hub.Inner.SetFanGroupAsync(["fan1"], 99).GetAwaiter().GetResult(); };
        Assert.Equal(Outcome.Rejected, (await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 61), default)).Outcome);
        Assert.Equal(0, hub.Reads); Assert.Equal(1, hub.Inner.Writes);
        Assert.Equal(Outcome.Unknown, Assert.Single(await driver.ReleaseAsync(DeviceReleaseReason.ClientDisconnected, default)).Result.Outcome);
        Assert.Equal(1, hub.Reads); Assert.Equal(1, hub.Inner.Writes); Assert.Single(Journal(driver, dir.Path).Pending);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ReusedPreflightDoesNotExtendTemperatureAuthorization(bool zeroBudget)
    {
        using var dir = new ServiceTestDirectory(); var clock = new TestClock(); var hub = new Hub { Smooth = true };
        await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path, clock);
        await driver.InitializeAsync(); var config = Config(driver, "fan1"); driver.Configure(driver.BindingCandidate, [config]);
        var result = await driver.ApplyCoolingAsync(new("fan1", 61, false, config.Safety,
            () => zeroBudget ? new(ValidFor: TimeSpan.Zero) : new("expired input", Failure: DeviceDriverFailure.CoolingInputExpired)), default);
        Assert.Equal(Outcome.Rejected, result.Outcome); Assert.Equal(DeviceDriverFailure.CoolingInputExpired, result.Failure);
        Assert.Equal(0, hub.Reads); Assert.Equal(0, hub.Inner.Writes); Assert.Empty(Journal(driver, dir.Path).Pending);
    }
}
