using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceLightingRuntimeTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SessionExitRestoresBothLightsWhileExplicitStopKeepsTheirBaseColor(bool sessionExit)
    {
        var a = new Driver("a"); var b = new Driver("b"); await using var r = new Rig(a, b).Open();
        await r.Start(["a", "b"], brightness: 50); await r.Tick(.5);
        var request = r.Request(sessionExit ? DeviceOperation.ReleaseSession : DeviceOperation.StopLighting, ["a", "b"]);
        if (sessionExit) request = request with { Targets = null, TopologyRevision = 0 };
        var reply = await r.Runtime.ExecuteAsync(request);
        Assert.True(reply.Success);
        var expected = sessionExit ? new RgbColor(9, 8, 7) : new RgbColor(50, 25, 10);
        Assert.Equal(expected, a.Color); Assert.Equal(expected, b.Color);
        Assert.All(r.Runtime.State().Endpoints, e => Assert.False(e.Owned));
        var writes = a.Writes + b.Writes; await r.Tick(.5);
        Assert.Equal(writes, a.Writes + b.Writes); Assert.Equal(1, a.Ends); Assert.Equal(1, b.Ends);
    }
}

public sealed partial class AsrockDeviceDriverTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NormalReleaseOfAnActiveStreamUsesTheConfiguredExitForOnlyTheSelectedZone(bool viaRuntime)
    {
        var cell = new Cell(1, true); await using var r = await new Rig(new(cell)).Open(); var other = Setting(cell, 6);
        if (viaRuntime) Assert.True((await r.Start((cell, 5))).Success);
        else
        {
            var stream = await r.Driver(cell).OpenLightingStreamAsync(["zone:5"], new("breathing", [new(0, 255, 255)]), default);
            Assert.All(await stream.WriteAsync(ExpansionFrame(1, 5), default), e => Assert.Equal(Outcome.WrittenUnverified, e.Result.Outcome));
        }
        if (viaRuntime) Assert.True((await r.Command(DeviceOperation.ReleaseSession, [])).Success);
        else Assert.All(await r.Driver(cell).ReleaseAsync(DeviceReleaseReason.NormalExit, default), e => Assert.Equal(Outcome.Confirmed, e.Result.Outcome));
        Assert.Equal(new BoardLightSetting(0, 0, 0, 0), Setting(cell, 5)); Assert.Equal(other, Setting(cell, 6));
        Assert.False(File.Exists(r.JournalPath(cell)));
    }
}

public sealed partial class EneMemoryDeviceDriverTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NormalReleaseOfAnActiveStreamUsesTheConfiguredExitForOnlyTheSelectedDimm(bool viaRuntime)
    {
        var rack = new EneMemoryBackendTests.Rack(0x71, 0x73); await using var r = await new Rig(rack).Open();
        r.Configure(exit: LightingExitAction.FixedColor, exitColor: new(255, 255, 255)); var other = Snapshot(rack, 0x73);
        if (viaRuntime) Assert.True((await r.Start(0x71)).Success);
        else
        {
            var stream = await r.Driver().OpenLightingStreamAsync([Endpoint], new("breathing", [new(0, 255, 255)]), default);
            var frame = new DeviceLightingFrame(1, new(0, 40, 40), () => new(ValidFor: TimeSpan.FromMilliseconds(500)));
            Assert.Equal(Outcome.WrittenUnverified, Assert.Single(await stream.WriteAsync(frame, default)).Result.Outcome);
        }
        if (viaRuntime) Assert.True((await r.Command(DeviceOperation.ReleaseSession, [])).Success);
        else Assert.Equal(Outcome.Confirmed, Assert.Single(await r.Driver().ReleaseAsync(DeviceReleaseReason.NormalExit, default)).Result.Outcome);
        Assert.Equal(new byte[] { 255, 255, 255 }, Snapshot(rack).NativeColors[..3]);
        Assert.True(EneMemoryProtocol.Same(other, Snapshot(rack, 0x73))); Assert.Null(r.Pending());
    }
}
