using System.Reflection;
using FlowDeck.Core;
using LibreHardwareMonitor.Hardware;

namespace FlowDeck.Infrastructure;

/// <summary>
/// Pinned LHM register map with per-channel address and request allowlists.
/// </summary>
internal sealed class NctCpuFanAccess : ICpuFanRegisterIo, IDisposable
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly ISensor rpm;
    private readonly object chip;
    private readonly MethodInfo read, write, vendor;
    private readonly Mutex bus;
    private readonly ushort mode, command, output;
    private readonly NctFanMap map;
    public NctCpuFanAccess(IHardware hardware, int channel = 1, string chipName = "NCT6799D")
    {
        map = NctFanMap.For(chipName, channel);
        mode = map.Mode; command = map.Command; output = map.Output;
        if (typeof(Computer).Assembly.GetName().Version != new Version(0, 9, 6, 0))
            throw new NotSupportedException("マザーファン制御は検証済みLHM 0.9.6専用です");
        if (hardware.Identifier.ToString() != $"/lpc/{chipName.ToLowerInvariant()}/0" || hardware.GetType().FullName != "LibreHardwareMonitor.Hardware.Motherboard.SuperIOHardware")
            throw new NotSupportedException("選択したマザーファンの対象チップが一致しません");
        rpm = hardware.Sensors.Single(s => s.Identifier.ToString() == $"/lpc/{chipName.ToLowerInvariant()}/0/fan/{channel}");
        chip = hardware.GetType().GetField("_superIO", Private)?.GetValue(hardware) ?? throw new NotSupportedException("Super I/Oの構造が未対応です");
        var type = chip.GetType();
        if (type.FullName != "LibreHardwareMonitor.Hardware.Motherboard.Lpc.Nct677X" || type.GetProperty("Chip")?.GetValue(chip)?.ToString() != chipName)
            throw new NotSupportedException("対象のNuvotonチップを確認できません");
        foreach (var (name, address) in new[] { ("FAN_CONTROL_MODE_REG", mode), ("FAN_PWM_COMMAND_REG", command), ("FAN_PWM_OUT_REG", output) })
            if (type.GetField(name, Private)?.GetValue(chip) is not ushort[] array || array.Length <= channel || array[channel] != address)
                throw new NotSupportedException("選択したマザーファンのレジスター配置が未対応です");
        if (map.Request is { } request && (type.GetField("FAN_PWM_REQUEST_REG", Private)?.GetValue(chip) is not ushort[] requests ||
            requests.Length <= channel || requests[channel] != request)) throw new NotSupportedException("EC要求レジスターが一致しません");
        read = type.GetMethod("ReadByte", Private, [typeof(ushort)]) ?? throw new MissingMethodException("ReadByte");
        write = type.GetMethod("WriteByte", Private, [typeof(ushort), typeof(byte)]) ?? throw new MissingMethodException("WriteByte");
        vendor = type.GetMethod("IsNuvotonVendor", Private) ?? throw new MissingMethodException("IsNuvotonVendor");
        if (!Mutex.TryOpenExisting(@"Global\Access_ISABUS.HTP.Method", out var existing)) throw new IOException("ISAバスの排他を取得できません");
        bus = existing;
    }
    public IDisposable Acquire()
    {
        bool acquired;
        try { acquired = bus.WaitOne(500); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new TimeoutException("選択したマザーファンのバスが使用中です");
        try
        {
            if (vendor.Invoke(chip, null) is not true) throw new IOException("Nuvotonのデバイス識別が一致しません");
            return new Lease(bus);
        }
        catch { bus.ReleaseMutex(); throw; }
    }
    public byte Read(ushort address)
    {
        if (address != mode && address != command && address != output) throw new ArgumentOutOfRangeException(nameof(address));
        return (byte)read.Invoke(chip, [address])!;
    }
    public void Write(ushort address, byte value)
    {
        if (address != mode && address != command && address != map.Request) throw new ArgumentOutOfRangeException(nameof(address));
        if (address == map.Request && value is not (0x80 or 0x40)) throw new ArgumentOutOfRangeException(nameof(value));
        write.Invoke(chip, [address, value]);
    }
    public int ReadRpm()
    {
        // The shared Computer updates the chip once per telemetry cycle, not once per fan.
        return rpm.Value is { } value && float.IsFinite(value) && value is >= 0 and <= 30000
            ? (int)Math.Round(value) : throw new IOException("選択したマザーファンの回転数が未取得です");
    }
    private sealed class Lease(Mutex mutex) : IDisposable { public void Dispose() => mutex.ReleaseMutex(); }
    public void Dispose() => bus.Dispose();
}
