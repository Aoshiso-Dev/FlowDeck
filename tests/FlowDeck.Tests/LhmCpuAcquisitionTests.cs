using System.Reflection;
using System.Runtime.CompilerServices;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32.SafeHandles;

namespace FlowDeck.Tests;

public partial class LhmSensorFreshnessTests
{
    private static Type LhmType(string name) => typeof(Computer).Assembly.GetType(name, true)!;
    private static void LhmField(object target, string name, object? value) => target.GetType().GetField(name,
        BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    private static object SmuTransport(Func<string, long[], int, long[]> execute) => Activator.CreateInstance(
        LhmType("LibreHardwareMonitor.PawnIo.RyzenSmu"), BindingFlags.NonPublic | BindingFlags.Instance, null, [execute], null)!;
    private static object SmuTable(object? transport, bool supported = true)
    {
        var target = RuntimeHelpers.GetUninitializedObject(LhmType("LibreHardwareMonitor.Hardware.RyzenSMU"));
        LhmField(target, "_ryzenSmu", transport); LhmField(target, "_supportedCPU", supported); LhmField(target, "_pmTableSize", 12U);
        return target;
    }
    private static float[] ReadSmu(object table) => (float[])table.GetType().GetMethod("GetPmTable")!.Invoke(table, null)!;
    private static long[] SmuData(params float[] values)
    {
        var result = new long[(values.Length + 1) / 2]; Buffer.BlockCopy(values, 0, result, 0, values.Length * sizeof(float)); return result;
    }
    private static void WithPciMutex(Action<Mutex> action)
    {
        lock (LibraryMutexGate)
        {
            var field = MutexField("_pciBusMutex"); var previous = field.GetValue(null); using var mutex = new Mutex();
            field.SetValue(null, mutex);
            try { action(mutex); }
            finally { field.SetValue(null, previous); }
        }
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(0, true)] [InlineData(1, true)]
    public void PawnIoWithoutALiveModuleCannotConfirmEvenAZeroOutputOperation(int outputLength, bool disposedHandle)
    {
        var target = RuntimeHelpers.GetUninitializedObject(LhmType("LibreHardwareMonitor.PawnIo.PawnIo"));
        using var handle = new SafeFileHandle(new IntPtr(-1), false);
        if (disposedHandle) { handle.Dispose(); LhmField(target, "_handle", handle); }
        object?[] args = ["ioctl_update_pm_table", Array.Empty<long>(), 0U, new long[outputLength], (uint)outputLength, null];
        int hr = (int)target.GetType().GetMethod("ExecuteHr")!.Invoke(target, args)!;
        Assert.Equal(unchecked((int)0x80070006), hr); Assert.Equal(0U, args[5]);
        var error = Assert.Throws<TargetInvocationException>(() => target.GetType().GetMethod("ExecuteChecked")!
            .Invoke(target, ["ioctl_update_pm_table", Array.Empty<long>(), outputLength]));
        Assert.Equal(hr, error.InnerException!.HResult);
    }

    [Fact]
    public void SmuRefreshAndCopyHoldOnePciLeaseIncludingTheGapBetweenCommands()
    {
        WithPciMutex(mutex =>
        {
            var calls = new List<string>(); var expected = SmuData(1, 60, 80);
            var transport = SmuTransport((name, input, count) =>
            {
                Assert.False(ProbeMutex(mutex)); Assert.Empty(input); calls.Add(name);
                return name == "ioctl_update_pm_table" ? [] : expected;
            });
            var table = SmuTable(transport); Assert.Equal(new float[] { 1, 60, 80 }, ReadSmu(table));
            Assert.Equal(new[] { "ioctl_update_pm_table", "ioctl_read_pm_table" }, calls); Assert.True(ProbeMutex(mutex));
        });
    }

    [Theory]
    [InlineData("updateFailure")]
    [InlineData("updateWrongSize")]
    [InlineData("readFailure")]
    [InlineData("shortRead")]
    [InlineData("longRead")]
    [InlineData("nullRead")]
    public void SmuFailuresDoNotReturnAPartialOrPreviouslyPopulatedTableAndReleaseThePciBus(string failure)
    {
        WithPciMutex(mutex =>
        {
            int updates = 0, reads = 0;
            var transport = SmuTransport((name, _, count) =>
            {
                Assert.False(ProbeMutex(mutex));
                if (name == "ioctl_update_pm_table")
                {
                    updates++; if (failure == "updateFailure") throw new IOException("update failed");
                    return failure == "updateWrongSize" ? [1] : [];
                }
                reads++;
                return failure switch { "readFailure" => throw new IOException("read failed"), "shortRead" => [1],
                    "longRead" => [1, 2, 3], "nullRead" => null!, _ => SmuData(1, 60, 80) };
            });
            Assert.Empty(ReadSmu(SmuTable(transport))); Assert.Equal(2, updates);
            Assert.Equal(failure.StartsWith("update", StringComparison.Ordinal) ? 0 : 2, reads);
            Assert.True(ProbeMutex(mutex));
        });
    }

    [Theory]
    [InlineData(0f)] [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)]
    public void SmuInvalidLeadingValueDoesNotExposePlausibleTemperaturesFromTheRestOfTheTable(float marker)
    {
        WithPciMutex(mutex =>
        {
            int reads = 0;
            var transport = SmuTransport((name, _, _) => name == "ioctl_update_pm_table" ? [] :
                ++reads > 0 ? SmuData(marker, 65, 85) : []);
            Assert.Empty(ReadSmu(SmuTable(transport))); Assert.Equal(2, reads); Assert.True(ProbeMutex(mutex));
        });
    }

