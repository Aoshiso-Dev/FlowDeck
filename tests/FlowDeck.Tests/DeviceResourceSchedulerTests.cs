using FlowDeck.Core;

namespace FlowDeck.Tests;

public class DeviceResourceSchedulerTests
{
    [Fact]
    public async Task SharedDomainsSerializeWhileIndependentResourcesContinue()
    {
        var scheduler = new DeviceResourceScheduler(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var secondEntered = false;
        var first = scheduler.RunAsync(["usb", "bus"], HardwarePriority.Manual, async _ => { entered.SetResult(); await exit.Task; return 1; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = scheduler.RunAsync(["bus"], HardwarePriority.Manual, _ => { secondEntered = true; return Task.FromResult(2); });
        var independent = scheduler.RunAsync(["other-usb"], HardwarePriority.Manual, _ => Task.FromResult(3));
        Assert.Equal(3, await independent.WaitAsync(TimeSpan.FromSeconds(2))); Assert.False(secondEntered);
        exit.SetResult(); Assert.Equal(new[] { 1, 2 }, await Task.WhenAll(first, second));
    }
    [Fact]
    public async Task RecoveryPassesWaitingFramesAndPendingCancellationFreesCapacity()
    {
        var scheduler = new DeviceResourceScheduler(2); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var order = new List<string>();
        var active = scheduler.RunAsync(["bus"], HardwarePriority.Manual, async _ => { entered.SetResult(); await exit.Task; return 0; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); using var cancel = new CancellationTokenSource();
        var cancelled = scheduler.RunAsync(["bus"], HardwarePriority.Manual, _ => Task.FromResult(9), cancel.Token);
        var frame = scheduler.RunAsync(["bus"], HardwarePriority.LightingFrame, _ => { order.Add("frame"); return Task.FromResult(1); });
        await Assert.ThrowsAsync<IOException>(async () => await scheduler.RunAsync(["bus"], HardwarePriority.Manual, _ => Task.FromResult(8)));
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var release = scheduler.RunAsync(["bus"], HardwarePriority.Recovery, _ => { order.Add("release"); return Task.FromResult(2); });
        exit.SetResult(); await Task.WhenAll(active, frame, release); Assert.Equal(new[] { "release", "frame" }, order);
    }
    [Fact]
    public async Task CancellingARunningTaskDoesNotReleaseItsResourcesBeforeItActuallyReturns()
    {
        var scheduler = new DeviceResourceScheduler(); using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = scheduler.RunAsync(["bus"], HardwarePriority.LightingFrame, async _ => { entered.SetResult(); await exit.Task; return 1; }, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
        var recovery = scheduler.RunAsync(["bus"], HardwarePriority.Recovery, _ => Task.FromResult(2)); Assert.False(recovery.IsCompleted);
        exit.SetResult(); await Task.WhenAll(active, recovery); Assert.Equal(0, scheduler.PendingCount);
    }
    [Fact]
    public async Task WaitingRecoveryReservesEveryRequiredDomainWhileUnrelatedWorkContinues()
    {
        var scheduler = new DeviceResourceScheduler(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var order = new List<string>();
        var active = scheduler.RunAsync(["usb"], HardwarePriority.Manual, async _ => { entered.SetResult(); await exit.Task; return 0; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var release = scheduler.RunAsync(["usb", "bus"], HardwarePriority.Recovery, _ => { order.Add("release"); return Task.FromResult(1); });
        var frame = scheduler.RunAsync(["bus"], HardwarePriority.LightingFrame, _ => { order.Add("frame"); return Task.FromResult(2); });
        Assert.Equal(3, await scheduler.RunAsync(["other"], HardwarePriority.Manual, _ => Task.FromResult(3)).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(frame.IsCompleted); exit.SetResult(); await Task.WhenAll(active, release, frame);
        Assert.Equal(new[] { "release", "frame" }, order);
    }
}
