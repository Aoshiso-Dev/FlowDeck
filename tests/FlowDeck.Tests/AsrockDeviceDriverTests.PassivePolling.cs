using FlowDeck.Core;

namespace FlowDeck.Tests;

public sealed partial class AsrockDeviceDriverTests
{
    [Theory]
    [InlineData("unconfigured")]
    [InlineData("configured")]
    [InlineData("native")]
    [InlineData("direct")]
    public async Task StableMonitoringDoesNotExchangeLightingReports(string mode)
    {
        var a = new Cell(1, true);
        await using var r = await new Rig(new(a)).Open(configure: mode != "unconfigured");
        if (mode == "native") Assert.Equal(Outcome.Confirmed, (await Fixed(r, a, 5)).Result!.Outcome);
        if (mode == "direct") Assert.Equal(Outcome.WrittenUnverified, (await r.Start((a, 5))).Result!.Outcome);
        var reports = new List<byte[]>();
        r.Backend.BeforeTransport = p => reports.Add(p.ToArray());

        for (var i = 0; i < 60; i++) await r.Runtime.PollAsync();

        Assert.NotNull(r.Driver(a).Observe());
        Assert.Empty(reports);
        r.Backend.BeforeTransport = null;
    }

    [Fact]
    public async Task ReconnectedControllerReadsNewTopologyBeforeItBecomesUsable()
    {
        var a = new Cell(1, true);
        await using var r = await new Rig(new(a)).Open();
        var driver = r.Driver(a); var previousBinding = driver.BindingCandidate;
        a.Connection = a.Connection with { ConnectionId = Guid.NewGuid().ToString("N") };
        a.Device.Counts[5]++;
        await r.Provider.PollAsync(default);
        Assert.Null(driver.Observe());
        var reports = new List<byte[]>();
        r.Backend.BeforeTransport = p => reports.Add(p.ToArray());

        await driver.PollAsync(default);

        Assert.Equal(6, reports.Count);
        Assert.All(reports, p => Assert.Contains(p[1], new byte[] { 0x14, 0x11 }));
        Assert.NotEqual(previousBinding, driver.BindingCandidate);
        Assert.Equal(16, driver.DescribeSetup().Single(s => s.EndpointId == "zone:5").DetectedLeds);
        Assert.Empty(a.Device.Writes);
        reports.Clear(); await driver.PollAsync(default); Assert.Empty(reports);
        r.Backend.BeforeTransport = null;
    }

    [Fact]
    public async Task FailedInitialReadCanRetryWithoutContinuingToPollSuccessfulReads()
    {
        var a = new Cell(1, true);
        await using var r = new Rig(new(a));
        await r.Provider.PollAsync(default); var driver = r.Driver(a);
        r.Backend.BeforeTransport = _ => throw new IOException("recording initial query failure");
        await Assert.ThrowsAsync<IOException>(() => driver.PollAsync(default));
        Assert.Null(driver.Observe());
        var reports = new List<byte[]>();
        r.Backend.BeforeTransport = p => reports.Add(p.ToArray());

        await driver.PollAsync(default);

        Assert.Equal(6, reports.Count); Assert.NotNull(driver.Observe());
        reports.Clear(); await driver.PollAsync(default); Assert.Empty(reports);
        Assert.Empty(a.Device.Writes); r.Backend.BeforeTransport = null;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LightingCommandsReadFreshTopologyEvenAfterPassivePolling(bool direct)
    {
        var a = new Cell(1, true);
        await using var r = await new Rig(new(a)).Open();
        var reports = new List<byte[]>();
        r.Backend.BeforeTransport = p => reports.Add(p.ToArray());
        a.Device.Counts[5]++;
        await r.Runtime.PollAsync(); Assert.Empty(reports);

        var reply = direct ? await r.Start((a, 5)) : await Fixed(r, a, 5);

        Assert.Equal(direct ? Outcome.Unknown : Outcome.Rejected, reply.Result!.Outcome);
        Assert.NotEmpty(reports);
        Assert.Empty(a.Device.Writes); r.Backend.BeforeTransport = null;
    }
}