    [Fact]
    public void SmuRetriesAnInterruptedRefreshBeforeReadingAndCanRecoverOnTheSecondAttempt()
    {
        WithPciMutex(mutex =>
        {
            int updates = 0, reads = 0;
            var transport = SmuTransport((name, _, _) =>
            {
                if (name == "ioctl_update_pm_table")
                { if (++updates == 1) throw new IOException("retry"); return []; }
                reads++; return SmuData(1, 61, 81);
            });
            Assert.Equal(new float[] { 1, 61, 81 }, ReadSmu(SmuTable(transport)));
            Assert.Equal(2, updates); Assert.Equal(1, reads); Assert.True(ProbeMutex(mutex));
        });
    }

    [Fact]
    public void SmuNestedCallerKeepsItsOwnPciLeaseAfterTheCombinedRead()
    {
        WithPciMutex(mutex =>
        {
            var transport = SmuTransport((name, _, _) => name == "ioctl_update_pm_table" ? [] : SmuData(1, 60, 80));
            mutex.WaitOne();
            try { Assert.NotEmpty(ReadSmu(SmuTable(transport))); Assert.False(ProbeMutex(mutex)); }
            finally { mutex.ReleaseMutex(); }
            Assert.True(ProbeMutex(mutex));
        });
    }

    [Fact]
    public void UnsupportedOrUninitializedSmuHasNoFabricatedTableAndCanClose()
    {
        var table = SmuTable(null, supported: false); Assert.Empty(ReadSmu(table));
        table.GetType().GetMethod("Close")!.Invoke(table, null);
    }

