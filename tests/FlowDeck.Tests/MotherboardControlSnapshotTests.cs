using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class MotherboardControlSnapshotTests
{
    private sealed class Registers(NctFanMap map) : ICpuFanRegisterIo
    {
        public Dictionary<ushort, byte> Values = new() { [map.Mode] = 0x40, [map.Command] = 255, [map.Output] = 170 };
        public List<(ushort Address, byte Value)> Writes = [];
        public Action<ushort>? BeforeRead;
        private int depth;
        public IDisposable Acquire() { Assert.Equal(0, depth++); return new Lease(this); }
        private sealed class Lease(Registers owner) : IDisposable { public void Dispose() { Assert.Equal(1, owner.depth--); } }
        public byte Read(ushort address) { Assert.Equal(1, depth); BeforeRead?.Invoke(address); return Values[address]; }
        public int ReadRpm() { Assert.Equal(1, depth); return 900; }
        public void WaitForController() { }
        public void Write(ushort address, byte value)
        {
            Assert.Equal(1, depth); Writes.Add((address, value)); Values[address] = value;
            if (address == map.Command) Values[map.Output] = value;
        }
    }
    private sealed class Journal(IFanRecoveryJournal inner) : IFanRecoveryJournal
    {
        public Action? AfterSave;
        public FanRegisterRecovery? Find(string chip, int channel) => inner.Find(chip, channel);
        public void Save(FanRegisterRecovery value) { inner.Save(value); AfterSave?.Invoke(); }
        public void Clear(string chip, int channel) => inner.Clear(chip, channel);
    }
    private sealed class Rig : IDisposable
    {
        private readonly ServiceTestDirectory directory = new();
        public NctFanMap Map { get; }
        public Registers Io { get; }
        public FanRecoveryJournal Saved { get; }
        public Journal Journal { get; }
        public CpuFanRegisters Driver { get; }
        public Rig(byte mode = 0x40, string chip = "NCT6799D", int channel = 2, bool guarded = true, bool journal = true)
        {
            Map = NctFanMap.For(chip, channel); Io = new(Map); Io.Values[Map.Mode] = mode;
            Saved = new(directory.Path, "boot"); Journal = new(Saved);
            Driver = new(Io, channel: channel, chip: chip, recovery: journal ? Journal : null, guardOwnedState: guarded);
        }
        public MotherboardFanReading Read() => Assert.IsType<MotherboardFanReading>(Driver.ReadForControl());
        public CpuFanReading Write(MotherboardFanReading before, MotherboardFanControlSnapshot? snapshot = null) =>
            Driver.WriteWithControlSnapshot(93, null, (int)Math.Round(before.Duty), snapshot ?? before.Control);
        public void Dispose() => directory.Dispose();
    }

    [Theory] [InlineData(0x10)] [InlineData(0x20)] [InlineData(0x40)]
    public void KnownAutomaticModeCanAcquireDespiteOutputDriftWithDurableOriginalAndVerifiedWrite(byte mode)
    {
        using var r = new Rig(mode); var before = r.Read(); r.Io.Values[r.Map.Output] = 210;
        Assert.Equal(93, Math.Round(r.Write(before).Duty));
        Assert.Equal(new[] { (r.Map.Command, (byte)237), (r.Map.Mode, (byte)0) }, r.Io.Writes);
        var saved = Assert.Single(r.Saved.Pending); Assert.Equal(mode, saved.OriginalMode); Assert.Equal(255, saved.OriginalCommand);
        Assert.Equal(mode, saved.BeforeMode); Assert.Equal(255, saved.BeforeCommand); Assert.Equal(0, saved.IntendedMode); Assert.Equal(237, saved.IntendedCommand);
        r.Driver.RecoverInterruptedControl(); Assert.Empty(r.Saved.Pending); Assert.False(r.Driver.NeedsRestore);
        Assert.Equal(mode, r.Io.Values[r.Map.Mode]); Assert.Equal(255, r.Io.Values[r.Map.Command]);
    }

    // Only the exact recognized control byte is eligible. Unknown/reserved bits never broaden authority.
    [Theory] [InlineData(0x00)] [InlineData(0x01)] [InlineData(0x11)] [InlineData(0x21)] [InlineData(0x30)]
    [InlineData(0x41)] [InlineData(0x50)] [InlineData(0x80)] [InlineData(0x90)] [InlineData(0xC0)]
    public void ManualUnknownAndReservedModesKeepStrictMeasuredOutputGuard(byte mode)
    {
        using var r = new Rig(mode); var before = r.Read(); r.Io.Values[r.Map.Output] = 210;
        Assert.Throws<MotherboardCommandRejectedException>(() => r.Write(before));
        Assert.Empty(r.Io.Writes); Assert.Empty(r.Saved.Pending); Assert.False(r.Driver.NeedsRestore);
    }

    [Theory] [InlineData(0)] [InlineData(4)]
    public void EcSharedModeDoesNotGainTheAutomaticAcquisitionException(byte mode)
    {
        using var r = new Rig(mode, "NCT6686D"); var before = r.Read(); r.Io.Values[r.Map.Output] = 210;
        Assert.Throws<MotherboardCommandRejectedException>(() => r.Write(before));
        Assert.Empty(r.Io.Writes); Assert.Empty(r.Saved.Pending);
    }

    [Theory] [InlineData("mode")] [InlineData("command")]
    public void StableOutputCannotHideAChangedControlRegister(string changed)
    {
        using var r = new Rig(); var before = r.Read(); r.Io.Values[changed == "mode" ? r.Map.Mode : r.Map.Command] = 0;
        Assert.Throws<MotherboardCommandRejectedException>(() => r.Write(before));
        Assert.Empty(r.Io.Writes); Assert.Empty(r.Saved.Pending); Assert.False(r.Driver.NeedsRestore);
    }

    [Theory] [InlineData("chip")] [InlineData("channel")]
    public void SnapshotOfAnotherPortIsRejectedEvenWhenItsRegisterBytesMatch(string changed)
    {
        using var r = new Rig(); var before = r.Read();
        var other = changed == "chip" ? before.Control with { Chip = "NCT6686D" } : before.Control with { Channel = 1 };
        Assert.Throws<MotherboardCommandRejectedException>(() => r.Write(before, other)); Assert.Empty(r.Io.Writes); Assert.Empty(r.Saved.Pending);
    }

    [Fact] public void ControlChangeDuringSnapshotReadIsRejectedWithoutProducingAuthority()
    {
        using var r = new Rig(); r.Io.BeforeRead = address => { if (address == r.Map.Output) r.Io.Values[r.Map.Mode] = 0; };
        Assert.Throws<MotherboardCommandRejectedException>(() => r.Driver.ReadForControl());
        Assert.Empty(r.Io.Writes); Assert.Empty(r.Saved.Pending);
    }

    [Theory] [InlineData("mode")] [InlineData("command")]
    public void ChangeWhileCapturingJournalBeforeStateCannotBecomeOurRecoveryAuthority(string changed)
    {
        using var r = new Rig(); var before = r.Read(); var reads = 0;
        var address = changed == "mode" ? r.Map.Mode : r.Map.Command;
        r.Io.BeforeRead = current => { if (current == address && ++reads == 2) r.Io.Values[address] = 0x20; };
        Assert.Throws<MotherboardCommandRejectedException>(() => r.Write(before));
        Assert.Empty(r.Io.Writes); Assert.Empty(r.Saved.Pending); Assert.False(r.Driver.NeedsRestore);
        Assert.Equal(0x20, r.Io.Values[address]);
    }

    [Theory] [InlineData("mode")] [InlineData("command")]
    public void ChangeDuringJournalSaveStopsBeforeAnyWriteAndRetiresOnlyTheUnwrittenAcquisition(string changed)
    {
        using var r = new Rig(); var before = r.Read(); r.Io.Values[r.Map.Output] = 210;
        r.Journal.AfterSave = () => r.Io.Values[changed == "mode" ? r.Map.Mode : r.Map.Command] = 0x20;
        Assert.Throws<MotherboardCommandRejectedException>(() => r.Write(before));
        Assert.Empty(r.Io.Writes); Assert.False(r.Driver.NeedsRestore); Assert.Empty(r.Saved.Pending);
        Assert.Equal(0x20, r.Io.Values[changed == "mode" ? r.Map.Mode : r.Map.Command]);
        r.Journal.AfterSave = null;
        r.Driver.RecoverInterruptedControl(); Assert.Empty(r.Saved.Pending); Assert.Empty(r.Io.Writes);
        Assert.Equal(0x20, r.Io.Values[changed == "mode" ? r.Map.Mode : r.Map.Command]);
    }

    [Fact] public void AlreadyOwnedPortCannotUseANewAutomaticSnapshotToIgnoreDrift()
    {
        using var r = new Rig(); r.Write(r.Read());
        r.Io.Values[r.Map.Mode] = 0x40; r.Io.Values[r.Map.Command] = 255;
        var before = r.Read(); r.Io.Values[r.Map.Output] = 210; var count = r.Io.Writes.Count;
        Assert.Throws<MotherboardCommandRejectedException>(() => r.Write(before));
        Assert.Equal(count, r.Io.Writes.Count); Assert.Single(r.Saved.Pending);
    }

    [Fact] public void RecreatedPortWithPendingRecoveryCannotAcquire()
    {
        using var r = new Rig(); r.Write(r.Read());
        r.Io.Values[r.Map.Mode] = 0x40; r.Io.Values[r.Map.Command] = 255; var count = r.Io.Writes.Count;
        var next = new CpuFanRegisters(r.Io, channel: 2, recovery: r.Journal, guardOwnedState: true);
        var before = Assert.IsType<MotherboardFanReading>(next.ReadForControl());
        Assert.Throws<IOException>(() => next.WriteWithControlSnapshot(93, null, (int)Math.Round(before.Duty), before.Control));
        Assert.Equal(count, r.Io.Writes.Count); Assert.Single(r.Saved.Pending);
    }

    [Theory] [InlineData(false, true)] [InlineData(true, false)]
    public void LegacyOrMissingJournalNeverUsesTheSnapshotException(bool guarded, bool journal)
    {
        using var r = new Rig(guarded: guarded, journal: journal); var before = r.Read(); r.Io.Values[r.Map.Output] = 210;
        Assert.Throws<MotherboardCommandRejectedException>(() => r.Write(before)); Assert.Empty(r.Io.Writes); Assert.Empty(r.Saved.Pending);
    }

    [Fact] public void ControlSnapshotStaysOutOfTelemetryAndCommandSerialization()
    {
        using var r = new Rig(); var reading = r.Read();
        var json = JsonSerializer.Serialize(reading, Contract.Json);
        Assert.Equal(JsonSerializer.Serialize(new CpuFanReading(reading.Duty, reading.Rpm, reading.Timestamp), Contract.Json), json);
        var command = new MotherboardFanCommand(new("native", "connection", new('a', 64), "board"), "port", MotherboardFanOperation.Write, 93, 67)
        { ExpectedControl = reading.Control };
        Assert.DoesNotContain("control", JsonSerializer.Serialize(command, Contract.Json), StringComparison.OrdinalIgnoreCase);
    }
}
