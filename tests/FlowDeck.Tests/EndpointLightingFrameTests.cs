using FlowDeck.Core;

namespace FlowDeck.Tests;

public sealed class EndpointLightingFrameTests
{
    [Fact]
    public void EndpointColorsAreCopiedAndRequireAnExactTargetSet()
    {
        EndpointLightingFrame[] entries = [new("a", new(1, 2, 3), new(4, 5, 6)), new("b", new(7, 8, 9), new(10, 11, 12))];
        var frame = new DeviceLightingFrame(1, new(255, 255, 255), () => new(), entries);
        entries[0] = new("changed", new(0, 0, 0), new(0, 0, 0));
        Assert.True(frame.HasEndpointColors); Assert.Null(frame.TargetError(["b", "a"]));
        Assert.NotNull(frame.TargetError(["a"])); Assert.NotNull(frame.TargetError(["a", "b", "c"]));
        Assert.Equal(new RgbColor(1, 2, 3), frame.ColorFor("a")); Assert.Equal(new RgbColor(4, 5, 6), frame.NormalStopColorFor("a"));
        Assert.Throws<KeyNotFoundException>(() => frame.ColorFor("missing"));
    }
    [Fact]
    public void UniformFramesKeepTheirExistingContractAndInvalidMapsAreRejected()
    {
        var frame = new DeviceLightingFrame(1, new(1, 2, 3), () => new()); Assert.False(frame.HasEndpointColors);
        Assert.Null(frame.TargetError(["a", "b"])); Assert.Equal(new RgbColor(1, 2, 3), frame.ColorFor("a")); Assert.Null(frame.NormalStopColorFor("a"));
        EndpointLightingFrame a = new("a", new(1, 2, 3), new(4, 5, 6));
        foreach (var bad in new EndpointLightingFrame[][] { [], [a, a], [a with { EndpointId = "" }], [a with { Color = new(256, 0, 0) }],
            [a with { NormalStopColor = new(-1, 0, 0) }], Enumerable.Range(0, 257).Select(i => a with { EndpointId = "light" + i }).ToArray() })
            Assert.Throws<ArgumentException>(() => new DeviceLightingFrame(1, new(0, 0, 0), () => new(), bad));
    }
}
