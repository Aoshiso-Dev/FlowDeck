using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class LightingBrightnessTests
{
    private static LightingScene Original() => new(Guid.NewGuid().ToString("N"), "部屋", [new("hub", "led1", new("fading", [new(120,40,240), new(20,180,80)], 3))],
        [new("board", 5, new(2, 40, 180, 240, 99))], new("hub", ["led2"], "board", [6], "color-cycle", new(255, 255, 255), 4));

    [Fact] public void DimmingPreservesPaletteEffectSpeedTargetsAndOriginalColors()
    {
        var original = Original(); var half = LightingBrightness.Build(original, 50);
        Assert.Equal(new(60,20,120), half.Hub[0].Effect.Colors[0]); Assert.Equal(new(10,90,40), half.Hub[0].Effect.Colors[1]);
        Assert.Equal("fading", half.Hub[0].Effect.Mode); Assert.Equal(3, half.Hub[0].Effect.Speed); Assert.Equal(99, half.Board[0].Effect.Speed);
        Assert.Equal(120, half.Board[0].Effect.Blue); Assert.Equal(128, half.Sync!.Color.R); Assert.Equal("color-cycle", half.Sync.Mode);
        var full = LightingBrightness.Build(original, 100); Assert.True(LightingBrightness.SameOutput(full, original));
        Assert.Equal(new(120,40,240), original.Hub[0].Effect.Colors[0]);
    }
    [Fact] public void AutomaticColorEffectsRejectDimmingButAllowOffAndExactRestoration()
    {
        var scene = Original() with { Hub = [new("hub", "led1", new("super-rainbow", [], 1, true))], Board = [new("board", 5, new(14, 255, 0, 0, 33))] };
        Assert.Equal(2, LightingBrightness.Unsupported(scene).Length);
        Assert.Throws<InvalidOperationException>(() => LightingBrightness.Build(scene, 50));
        var off = LightingBrightness.Build(scene, 100, true); Assert.Equal("off", off.Hub[0].Effect.Mode); Assert.Equal(0, off.Board[0].Effect.Mode); Assert.Equal("off", off.Sync!.Mode);
        Assert.True(LightingBrightness.SameOutput(scene, LightingBrightness.Build(scene, 100)));
        Assert.Throws<ArgumentOutOfRangeException>(() => LightingBrightness.Build(scene, 0));
    }
    [Fact] public void OutputComparisonIgnoresNamesAndNormalizesCompletedFixedSynchronization()
    {
        var scene = Original() with { Hub = [], Board = [], Sync = new("hub", ["led2"], "board", [6], "fixed", new(1,2,3), 4) };
        var actual = scene with { Id = Guid.NewGuid().ToString("N"), Name = "再取得", Sync = null,
            Hub = [new("hub", "led2", new("fixed", [new(1,2,3)]))], Board = [new("board",6,new(1,1,2,3))] };
        Assert.True(LightingBrightness.SameOutput(scene, actual));
        Assert.False(LightingBrightness.SameOutput(scene, actual with { Board = [new("board",6,new(1,1,2,4))] }));
    }
    [Fact] public async Task OriginalAndOffStateSurviveRestartAndCorruptFilesAreNotReplaced()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FlowDeck-brightness-test-" + Guid.NewGuid().ToString("N"));
        var store = new LightingAdjustmentStore(directory);
        var state = new LightingAdjustmentState(BaseScene: Original(), Brightness: 45, IsOff: true, Applied: true);
        await store.SaveAsync(state); var copy = await new LightingAdjustmentStore(directory).LoadAsync();
        Assert.Equal(45, copy.Brightness); Assert.True(copy.IsOff); Assert.True(LightingBrightness.SameOutput(state.BaseScene!, copy.BaseScene!));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(state with { Brightness = -1 }));
        Assert.Equal(45, (await store.LoadAsync()).Brightness);
        await File.WriteAllTextAsync(store.PathName, "{bad");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => store.LoadAsync());
        Assert.Equal("{bad", await File.ReadAllTextAsync(store.PathName));
    }
}
