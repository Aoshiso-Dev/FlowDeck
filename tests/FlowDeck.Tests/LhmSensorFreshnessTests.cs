using FlowDeck.Core;
using FlowDeck.Infrastructure;
using LibreHardwareMonitor.Hardware;

namespace FlowDeck.Tests;

public partial class LhmSensorFreshnessTests
{
    private sealed class Settings : ISettings
    {
        public bool Contains(string name) => false;
        public string GetValue(string name, string value) => value;
        public void SetValue(string name, string value) => throw new InvalidOperationException("Tests must not persist LHM state");
        public void Remove(string name) { }
    }
    private sealed class CachedHardware(HardwareType type = HardwareType.Cpu) : Hardware("Test model", new Identifier("test"), new Settings())
    {
        public Action OnUpdate = () => { };
        public override HardwareType HardwareType => type;
        public override void Update() => OnUpdate();
        public void Add(ISensor sensor) => ActivateSensor(sensor);
        public void Remove(ISensor sensor) => DeactivateSensor(sensor);
    }
    private static ISensor Sensor(CachedHardware hardware, int index = 0, bool disabledHistory = false)
    {
        var type = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Hardware.Sensor", throwOnError: true)!;
        var sensor = (ISensor)Activator.CreateInstance(type,
            ["Temperature", index, false, SensorType.Temperature, hardware, Array.Empty<ParameterDescription>(), new Settings(), disabledHistory])!;
        hardware.Add(sensor); return sensor;
    }
    private static void Set(ISensor sensor, float? value) => sensor.GetType().GetProperty("Value")!.SetValue(sensor, value);
    private static HardwareSensorValue Read(LhmSensorFreshness freshness, CachedHardware hardware, long timestamp)
    {
        var acquisitions = freshness.Update(hardware, timestamp, out _); var readings = new List<HardwareSensorValue>();
        HardwareSensorCapture.AddValues(hardware, readings, acquisitions); return Assert.Single(readings);
    }
    [Fact] public void AnUnchangedTemperatureAssignedAgainIsFreshButACachedValueIsNot()
    {
        var hardware = new CachedHardware(); var sensor = Sensor(hardware); Set(sensor, 60);
        var freshness = new LhmSensorFreshness(); hardware.OnUpdate = () => Set(sensor, 60);
        var first = Read(freshness, hardware, 1000); Assert.Equal(60, first.Value); Assert.Null(first.Problem);
        var second = Read(freshness, hardware, 2000); Assert.Equal(60, second.Value); Assert.Equal(2000, second.Timestamp);
        hardware.OnUpdate = () => { }; var cached = Read(freshness, hardware, 3000);
        Assert.Null(cached.Value); Assert.NotNull(cached.Problem); Assert.Equal(2000, cached.Timestamp); Assert.Equal(60, sensor.Value);
        hardware.OnUpdate = () => Set(sensor, 60); var recovered = Read(freshness, hardware, 4000);
        Assert.Equal(60, recovered.Value); Assert.Equal(4000, recovered.Timestamp);
    }
    [Fact] public void CounterWrapAndCompressedHistoryDoNotRequireTheNumericValueToChange()
    {
        var hardware = new CachedHardware(); var sensor = Sensor(hardware); var freshness = new LhmSensorFreshness();
        hardware.OnUpdate = () => Set(sensor, 60);
        for (var i = 1; i <= 20; i++) { var reading = Read(freshness, hardware, i * 1000); Assert.Equal(60, reading.Value); Assert.Equal(i * 1000, reading.Timestamp); }
        Assert.Equal(2, sensor.Values.Count());
    }
    [Fact] public void SeveralAssignmentsDuringOneUpdateCanWrapTheCounterAndStillProveAReading()
    {
        var hardware = new CachedHardware(); var sensor = Sensor(hardware); var freshness = new LhmSensorFreshness();
        hardware.OnUpdate = () => { for (var i = 0; i < 4; i++) Set(sensor, 60); };
        var reading = Read(freshness, hardware, 1000); Assert.Equal(60, reading.Value); Assert.Null(reading.Problem);
    }
    [Fact] public void AThrowAfterAnAssignmentDoesNotPublishThePartialUpdate()
    {
        var hardware = new CachedHardware(); var sensor = Sensor(hardware); var freshness = new LhmSensorFreshness();
        hardware.OnUpdate = () => Set(sensor, 60); Read(freshness, hardware, 1000);
        hardware.OnUpdate = () => { Set(sensor, 70); throw new IOException("failed"); };
        var reading = Read(freshness, hardware, 2000); Assert.Null(reading.Value); Assert.Equal(1000, reading.Timestamp); Assert.Contains("failed", reading.Problem);
    }
    [Fact] public void AssignmentEvidenceWorksWithoutEnablingOptionalHistory()
    {
        var hardware = new CachedHardware(); var sensor = Sensor(hardware, disabledHistory: true); var freshness = new LhmSensorFreshness();
        hardware.OnUpdate = () => Set(sensor, 60); var first = Read(freshness, hardware, 1000);
        hardware.OnUpdate = () => Set(sensor, 70); var second = Read(freshness, hardware, 2000);
        Assert.Equal(60, first.Value); Assert.Equal(70, second.Value); Assert.Null(second.Problem); Assert.Equal(2000, second.Timestamp);
        Assert.Equal(TimeSpan.Zero, sensor.ValuesTimeWindow); Assert.Equal(70, sensor.Value);
        hardware.OnUpdate = () => { }; var cached = Read(freshness, hardware, 3000);
        Assert.Null(cached.Value); Assert.Equal(2000, cached.Timestamp);
    }
    [Fact] public void SuperIoReassigningItsChipCacheIsNotEvidenceOfAFreshHardwareMeasurement()
    {
        var hardware = new CachedHardware(HardwareType.SuperIO); var sensor = Sensor(hardware); var freshness = new LhmSensorFreshness();
        hardware.OnUpdate = () => Set(sensor, 60); var first = Read(freshness, hardware, 1000);
        var second = Read(freshness, hardware, 2000);
        Assert.Null(first.Value); Assert.Null(second.Value); Assert.Equal(1000, second.Timestamp); Assert.Contains("実測", second.Problem);
    }
    [Fact] public async Task VerifiedSuperIoLeaseCoversUpdateAndPreservesRecursiveMutexOwnership()
    {
        using var mutex = new Mutex(); var hardware = new CachedHardware(HardwareType.SuperIO); var sensor = Sensor(hardware);
        var verified = false; var freshness = new LhmSensorFreshness(_ => LhmSuperIoReadLease.Acquire(mutex, () => { verified = true; return true; }));
        hardware.OnUpdate = () => { Assert.True(verified); Assert.True(mutex.WaitOne(0)); try { Set(sensor, 60); } finally { mutex.ReleaseMutex(); } };
        Assert.Equal(60, Read(freshness, hardware, 1000).Value);
        Assert.True(await Task.Run(() => { var entered = mutex.WaitOne(0); if (entered) mutex.ReleaseMutex(); return entered; }));
    }
    [Fact] public async Task FailedChipIdentityPreventsTheReadAndReleasesTheBus()
    {
        using var mutex = new Mutex(); var hardware = new CachedHardware(HardwareType.SuperIO); var sensor = Sensor(hardware); Set(sensor, 60);
        var calls = 0; hardware.OnUpdate = () => calls++; var freshness = new LhmSensorFreshness(_ => LhmSuperIoReadLease.Acquire(mutex, () => false));
        Assert.Null(Read(freshness, hardware, 1000).Value); Assert.Equal(0, calls);
        Assert.True(await Task.Run(() => { var entered = mutex.WaitOne(0); if (entered) mutex.ReleaseMutex(); return entered; }));
    }
    [Fact] public void ANewlyActivatedSensorRequiresAnObservedUpdateBeforeItCanBecomeAControlInput()
    {
        var hardware = new CachedHardware(); var sensor = Sensor(hardware); hardware.Remove(sensor); Set(sensor, 60);
        var freshness = new LhmSensorFreshness(); hardware.OnUpdate = () => hardware.Add(sensor);
        Assert.Null(Read(freshness, hardware, 1000).Value);
        hardware.OnUpdate = () => Set(sensor, 61); Assert.Equal(61, Read(freshness, hardware, 2000).Value);
    }
    [Theory] [InlineData(null)] [InlineData(float.NaN)] [InlineData(0f)] [InlineData(151f)]
    public void UpdatedButUnavailableOrOutOfRangeTemperaturesNeverBecomeZeroOrValidInputs(float? value)
    {
        var hardware = new CachedHardware(); var sensor = Sensor(hardware); var freshness = new LhmSensorFreshness();
        hardware.OnUpdate = () => Set(sensor, value); var reading = Read(freshness, hardware, 1000);
        Assert.Null(reading.Value); Assert.NotNull(reading.Problem); Assert.Equal(DeviceFeature.Temperature, reading.Feature);
    }
}
