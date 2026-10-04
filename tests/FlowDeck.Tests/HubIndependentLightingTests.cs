using FlowDeck.Core;

namespace FlowDeck.Tests;

public class HubIndependentLightingTests
{
    [Fact]
    public async Task MotherboardAndMemoryCanShareFramesWithoutAnyHubCoordinator()
    {
        var board = new List<RgbColor>(); var memory = new List<RgbColor>(); var selected = new RgbColor(20, 40, 230);
        var target = new LightingFrameTarget(c => { board.Add(c); memory.Add(c); return Task.CompletedTask; }, () => true);
        await using var animation = new LightingAnimator(target, "breathing", selected, 4);
        await animation.Started.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(230); await animation.StopAsync();
        Assert.Null(animation.Error); Assert.True(animation.Frames >= 2);
        Assert.Equal(board, memory); Assert.Equal(selected, board[^1]);
    }
    [Fact]
    public async Task InvalidatedTargetDoesNotReceiveASettlingWrite()
    {
        var valid = true; var writes = 0;
        var target = new LightingFrameTarget(_ => { Interlocked.Increment(ref writes); return Task.CompletedTask; }, () => Volatile.Read(ref valid));
        await using var animation = new LightingAnimator(target, "breathing", new(1, 2, 3), 4);
        await animation.Started.WaitAsync(TimeSpan.FromSeconds(3));
        Volatile.Write(ref valid, false); var before = Volatile.Read(ref writes);
        await animation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(animation.Error); Assert.Equal(before, writes);
    }
    [Fact]
    public async Task SlowWriterDoesNotAccumulateConcurrentFrames()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var writes = 0;
        var target = new LightingFrameTarget(async _ => { Interlocked.Increment(ref writes); await gate.Task; }, () => true);
        await using var animation = new LightingAnimator(target, "color-cycle", new(1, 2, 3), 4);
        await Task.Delay(330); var countWhileBlocked = Volatile.Read(ref writes);
        gate.TrySetResult(); await animation.Started.WaitAsync(TimeSpan.FromSeconds(3)); await animation.StopAsync();
        Assert.Equal(1, countWhileBlocked);
        Assert.Null(animation.Error);
    }
    [Fact]
    public void BoardAndMemoryScenesRetainExactTargetsAndBrightnessWithoutHubIdentity()
    {
        var memory = MemoryLighting.Identity(0x71);
        var sync = new SceneSyncLight("", [], "board", [2], "breathing", new(20, 40, 200), 2, [memory]);
        var scene = new LightingScene(Guid.NewGuid().ToString("N"), "基板とメモリ", [], [], sync);
        Assert.Null(LightingScenes.Validate(scene));
        var group = new LightingGroup(Guid.NewGuid().ToString("N"), "メモリ", [], [new(memory, 0)]);
        var selected = LightingGroups.Select(scene, group);
        Assert.Empty(selected.Sync!.Channels); Assert.Equal("", selected.Sync.HubId); Assert.Equal("", selected.Sync.BoardIdentity);
        Assert.Equal(new[] { memory }, selected.Sync.Memory);
        Assert.Equal(new RgbColor(10, 20, 100), LightingBrightness.Build(selected, 50).Sync!.Color);
        Assert.NotNull(LightingScenes.Validate(scene with { Sync = sync with { HubId = "unexpected-hub" } }));
        Assert.NotNull(LightingScenes.Validate(scene with { Sync = sync with { Zones = [], BoardIdentity = "", Memory = [] } }));
    }
}
