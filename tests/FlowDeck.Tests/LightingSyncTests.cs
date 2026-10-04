using FlowDeck.Core;

namespace FlowDeck.Tests;

public class LightingSyncTests
{
    [Fact] public void BreathingHasOneSharedPeriodAndNoDrift()
    {
        var color = new RgbColor(200, 100, 50);
        Assert.Equal(color, HubLighting.Frame("breathing", color, 0, 6));
        Assert.Equal(new(0, 0, 0), HubLighting.Frame("breathing", color, 3, 6));
        Assert.Equal(color, HubLighting.Frame("breathing", color, 3600, 6));
        Assert.Equal(new(100, 50, 25), HubLighting.Frame("breathing", color, 1.5, 6));
    }
    [Theory] [InlineData("fixed", 0)] [InlineData("fading", 9)] [InlineData("super-rainbow", 1)] [InlineData("unknown", 1)]
    public void UnsupportedColorsAndModesAreRejected(string mode, int count)
        => Assert.False(HubLighting.Valid(new(mode, Enumerable.Repeat(new RgbColor(20, 30, 40), count).ToArray())));
    [Fact] public void ColorCycleWrapsWithoutIndependentTimers()
    {
        var color = new RgbColor(255, 255, 255);
        Assert.Equal(new(255, 0, 0), HubLighting.Frame("color-cycle", color, 0, 6));
        Assert.Equal(new(0, 255, 0), HubLighting.Frame("color-cycle", color, 2, 6));
        Assert.Equal(new(255, 0, 0), HubLighting.Frame("color-cycle", color, 600, 6));
    }
    [Fact] public async Task NativeEffectUsesOnlySelectedConfiguredChannelAndPersists()
    {
        await using var f = new Fixture(); await f.Initialize(); var revision = f.Core.Snapshot.ControlRevision;
        var setting = new HubLightSetting("fading", [new(52, 137, 255), new(160, 121, 236)]);
        var result = await f.Core.RequestAsync(new("native", "PC", f.Core.Epoch, f.Core.Snapshot.LightingRevision, "SetLighting", LightingEffect: setting, LightingChannels: ["led1"]));
        Assert.Equal(Outcome.WrittenUnverified, result.Outcome); Assert.Equal(setting, f.Hub.LightEffects["led1"]);
        Assert.Equal("fading", f.Core.Settings.Lighting.Effect!.Mode); Assert.Equal(revision, f.Core.Snapshot.ControlRevision);
        var rejected = await f.Core.RequestAsync(new("wrong", "PC", f.Core.Epoch, f.Core.Snapshot.LightingRevision, "SetLighting", LightingEffect: setting, LightingChannels: ["led2"]));
        Assert.Equal(Outcome.Rejected, rejected.Outcome); Assert.Single(f.Hub.LightEffects);
    }
    [Fact] public async Task FrameSamplesOnceForBothDevicesAndDoesNotWriteSettings()
    {
        await using var f = new Fixture(); await f.Initialize(); var persisted = 0;
        f.Core.SettingsChanged += _ => persisted++;
        var lease = f.Core.BeginLightingStream(["led1"]); RgbColor? board = null; var samples = 0;
        Assert.True(await f.Core.SendLightingFrameAsync(lease, () => { samples++; return new(30, 60, 90); }, c => { board = c; return Task.CompletedTask; }));
        Assert.Equal(1, samples); Assert.Equal(board, f.Hub.LightEffects["led1"].Colors[0]); Assert.Equal(0, persisted);
        f.Core.EndLightingStream(lease); Assert.Equal(1, persisted);
    }
    [Fact] public async Task NewM5LightingRequestInvalidatesEveryFutureSyncFrame()
    {
        await using var f = new Fixture(); await f.Initialize(); var lease = f.Core.BeginLightingStream(["led1"]);
        await f.Core.SendLightingFrameAsync(lease, () => new(20, 30, 40));
        await f.Core.RequestAsync(new("m5", "M5GO", f.Core.Epoch, f.Core.Snapshot.LightingRevision, "SetLighting", Lighting: "white"));
        Assert.False(f.Core.LightingLeaseValid(lease));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Core.SendLightingFrameAsync(lease, () => new(0, 0, 0)));
        f.Core.EndLightingStream(lease); Assert.Equal("white", f.Core.Snapshot.Lighting.LastSent);
    }
    [Fact] public async Task CoolingGateSkipsFramesInsteadOfBuildingAQueue()
    {
        await using var f = new Fixture(); await f.Initialize(); f.Hub.DelayMs = 300;
        var lease = f.Core.BeginLightingStream(["led1"]);
        var fan = f.Core.RequestAsync(new("manual", "PC", f.Core.Epoch, f.Core.Snapshot.ControlRevision, "SetManualDuty", Duty: 50));
        var touchedBoard = false;
        Assert.False(await f.Core.SendLightingFrameAsync(lease, () => new(0, 0, 0), _ => { touchedBoard = true; return Task.CompletedTask; }));
        Assert.False(touchedBoard); Assert.Equal(Outcome.Confirmed, (await fan).Outcome);
        Assert.True(await f.Core.SendLightingFrameAsync(lease, () => new(10, 10, 10)));
    }
    [Fact] public async Task SuspendAndStopInvalidateTheLease()
    {
        await using var f = new Fixture(); await f.Initialize(); var lease = f.Core.BeginLightingStream(["led1"]);
        await f.Core.StopControlAsync(); Assert.False(f.Core.LightingLeaseValid(lease));
        await f.Core.OpenAsync(FlowDeck.Infrastructure.DemoHub.Identity); await f.Core.TickAsync();
        lease = f.Core.BeginLightingStream(["led1"]); f.Core.Suspend(); Assert.False(f.Core.LightingLeaseValid(lease));
    }
    [Fact] public async Task CompanionFailureCompletesTheHubWriteBeforeReleasingUsb()
    {
        await using var f = new Fixture(); await f.Initialize(); var lease = f.Core.BeginLightingStream(["led1"]);
        await Assert.ThrowsAsync<IOException>(() => f.Core.SendLightingFrameAsync(lease, () => new(1, 2, 3), _ => throw new IOException("board gone")));
        Assert.Equal(new(1, 2, 3), f.Hub.LightEffects["led1"].Colors[0]);
        await f.Core.TickAsync(); Assert.True(f.Core.Snapshot.HubReady);
    }
    [Fact] public async Task AnimatorStopsAtBaseColorAndCoolingKeepsRunning()
    {
        await using var f = new Fixture(); await f.Initialize();
        var values = new System.Collections.Concurrent.ConcurrentQueue<RgbColor>();
        await using var animator = new LightingAnimator(f.Core, ["led1"], "breathing", new(100, 50, 25), 4,
            c => { values.Enqueue(c); return Task.CompletedTask; });
        await animator.Started; await Task.Delay(260); await f.Core.TickAsync(); await animator.StopAsync();
        Assert.False(animator.Running); Assert.Null(animator.Error); Assert.True(animator.Frames >= 2);
        Assert.Equal(new(100, 50, 25), values.Last()); Assert.Equal(values.Last(), f.Hub.LightEffects["led1"].Colors[0]);
        Assert.Equal("fixed", f.Core.Snapshot.Lighting.LastSent);
    }
}
