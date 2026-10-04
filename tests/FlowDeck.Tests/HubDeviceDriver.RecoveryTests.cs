using FlowDeck.Core;

namespace FlowDeck.Tests;

public partial class HubDeviceDriverTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ReleaseReacquiresLostConnectionWithoutDependingOnTheNextPoll(bool observed)
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); await using var driver = await Ready(hub, dir.Path);
        await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 59), default);
        hub.Disconnected = true;
        if (observed) await Assert.ThrowsAsync<IOException>(() => driver.RefreshAsync());
        var result = await driver.ApplyAsync(new("fan1", DeviceOperation.ReleaseCooling), default);
        Assert.Equal(Outcome.Confirmed, result.Outcome); Assert.Equal(55, result.ReportedDuty);
        Assert.Equal(2, hub.Opened.Count); Assert.Equal(2, hub.Inner.Writes);
        Assert.Empty(Journal(driver, dir.Path).Pending);
        Assert.All((await hub.ReadAsync()).Channels.Skip(1), c => Assert.Equal(45, c.Duty));
    }

    [Theory]
    [InlineData("missing")] [InlineData("duplicate")] [InlineData("serial")]
    [InlineData("wiring")] [InlineData("firmware")] [InlineData("external")]
    [InlineData("without-serial")] [InlineData("read-failure")]
    public async Task ReopeningPreservesRecoveryGuardsAndNeverReplaysTheOldOutput(string condition)
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(withoutSerial: condition == "without-serial");
        await using var driver = await Ready(hub, dir.Path);
        await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 59), default);
        var original = Assert.Single(Journal(driver, dir.Path).Pending);
        hub.Disconnected = true;
        if (condition == "missing") hub.Candidates = [hub.Identity with { Id = "another-hub" }];
        if (condition == "duplicate") hub.Candidates = [hub.Identity, hub.Identity with { Path = "another-path" }];
        if (condition == "serial") hub.Candidates = [hub.Identity with { Serial = "replacement" }];
        if (condition == "wiring") hub.Transform = s => s with { Channels = s.Channels.Select(c => c.Id == "fan1" ? c with { ControlMode = "DC" } : c).ToArray() };
        if (condition == "firmware") hub.Transform = s => s with { Firmware = "replacement-firmware" };
        if (condition == "external") await hub.Inner.SetFanGroupAsync(["fan1"], 91);
        if (condition == "read-failure") hub.FailEveryRead = true;
        var writes = hub.Inner.Writes; var reads = hub.Reads;
        var result = Assert.Single(await driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default));
        Assert.Equal(Outcome.Unknown, result.Result.Outcome); Assert.Equal(writes, hub.Inner.Writes);
        Assert.Equal(original, Assert.Single(Journal(driver, dir.Path).Pending));
        Assert.Equal(condition is "missing" or "duplicate" ? 1 : 2, hub.Opened.Count);
        Assert.InRange(hub.Reads - reads, 1, 2);
    }

    [Fact] public async Task CancelledRecoveryDoesNotOpenOrWriteAndKeepsTheJournal()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); await using var driver = await Ready(hub, dir.Path);
        await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 59), default); hub.Disconnected = true;
        using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => driver.ReleaseAsync(DeviceReleaseReason.NormalExit, ct.Token));
        Assert.Single(hub.Opened); Assert.Equal(1, hub.Inner.Writes); Assert.Single(Journal(driver, dir.Path).Pending);
    }
}
