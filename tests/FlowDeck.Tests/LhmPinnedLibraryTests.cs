using System.Reflection;
using System.Runtime.CompilerServices;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using LibreHardwareMonitor.Hardware;

namespace FlowDeck.Tests;

public partial class LhmSensorFreshnessTests
{
    private static readonly object LibraryMutexGate = new();
    private static bool ProbeMutex(Mutex mutex)
    {
        bool result = false;
        var thread = new Thread(() => { result = mutex.WaitOne(20); if (result) mutex.ReleaseMutex(); });
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(2))); return result;
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void TheCompiledNuvotonUpdaterReleasesItsOwnRecursiveLeaseAfterAReadThrows(bool outerOwner)
    {
        lock (LibraryMutexGate)
        {
            Assert.True(LhmSensorFreshness.PatchedLibrary);
            var assembly = typeof(Computer).Assembly;
            var mutexField = assembly.GetType("LibreHardwareMonitor.Hardware.Mutexes")!.GetField("_isaBusMutex", BindingFlags.NonPublic | BindingFlags.Static)!;
            var previous = mutexField.GetValue(null); using var mutex = new Mutex(); mutexField.SetValue(null, mutex);
            try
            {
                // No driver/port is opened. A deliberately absent LPC transport faults the actual compiled Update method.
                var type = assembly.GetType("LibreHardwareMonitor.Hardware.Motherboard.Lpc.Nct677X")!;
                var chip = RuntimeHelpers.GetUninitializedObject(type);
                void SetField(string name, object value) => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(chip, value);
                SetField("_isNuvotonVendor", true); SetField("_voltageRegisters", new ushort[] { 0x480 });
                SetField("<Voltages>k__BackingField", new float?[1]); var temperatures = new float?[] { 60 };
                SetField("<Temperatures>k__BackingField", temperatures);
                if (outerOwner) Assert.True(mutex.WaitOne(0));
                try
                {
                    var error = Assert.Throws<TargetInvocationException>(() => type.GetMethod("Update")!.Invoke(chip, null));
                    Assert.IsType<NullReferenceException>(error.InnerException); Assert.Null(temperatures[0]);
                    Assert.Equal(!outerOwner, ProbeMutex(mutex));
                }
                finally { if (outerOwner) mutex.ReleaseMutex(); }
                Assert.True(ProbeMutex(mutex));
            }
            finally { mutexField.SetValue(null, previous); }
        }
    }
    [Theory]
    [InlineData(60f, 80f, 80f, 70f)]
    [InlineData(60f, null, null, null)]
    [InlineData(60f, float.NaN, null, null)]
    [InlineData(60f, -10f, null, null)]
    [InlineData(60f, 125f, null, null)]
    public void CpuCcdSummaryRequiresEveryKnownCcdToHaveACurrentUsableValue(float? first, float? second, float? maximum, float? average)
    {
        var method = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Hardware.Cpu.Amd17Cpu")!
            .GetMethod("CurrentCcdSummary", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = ((float? Maximum, float? Average))method.Invoke(null, [new float?[] { first, second }])!;
        Assert.Equal(maximum, result.Maximum); Assert.Equal(average, result.Average);
    }
    [Fact]
    public void LegacyTemperatureReadKeepsAcquisitionTimeAndDoesNotRepublishCachedValues()
    {
        var hardware = new CachedHardware(HardwareType.GpuNvidia); var sensor = Sensor(hardware);
        var source = new HardwareTemperatures(); var method = typeof(HardwareTemperatures).GetMethod("Read", BindingFlags.Instance | BindingFlags.NonPublic)!;
        SensorReading Capture()
        {
            var rows = new List<SensorReading>(); var values = new List<HardwareSensorValue>();
            method.Invoke(source, [hardware, rows, values, "gpu"]); Assert.Equal(Assert.Single(values).Value, Assert.Single(rows).Celsius);
            return rows.Single();
        }
        hardware.OnUpdate = () => Set(sensor, 60); var first = Capture(); Assert.Equal(60, first.Celsius);
        hardware.OnUpdate = () => { }; var cached = Capture(); Assert.Null(cached.Celsius);
        Assert.Equal(first.Timestamp, cached.Timestamp); Assert.Equal(60, cached.RawCelsius); Assert.NotNull(cached.InvalidReason);
    }
}
