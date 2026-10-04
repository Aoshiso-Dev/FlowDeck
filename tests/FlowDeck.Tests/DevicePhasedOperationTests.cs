using FlowDeck.Core;

namespace FlowDeck.Tests;

public sealed class DevicePhasedOperationTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Driver : IDevicePhasedOperation, IDeviceOperationPreparation
    {
        public readonly AsyncLocal<string?> Metadata = new();
        public IDeviceHardwareExecution Hardware = null!;
        public async Task<T> PrepareOperationAsync<T>(DevicePreparationKind kind, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
        {
            Metadata.Value = "prepared";
            try { return await operation(ct); } finally { Metadata.Value = null; }
        }
        public Task<T> RunPhasedOperationAsync<T>(DevicePreparationKind kind, IDeviceHardwareExecution hardware,
            Func<CancellationToken, Task<T>> operation, CancellationToken ct)
        { Hardware = hardware; return operation(ct); }
    }
    [Fact]
    public async Task DurableWorkBetweenPhasesFreesResourcesAndPreservesRequestMetadata()
    {
        var scheduler = new DeviceResourceScheduler(); var driver = new Driver(); var saved = Signal(); var resume = Signal();
        var job = scheduler.RunPreparedAsync(["smbus"], HardwarePriority.Manual, driver, DevicePreparationKind.Command, async ct =>
        {
            var first = await driver.Hardware.RunAsync(_ => Task.FromResult(driver.Metadata.Value), ct);
            saved.SetResult(); await resume.Task;
            var second = await driver.Hardware.RunAsync(async _ => { await Task.Yield(); return driver.Metadata.Value; }, ct);
            return (first, second);
        });
        try
        {
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(0, scheduler.PendingCount);
            Assert.Equal(75, await scheduler.RunAsync(["smbus"], HardwarePriority.Automatic, _ => Task.FromResult(75)).WaitAsync(TimeSpan.FromSeconds(3)));
            resume.SetResult(); Assert.Equal(("prepared", "prepared"), await job); Assert.Null(driver.Metadata.Value);
            Assert.Throws<InvalidOperationException>(() => driver.Hardware.CheckAuthorization());
            Assert.Throws<InvalidOperationException>(() => { _ = driver.Hardware.RunAsync(_ => Task.FromResult(1), default); });
        }
        finally { resume.TrySetResult(); await job; }
    }
    [Fact]
    public async Task EachQueuedPhaseRechecksOwnershipBeforeEnteringHardware()
    {
        var scheduler = new DeviceResourceScheduler(); var driver = new Driver(); var entered = Signal(); var resume = Signal(); var current = true; var calls = 0;
        var blocker = scheduler.RunAsync(["smbus"], HardwarePriority.Manual, async _ => { entered.SetResult(); await resume.Task; return true; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var job = scheduler.RunPreparedAsync(["smbus"], HardwarePriority.Manual, driver, DevicePreparationKind.Command,
            ct => driver.Hardware.RunAsync(_ => { calls++; return Task.FromResult(true); }, ct), authorize: () => { if (!current) throw new IOException("owner changed"); });
        try
        {
            Assert.Equal(1, scheduler.PendingCount); current = false; resume.SetResult(); await blocker;
            await Assert.ThrowsAsync<IOException>(() => job); Assert.Equal(0, calls);
        }
        finally { resume.TrySetResult(); await blocker; }
    }
    [Fact]
    public async Task CancellationDuringStorageNeverEntersTheNextPhysicalPhase()
    {
        var scheduler = new DeviceResourceScheduler(); var driver = new Driver(); var saved = Signal(); var resume = Signal(); var calls = 0;
        using var stop = new CancellationTokenSource();
        var job = scheduler.RunPreparedAsync(["smbus"], HardwarePriority.Manual, driver, DevicePreparationKind.Command, async ct =>
        {
            await driver.Hardware.RunAsync(_ => { calls++; return Task.FromResult(true); }, ct); saved.SetResult(); await resume.Task;
            return await driver.Hardware.RunAsync(_ => { calls++; return Task.FromResult(true); }, ct);
        }, stop.Token);
        try
        {
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel(); resume.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job); Assert.Equal(1, calls); Assert.Equal(0, scheduler.PendingCount);
        }
        finally { resume.TrySetResult(); }
    }
    [Fact]
    public async Task AnUnawaitedPhysicalPhaseIsDrainedAndCannotBeOverlappedByTheSameScope()
    {
        var scheduler = new DeviceResourceScheduler(); var driver = new Driver(); var entered = Signal(); var resume = Signal(); var leave = Signal();
        var job = scheduler.RunPreparedAsync(["smbus"], HardwarePriority.Manual, driver, DevicePreparationKind.Command, async ct =>
        {
            _ = driver.Hardware.RunAsync(async _ => { entered.SetResult(); await resume.Task; return 1; }, ct);
            await entered.Task;
            Assert.Throws<InvalidOperationException>(() => { _ = driver.Hardware.RunAsync(_ => Task.FromResult(2), ct); });
            leave.SetResult(); return true;
        });
        try
        {
            await leave.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.False(job.IsCompleted);
            var next = scheduler.RunAsync(["smbus"], HardwarePriority.Recovery, _ => Task.FromResult(100));
            Assert.False(next.IsCompleted); resume.SetResult(); Assert.True(await job); Assert.Equal(100, await next);
        }
        finally { resume.TrySetResult(); await job; }
    }
}
