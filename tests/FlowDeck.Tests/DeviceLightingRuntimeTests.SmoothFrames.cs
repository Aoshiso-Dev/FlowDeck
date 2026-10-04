using FlowDeck.Core;

namespace FlowDeck.Tests;

public partial class DeviceLightingRuntimeTests
{
    [Theory] [InlineData("breathing")] [InlineData("color-cycle")]
    public async Task FastTargetsReceiveSmallSynchronizedStepsWithoutTheOldHundredMillisecondFloor(string effect)
    {
        var a = new Driver("a") { LightingLimits = new(33, 500, 24, EndpointColors: true) };
        var b = new Driver("b") { LightingLimits = a.LightingLimits };
        await using var r = new Rig(a, b).Open();
        var program = new LightingProgram(effect, [new(255, 255, 255)], 2);
        Assert.Equal(Outcome.WrittenUnverified, (await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetLighting, ["a", "b"], program))).Result!.Outcome);
        for (var i = 0; i < 180; i++) await r.Tick(.034);
        Assert.Equal(181, a.Frames.Count); Assert.Equal(a.Frames, b.Frames);
        var steps = a.Frames.Zip(a.Frames.Skip(1), (x, y) => new[] { Math.Abs(x.R-y.R), Math.Abs(x.G-y.G), Math.Abs(x.B-y.B) }.Max());
        Assert.All(steps, delta => Assert.InRange(delta, 0, 9));
        Assert.Equal(0, r.Runtime.State().Endpoints[0].Lighting!.DroppedFrames);
    }

    [Fact]
    public async Task MixedGroupsStillRespectTheSlowestTargetAndNeverQueueAFrameDuringAWritingRound()
    {
        var a = new Driver("a") { LightingLimits = new(33, 500, 24) };
        var b = new Driver("b") { LightingLimits = new(100, 500, 24) };
        await using var r = new Rig(a, b).Open(); await r.Start(["a", "b"]);
        await r.Tick(.034); await r.Tick(.034);
        Assert.Equal(1, a.Writes); Assert.Equal(1, b.Writes);
        await r.Tick(.034); Assert.Equal(2, a.Writes); Assert.Equal(2, b.Writes);
        a.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = r.Tick(.102); await a.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        for (var i = 0; i < 10; i++) { r.Clock.Advance(.034); _ = r.Runtime.TickLightingAsync(); }
        Assert.Equal(2, a.Writes);
        a.Continue.SetResult(); await pending;
        Assert.Equal(3, a.Writes); Assert.Equal(3, b.Writes);
    }
}
