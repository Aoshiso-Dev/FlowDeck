using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class FanRecoveryTests
{
    private sealed class Registers : ICpuFanRegisterIo
    {
        public Dictionary<ushort, byte> Values = [];
        public List<(ushort Address, byte Value)> Writes = [];
        public ushort? FailAt;
        public Action? BeforeWrite;
        public Registers(NctFanMap map) { Values[map.Mode] = map.SharedMode ? (byte)0 : (byte)32; Values[map.Command] = 150; Values[map.Output] = 150; }
        public IDisposable Acquire() => new Lease();
        private sealed class Lease : IDisposable { public void Dispose() { } }
        public byte Read(ushort address) => Values.GetValueOrDefault(address);
        public void Write(ushort address, byte value) { BeforeWrite?.Invoke(); if (FailAt == address) { FailAt = null; throw new IOException("partial"); } Writes.Add((address, value)); Values[address] = value; }
        public int ReadRpm() => 900;
        public void WaitForController() { }
    }
    private static string Folder() => Path.Combine(Path.GetTempPath(), "FlowDeck-recovery-" + Guid.NewGuid().ToString("N"));
    private static PhysicalDeviceBinding Binding(char fingerprint = 'a') => new("board-test", "serial:verified", "asrock-x870e-taichi-white-v1", new(fingerprint, 64));
    [Fact] public void BoundJournalCannotRestoreAcrossBoardOrBiosChangeWithinTheSameBoot()
    {
        var folder = Folder(); var journal = new FanRecoveryJournal(folder, "boot", Binding());
        journal.Save(new("NCT6799D", 1, 32, 150, 32, 150, 0, 120));
        Assert.Single(new FanRecoveryJournal(folder, "boot", Binding()).Pending);
        Assert.Throws<IOException>(() => new FanRecoveryJournal(folder, "boot", Binding('b')));
        Assert.Throws<IOException>(() => new FanRecoveryJournal(folder, "boot"));
        Assert.Throws<IOException>(() => new FanRecoveryJournal(folder, "next-boot", Binding() with { DeviceId = "other-board" }));
        journal.Clear("NCT6799D", 1); Assert.Empty(new FanRecoveryJournal(folder, "boot", Binding('b')).Pending);
    }
    [Fact] public void AChangedBiosAfterRebootArchivesBoundValuesWithoutReplayingThem()
    {
        var folder = Folder(); var journal = new FanRecoveryJournal(folder, "old-boot", Binding());
        journal.Save(new("NCT6799D", 1, 32, 150, 32, 150, 0, 120));
        var next = new FanRecoveryJournal(folder, "new-boot", Binding('b')); Assert.True(next.PreviousBoot);
        Assert.Throws<IOException>(() => next.Find("NCT6799D", 1)); next.ArchivePreviousBoot();
        Assert.Empty(next.Pending); Assert.Single(Directory.EnumerateFiles(folder, "*.previous-boot-*"));
    }
    [Fact] public void RepeatedGenericWritesRejectExternalModeOrCommandChanges()
    {
        var map = NctFanMap.For("NCT6799D", 1); var io = new Registers(map); var journal = new FanRecoveryJournal(Folder(), "boot", Binding());
        var driver = new CpuFanRegisters(io, recovery: journal, guardOwnedState: true); driver.Write(50);
        io.Values[map.Command] = 240; var count = io.Writes.Count;
        Assert.Throws<IOException>(() => driver.Write(60)); Assert.Equal(count, io.Writes.Count); Assert.Single(journal.Pending);
    }
    [Fact] public void GuardExpiryDuringEcHandshakeEndsTheHandshakeWithoutChangingPwmOrMode()
    {
        var map = NctFanMap.For("NCT6686D", 0); var io = new Registers(map); var journal = new FanRecoveryJournal(Folder(), "boot", Binding());
        var driver = new CpuFanRegisters(io, channel: 0, chip: "NCT6686D", recovery: journal, guardOwnedState: true); var checks = 0;
        Assert.Throws<MotherboardCommandRejectedException>(() => driver.Write(50, () =>
        { if (++checks == 3) throw new MotherboardCommandRejectedException("expired", DeviceDriverFailure.CoolingInputExpired); }));
        Assert.Equal(new[] { (map.Request!.Value, (byte)0x80), (map.Request.Value, (byte)0x40) }, io.Writes);
        Assert.Equal(150, io.Values[map.Command]); Assert.Equal(0, io.Values[map.Mode]); Assert.Single(journal.Pending);
        driver.RecoverInterruptedControl(); Assert.Empty(journal.Pending);
    }
    [Fact] public void AnOutputChangedSinceReadingIsRejectedBeforeAnyRegisterWrite()
    {
        var io = new Registers(NctFanMap.For("NCT6799D", 1)); var driver = new CpuFanRegisters(io);
        Assert.Throws<MotherboardCommandRejectedException>(() => driver.Write(70, expectedDuty: 20)); Assert.Empty(io.Writes); Assert.False(driver.NeedsRestore);
    }
    [Theory] [InlineData("NCT6799D", 1)] [InlineData("NCT6686D", 0)] [InlineData("NCT6686D", 7)]
    public void OriginalRegistersAreDurableBeforeFirstHardwareWriteAndRecoverAfterRecreation(string chip, int channel)
    {
        var folder = Folder(); var journal = new FanRecoveryJournal(folder, "boot-1"); var map = NctFanMap.For(chip, channel); var io = new Registers(map);
        io.BeforeWrite = () => Assert.NotNull(new FanRecoveryJournal(folder, "boot-1").Find(chip, channel));
        var first = new CpuFanRegisters(io, channel: channel, chip: chip, recovery: journal); first.Write(45); first.Write(60);
        var reopened = new FanRecoveryJournal(folder, "boot-1"); var saved = reopened.Find(chip, channel)!; Assert.Equal(150, saved.OriginalCommand);
        var second = new CpuFanRegisters(io, channel: channel, chip: chip, recovery: reopened);
        Assert.Throws<IOException>(() => second.Write(90));
        second.RecoverInterruptedControl(); Assert.Equal(150, io.Values[map.Command]); Assert.Empty(reopened.Pending);
        Assert.Equal(map.SharedMode ? 0 : 32, io.Values[map.Mode]);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void PartialCommandOrModeWritesKeepTheirRecoveryObligation(bool modeFailure)
    {
        var folder = Folder(); var map = NctFanMap.For("NCT6799D", 1); var io = new Registers(map) { FailAt = modeFailure ? map.Mode : map.Command };
        var journal = new FanRecoveryJournal(folder, "boot"); var first = new CpuFanRegisters(io, recovery: journal);
        Assert.Throws<IOException>(() => first.Write(50)); Assert.Single(journal.Pending);
        var second = new CpuFanRegisters(io, recovery: new FanRecoveryJournal(folder, "boot")); second.RecoverInterruptedControl();
        Assert.Equal(150, io.Values[map.Command]); Assert.Equal(32, io.Values[map.Mode]); Assert.Empty(new FanRecoveryJournal(folder, "boot").Pending);
    }
    [Fact] public void ExternalControllerChangesAreNotOverwrittenOnRecovery()
    {
        var folder = Folder(); var map = NctFanMap.For("NCT6799D", 1); var io = new Registers(map); var journal = new FanRecoveryJournal(folder, "boot");
        new CpuFanRegisters(io, recovery: journal).Write(50); io.Values[map.Command] = 200; io.Values[map.Mode] = 48; var writes = io.Writes.Count;
        Assert.Throws<IOException>(() => new CpuFanRegisters(io, recovery: new FanRecoveryJournal(folder, "boot")).RecoverInterruptedControl());
        Assert.Equal(writes, io.Writes.Count); Assert.Single(journal.Pending);
    }
    [Fact] public void ANewWindowsBootArchivesButNeverReplaysOldFirmwareValues()
    {
        var folder = Folder(); var map = NctFanMap.For("NCT6799D", 1); var io = new Registers(map);
        new CpuFanRegisters(io, recovery: new FanRecoveryJournal(folder, "old-boot")).Write(50); var writes = io.Writes.Count;
        var current = new FanRecoveryJournal(folder, "new-boot"); Assert.True(current.PreviousBoot);
        Assert.Throws<IOException>(() => new CpuFanRegisters(io, recovery: current).RecoverInterruptedControl()); Assert.Equal(writes, io.Writes.Count);
        current.ArchivePreviousBoot(); Assert.Empty(current.Pending); Assert.False(current.PreviousBoot);
        Assert.Single(Directory.EnumerateFiles(folder, "*.previous-boot-*"));
    }
    [Fact] public void OriginalOwnershipCannotBeReplacedAndOtherSharedModeBitsArePreserved()
    {
        var folder = Folder(); var journal = new FanRecoveryJournal(folder, "boot"); var map = NctFanMap.For("NCT6686D", 0); var io = new Registers(map);
        new CpuFanRegisters(io, channel: 0, chip: "NCT6686D", recovery: journal).Write(50);
        var saved = journal.Pending.Single(); Assert.Throws<IOException>(() => journal.Save(saved with { OriginalCommand = 250 }));
        io.Values[map.Mode] |= 128; new CpuFanRegisters(io, channel: 0, chip: "NCT6686D", recovery: journal).RecoverInterruptedControl();
        Assert.Equal(128, io.Values[map.Mode]); Assert.Empty(journal.Pending);
    }
    [Fact] public void EvenAnEmptyRecordFromAnEarlierBootIsArchivedBeforeNewWrites()
    {
        var folder = Folder(); var old = new FanRecoveryJournal(folder, "old"); old.Clear("NCT6799D", 1);
        var current = new FanRecoveryJournal(folder, "new"); Assert.True(current.PreviousBoot); Assert.Empty(current.Pending);
        current.ArchivePreviousBoot(); current.Save(new("NCT6799D", 1, 32, 150, 32, 150, 0, 100));
        Assert.Single(new FanRecoveryJournal(folder, "new").Pending);
    }
    [Fact] public void CorruptUnrecognizedOrTamperedRecoveryRecordsAreRefused()
    {
        var folder = Folder(); var journal = new FanRecoveryJournal(folder, "boot"); journal.Save(new("NCT6799D", 1, 32, 150, 32, 150, 0, 120));
        var path = Path.Combine(folder, "fan-recovery.json"); var original = File.ReadAllText(path);
        File.WriteAllText(path, original.Replace("\"originalCommand\":150", "\"originalCommand\":250")); Assert.Throws<IOException>(() => new FanRecoveryJournal(folder, "boot"));
        File.WriteAllText(path, "{broken"); Assert.Throws<IOException>(() => new FanRecoveryJournal(folder, "boot"));
        Assert.Throws<ArgumentException>(() => journal.Save(new("other-chip", 0, 0, 1, 0, 1, 0, 2)));
        Assert.Throws<ArgumentException>(() => journal.Save(new("NCT6686D", 0, 128, 1, 0, 1, 1, 2)));
    }
    private sealed class UnwritableJournal : IFanRecoveryJournal
    {
        public FanRegisterRecovery? Find(string chip, int channel) => null;
        public void Save(FanRegisterRecovery value) => throw new IOException("disk full");
        public void Clear(string chip, int channel) { }
    }
    [Fact] public void AnUnwritableJournalPreventsEveryHardwareWrite()
    {
        var io = new Registers(NctFanMap.For("NCT6799D", 1)); var driver = new CpuFanRegisters(io, recovery: new UnwritableJournal());
        Assert.Throws<IOException>(() => driver.Write(50)); Assert.Empty(io.Writes); Assert.False(driver.NeedsRestore);
    }
    [Fact] public void ACrashDuringRestoreCanBeRecoveredAndAnAlreadyRestoredPortNeedsNoWrite()
    {
        var folder = Folder(); var map = NctFanMap.For("NCT6799D", 1); var io = new Registers(map); var journal = new FanRecoveryJournal(folder, "boot");
        var first = new CpuFanRegisters(io, recovery: journal); first.Write(50); io.FailAt = map.Mode;
        Assert.Throws<IOException>(() => first.Restore()); Assert.Single(journal.Pending);
        new CpuFanRegisters(io, recovery: new FanRecoveryJournal(folder, "boot")).RecoverInterruptedControl(); Assert.Empty(new FanRecoveryJournal(folder, "boot").Pending);
        journal = new FanRecoveryJournal(folder, "boot"); journal.Save(new("NCT6799D", 1, 32, 150, 0, 120, 32, 150)); var writes = io.Writes.Count;
        new CpuFanRegisters(io, recovery: journal).RecoverInterruptedControl(); Assert.Equal(writes, io.Writes.Count); Assert.Empty(journal.Pending);
    }
}
