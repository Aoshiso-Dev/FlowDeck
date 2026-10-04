using System.Text.Json;
using System.Reflection;
using LibreHardwareMonitor.Hardware;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

internal static class MotherboardCheck
{
    // Never load persisted LHM control modes: opening a saved Software control can write PWM.
    private sealed class ReadOnlySettings : ISettings
    {
        public bool Contains(string name) => false;
        public string GetValue(string name, string value) => value;
        public void SetValue(string name, string value) { }
        public void Remove(string name) { }
    }
    public static async Task RunAsync(string output, bool includeFanCounters = false)
    {
        var computer = new Computer(new ReadOnlySettings()) { IsMotherboardEnabled = true };
        var samples = new List<object>();
        try
        {
            computer.Open();
            for (var i = 0; i < 4; ++i)
            {
                var rows = new List<object>();
                void Read(IHardware hardware)
                {
                    hardware.Update();
                    rows.Add(new { id = hardware.Identifier.ToString(), hardware.Name, kind = hardware.HardwareType.ToString(),
                        fanCounters = includeFanCounters ? FanCounters(hardware) : null,
                        sensors = hardware.Sensors.Select(s => new { id = s.Identifier.ToString(), s.Name,
                            type = s.SensorType.ToString(), s.Index, value = s.Value is { } v && float.IsFinite(v) ? s.Value : null,
                            controlMode = s.Control?.ControlMode.ToString() }).ToArray() });
                    foreach (var child in hardware.SubHardware) Read(child);
                }
                foreach (var hardware in computer.Hardware) Read(hardware);
                samples.Add(new { utc = DateTimeOffset.UtcNow, hardware = rows });
                await Task.Delay(500);
            }
            var report = new { access = HardwareTemperatures.AccessStatus, fanControlCalls = 0, samples };
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(Contract.Json) { WriteIndented = true });
            await File.WriteAllTextAsync(output, json); Console.WriteLine(json);
        }
        finally { computer.Close(); }
    }

    // Diagnostic only: use the pinned driver's seven known tachometer counter pairs.
    // Do not probe other registers or call SetControl/WriteByte.
    private static object[]? FanCounters(IHardware hardware)
    {
        if (hardware.Identifier.ToString() != "/lpc/nct6799d/0") return null;
        if (typeof(Computer).Assembly.GetName().Version != new Version(0, 9, 6, 0))
            throw new NotSupportedException("Fan counter diagnostic requires LHM 0.9.6");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var chip = hardware.GetType().GetField("_superIO", flags)?.GetValue(hardware)
            ?? throw new NotSupportedException("Super I/O structure unavailable");
        var type = chip.GetType();
        if (type.FullName != "LibreHardwareMonitor.Hardware.Motherboard.Lpc.Nct677X" || type.GetProperty("Chip")?.GetValue(chip)?.ToString() != "NCT6799D")
            throw new NotSupportedException("Unexpected Super I/O chip");
        ushort[] expected = [0x4B0, 0x4B2, 0x4B4, 0x4B6, 0x4B8, 0x4BA, 0x4CC];
        if (type.GetField("_fanCountRegister", flags)?.GetValue(chip) is not ushort[] addresses || !addresses.SequenceEqual(expected))
            throw new NotSupportedException("Unexpected fan counter map");
        var read = type.GetMethod("ReadByte", flags, [typeof(ushort)]) ?? throw new MissingMethodException("ReadByte");
        var vendor = type.GetMethod("IsNuvotonVendor", flags) ?? throw new MissingMethodException("IsNuvotonVendor");
        if (!Mutex.TryOpenExisting(@"Global\Access_ISABUS.HTP.Method", out var bus)) throw new IOException("ISA mutex unavailable");
        using (bus)
        {
            bool acquired;
            try { acquired = bus.WaitOne(500); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TimeoutException("ISA bus busy");
            try
            {
                if (vendor.Invoke(chip, null) is not true) throw new IOException("Nuvoton vendor check failed");
                return addresses.Select((address, index) =>
                {
                    var high = (byte)read.Invoke(chip, [address])!;
                    var low = (byte)read.Invoke(chip, [(ushort)(address + 1)])!;
                    var count = (high << 5) | (low & 0x1F);
                    return (object)new { index, address = $"0x{address:X3}", high = $"0x{high:X2}", low = $"0x{low:X2}", count,
                        rpm = count is >= 0x15 and < 0x1FFF ? (double?)(1.35e6 / count) : null,
                        reason = count == 0x1FFF ? "counter limit / no measurable tach pulses" : count < 0x15 ? "invalid count" : "measured" };
                }).ToArray();
            }
            finally { bus.ReleaseMutex(); }
        }
    }
}
