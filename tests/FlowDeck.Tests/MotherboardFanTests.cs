using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class MotherboardFanTests
{
    private sealed class Driver(TimeProvider? time = null) : ICpuFanDriver
    {
        public int Duty = 65, Restores;
        public bool FailRestore;
        public List<int> Writes = [];
        public Task<CpuFanReading> ReadAsync(CancellationToken ct = default) => Task.FromResult(new CpuFanReading(Duty, Duty * 20, (time ?? TimeProvider.System).GetTimestamp()));
        public Task<CpuFanReading> WriteAsync(int duty, CancellationToken ct = default) { Writes.Add(duty); Duty = duty; return ReadAsync(ct); }
        public Task RestoreAsync(CancellationToken ct = default) { Restores++; if (FailRestore) throw new IOException("restore failed"); Duty = 65; return Task.CompletedTask; }
    }
    [Fact] public async Task IndependentOwnersDoNotShareSettingsDutyOrRestoration()
    {
        var drivers = MotherboardFans.Ports.ToDictionary(p => p.Id, _ => new Driver());
        await using var fans = new MotherboardFanControllers(p => drivers[p.Id], () => [], new TestLog());
        await fans["cpu-fan1"].ManualAsync(new(true, 40), 70);
        await fans["cha-fan1"].ManualAsync(new(true, 25), 35);
        Assert.Equal(70, drivers["cpu-fan1"].Duty); Assert.Equal(35, drivers["cha-fan1"].Duty);
        await fans["cha-fan1"].RestoreAsync();
        Assert.True(fans["cpu-fan1"].State.Managing); Assert.Equal(70, drivers["cpu-fan1"].Duty);
        Assert.Equal(65, drivers["cha-fan1"].Duty); Assert.Equal(0, drivers["cpu-fan1"].Restores);
    }
    [Fact] public async Task OneRestoreFailureDoesNotSkipOtherPortAndCanBeRetried()
    {
        var drivers = MotherboardFans.Ports.ToDictionary(p => p.Id, _ => new Driver());
        await using var fans = new MotherboardFanControllers(p => drivers[p.Id], () => [], new TestLog());
        foreach (var p in MotherboardFans.Ports) await fans[p.Id].ManualAsync(new(true, 30), 55);
        drivers["cpu-fan1"].FailRestore = true;
        await Assert.ThrowsAsync<AggregateException>(() => fans.RestoreAsync());
        Assert.False(fans["cha-fan1"].State.Managing); Assert.Equal(65, drivers["cha-fan1"].Duty);
        Assert.True(fans["cpu-fan1"].State.Managing);
        drivers["cpu-fan1"].FailRestore = false; await fans.RestoreAsync();
        Assert.All(fans.States.Values, s => Assert.False(s.Managing));
    }
    [Fact] public async Task SuspendRestoresBothAndResumeDoesNotReapply()
    {
        var drivers = MotherboardFans.Ports.ToDictionary(p => p.Id, _ => new Driver());
        await using var fans = new MotherboardFanControllers(p => drivers[p.Id], () => [], new TestLog());
        foreach (var p in MotherboardFans.Ports) await fans[p.Id].ManualAsync(new(true, 30), 55);
        await fans.SuspendAsync(); fans.Resume();
        foreach (var p in MotherboardFans.Ports) await fans[p.Id].TickAsync();
        Assert.All(fans.States.Values, s => Assert.Equal("monitor", s.Mode));
        Assert.All(drivers.Values, d => { Assert.Single(d.Writes); Assert.Equal(65, d.Duty); });
    }
    [Fact] public async Task NewChassisPortCannotInheritCpuWiringOrMinimum()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("{\"cpuFan\":{\"wiringConfirmed\":true,\"minimum\":40}}", Contract.Json)!;
        Assert.Equal(40, MotherboardFans.Settings(old, "cpu-fan1").Minimum);
        Assert.Null(old.ChassisFan.Minimum); Assert.False(old.ChassisFan.WiringConfirmed);
        var driver = new Driver(); await using var owner = new CpuFanController(driver, () => [], new TestLog(), name: "CHA_FAN1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ManualAsync(old.ChassisFan, 100));
        Assert.Empty(driver.Writes);
        var updated = MotherboardFans.WithSettings(old, "cha-fan1", new(true, 35));
        Assert.Equal(old.CpuFan, updated.CpuFan); Assert.Equal(old.Limits, updated.Limits);
        Assert.Equal(35, Contract.Copy(updated).ChassisFan.Minimum);
    }
    [Fact] public async Task TwoTemperatureCurvesChooseHigherDemandAndMissingBoardTemperatureFaultsOnlyItsOwner()
    {
        var clock = new TestClock(); var driver = new Driver(clock); var other = new Driver(clock); var missing = false;
        SensorReading[] Temperatures() => [new("cpu", "CPU", "cpu", 40, clock.GetTimestamp()), new("board", "Board", "motherboard", missing ? null : 70, clock.GetTimestamp())];
        await using var cpu = new CpuFanController(other, Temperatures, new TestLog(), clock);
        await using var chassis = new CpuFanController(driver, Temperatures, new TestLog(), clock, "CHA_FAN1");
        await cpu.ManualAsync(new(true, 40), 60);
        var settings = new CpuFanSettings(true, 30, new("cha", "CHA_FAN1", Cpu: new("cpu", [new(40, 30), new(80, 70)], 95, 85),
            Gpu: new("board", [new(40, 30), new(80, 90)], 90, 80), DescentDelaySeconds: 2, DescentPerSecond: 5));
        await chassis.AutoAsync(settings); Assert.Equal(75, driver.Duty);
        missing = true; await chassis.TickAsync();
        Assert.Equal("fault", chassis.State.Mode); Assert.Equal(100, driver.Duty);
        Assert.Equal("manual", cpu.State.Mode); Assert.Equal(60, other.Duty);
    }
    [Fact] public void HeaderIdentityIsRestrictedToVerifiedBoardAndSensorIds()
    {
        Assert.Equal(MotherboardFans.Chassis, MotherboardFans.ForSensor(new("ASRock X870E Taichi White", []), "/lpc/nct6799d/0/fan/0"));
        Assert.Null(MotherboardFans.ForSensor(new("different board", []), "/lpc/nct6799d/0/fan/0"));
        Assert.Equal("nct6686d-0", MotherboardFans.ForSensor(new("ASRock X870E Taichi White", []), "/lpc/nct6686d/0/fan/0")?.Id);
        Assert.Throws<NotSupportedException>(() => MotherboardFans.Find("pump"));
    }
    [Fact] public async Task DashboardUsesEachPortsOwnModeDutyAndIdentity()
    {
        await using var f = new Fixture(); await f.Initialize();
        var board = new BoardSnapshot("ASRock X870E Taichi White", MotherboardFans.Ports.Select(p =>
            new BoardSensor(p.SensorId, "Fan", "Nuvoton NCT6799D", "fan", "rpm", 1000, 1000)).ToArray(), f.Clock.GetTimestamp());
        var states = new Dictionary<string, CpuFanState> { ["cpu-fan1"] = new("manual", 70, new(70, 1400, f.Clock.GetTimestamp()), true),
            ["cha-fan1"] = new("auto", 45, new(45, 900, f.Clock.GetTimestamp()), true) };
        var rows = DashboardDisplay.ReadAll(f.Core.Settings, f.Core.Snapshot, board, states, f.Clock);
        var cpu = rows.Single(r => r.Name == "CPU_FAN1"); var cha = rows.Single(r => r.Name == "CHA_FAN1");
        Assert.Equal(70, cpu.Duty); Assert.Equal(45, cha.Duty); Assert.Equal(900, cha.Value); Assert.Equal("個別・自動", cha.Control);
        f.Clock.Advance(3); board = board with { Timestamp = f.Clock.GetTimestamp() };
        rows = DashboardDisplay.ReadAll(f.Core.Settings, f.Core.Snapshot, board, states, f.Clock);
        Assert.All(rows.Where(r => r.Item.Source == "motherboard"), r => Assert.Null(r.Duty));
    }
    private sealed class Registers : ICpuFanRegisterIo
    {
        public Dictionary<ushort, byte> Values = new() { [0x102] = 32, [0x109] = 110, [0x001] = 110, [0x202] = 64, [0x209] = 170, [0x003] = 170 };
        public List<(ushort Address, byte Value)> Writes = [];
        public IDisposable Acquire() => new Lease();
        private sealed class Lease : IDisposable { public void Dispose() { } }
        public byte Read(ushort address) => Values[address];
        public int ReadRpm() => 1000;
        public void Write(ushort address, byte value)
        {
            Writes.Add((address, value)); Values[address] = value;
            if (Values[0x102] == 0) Values[0x001] = Values[0x109];
            if (Values[0x202] == 0) Values[0x003] = Values[0x209];
        }
    }
    [Fact] public void ChassisRegisterWritesNeverTouchCpuAndRestoreTheirOwnOriginalValues()
    {
        var io = new Registers(); var cpu = new CpuFanRegisters(io); var cha = new CpuFanRegisters(io, channel: 0);
        cpu.Write(70); var cpuMode = io.Values[0x202]; var cpuCommand = io.Values[0x209]; io.Writes.Clear();
        cha.Write(100); cha.Write(50); cha.Restore();
        Assert.Equal(((ushort)0x109, (byte)255), io.Writes[0]); Assert.Equal(((ushort)0x102, (byte)0), io.Writes[1]);
        Assert.All(io.Writes, w => Assert.Contains(w.Address, new ushort[] { 0x102, 0x109 }));
        Assert.Equal(cpuMode, io.Values[0x202]); Assert.Equal(cpuCommand, io.Values[0x209]);
        Assert.Equal(32, io.Values[0x102]); Assert.Equal(110, io.Values[0x109]);
        cpu.Restore(); Assert.Equal(64, io.Values[0x202]); Assert.Equal(170, io.Values[0x209]);
    }
    [Theory] [InlineData(-1)] [InlineData(7)] [InlineData(8)]
    public void OtherRegisterChannelsAreRejectedWithoutIo(int channel)
    { var io = new Registers(); Assert.Throws<NotSupportedException>(() => new CpuFanRegisters(io, channel: channel)); Assert.Empty(io.Writes); }
}