    [Fact]
    public void SmuUpdateFailureInvalidatesOnlyItsTemperatureWithoutRejuvenatingItsAge()
    {
        WithPciMutex(mutex =>
        {
            bool fail = false; int reads = 0;
            var transport = SmuTransport((name, _, _) =>
            {
                if (name == "ioctl_update_pm_table") { if (fail) throw new IOException("update failed"); return []; }
                reads++; return SmuData(1, 60, 80);
            });
            var table = SmuTable(transport); var hardware = new CachedHardware();
            var smu = Sensor(hardware); var direct = Sensor(hardware, 1); var freshness = new LhmSensorFreshness();
            hardware.OnUpdate = () => { Set(direct, 70); var values = ReadSmu(table); if (values.Length > 1) Set(smu, values[1]); };
            HardwareSensorValue[] ReadAll(long time)
            {
                var acquired = freshness.Update(hardware, time, out _); var rows = new List<HardwareSensorValue>();
                HardwareSensorCapture.AddValues(hardware, rows, acquired); return rows.ToArray();
            }
            Assert.All(ReadAll(1000), r => Assert.Null(r.Problem)); fail = true;
            var failed = ReadAll(2000); var missing = failed.Single(r => r.Id == smu.Identifier.ToString());
            Assert.Null(missing.Value); Assert.NotNull(missing.Problem); Assert.Equal(1000, missing.Timestamp);
            Assert.Equal(70, failed.Single(r => r.Id == direct.Identifier.ToString()).Value); Assert.Equal(1, reads);
            fail = false; var recovered = ReadAll(3000).Single(r => r.Id == smu.Identifier.ToString());
            Assert.Equal(60, recovered.Value); Assert.Equal(3000, recovered.Timestamp); Assert.Null(recovered.Problem);
        });
    }

    [Theory]
    [InlineData(null)] [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(0f)] [InlineData(-10f)] [InlineData(150f)]
    public void IntelCoreAverageAndMaximumRequireEveryKnownCoreToHaveAUsableCurrentTemperature(float? missing)
    {
        var hardware = new CachedHardware(); var first = Sensor(hardware); var second = Sensor(hardware, 1);
        var maximum = Sensor(hardware, 2); var average = Sensor(hardware, 3);
        var type = LhmType("LibreHardwareMonitor.Hardware.Cpu.IntelCpu"); var cpu = RuntimeHelpers.GetUninitializedObject(type);
        var sensors = Array.CreateInstance(first.GetType(), 2); sensors.SetValue(first, 0); sensors.SetValue(second, 1);
        LhmField(cpu, "_coreTemperatures", sensors); LhmField(cpu, "_coreMax", maximum); LhmField(cpu, "_coreAvg", average);
        var summarize = type.GetMethod("UpdateCoreTemperatureSummary", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var freshness = new LhmSensorFreshness();
        hardware.OnUpdate = () => { Set(first, 80); Set(second, 100); summarize.Invoke(cpu, null); };
        freshness.Update(hardware, 1000, out _); Assert.Equal(90, average.Value); Assert.Equal(100, maximum.Value);
        hardware.OnUpdate = () => { Set(first, 80); Set(second, missing); summarize.Invoke(cpu, null); };
        var acquired = freshness.Update(hardware, 2000, out _); var values = new List<HardwareSensorValue>();
        HardwareSensorCapture.AddValues(hardware, values, acquired);
        Assert.Null(average.Value); Assert.Null(maximum.Value);
        Assert.Null(values.Single(r => r.Id == average.Identifier.ToString()).Value);
        Assert.Equal(80, values.Single(r => r.Id == first.Identifier.ToString()).Value);
        hardware.OnUpdate = () => { Set(first, 70); Set(second, 90); summarize.Invoke(cpu, null); };
        freshness.Update(hardware, 3000, out _); Assert.Equal(80, average.Value); Assert.Equal(90, maximum.Value);
    }

    [Fact]
    public void EmptyIntelCoreSetInvalidatesAPreviousDerivedReading()
    {
        var hardware = new CachedHardware(); var maximum = Sensor(hardware); var average = Sensor(hardware, 1);
        Set(maximum, 80); Set(average, 60); var type = LhmType("LibreHardwareMonitor.Hardware.Cpu.IntelCpu");
        var cpu = RuntimeHelpers.GetUninitializedObject(type); LhmField(cpu, "_coreTemperatures", Array.CreateInstance(maximum.GetType(), 0));
        LhmField(cpu, "_coreMax", maximum); LhmField(cpu, "_coreAvg", average);
        type.GetMethod("UpdateCoreTemperatureSummary", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(cpu, null);
        Assert.Null(maximum.Value); Assert.Null(average.Value);
    }
}
