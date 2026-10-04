using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class CpuFanTests
{
    private sealed class Driver(TestClock clock) : ICpuFanDriver
    {
        public List<int> Writes { get; } = [];
        public int Restores, Duty = 65, Rpm = 1400;
        public bool FailWrite, FailRestore, FailRead, Stale;
        public Task<CpuFanReading> ReadAsync(CancellationToken ct = default) => FailRead
            ? throw new IOException("read failed") : Task.FromResult(new CpuFanReading(Duty, Rpm, clock.GetTimestamp() - (Stale ? 4000 : 0)));
        public Task<CpuFanReading> WriteAsync(int duty, CancellationToken ct = default)
        { Writes.Add(duty); if (FailWrite) throw new IOException("write failed"); Duty = duty; return ReadAsync(ct); }
        public Task RestoreAsync(CancellationToken ct = default)
        { Restores++; if (FailRestore) throw new IOException("restore failed"); Duty = 65; return Task.CompletedTask; }
    }
    private static CpuFanSettings Config() => new(true, 30,
        new("cpu-fan", "CPU_FAN1", Cpu: new("cpu", [new(40, 30), new(80, 90)], 95, 85), DescentDelaySeconds: 2, DescentPerSecond: 5));
    [Fact] public async Task MonitoringStartupAndDisposalNeverWrite()
    {
        var clock = new TestClock(); var driver = new Driver(clock);
        await using (var owner = new CpuFanController(driver, () => [], new TestLog(), clock))
        { await owner.TickAsync(); Assert.Equal("monitor", owner.State.Mode); Assert.Equal(1400, owner.State.Reading!.Rpm); }
        Assert.Empty(driver.Writes); Assert.Equal(0, driver.Restores);
    }
    [Fact] public async Task MissingWiringMinimumAndOutOfRangeNeverWrite()
    {
        var clock = new TestClock(); var driver = new Driver(clock);
        await using var owner = new CpuFanController(driver, () => [], new TestLog(), clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ManualAsync(new(), 50));
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ManualAsync(new(true), 50));
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ManualAsync(new(true, 30), 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ManualAsync(new(true, 30), 29));
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ManualAsync(new(true, 30), 101));
        Assert.Empty(driver.Writes);
    }
    [Fact] public async Task ManualAndRestoreAreIndependentOfNzxt()
    {
        await using var nzxt = new Fixture(); await nzxt.Initialize(); await nzxt.Manual(54);
        var before = nzxt.Core.Snapshot; var writes = nzxt.Hub.Writes;
        var driver = new Driver(nzxt.Clock);
        await using var owner = new CpuFanController(driver, () => nzxt.Core.Snapshot.Sensors, new TestLog(), nzxt.Clock);
        await owner.ManualAsync(new(true, 30), 47);
        Assert.Equal(47, owner.State.Reading!.Duty); Assert.Equal("manual", owner.State.Mode);
        await owner.RestoreAsync(); Assert.Equal(65, driver.Duty); Assert.False(owner.State.Managing);
        Assert.Equal(writes, nzxt.Hub.Writes); Assert.Equal(before.ControlRevision, nzxt.Core.Snapshot.ControlRevision);
        Assert.Equal(54, nzxt.Core.Snapshot.ConfirmedDuty);
    }
    [Fact] public async Task StaleTemperatureLatches100UntilExplicitRestoration()
    {
        var clock = new TestClock(); var driver = new Driver(clock);
        var sensor = new SensorReading("cpu", "CPU", "cpu", 60, clock.GetTimestamp());
        await using var owner = new CpuFanController(driver, () => [sensor], new TestLog(), clock);
        await owner.AutoAsync(Config()); Assert.Equal(60, driver.Duty);
        clock.Advance(3); await owner.TickAsync(); Assert.Equal(100, driver.Duty); Assert.Equal("fault", owner.State.Mode);
        sensor = sensor with { Timestamp = clock.GetTimestamp() }; await owner.TickAsync(); Assert.Equal("fault", owner.State.Mode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ManualAsync(new(true, 30), 40));
        await owner.RestoreAsync(); await owner.ManualAsync(new(true, 30), 40); Assert.Equal(40, driver.Duty);
    }
    [Fact] public async Task AutoCopiesCurveAndLimitsDescent()
    {
        var clock = new TestClock(); var driver = new Driver(clock); double temperature = 80;
        await using var owner = new CpuFanController(driver, () => [new("cpu", "CPU", "cpu", temperature, clock.GetTimestamp())], new TestLog(), clock);
        var settings = Config(); await owner.AutoAsync(settings); Assert.Equal(90, driver.Duty);
        settings.Profile!.Cpu!.Points[0] = new(40, 100);
        temperature = 40; await owner.TickAsync(); clock.Advance(2); await owner.TickAsync(); Assert.Equal(90, driver.Duty);
        clock.Advance(1); await owner.TickAsync(); Assert.Equal(85, driver.Duty);
        temperature = 80; await owner.TickAsync(); Assert.Equal(90, driver.Duty);
        temperature = 95; await owner.TickAsync(); Assert.Equal(100, driver.Duty); Assert.Equal("fault", owner.State.Mode);
    }
    [Fact] public async Task ZeroRpmTimerIsNotResetByChangingAutoOutput()
    {
        var clock = new TestClock(); var driver = new Driver(clock) { Rpm = 0 }; double temperature = 50;
        await using var owner = new CpuFanController(driver, () => [new("cpu", "CPU", "cpu", temperature, clock.GetTimestamp())], new TestLog(), clock);
        await owner.AutoAsync(Config()); await owner.TickAsync();
        for (var i = 0; i < 5; i++) { temperature += 2; clock.Advance(1); await owner.TickAsync(); }
        Assert.Equal("fault", owner.State.Mode); Assert.Equal(100, driver.Duty);
    }
    [Fact] public async Task ExternalDutyChangeFaultsInsteadOfSilentlyFightingOtherController()
    {
        var clock = new TestClock(); var driver = new Driver(clock);
        await using var owner = new CpuFanController(driver, () => [], new TestLog(), clock);
        await owner.ManualAsync(new(true, 30), 50); driver.Duty = 20; await owner.TickAsync();
        Assert.Equal("fault", owner.State.Mode); Assert.Equal(100, driver.Duty);
        var writes = driver.Writes.Count; driver.Duty = 20; await owner.TickAsync(); Assert.Equal(writes, driver.Writes.Count);
    }
    [Fact] public async Task FailedWritesRetainRestorationObligation()
    {
        var clock = new TestClock(); var driver = new Driver(clock) { FailWrite = true };
        await using var owner = new CpuFanController(driver, () => [], new TestLog(), clock);
        await Assert.ThrowsAsync<IOException>(() => owner.ManualAsync(new(true, 30), 50));
        Assert.Equal(new[] { 50, 100 }, driver.Writes); Assert.True(owner.State.Managing);
        await owner.RestoreAsync(); Assert.Equal(1, driver.Restores); Assert.False(owner.State.Managing);
    }
    [Fact] public async Task FailedRestoreRemainsRetryableAndReadFailureDoesNotUndoVerifiedRestore()
    {
        var clock = new TestClock(); var driver = new Driver(clock);
        await using var owner = new CpuFanController(driver, () => [], new TestLog(), clock);
        await owner.ManualAsync(new(true, 30), 50); driver.FailRestore = true;
        await Assert.ThrowsAsync<IOException>(() => owner.RestoreAsync()); Assert.True(owner.State.Managing);
        driver.FailRestore = false; driver.FailRead = true; await owner.RestoreAsync(); Assert.False(owner.State.Managing); Assert.Null(owner.State.Reading);
    }
    [Fact] public async Task SleepRestoresAndResumeDoesNotRestartControl()
    {
        var clock = new TestClock(); var driver = new Driver(clock);
        await using var owner = new CpuFanController(driver, () => [], new TestLog(), clock);
        await owner.ManualAsync(new(true, 30), 50); await owner.SuspendAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ManualAsync(new(true, 30), 50));
        owner.Resume(); await owner.TickAsync(); Assert.Equal("monitor", owner.State.Mode); Assert.Equal(65, driver.Duty);
    }
    [Fact] public async Task ClosingDuringBoundedTestRestoresOriginalControl()
    {
        var clock = new TestClock(); var driver = new Driver(clock);
        var owner = new CpuFanController(driver, () => [], new TestLog(), clock);
        var test = owner.TestAsync(); Assert.Equal(100, driver.Duty);
        await owner.DisposeAsync(); await Assert.ThrowsAsync<IOException>(() => test);
        Assert.Equal(65, driver.Duty); Assert.Equal(1, driver.Restores); Assert.False(owner.State.Managing);
    }
    [Fact] public async Task MissingRpmRejectsIdentificationWithoutWriting()
    {
        var clock = new TestClock(); var driver = new Driver(clock) { Rpm = 0 };
        await using var owner = new CpuFanController(driver, () => [], new TestLog(), clock);
        await Assert.ThrowsAsync<IOException>(() => owner.TestAsync()); Assert.Empty(driver.Writes);
    }
    [Fact] public async Task StaleDriverFeedbackIsNotReportedAsConfirmed()
    {
        var clock = new TestClock(); var driver = new Driver(clock) { Stale = true };
        await using var owner = new CpuFanController(driver, () => [], new TestLog(), clock);
        await Assert.ThrowsAsync<IOException>(() => owner.ManualAsync(new(true, 30), 50));
        Assert.Equal("fault", owner.State.Mode); Assert.Null(owner.State.Reading);
    }
    [Fact] public async Task SavedCpuFanSetupDoesNotImportNzxtLimitsOrResumeOnStartup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NzxtCpuFan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonSettingsStore(directory); var saved = DemoHub.CreateSettings() with { CpuFan = Config() };
            await store.SaveAsync(saved); var loaded = await store.LoadAsync(); Assert.Equal(30, loaded.CpuFan.Minimum); Assert.Equal(saved.Limits, loaded.Limits);
            Assert.Null(new AppSettings().CpuFan.Minimum); Assert.False(new AppSettings().CpuFan.WiringConfirmed);
        }
        finally { Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(directory)); Directory.Delete(directory, true); }
    }
}

