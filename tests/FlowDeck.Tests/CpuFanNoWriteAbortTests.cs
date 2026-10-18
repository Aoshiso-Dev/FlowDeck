using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class CpuFanNoWriteAbortTests
{
    private sealed class Registers(NctFanMap map) : ICpuFanRegisterIo
    {
        public Dictionary<ushort, byte> Values { get; } = new()
        { [map.Mode] = map.SharedMode ? (byte)0 : (byte)0x40, [map.Command] = 150, [map.Output] = 150 };
        public List<(ushort Address, byte Value)> Attempts { get; } = [];
        public ushort? ThrowAt;
        public bool ThrowAfterEffect;
        public Action? BeforeWrite;
        public IDisposable Acquire() => new Lease();
        private sealed class Lease : IDisposable { public void Dispose() { } }
        public byte Read(ushort address) => Values.GetValueOrDefault(address);
        public int ReadRpm() => 0; // An unused header is still a register endpoint, not evidence of ownership.
        public void WaitForController() { }
        public void Write(ushort address, byte value)
        {
            Attempts.Add((address, value)); BeforeWrite?.Invoke();
            var fail = ThrowAt == address;
            if (fail) ThrowAt = null;
            if (fail && !ThrowAfterEffect) throw new IOException("write failed before an observable effect");
            Values[address] = value;
            if (address == map.Command) Values[map.Output] = value;
            if (fail) throw new IOException("write failed after an observable effect");
        }
    }
    private sealed class Journal(FanRecoveryJournal inner) : IFanRecoveryJournal
    {
        public bool FailSaveBefore, FailSaveAfter, FailClear;
        public int Clears;
        public Action? AfterSave;
        public FanRegisterRecovery? Find(string chip, int channel) => inner.Find(chip, channel);
        public void Save(FanRegisterRecovery value)
        {
            if (FailSaveBefore) throw new IOException("save failed");
            inner.Save(value); AfterSave?.Invoke();
            if (FailSaveAfter) throw new IOException("save completion unknown");
        }
        public void Clear(string chip, int channel)
        { Clears++; if (FailClear) throw new IOException("journal storage unavailable"); inner.Clear(chip, channel); }
    }
    private sealed class Rig : IDisposable
    {
        public ServiceTestDirectory Directory { get; } = new();
        public NctFanMap Map { get; }
        public Registers Io { get; }
        public FanRecoveryJournal Durable { get; }
        public Journal Journal { get; }
        public CpuFanRegisters Driver { get; }
        public Rig(string chip = "NCT6799D", int channel = 2)
        {
            Map = NctFanMap.For(chip, channel); Io = new(Map); Durable = new(Directory.Path, "boot"); Journal = new(Durable);
            Driver = new(Io, channel: channel, chip: chip, recovery: Journal, guardOwnedState: true);
            Io.BeforeWrite = () => Assert.NotNull(new FanRecoveryJournal(Directory.Path, "boot").Find(chip, channel));
        }
        public void Dispose() => Directory.Dispose();
    }

    [Theory] [InlineData("NCT6799D", 2)] [InlineData("NCT6686D", 0)]
    public void ExpiryAfterDurableSaveAndBeforeFirstWriteRetiresOnlyThisNewObligation(string chip, int channel)
    {
        using var r = new Rig(chip, channel);
        var sibling = new FanRegisterRecovery("NCT6799D", 1, 0x40, 200, 0, 130, 0, 140); r.Durable.Save(sibling);
        var checks = 0;
        var error = Assert.Throws<MotherboardCommandRejectedException>(() => r.Driver.Write(70, () =>
        { if (++checks == 2) throw new MotherboardCommandRejectedException("expired", DeviceDriverFailure.CoolingInputExpired); }));
        Assert.Equal(DeviceDriverFailure.CoolingInputExpired, error.Failure);
        Assert.Empty(r.Io.Attempts); Assert.False(r.Driver.NeedsRestore); Assert.Equal(1, r.Journal.Clears);
        Assert.Equal(sibling, Assert.Single(new FanRecoveryJournal(r.Directory.Path, "boot").Pending));
        r.Driver.RecoverInterruptedControl(); Assert.Empty(r.Io.Attempts);
    }

    [Fact]
    public void CancellationAfterSaveIsStillReturnedAfterDurableUnwrittenCleanup()
    {
        using var r = new Rig(); var checks = 0; using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() => r.Driver.Write(70, () =>
        { if (++checks == 2) cancellation.Token.ThrowIfCancellationRequested(); }));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(r.Io.Attempts); Assert.False(r.Driver.NeedsRestore);
        Assert.Empty(new FanRecoveryJournal(r.Directory.Path, "boot").Pending);
    }

    [Theory] [InlineData("NCT6799D", 2, false)] [InlineData("NCT6799D", 2, true)]
    [InlineData("NCT6686D", 0, false)] [InlineData("NCT6686D", 0, true)]
    public void AttemptingTheFirstHardwareWriteKeepsRecoveryEvenIfThatCallThrows(string chip, int channel, bool afterEffect)
    {
        using var r = new Rig(chip, channel);
        r.Io.ThrowAt = r.Map.Request ?? r.Map.Command; r.Io.ThrowAfterEffect = afterEffect;
        Assert.Throws<IOException>(() => r.Driver.Write(70));
        Assert.NotEmpty(r.Io.Attempts); Assert.True(r.Driver.NeedsRestore); Assert.Equal(0, r.Journal.Clears);
        Assert.Single(new FanRecoveryJournal(r.Directory.Path, "boot").Pending);
        if (r.Map.Request is { } request) Assert.Equal((request, (byte)0x40), r.Io.Attempts[^1]);
    }

    [Theory] [InlineData("NCT6799D", 2)] [InlineData("NCT6686D", 0)]
    public void AnAlreadyOwnedPortKeepsItsOriginalObligationOnAPrewriteRejection(string chip, int channel)
    {
        using var r = new Rig(chip, channel); r.Driver.Write(60);
        var original = Assert.Single(r.Durable.Pending); var count = r.Io.Attempts.Count; var checks = 0;
        Assert.Throws<MotherboardCommandRejectedException>(() => r.Driver.Write(70, () =>
        { if (++checks == 2) throw new MotherboardCommandRejectedException("expired"); }));
        Assert.Equal(count, r.Io.Attempts.Count); Assert.True(r.Driver.NeedsRestore); Assert.Equal(0, r.Journal.Clears);
        var retained = Assert.Single(new FanRecoveryJournal(r.Directory.Path, "boot").Pending);
        Assert.Equal(original.OriginalMode, retained.OriginalMode); Assert.Equal(original.OriginalCommand, retained.OriginalCommand);
        r.Driver.RecoverInterruptedControl(); Assert.Empty(r.Durable.Pending); Assert.False(r.Driver.NeedsRestore);
        Assert.Equal(original.OriginalCommand, r.Io.Values[r.Map.Command]);
    }

    [Fact]
    public void FailedUnwrittenCleanupRetainsOwnershipAndReportsBothErrorsWithoutTouchingHardware()
    {
        using var r = new Rig(); r.Journal.FailClear = true;
        r.Journal.AfterSave = () => r.Io.Values[r.Map.Command] = 245;
        var before = Assert.IsType<MotherboardFanReading>(r.Driver.ReadForControl());
        var error = Assert.Throws<IOException>(() => r.Driver.WriteWithControlSnapshot(70, null, 59, before.Control));
        Assert.Contains("復旧記録の取り消しを確認できません", error.Message);
        var errors = Assert.IsType<AggregateException>(error.InnerException).InnerExceptions;
        Assert.IsType<MotherboardCommandRejectedException>(errors[0]); Assert.Contains("journal storage unavailable", errors[1].Message);
        Assert.Empty(r.Io.Attempts); Assert.True(r.Driver.NeedsRestore); Assert.Equal(245, r.Io.Values[r.Map.Command]);
        Assert.Single(new FanRecoveryJournal(r.Directory.Path, "boot").Pending);
        Assert.Throws<IOException>(() => r.Driver.RecoverInterruptedControl()); Assert.Empty(r.Io.Attempts);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void UncertainJournalSaveNeverRunsTheNewCleanupPath(bool afterSave)
    {
        using var r = new Rig(); r.Journal.FailSaveBefore = !afterSave; r.Journal.FailSaveAfter = afterSave;
        Assert.Throws<IOException>(() => r.Driver.Write(70)); Assert.Empty(r.Io.Attempts); Assert.Equal(0, r.Journal.Clears);
        Assert.Equal(afterSave ? 1 : 0, new FanRecoveryJournal(r.Directory.Path, "boot").Pending.Length);
    }

    [Fact]
    public void AnExistingUncertainRecordIsNotRetroactivelyClassifiedAsUnwritten()
    {
        using var r = new Rig();
        var legacy = new FanRegisterRecovery("NCT6799D", 2, 0x40, 245, 0x40, 245, 0, 232); r.Durable.Save(legacy);
        r.Io.Values[r.Map.Command] = 240;
        Assert.Throws<IOException>(() => r.Driver.Write(70));
        Assert.Throws<IOException>(() => r.Driver.RecoverInterruptedControl());
        Assert.Empty(r.Io.Attempts); Assert.Equal(0, r.Journal.Clears);
        Assert.Equal(legacy, Assert.Single(new FanRecoveryJournal(r.Directory.Path, "boot").Pending));
    }

    [Fact]
    public void APrewriteAbortWithoutAJournalDoesNotInventAnInMemoryRestoreObligation()
    {
        var io = new Registers(NctFanMap.For("NCT6799D", 2)); var driver = new CpuFanRegisters(io, channel: 2); var checks = 0;
        Assert.Throws<MotherboardCommandRejectedException>(() => driver.Write(70, () =>
        { if (++checks == 2) throw new MotherboardCommandRejectedException("expired"); }));
        Assert.Empty(io.Attempts); Assert.False(driver.NeedsRestore); driver.Restore(); Assert.Empty(io.Attempts);
    }
}
