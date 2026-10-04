using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class RecoveryRaceTests
{
    [Fact]
    public async Task InFlightReadCannotCompareOldOutputWithANewCommandWaitingForUsb()
    {
        var hub = new GatedHub();
        await using var core = new ControlCoordinator(hub, new DemoTemperatures(), DemoHub.CreateSettings(), new TestLog());
        await core.OpenAsync(DemoHub.Identity); await core.TickAsync();
        await core.RequestAsync(new("first", "PC", core.Epoch, core.Snapshot.ControlRevision, "SetManualDuty", Duty: 40));
        hub.BlockNextRead = true; var poll = core.TickAsync();
        await hub.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var writing = core.RequestAsync(new("changed", "PC", core.Epoch, core.Snapshot.ControlRevision, "SetManualDuty", Duty: 54));
        Assert.True(core.Snapshot.FanBusy); Assert.Equal(54, core.Snapshot.TargetDuty); Assert.Equal(40, core.Snapshot.ConfirmedDuty);
        hub.ReleaseRead.SetResult(); await Task.WhenAll(poll, writing).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(Outcome.Confirmed, (await writing).Outcome); Assert.False(core.Snapshot.Faulted); Assert.Equal(54, core.Snapshot.ConfirmedDuty);
        await core.TickAsync(); Assert.False(core.Snapshot.Faulted);
    }
    [Theory]
    [InlineData("SetManualDuty")]
    [InlineData("SetLighting")]
    public async Task HubReopenRejectsDraftFromPreviousConnection(string command)
    {
        await using var f = new Fixture();
        await f.Initialize();
        var before = f.Core.Snapshot;
        var revision = command == "SetLighting" ? before.LightingRevision : before.ControlRevision;
        await f.Core.OpenAsync(DemoHub.Identity);
        var result = await f.Core.RequestAsync(f.Request(command, revision: revision) with { Lighting = "blue" });
        Assert.Equal(Outcome.Rejected, result.Outcome);
        Assert.Equal("CONFLICT", result.ErrorCode);
    }

    [Fact]
    public async Task ChangedLightingConfigurationRejectsPreviousLightingDraft()
    {
        await using var f = new Fixture();
        await f.Initialize();
        var before = f.Core.Snapshot.LightingRevision;
        var settings = f.Core.Settings;
        f.Core.UpdateSettings(settings with { Hardware = settings.Hardware with { LedDetails = [new("led1", "confirmed model", 24)] } });
        var result = await f.Core.RequestAsync(f.Request("SetLighting", revision: before) with { Lighting = "blue" });
        Assert.Equal(Outcome.Rejected, result.Outcome);
        Assert.Equal("CONFLICT", result.ErrorCode);
    }

    [Fact]
    public async Task NormalShutdownDoesNotLogCancelledPollingAsHubFailure()
    {
        var hub = new GatedHub { BlockNextRead = true };
        var log = new TestLog();
        var core = new ControlCoordinator(hub, new DemoTemperatures(), DemoHub.CreateSettings(), log);
        await core.OpenAsync(DemoHub.Identity);
        core.Start();
        await hub.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var entriesBeforeShutdown = log.Entries.Count;
        await core.DisposeAsync();
        Assert.Equal(entriesBeforeShutdown, log.Entries.Count);
    }

    [Fact]
    public async Task FallbackDoesNotLowerAHigherConfirmedChannel()
    {
        await using var f = new Fixture(DemoHub.CreateSettings() with { Limits = new(25, 70, 70) });
        await f.Initialize();
        await f.Manual(90);
        f.Hub.FailNextFanWrite = true;
        await f.Manual(50);
        Assert.Equal(FanMode.Fallback, f.Core.Snapshot.Mode);
        Assert.Equal(90, f.Core.Snapshot.ConfirmedDuty);
        Assert.All(f.Core.Snapshot.Channels, c => Assert.Equal(90, c.Duty));
    }

    [Fact]
    public async Task RecoveryWithoutConfirmedPolicyRemainsMonitorOnLaterTicks()
    {
        await using var f = new Fixture();
        await f.Initialize();
        f.Hub.FailNextFanWrite = true;
        await f.Manual(55);
        await f.Core.RequestAsync(f.Request("ResumeControl"));
        var writes = f.Hub.Writes;
        await f.Core.TickAsync();
        Assert.Equal(FanMode.Monitor, f.Core.Snapshot.Mode);
        Assert.False(f.Core.Snapshot.Faulted);
        Assert.False(f.Core.Snapshot.Managing);
        Assert.Equal(writes, f.Hub.Writes);
        await f.Core.StopControlAsync();
        Assert.Equal(writes, f.Hub.Writes);
    }

    [Fact]
    public async Task StopCancelsLightingStillWaitingForUsb()
    {
        var hub = new GatedHub();
        await using var core = new ControlCoordinator(hub, new DemoTemperatures(), DemoHub.CreateSettings(), new TestLog());
        await core.OpenAsync(DemoHub.Identity);
        await core.TickAsync();
        await core.RequestAsync(new("manual", "PC", core.Epoch, core.Snapshot.ControlRevision, "SetManualDuty", Duty: 40));
        hub.BlockNextRead = true;
        var poll = core.TickAsync();
        await hub.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var light = core.RequestAsync(new("light", "PC", core.Epoch, core.Snapshot.LightingRevision, "SetLighting", Lighting: "blue"));
        var stop = core.StopControlAsync();
        hub.ReleaseRead.SetResult();
        await Task.WhenAll(poll, stop, light).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(Outcome.Rejected, (await light).Outcome);
        Assert.Equal(0, hub.LightingWrites);
        Assert.Equal(FanMode.Monitor, core.Snapshot.Mode);
        Assert.Equal(70, core.Snapshot.ConfirmedDuty);
    }

    [Fact]
    public async Task ConcurrentStopCallsShareTheSameExitResult()
    {
        await using var f = new Fixture();
        await f.Initialize();
        await f.Manual(40);
        f.Hub.DelayMs = 150;
        var first = f.Core.StopControlAsync();
        var second = f.Core.StopControlAsync();
        var results = await Task.WhenAll(first, second);
        Assert.Equal(results[0].Id, results[1].Id);
        Assert.Equal(2, f.Hub.Writes);
        Assert.False(f.Core.Snapshot.Managing);
    }

    [Fact]
    public async Task RestartHoldKeepsConfirmedBoostTelemetryFresh()
    {
        var settings = DemoHub.CreateSettings() with { Limits = new(25, 70, 70, true, new(65, 70, 100, 5)) };
        await using var f = new Fixture(settings);
        await f.Initialize();
        await f.Manual(0);
        var restart = f.Manual(50);
        try
        {
            await Until(() => f.Hub.Writes == 2);
            await Task.Delay(80);
            Assert.Equal(100, f.Core.Snapshot.ConfirmedDuty);
            for (var i = 0; i < 4; i++)
            {
                f.Clock.Advance(1.1);
                await Until(() => f.Core.Snapshot.TelemetryTimestamp == f.Clock.GetTimestamp());
                await f.Core.TickAsync();
                Assert.False(f.Core.Snapshot.Faulted);
                Assert.Equal(100, f.Core.Snapshot.ConfirmedDuty);
            }
        }
        finally { f.Clock.Advance(10); await restart.WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.Equal(Outcome.Confirmed, (await restart).Outcome);
        Assert.Equal(50, f.Core.Snapshot.ConfirmedDuty);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++) { if (condition()) return; await Task.Delay(10); }
        throw new TimeoutException();
    }

    private sealed class GatedHub : IHubAdapter
    {
        private readonly DemoHub inner = new() { DelayMs = 0 };
        public bool BlockNextRead { get; set; }
        public int LightingWrites { get; private set; }
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsDemo => true;
        public string Version => "GATED TEST";
        public Task<HubIdentity[]> ProbeAsync(CancellationToken ct = default) => inner.ProbeAsync(ct);
        public Task<HubStatus> OpenAsync(HubIdentity identity, long generation, CancellationToken ct = default) => inner.OpenAsync(identity, generation, ct);
        public async Task<HubStatus> ReadAsync(CancellationToken ct = default)
        {
            if (BlockNextRead) { BlockNextRead = false; ReadEntered.TrySetResult(); await ReleaseRead.Task.WaitAsync(ct); }
            return await inner.ReadAsync(ct);
        }
        public Task<HubResult> SetFanGroupAsync(string[] channels, int duty, CancellationToken ct = default) => inner.SetFanGroupAsync(channels, duty, ct);
        public Task<HubResult> SetLightingAsync(string[] channels, string preset, CancellationToken ct = default)
        { LightingWrites++; return inner.SetLightingAsync(channels, preset, ct); }
        public Task CloseAsync(CancellationToken ct = default) => inner.CloseAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