public class CpuFanRegisterTests
{
    private sealed class Registers : ICpuFanRegisterIo
    {
        public Dictionary<ushort, byte> Values = new() { [0x202] = 32, [0x209] = 170, [0x003] = 170, [0x102] = 64, [0x109] = 200 };
        public List<(ushort Address, byte Value)> Writes = [];
        public bool IgnoreWrites;
        public IDisposable Acquire() => new Lease();
        public byte Read(ushort address) => Values[address];
        public int ReadRpm() => 1500;
        public void Write(ushort address, byte value) { Writes.Add((address, value)); if (IgnoreWrites) return; Values[address] = value; if (Values[0x202] == 0) Values[0x003] = Values[0x209]; }
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }
    [Fact] public void FirstManualWriteNeverAppliesAnUninitializedZeroAndRestoresOriginalRegisters()
    {
        var io = new Registers(); var control = new CpuFanRegisters(io);
        control.Read(); Assert.Empty(io.Writes); control.Write(54);
        Assert.Equal(((ushort)0x209, (byte)138), io.Writes[0]); Assert.Equal(((ushort)0x202, (byte)0), io.Writes[1]);
        control.Write(40); control.Restore(); Assert.False(control.NeedsRestore);
        Assert.Equal(32, io.Values[0x202]); Assert.Equal(170, io.Values[0x209]);
        Assert.Equal(64, io.Values[0x102]); Assert.Equal(200, io.Values[0x109]);
        Assert.All(io.Writes, item => Assert.Contains(item.Address, new ushort[] { 0x202, 0x209 }));
    }
    [Fact] public void FailedRestorationKeepsOriginalValuesForRetry()
    {
        var io = new Registers(); var control = new CpuFanRegisters(io); control.Write(100); io.IgnoreWrites = true;
        Assert.Throws<IOException>(() => control.Restore()); Assert.True(control.NeedsRestore);
        io.IgnoreWrites = false; control.Restore(); Assert.False(control.NeedsRestore); Assert.Equal(32, io.Values[0x202]); Assert.Equal(170, io.Values[0x209]);
    }
    [Theory] [InlineData(0)] [InlineData(-1)] [InlineData(101)]
    public void UnboundedOutputIsRejectedBeforeIo(int duty)
    { var io = new Registers(); var control = new CpuFanRegisters(io); Assert.Throws<ArgumentOutOfRangeException>(() => control.Write(duty)); Assert.Empty(io.Writes); }
}
