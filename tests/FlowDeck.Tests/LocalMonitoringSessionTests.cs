using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class LocalMonitoringSessionTests
{
    private sealed class Backend : IHardwareSensorBackend
    {
        public HardwareSensorDevice[] Values = [Gpu("gpu-a"), Gpu("gpu-b")];
        public Exception? Failure; public TaskCompletionSource? Wait; public int Reads, Disposals, FailDisposal;
        public async Task<HardwareSensorDevice[]> ReadDevicesAsync(CancellationToken ct)
        { Reads++; if (Wait is not null) await Wait.Task.WaitAsync(ct); if (Failure is not null) throw Failure; return Values; }
        public ValueTask DisposeAsync() { Disposals++; if (FailDisposal-- > 0) throw new IOException("mock cleanup"); return ValueTask.CompletedTask; }
    }
    internal static HardwareSensorDevice Gpu(string id) => new("/" + id, "Synthetic GPU", DeviceKind.Graphics, new('a', 64), null, SettingsTransfer.Hash(id)[..32].ToLowerInvariant(), null,
        [new("/" + id + "/temp", "GPU", "/" + id, DeviceFeature.Temperature, 52, TimeProvider.System.GetTimestamp()),
         new("/" + id + "/rpm", "GPU fan", "/" + id, DeviceFeature.Rpm, 0, TimeProvider.System.GetTimestamp())]);
    [Fact] public async Task SnapshotShowsOnlyObservedValuesAndDoesNotInventCpuOrMotherboardSensors()
    {
        var backend = new Backend(); await using var monitor = new LocalMonitoringSession(backend);
        Assert.Empty(monitor.Snapshot.Temperatures); await monitor.PollAsync();
        var snapshot = monitor.Snapshot; Assert.Equal(2, snapshot.Temperatures.Length); Assert.Equal(2, snapshot.Fans.Length);
        Assert.All(snapshot.Fans, s => { Assert.Equal(0d, s.Value); Assert.Null(s.Duty); Assert.Equal("監視のみ", s.Control); });
        Assert.NotEqual(snapshot.Temperatures[0].Key, snapshot.Temperatures[1].Key);
        Assert.NotEqual(snapshot.Temperatures[0].Name, snapshot.Temperatures[1].Name);
    }
    [Fact] public async Task ReorderedAndMissingDevicesKeepTheirKeysAndMissingValuesBecomeDisconnected()
    {
        var backend = new Backend(); await using var monitor = new LocalMonitoringSession(backend); await monitor.PollAsync();
        var first = monitor.Snapshot; Assert.Equal(2, first.Temperatures.Length); backend.Values = backend.Values.Reverse().ToArray(); await monitor.PollAsync();
        Assert.Equal(first.Temperatures.Select(t => t.Key), monitor.Snapshot.Temperatures.Select(t => t.Key));
        backend.Values = []; await monitor.PollAsync();
        Assert.All(monitor.Snapshot.Temperatures.Concat(monitor.Snapshot.Fans), v => { Assert.Null(v.Value); Assert.Equal("切断", v.State); });
    }
    [Theory] [InlineData(DeviceMeasurementIssue.OutOfRange, "範囲外")] [InlineData(DeviceMeasurementIssue.Unavailable, "未取得")]
    public async Task InvalidAcquisitionReasonIsPreserved(DeviceMeasurementIssue issue, string expected)
    {
        var gpu = Gpu("gpu"); gpu = gpu with { Sensors = gpu.Sensors.Select(s => s with { Value = null, Issue = issue }).ToArray() };
        var backend = new Backend { Values = [gpu] }; await using var monitor = new LocalMonitoringSession(backend); await monitor.PollAsync();
        Assert.Single(monitor.Snapshot.Temperatures); Assert.Single(monitor.Snapshot.Fans);
        Assert.All(monitor.Snapshot.Temperatures.Concat(monitor.Snapshot.Fans), s => { Assert.Null(s.Value); Assert.Equal(expected, s.State); });
    }
    [Fact] public async Task OldReadingsAreNotRefreshedByPollingAndAcquisitionFailureHidesCachedValues()
    {
        var old = Gpu("old"); old = old with { Sensors = old.Sensors.Select(s => s with { Timestamp = TimeProvider.System.GetTimestamp() - 5 * TimeProvider.System.TimestampFrequency }).ToArray() };
        var backend = new Backend { Values = [old] }; await using var monitor = new LocalMonitoringSession(backend); await monitor.PollAsync();
        Assert.Equal("更新停止", monitor.Snapshot.Temperatures[0].State); Assert.Null(monitor.Snapshot.Temperatures[0].Value);
        backend.Values = [Gpu("old")]; await monitor.PollAsync(); Assert.Equal(52d, monitor.Snapshot.Temperatures[0].Value);
        backend.Failure = new IOException("secret details"); await monitor.PollAsync();
        Assert.Null(monitor.Snapshot.Temperatures[0].Value); Assert.DoesNotContain("secret", monitor.Snapshot.Problem!);
    }
    [Fact] public async Task ConcurrentPollsCoalesceAndClosingCancelsBeforeReleasingTheBackend()
    {
        var backend = new Backend { Wait = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var monitor = new LocalMonitoringSession(backend);
        var first = monitor.PollAsync(); var next = monitor.PollAsync(); Assert.Same(first, next); Assert.Equal(1, backend.Reads);
        await monitor.DisposeAsync(); await first; Assert.Equal(1, backend.Disposals);
        await monitor.PollAsync(); await monitor.DisposeAsync(); Assert.Equal(1, backend.Reads); Assert.Equal(1, backend.Disposals);
    }
    [Fact] public async Task FailedReleaseCanBeRetriedWithoutRestartingMonitoring()
    {
        var backend = new Backend { FailDisposal = 1 }; var monitor = new LocalMonitoringSession(backend); await monitor.PollAsync();
        await Assert.ThrowsAsync<IOException>(async () => await monitor.DisposeAsync()); await monitor.PollAsync();
        Assert.Equal(1, backend.Reads); await monitor.DisposeAsync(); Assert.Equal(2, backend.Disposals);
    }
    [Fact] public async Task MissingDriverAndMalformedCapabilitiesDoNotAppearAsAWorkingMonitor()
    {
        var backend = new Backend { Failure = new DllNotFoundException() }; await using var monitor = new LocalMonitoringSession(backend);
        await monitor.PollAsync(); Assert.Empty(monitor.Snapshot.Temperatures); Assert.Contains("ドライバー", monitor.Snapshot.Status);
        backend.Failure = null; backend.Values = [Gpu("invalid") with { Kind = DeviceKind.Motherboard }];
        await monitor.PollAsync(); Assert.Empty(monitor.Snapshot.Temperatures); Assert.NotNull(monitor.Snapshot.Problem);
    }
}
