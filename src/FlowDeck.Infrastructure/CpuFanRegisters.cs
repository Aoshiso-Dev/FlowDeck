using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public interface ICpuFanRegisterIo
{
    IDisposable Acquire();
    byte Read(ushort address);
    void Write(ushort address, byte value);
    int ReadRpm();
    void WaitForController() => Thread.Sleep(50);
}

/// <summary>Pinned NCT6799D and NCT6686D channel protocol, serialized with the shared LHM Computer.</summary>
public sealed class CpuFanRegisters(ICpuFanRegisterIo io, TimeProvider? time = null, int channel = 1, string chip = "NCT6799D", Func<long>? timestamp = null,
    IFanRecoveryJournal? recovery = null, bool guardOwnedState = false)
{
    public const ushort ModeAddress = 0x202, CommandAddress = 0x209, OutputAddress = 0x003;
    private readonly NctFanMap map = NctFanMap.For(chip, channel);
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private (byte Mode, byte Command)? original;
    public bool NeedsRestore => original is not null;
    private CpuFanReading Reading() => new(io.Read(map.Output) * 100d / 255, io.ReadRpm(), timestamp?.Invoke() ?? clock.GetTimestamp());
    public CpuFanReading Read() { using var lease = io.Acquire(); return Reading(); }
    private MotherboardFanControlSnapshot ControlSnapshot() => new(chip, channel, io.Read(map.Mode), io.Read(map.Command));
    internal CpuFanReading ReadForControl()
    {
        using var lease = io.Acquire();
        var control = ControlSnapshot(); var reading = Reading();
        if (control != ControlSnapshot()) throw new MotherboardCommandRejectedException("取得中に端子の制御設定が変更されています");
        return new MotherboardFanReading(reading, control);
    }
    public CpuFanReading Write(int duty, Action? validateBeforeWrite = null, int? expectedDuty = null) =>
        WriteWithControlSnapshot(duty, validateBeforeWrite, expectedDuty, null);
    internal CpuFanReading WriteWithControlSnapshot(int duty, Action? validateBeforeWrite, int? expectedDuty,
        MotherboardFanControlSnapshot? expectedControl)
    {
        if (duty is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(duty));
        using var lease = io.Acquire();
        validateBeforeWrite?.Invoke();
        var pending = recovery?.Find(chip, channel);
        if (expectedControl is not null && expectedControl != ControlSnapshot())
            throw new MotherboardCommandRejectedException("取得後に端子の制御設定が変更されています");
        // Firmware can change the measured output while its automatic mode is unchanged.
        // Only the first normal acquisition may use this evidence; all owned/manual/legacy
        // operations retain the measured-output guard, as do unrecognized control modes.
        var acquiringAutomatic = original is null && pending is null && recovery is not null && guardOwnedState && expectedDuty is not null &&
            expectedControl is { Chip: "NCT6799D", Mode: 0x10 or 0x20 or 0x40 } && chip == "NCT6799D";
        if (!acquiringAutomatic && expectedDuty is not null && Math.Round(io.Read(map.Output) * 100d / 255) != expectedDuty)
            throw new MotherboardCommandRejectedException("取得後に端子の出力が変更されています");
        if (original is null && pending is not null) throw new IOException("前のファン制御を復旧してから操作してください");
        if (guardOwnedState && pending is not null) CheckOwned(pending);
        var previouslyOwned = original is not null;
        original ??= acquiringAutomatic ? (expectedControl!.Mode, expectedControl.Command) :
            ((byte)(io.Read(map.Mode) & (map.SharedMode ? map.Mask : 255)), io.Read(map.Command));
        var value = (byte)Math.Clamp((int)Math.Round(duty * 255d / 100), 1, 255);
        // Write the requested value BEFORE switching to manual. IControl.SetSoftware switches
        // mode before assigning its initial software value (normally zero); do not use that path.
        var mode = map.SharedMode ? map.Mask : (byte)0;
        try { Record(value, mode, acquiringAutomatic ? expectedControl : null); }
        catch { if (!previouslyOwned) original = null; throw; }
        void ValidateWrite()
        {
            validateBeforeWrite?.Invoke();
            if (acquiringAutomatic && expectedControl != ControlSnapshot())
                throw new MotherboardCommandRejectedException("取得後に端子の制御設定が変更されています");
        }
        var writeStarted = false;
        try { Apply(value, mode, ValidateWrite, () => writeStarted = true); }
        catch (Exception failure) when (!previouslyOwned && pending is null && !writeStarted)
        {
            // Record completed, but this call has not attempted even its first register write.
            // Retire only this new obligation; never infer that an older record is unwritten.
            try { recovery?.Clear(chip, channel); }
            catch (Exception cleanup)
            {
                throw new IOException("書き込み前に中止しましたが、復旧記録の取り消しを確認できません: " + cleanup.Message,
                    new AggregateException(failure, cleanup));
            }
            original = null;
            throw;
        }
        return Reading(); // The controller polls the measured duty until the output register catches up.
    }
    public void Restore()
    {
        if (original is not { } saved) return;
        using var lease = io.Acquire();
        Record(saved.Command, saved.Mode);
        Apply(saved.Command, saved.Mode);
        recovery?.Clear(chip, channel);
        original = null;
    }
    private void Record(byte command, byte mode, MotherboardFanControlSnapshot? expectedBefore = null)
    {
        if (recovery is null || original is not { } saved) return;
        var before = ControlSnapshot();
        // Never save a later foreign setting as a recoverable partial state of our acquisition.
        if (expectedBefore is not null && before != expectedBefore)
            throw new MotherboardCommandRejectedException("取得後に端子の制御設定が変更されています");
        recovery.Save(new(chip, channel, saved.Mode, saved.Command, (byte)(before.Mode & (map.SharedMode ? map.Mask : 255)), before.Command, mode, command));
    }
    public void RecoverInterruptedControl()
    {
        var pending = recovery?.Find(chip, channel); if (pending is null) return;
        using var lease = io.Acquire();
        var mode = (byte)(io.Read(map.Mode) & (map.SharedMode ? map.Mask : 255)); var command = io.Read(map.Command);
        if (mode == pending.OriginalMode && command == pending.OriginalCommand) { recovery!.Clear(chip, channel); original = null; return; }
        // Do not overwrite a BIOS or another controller's later change. Also cover an interrupted two-register write.
        CheckOwned(pending);
        original = (pending.OriginalMode, pending.OriginalCommand);
        Record(pending.OriginalCommand, pending.OriginalMode); Apply(pending.OriginalCommand, pending.OriginalMode);
        recovery!.Clear(chip, channel); original = null;
    }
    private void CheckOwned(FanRegisterRecovery pending)
    {
        var mode = (byte)(io.Read(map.Mode) & (map.SharedMode ? map.Mask : 255)); var command = io.Read(map.Command);
        if ((mode != pending.BeforeMode && mode != pending.IntendedMode) || (command != pending.BeforeCommand && command != pending.IntendedCommand))
            throw new IOException("ファン設定が保存した制御状態と異なります。自動操作を中止しました");
    }
    private void Apply(byte command, byte mode, Action? validateBeforeWrite = null, Action? beforeFirstWrite = null)
    {
        validateBeforeWrite?.Invoke();
        if (map.Request is { } request)
        {
            // Finish the EC transaction even after a partial failure; keep original until verified.
            try
            {
                beforeFirstWrite?.Invoke();
                io.Write(request, 0x80); io.WaitForController();
                validateBeforeWrite?.Invoke();
                io.Write(map.Command, command);
                var next = (byte)((io.Read(map.Mode) & ~map.Mask) | (mode & map.Mask));
                io.Write(map.Mode, next);
            }
            finally { io.Write(request, 0x40); io.WaitForController(); }
        }
        else { beforeFirstWrite?.Invoke(); io.Write(map.Command, command); io.Write(map.Mode, mode); }
        var actual = io.Read(map.Mode);
        if ((map.SharedMode ? actual & map.Mask : actual) != mode || io.Read(map.Command) != command)
            throw new IOException("選択したファンの制御モード・出力設定の読み戻しが一致しません");
    }
}
