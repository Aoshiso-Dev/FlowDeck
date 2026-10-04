using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceUpdateHandoffTests
{
    [Fact] public async Task FailedReleaseKeepsConnectionAndOnlyExplicitRetryCanProgress()
    {
        var released = false; var attempts = 0; var disconnects = 0;
        var handoff = new DeviceUpdateHandoff(() => { attempts++; return Task.FromResult(released); },
            () => { disconnects++; return ValueTask.CompletedTask; });
        Assert.False(await handoff.PrepareAsync()); Assert.False(handoff.Prepared); Assert.Equal(0, disconnects);
        released = true; Assert.True(await handoff.PrepareAsync()); Assert.True(handoff.Prepared);
        Assert.True(await handoff.PrepareAsync()); Assert.Equal(2, attempts); Assert.Equal(1, disconnects);
    }
    [Fact] public async Task CleanupFailureRetainsSuccessfulReleaseAndRetriesOnlyCleanup()
    {
        var releases = 0; var disconnects = 0;
        var handoff = new DeviceUpdateHandoff(() => { releases++; return Task.FromResult(true); }, () =>
        { if (++disconnects == 1) throw new IOException("close failed"); return ValueTask.CompletedTask; });
        Assert.False(await handoff.PrepareAsync()); Assert.False(handoff.Prepared);
        Assert.True(await handoff.PrepareAsync()); Assert.Equal(1, releases); Assert.Equal(2, disconnects);
    }
    [Fact] public async Task PendingReleaseCoalescesRepeatedPreparationAndDisconnectsOnlyAfterConfirmation()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var disconnected = false;
        var handoff = new DeviceUpdateHandoff(() => release.Task, () => { disconnected = true; return ValueTask.CompletedTask; });
        var first = handoff.PrepareAsync(); Assert.Same(first, handoff.PrepareAsync()); Assert.False(disconnected);
        release.SetResult(true); Assert.True(await first); Assert.True(disconnected);
    }
    [Fact] public async Task ReleaseExceptionDoesNotDiscardTheConnection()
    {
        var fail = true; var disconnected = false;
        var handoff = new DeviceUpdateHandoff(() => fail ? throw new IOException() : Task.FromResult(true),
            () => { disconnected = true; return ValueTask.CompletedTask; });
        Assert.False(await handoff.PrepareAsync()); Assert.False(disconnected);
        fail = false; Assert.True(await handoff.PrepareAsync()); Assert.True(disconnected);
    }

    [Fact] public async Task ConcurrentCallersShareOneReleaseAndCleanup()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = 0; var closes = 0;
        var handoff = new DeviceUpdateHandoff(() => { Interlocked.Increment(ref releases); return release.Task; },
            () => { Interlocked.Increment(ref closes); return ValueTask.CompletedTask; });
        var waits = Enumerable.Range(0, 24).Select(_ => Task.Run(handoff.PrepareAsync)).ToArray();
        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() => handoff.PrepareAsync().IsCompleted)));
        Assert.Equal(1, releases); Assert.Equal(0, closes);
        release.SetResult(true); Assert.All(await Task.WhenAll(waits), Assert.True);
        Assert.Equal(1, releases); Assert.Equal(1, closes);
    }

    private static ServiceDeploymentPlan Plan() => new("update", CoolingServiceProtocol.ServiceName,
        ServiceEnvironment.InstallationDirectory, ServiceEnvironment.DataDirectory, "2.0.1", new('a', 64), 12, 120,
        [ServiceFixture.Sid], true, [], ["更新"], Runtime: "devices-v2");

    [Fact] public async Task SetupReviewsWithoutReleaseAndRefusesElevationUntilBothHandoffStagesSucceed()
    {
        var failRelease = true; var preparations = 0; var launches = 0;
        var session = new ServiceSetupSession(_ => Task.FromResult(Plan()), _ =>
        { launches++; return Task.FromResult(new ServiceSetupExecution(true, null)); }, _ => Task.FromResult(false),
            () => { preparations++; return Task.FromResult(!failRelease); });
        await session.ReviewAsync(); Assert.Equal(0, preparations); Assert.Equal(0, launches);
        await session.ApplyAsync(); Assert.Equal(ServiceSetupState.Blocked, session.State); Assert.False(session.Prepared); Assert.Equal(0, launches);
        await session.ApplyAsync(); Assert.Equal(1, preparations);
        failRelease = false; await session.ReviewAsync(); await session.ApplyAsync();
        Assert.True(session.Prepared); Assert.Equal(2, preparations); Assert.Equal(1, launches); Assert.Equal(ServiceSetupState.Cancelled, session.State);
        await session.ReviewAsync(); await session.ApplyAsync(); Assert.Equal(2, preparations); Assert.Equal(2, launches);
    }
    [Fact] public async Task ChangedPackageDuringReleaseIsRecheckedBeforeElevating()
    {
        var plan = Plan(); var launches = 0;
        var session = new ServiceSetupSession(_ => Task.FromResult(plan), _ =>
        { launches++; return Task.FromResult(new ServiceSetupExecution(false, 0)); }, _ => Task.FromResult(true),
            () => { plan = plan with { ManifestHash = new('b', 64) }; return Task.FromResult(true); });
        await session.ReviewAsync(); await session.ApplyAsync();
        Assert.True(session.Prepared); Assert.Equal(ServiceSetupState.Blocked, session.State); Assert.Equal(0, launches);
        await session.ReviewAsync(); await session.ApplyAsync(); Assert.Equal(ServiceSetupState.Succeeded, session.State);
    }
    [Fact] public async Task AnAlreadyChangedPlanCannotStopCurrentControl()
    {
        var plan = Plan(); var preparations = 0;
        var session = new ServiceSetupSession(_ => Task.FromResult(plan), _ => throw new InvalidOperationException(), _ => Task.FromResult(true),
            () => { preparations++; return Task.FromResult(true); });
        await session.ReviewAsync(); plan = plan with { Blockers = ["not ready"] }; await session.ApplyAsync();
        Assert.Equal(0, preparations); Assert.False(session.Prepared);
    }
    [Fact] public async Task HandoffExceptionDoesNotLaunchAndAllowsReviewAgain()
    {
        var session = new ServiceSetupSession(_ => Task.FromResult(Plan()), _ => throw new InvalidOperationException(), _ => Task.FromResult(true),
            () => throw new IOException("release not confirmed"));
        await session.ReviewAsync(); await session.ApplyAsync(); Assert.Equal(ServiceSetupState.Failed, session.State); Assert.False(session.Prepared);
        await session.ReviewAsync(); Assert.Equal(ServiceSetupState.Ready, session.State);
    }
}
