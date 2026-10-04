using FlowDeck.Core;
namespace FlowDeck.Tests;
public partial class DeviceLightingRuntimeTests
{
    [Fact] public async Task BodyMirrorCarriesAppliedAnimationPhaseWithoutHardwareReadsOrWrites()
    {
        var a = new Driver("a"); var b = new Driver("b"); await using var r = new Rig(a, b).Open();
        await r.Start(["a", "b"], "breathing", 60); await r.Tick(.5);
        var writes = a.Writes + b.Writes;
        var first = r.Runtime.State().Endpoints.Select(e => e.Lighting!.Mirror!).ToArray();
        Assert.All(first, f => { Assert.True(f.Valid); Assert.Equal(500, f.PhaseMs, 4); Assert.Equal(60, f.Brightness); });
        Assert.Equal(first[0], first[1]); Assert.Equal(writes, a.Writes + b.Writes);
        r.Clock.Advance(1); var next = r.Runtime.State().Endpoints[0].Lighting!.Mirror!;
        Assert.Equal(1500, next.PhaseMs, 4); Assert.Equal(2000, next.ValidForMs);
        r.Clock.Advance(2); Assert.Null(r.Runtime.State().Endpoints[0].Lighting!.Mirror);
        Assert.Equal(writes, a.Writes + b.Writes);
        await r.Tick(.1); Assert.NotNull(r.Runtime.State().Endpoints[0].Lighting!.Mirror);
        await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.StopLighting, ["a", "b"]));
        Assert.All(r.Runtime.State().Endpoints, e => Assert.Null(e.Lighting?.Mirror));
    }
    [Fact] public async Task BodyMirrorFollowsIntensityEditsWithoutRestartingOtherTargets()
    {
        var a = new Driver("a"); var b = new Driver("b"); await using var r = new Rig(a, b).Open();
        await r.Start(["a", "b"]); await r.Tick(.75);
        await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetLighting, ["a"], new("color-cycle", [new(100, 50, 20)], 4, Brightness: 30)));
        var all = r.Runtime.State().Endpoints; var first = all.Single(e => e.Target.DeviceId == "a").Lighting!.Mirror!;
        var second = all.Single(e => e.Target.DeviceId == "b").Lighting!.Mirror!;
        Assert.Equal(750, first.PhaseMs, 4); Assert.Equal(first.PhaseMs, second.PhaseMs);
        Assert.Equal(30, first.Brightness); Assert.Equal(100, second.Brightness);
    }
}
