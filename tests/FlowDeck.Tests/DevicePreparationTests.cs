using FlowDeck.Core;

namespace FlowDeck.Tests;

public sealed class DevicePreparationTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Preparation(string name) : IDeviceOperationPreparation
    {
        public static readonly AsyncLocal<string?> Current = new();
        public readonly TaskCompletionSource Entered = Signal();
        public Task? Continue;
        public bool Fail;
        public async Task<T> PrepareOperationAsync<T>(DevicePreparationKind kind, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
        {
            Entered.TrySetResult(); if (Continue is not null) await Continue;
            ct.ThrowIfCancellationRequested(); if (Fail) throw new IOException("preparation failed");
            var before = Current.Value; Current.Value = name;
            try { return await operation(ct); } finally { Current.Value = before; }
        }
    }
    [Fact]
    public async Task SlowPreparationDoesNotReserveTheBusButStartedHardwareRemainsExclusive()
    {
        var scheduler = new DeviceResourceScheduler(); var prepareContinue = Signal(); var hardwareEntered = Signal(); var hardwareContinue = Signal();
        var preparation = new Preparation("lighting") { Continue = prepareContinue.Task };
        var job = scheduler.RunPreparedAsync(["smbus"], HardwarePriority.LightingFrame, preparation, DevicePreparationKind.LightingFrame,
            async _ => { hardwareEntered.SetResult(); await hardwareContinue.Task; return 1; });
        try
        {
            await preparation.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(0, scheduler.PendingCount);
            Assert.Equal(100, await scheduler.RunAsync(["smbus"], HardwarePriority.Recovery, _ => Task.FromResult(100)).WaitAsync(TimeSpan.FromSeconds(3)));
            prepareContinue.SetResult(); await hardwareEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var cooling = scheduler.RunAsync(["smbus"], HardwarePriority.Automatic, _ => Task.FromResult(75));
            Assert.False(cooling.IsCompleted); hardwareContinue.SetResult(); Assert.Equal(1, await job); Assert.Equal(75, await cooling);
        }
        finally { prepareContinue.TrySetResult(); hardwareContinue.TrySetResult(); await job; }
    }
    [Fact]
    public async Task QueuedOperationsKeepTheirOwnPreparationContextAcrossUnrelatedDispatch()
    {
        var scheduler = new DeviceResourceScheduler(); var entered = Signal(); var next = Signal();
        var blocker = scheduler.RunAsync(["smbus"], HardwarePriority.Manual, async _ => { entered.SetResult(); await next.Task; return 0; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); var a = new Preparation("a"); var b = new Preparation("b");
        var first = scheduler.RunPreparedAsync(["smbus"], HardwarePriority.Monitoring, a, DevicePreparationKind.Polling,
            async _ => { await Task.Yield(); return Preparation.Current.Value; });
        var second = scheduler.RunPreparedAsync(["smbus"], HardwarePriority.Monitoring, b, DevicePreparationKind.Polling,
            async _ => { await Task.Yield(); return Preparation.Current.Value; });
        try
        {
            Assert.Equal(2, scheduler.PendingCount); Assert.Null(Preparation.Current.Value);
            next.SetResult(); await blocker; Assert.Equal("a", await first); Assert.Equal("b", await second); Assert.Null(Preparation.Current.Value);
        }
        finally { next.TrySetResult(); await blocker; }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CancellationOrFailureDuringPreparationNeverRunsHardware(bool cancel)
    {
        var scheduler = new DeviceResourceScheduler(); var ready = Signal(); var preparation = new Preparation("test") { Continue = ready.Task, Fail = !cancel };
        using var cancellation = new CancellationTokenSource(); var writes = 0;
        var job = scheduler.RunPreparedAsync(["smbus"], HardwarePriority.Manual, preparation, DevicePreparationKind.Command,
            _ => { writes++; return Task.FromResult(1); }, cancellation.Token);
        await preparation.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); if (cancel) cancellation.Cancel(); ready.SetResult();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job); else await Assert.ThrowsAsync<IOException>(() => job);
        Assert.Equal(0, writes); Assert.Equal(0, scheduler.PendingCount); Assert.Null(Preparation.Current.Value);
    }
    [Fact]
    public void InvalidRequestsAreRejectedBeforeStartingPreparation()
    {
        var scheduler = new DeviceResourceScheduler(); var p = new Preparation("test");
        Assert.Throws<ArgumentException>(() => { _ = scheduler.RunPreparedAsync([], HardwarePriority.Manual, p, DevicePreparationKind.Command, _ => Task.FromResult(1)); });
        Assert.Throws<ArgumentException>(() => { _ = scheduler.RunPreparedAsync(["smbus"], HardwarePriority.Manual, p, (DevicePreparationKind)100, _ => Task.FromResult(1)); });
        Assert.False(p.Entered.Task.IsCompleted);
    }
}
