using System.Reflection;
using System.Runtime.CompilerServices;
using FlowDeck.Infrastructure;
using LibreHardwareMonitor.Hardware;

namespace FlowDeck.Tests;

public partial class LhmSensorFreshnessTests
{
    private static readonly Type MutexType = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Hardware.Mutexes")!;
    private static FieldInfo MutexField(string name) => MutexType.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!;
    private static bool WaitBus(string name, int millisecondsTimeout = 0) => (bool)MutexType.GetMethod(name)!.Invoke(null, [millisecondsTimeout])!;

    [Theory]
    [InlineData("_isaBusMutex", "WaitIsaBus")]
    [InlineData("_pciBusMutex", "WaitPciBus")]
    [InlineData("_ecMutex", "WaitEc")]
    [InlineData("_razerMutex", "WaitRazer")]
    [InlineData("_usbSensors", "WaitUsbSensors")]
    public void MissingOrClosedLibraryMutexNeverGrantsBusOwnership(string fieldName, string wait)
    {
        lock (LibraryMutexGate)
        {
            var field = MutexField(fieldName); var previous = field.GetValue(null);
            try
            {
                field.SetValue(null, null); Assert.False(WaitBus(wait));
                var closed = new Mutex(); closed.Dispose(); field.SetValue(null, closed); Assert.False(WaitBus(wait));
            }
            finally { field.SetValue(null, previous); }
        }
    }

    [Fact]
    public void RecursiveBusAcquisitionAndTimeoutKeepTheirOwnershipCounts()
    {
        lock (LibraryMutexGate)
        {
            var field = MutexField("_pciBusMutex"); var previous = field.GetValue(null); using var mutex = new Mutex();
            field.SetValue(null, mutex);
            try
            {
                Assert.True(WaitBus("WaitPciBus")); Assert.True(WaitBus("WaitPciBus"));
                var foreignAcquired = true;
                var other = new Thread(() => { foreignAcquired = WaitBus("WaitPciBus"); if (foreignAcquired) mutex.ReleaseMutex(); });
                other.Start(); Assert.True(other.Join(TimeSpan.FromSeconds(2))); Assert.False(foreignAcquired);
                mutex.ReleaseMutex(); Assert.False(ProbeMutex(mutex));
                mutex.ReleaseMutex(); Assert.True(ProbeMutex(mutex));
            }
            finally { field.SetValue(null, previous); }
        }
    }

    [Fact]
    public void AbandonedMutexStillConveysOneOwnedLeaseWhichMustBeReleased()
    {
        lock (LibraryMutexGate)
        {
            var field = MutexField("_pciBusMutex"); var previous = field.GetValue(null); using var mutex = new Mutex();
            field.SetValue(null, mutex);
            try
            {
                var abandoning = new Thread(() => mutex.WaitOne()); abandoning.Start(); Assert.True(abandoning.Join(TimeSpan.FromSeconds(2)));
                // Join can return before Windows finishes abandoning the thread's mutex.
                // Allow that transition, then still verify exactly one acquired lease below.
                Assert.True(WaitBus("WaitPciBus", 2000));
                try { Assert.False(ProbeMutex(mutex)); }
                finally { mutex.ReleaseMutex(); }
                Assert.True(ProbeMutex(mutex));
            }
            finally { field.SetValue(null, previous); }
        }
    }

    [Theory]
    [InlineData("GetSmuVersion")]
    [InlineData("ReadPmTable")]
    [InlineData("UpdatePmTable")]
    [InlineData("UpdateAndReadPmTable")]
    [InlineData("ResolvePmTable")]
    public void CompiledCpuSmuCallsDoNotReachTheTransportWithoutPciOwnership(string methodName)
    {
        lock (LibraryMutexGate)
        {
            var field = MutexField("_pciBusMutex"); var previous = field.GetValue(null); field.SetValue(null, null);
            try
            {
                // A null PawnIO transport would cause NullReferenceException if reached. No module is loaded.
                var type = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.PawnIo.RyzenSmu")!;
                var target = RuntimeHelpers.GetUninitializedObject(type);
                object?[] args = methodName switch { "ReadPmTable" or "UpdateAndReadPmTable" => [4], "ResolvePmTable" => [null, null], _ => [] };
                var error = Assert.Throws<TargetInvocationException>(() => type.GetMethod(methodName)!.Invoke(target, args));
                Assert.IsType<TimeoutException>(error.InnerException);
            }
            finally { field.SetValue(null, previous); }
        }
    }

    [Fact]
    public void CompiledNuvotonReadSkipsItsAbsentTransportWhenTheIsaMutexIsMissing()
    {
        lock (LibraryMutexGate)
        {
            var field = MutexField("_isaBusMutex"); var previous = field.GetValue(null); field.SetValue(null, null);
            try
            {
                var type = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Hardware.Motherboard.Lpc.Nct677X")!;
                var target = RuntimeHelpers.GetUninitializedObject(type);
                type.GetField("_isNuvotonVendor", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, true);
                var temperatures = new float?[] { 60 };
                type.GetField("<Temperatures>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, temperatures);
                type.GetMethod("Update")!.Invoke(target, null);
                // The chip cache is not a new measurement. The FlowDeck lease/evidence layer must reject it.
                Assert.Equal(60f, temperatures[0]);
            }
            finally { field.SetValue(null, previous); }
        }
    }

    [Fact]
    public void MissingPciOwnershipMakesCachedTemperatureUnavailableUntilANewAcquisition()
    {
        lock (LibraryMutexGate)
        {
            var field = MutexField("_pciBusMutex"); var previous = field.GetValue(null); using var mutex = new Mutex();
            try
            {
                var hardware = new CachedHardware(); var sensor = Sensor(hardware); var freshness = new LhmSensorFreshness();
                hardware.OnUpdate = () =>
                {
                    if (!WaitBus("WaitPciBus")) return;
                    try { Set(sensor, 70); }
                    finally { mutex.ReleaseMutex(); }
                };
                field.SetValue(null, mutex); var first = Read(freshness, hardware, 1000); Assert.Equal(70, first.Value);
                field.SetValue(null, null); var unavailable = Read(freshness, hardware, 2000);
                Assert.Null(unavailable.Value); Assert.NotNull(unavailable.Problem); Assert.Equal(1000, unavailable.Timestamp);
                Assert.Equal(70f, sensor.Value);
                field.SetValue(null, mutex); var recovered = Read(freshness, hardware, 3000);
                Assert.Equal(70, recovered.Value); Assert.Null(recovered.Problem); Assert.Equal(3000, recovered.Timestamp);
            }
            finally { field.SetValue(null, previous); }
        }
    }
}
