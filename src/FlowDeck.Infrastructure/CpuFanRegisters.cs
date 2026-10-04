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
    public CpuFanReading Write(int duty, Action? validateBeforeWrite = null, int? expectedDuty = null)
    {
        if (duty is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(duty));
        using var lease = io.Acquire();
        validateBeforeWrite?.Invoke();
        if (expectedDuty is not null && Math.Round(io.Read(map.Output) * 100d / 255) != expectedDuty)
            throw new MotherboardCommandRejectedException("取得後に端子の出力が変更されています");
        if (original is null && recovery?.Find(chip, channel) is not null) throw new IOException("前のファン制御を復旧してから操作してください");
        if (guardOwnedState && recovery?.Find(chip, channel) is { } previous) CheckOwned(previous);
        var previouslyOwned = original is not null;
        original ??= ((byte)(io.Read(map.Mode) & (map.SharedMode ? map.Mask : 255)), io.Read(map.Command));
        var value = (byte)Math.Clamp((int)Math.Round(duty * 255d / 100), 1, 255);
        // Write the requested value BEFORE switching to manual. IControl.SetSoftware switches
        // mode before assigning its initial software value (normally zero); do not use that path.
        var mode = map.SharedMode ? map.Mask : (byte)0;
        try { Record(value, mode); }
        catch { if (!previouslyOwned) original = null; throw; }
        Apply(value, mode, validateBeforeWrite);
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
    private void Record(byte command, byte mode)
    {
        if (recovery is null || original is not { } saved) return;
        recovery.Save(new(chip, channel, saved.Mode, saved.Command, (byte)(io.Read(map.Mode) & (map.SharedMode ? map.Mask : 255)), io.Read(map.Command), mode, command));
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
    private void Apply(byte command, byte mode, Action? validateBeforeWrite = null)
    {
        validateBeforeWrite?.Invoke();
        if (map.Request is { } request)
        {
            // Finish the EC transaction even after a partial failure; keep original until verified.
            try
            {
                io.Write(request, 0x80); io.WaitForController();
                validateBeforeWrite?.Invoke();
                io.Write(map.Command, command);
                var next = (byte)((io.Read(map.Mode) & ~map.Mask) | (mode & map.Mask));
                io.Write(map.Mode, next);
            }
            finally { io.Write(request, 0x40); io.WaitForController(); }
        }
        else { io.Write(map.Command, command); io.Write(map.Mode, mode); }
        var actual = io.Read(map.Mode);
        if ((map.SharedMode ? actual & map.Mask : actual) != mode || io.Read(map.Command) != command)
            throw new IOException("選択したファンの制御モード・出力設定の読み戻しが一致しません");
    }
}
