using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class AllBoardFanTests
{
    private sealed class Io : ICpuFanRegisterIo
    {
        public readonly Dictionary<ushort, byte> Values = [];
        public readonly List<(ushort Address, byte Value)> Writes = [];
        public ushort? FailOnce;
        public int Waits;
        public Io(string chip)
        {
            foreach (var port in MotherboardFans.Ports.Where(p => p.Chip == chip))
            {
                var m = NctFanMap.For(chip, port.Channel);
                Values[m.Mode] = m.SharedMode ? (byte)0 : (byte)32;
                Values[m.Command] = 170; Values[m.Output] = 170;
            }
        }
        public IDisposable Acquire() => new Lease();
        private sealed class Lease : IDisposable { public void Dispose() { } }
        public byte Read(ushort address) => Values[address];
        public int ReadRpm() => 1300;
        public void WaitForController() => Waits++;
        public void Write(ushort address, byte value)
        {
            Writes.Add((address, value));
            if (FailOnce == address) { FailOnce = null; throw new IOException("partial write"); }
            Values[address] = value;
        }
    }
    public static IEnumerable<object[]> Channels() => MotherboardFans.Ports.Select(p => new object[] { p.Chip, p.Channel });
    [Theory, MemberData(nameof(Channels))]
    public void EveryDetectedChannelRestrictsWritesAndRestoresItsOriginalSettings(string chip, int channel)
    {
        var io = new Io(chip); var map = NctFanMap.For(chip, channel); var saved = new Dictionary<ushort, byte>(io.Values);
        var driver = new CpuFanRegisters(io, channel: channel, chip: chip);
        driver.Read(); driver.Restore(); Assert.Empty(io.Writes);
        driver.Write(100); driver.Write(55); driver.Restore();
        Assert.False(driver.NeedsRestore);
        foreach (var (address, value) in saved) Assert.Equal(value, io.Values[address]);
        Assert.All(io.Writes, w => Assert.True(w.Address == map.Command || w.Address == map.Mode || w.Address == map.Request));
        if (map.SharedMode)
        {
            Assert.Equal((map.Request!.Value, (byte)0x80), io.Writes[0]);
            Assert.Equal((map.Command, (byte)255), io.Writes[1]);
            Assert.Equal((map.Mode, map.Mask), io.Writes[2]);
            Assert.Equal((map.Request.Value, (byte)0x40), io.Writes[3]); Assert.Equal(6, io.Waits);
        }
        else Assert.Equal((map.Command, (byte)255), io.Writes[0]);
    }
    [Theory, InlineData(0), InlineData(1), InlineData(128), InlineData(255)]
    public void SharedModeRestorationPreservesOtherChannelsAndOriginalOwnership(byte original)
    {
        var io = new Io("NCT6686D"); io.Values[0xA00] = original;
        var first = new CpuFanRegisters(io, channel: 0, chip: "NCT6686D");
        var last = new CpuFanRegisters(io, channel: 7, chip: "NCT6686D");
        first.Write(60); last.Write(75); first.Restore();
        Assert.Equal((byte)(original | 128), io.Values[0xA00]); Assert.Equal(191, io.Values[0xA2F]);
        last.Restore(); Assert.Equal(original, io.Values[0xA00]);
        Assert.Equal(170, io.Values[0xA28]); Assert.Equal(170, io.Values[0xA2F]);
    }
    [Theory, InlineData(0xA01), InlineData(0xA28), InlineData(0xA00)]
    public void PartialEcFailureFinishesTransactionAndKeepsRetryableOriginal(int address)
    {
        var io = new Io("NCT6686D"); var driver = new CpuFanRegisters(io, channel: 0, chip: "NCT6686D");
        io.FailOnce = (ushort)address;
        Assert.Throws<IOException>(() => driver.Write(100)); Assert.True(driver.NeedsRestore);
        Assert.Equal(((ushort)0xA01, (byte)0x40), io.Writes[^1]);
        driver.Restore(); Assert.False(driver.NeedsRestore); Assert.Equal(0, io.Values[0xA00]); Assert.Equal(170, io.Values[0xA28]);
        driver.Write(80); io.FailOnce = 0xA28;
        Assert.Throws<IOException>(() => driver.Restore()); Assert.True(driver.NeedsRestore);
        driver.Restore(); Assert.False(driver.NeedsRestore); Assert.Equal(0, io.Values[0xA00]);
    }
    [Theory, InlineData("NCT6686D", -1), InlineData("NCT6686D", 8), InlineData("NCT6799D", 7), InlineData("NCT6687D", 0)]
    public void UnsupportedMapsFailBeforeIo(string chip, int channel) =>
        Assert.Throws<NotSupportedException>(() => new CpuFanRegisters(new Io("NCT6686D"), channel: channel, chip: chip));
    [Fact] public void RpmTimestampRemainsTheSharedTelemetryGeneration()
    {
        var clock = new TestClock(); var generation = clock.GetTimestamp();
        var driver = new CpuFanRegisters(new Io("NCT6799D"), clock, timestamp: () => generation);
        clock.Advance(4); Assert.Equal(generation, driver.Read().Timestamp);
        generation = clock.GetTimestamp(); Assert.Equal(generation, driver.Read().Timestamp);
    }
    [Fact] public void AllFifteenDetectedIdsIncludeZeroRpmButIgnoreUnrelatedSensorsAndBoards()
    {
        var board = new BoardSnapshot("ASRock X870E Taichi White", MotherboardFans.Ports.Select(p => new BoardSensor(p.SensorId, p.Name, p.Chip, "fan", "rpm", 0, 0)).ToArray());
        Assert.Equal(15, MotherboardFans.Detected(board).Length);
        Assert.Empty(MotherboardFans.Detected(board with { Name = "unknown" }));
        Assert.Equal(15, MotherboardFans.Ports.Select(p => p.SensorId).Distinct().Count());
    }
    [Fact] public async Task PerFanSettingsRoundTripWithoutChangingLegacyCalibration()
    {
        var saved = new AppSettings { CpuFan = new(true, 40), ChassisFan = new(true, 35) };
        foreach (var p in MotherboardFans.Ports.Skip(2))
        {
            Assert.Null(MotherboardFans.Settings(saved, p.Id).Minimum);
            Assert.False(MotherboardFans.Settings(saved, p.Id).WiringConfirmed);
            saved = MotherboardFans.WithSettings(saved, p.Id, new(true, 30 + p.Channel));
        }
        Assert.Null(MotherboardFans.ValidateSettings(saved.BoardFans));
        var directory = Path.Combine(Path.GetTempPath(), "all-board-fans-" + Guid.NewGuid());
        try
        {
            var store = new JsonSettingsStore(directory); await store.SaveAsync(saved); var loaded = await store.LoadAsync();
            Assert.Equal(40, loaded.CpuFan.Minimum); Assert.Equal(35, loaded.ChassisFan.Minimum);
            foreach (var p in MotherboardFans.Ports.Skip(2)) Assert.Equal(30 + p.Channel, MotherboardFans.Settings(loaded, p.Id).Minimum);
        }
        finally { Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(directory)); Directory.Delete(directory, true); }
        Assert.NotNull(MotherboardFans.ValidateSettings(new() { ["cpu-fan1"] = new(true, 20) }));
        Assert.NotNull(MotherboardFans.ValidateSettings(new() { ["unknown"] = new(true, 20) }));
        Assert.NotNull(MotherboardFans.ValidateSettings(new() { ["nct6686d-0"] = null! }));
    }
}
