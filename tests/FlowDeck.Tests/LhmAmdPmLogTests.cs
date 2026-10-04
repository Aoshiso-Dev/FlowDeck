using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using LibreHardwareMonitor.Hardware;

namespace FlowDeck.Tests;

public partial class LhmSensorFreshnessTests
{
    private sealed class PmLog
    {
        private static readonly Assembly Library = typeof(Computer).Assembly;
        private static Type Type(string name) => Library.GetType(name, true)!;
        private static readonly Type DataType = Type("LibreHardwareMonitor.Interop.AtiAdlxx+ADLPMLogData");
        private static readonly Type OutputType = Type("LibreHardwareMonitor.Interop.AtiAdlxx+ADLPMLogDataOutput");
        private static readonly Type SensorTypeId = Type("LibreHardwareMonitor.Interop.AtiAdlxx+ADLPMLogSensors");
        private readonly Type gpuType = Type("LibreHardwareMonitor.Hardware.Gpu.AmdGpu");
        private readonly object gpu;
        public readonly object Snapshot = Activator.CreateInstance(Type("LibreHardwareMonitor.Hardware.Gpu.AmdPmLogSnapshot"))!;
        public const int Edge = 8, Fan = 14;
        public PmLog(params int[] supported)
        {
            // Only the compiled log-to-sensor path is used. No ADL context, DLL, GPU or PawnIO handle is opened.
            gpu = RuntimeHelpers.GetUninitializedObject(gpuType);
            typeof(Hardware).GetField("_active", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(gpu, new HashSet<ISensor>());
            Field("_pmLogSnapshot", Snapshot); Field("_pmLogStarted", true);
            var support = Activator.CreateInstance(Type("LibreHardwareMonitor.Interop.AtiAdlxx+ADLPMLogSupportInfo"))!;
            var ids = new ushort[256]; for (var i = 0; i < supported.Length; i++) ids[i] = (ushort)supported[i];
            SetField(support, "usSensors", ids); Field("_adlPMLogSupportInfo", support);
        }
        private void Field(string name, object value) => gpuType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(gpu, value);
        public static void SetField(object target, string field, object? value) => target.GetType().GetField(field)!.SetValue(target, value);
        public static object Frame(ulong stamp = 1, uint value = 60, uint rate = 1000, uint version = 1)
        {
            var frame = Activator.CreateInstance(DataType)!; var values = new uint[512]; values[0] = Edge; values[1] = value;
            values[2] = Fan; values[3] = 0;
            SetField(frame, "ulVersion", version); SetField(frame, "ulLastUpdated", stamp);
            SetField(frame, "ulActiveSampleRate", rate); SetField(frame, "ulValues", values); SetField(frame, "ulReserved", new uint[256]);
            return frame;
        }
        public void Observe(object first, object second, double seconds) => Snapshot.GetType().GetMethod("Observe")!
            .Invoke(Snapshot, [first, second, (long)(seconds * Stopwatch.Frequency)]);
        public void Observe(object frame, double seconds) => Observe(frame, frame, seconds);
        public void Capture(object frame)
        {
            var address = Marshal.AllocHGlobal(Marshal.SizeOf(DataType));
            try
            {
                Marshal.StructureToPtr(frame, address, false);
                Snapshot.GetType().GetMethod("Capture")!.Invoke(Snapshot, [address]);
            }
            finally { Marshal.FreeHGlobal(address); }
        }
        public void BeginUpdate(ISensor sensor) => Field("_beforeUpdate", new Dictionary<ISensor, ulong>
            { [sensor] = ((ISensorUpdateEvidence)sensor).UpdateSequence });
        public bool Read(ISensor sensor, int id = Edge, bool reset = false, int? od8Value = null)
        {
            var output = Activator.CreateInstance(OutputType)!;
            if (od8Value is { } value)
            {
                var cellType = Type("LibreHardwareMonitor.Interop.AtiAdlxx+ADLSingleSensorData");
                var cells = Array.CreateInstance(cellType, 256); var cell = Activator.CreateInstance(cellType)!;
                SetField(cell, "supported", 1); SetField(cell, "value", value); cells.SetValue(cell, id);
                SetField(output, "sensors", cells);
            }
            Field("_overdrive8LogExists", od8Value.HasValue);
            return (bool)gpuType.GetMethod("GetAdlSensor", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(gpu, [output, Enum.ToObject(SensorTypeId, id), sensor, 1f, reset])!;
        }
    }

    [Fact]
    public void SharedLogMustAdvanceAndRepeatedCopiesRetainTheirOriginalAcquisitionTime()
    {
        var gpu = new CachedHardware(HardwareType.GpuAmd); var sensor = Sensor(gpu); var log = new PmLog(PmLog.Edge);
        var freshness = new LhmSensorFreshness(); object frame = PmLog.Frame(); double seconds = 0;
        gpu.OnUpdate = () => { log.Observe(frame, seconds); log.Read(sensor); };
        Assert.Null(Read(freshness, gpu, 1000).Value); // Unknown age on first attachment.
        seconds = 1; frame = PmLog.Frame(2); var acquired = Read(freshness, gpu, 2000);
        Assert.Equal(60, acquired.Value); Assert.Null(acquired.Problem);
        for (var i = 2; i < 8; i++)
        {
            seconds = i; var repeat = Read(freshness, gpu, 1000 + i * 1000);
            Assert.Equal(60, repeat.Value); Assert.Null(repeat.Problem); Assert.Equal(2000, repeat.Timestamp);
        }
        frame = PmLog.Frame(3); seconds = 8; var recovered = Read(freshness, gpu, 9000);
        Assert.Equal(60, recovered.Value); Assert.Equal(9000, recovered.Timestamp);
    }

    [Fact]
    public void SharedMemoryCaptureAndCompiledAmdParserProduceSampleEvidenceWithoutHardware()
    {
        var hardware = new CachedHardware(); var sensor = Sensor(hardware); var log = new PmLog(PmLog.Edge);
        log.Capture(PmLog.Frame()); Assert.False(log.Read(sensor));
        log.Capture(PmLog.Frame(2)); Assert.True(log.Read(sensor)); Assert.Equal(60, sensor.Value);
        var evidence = (ISensorUpdateEvidence)sensor; var sample = evidence.SourceSample; Assert.NotNull(sample);
        log.Capture(PmLog.Frame(2)); Assert.True(log.Read(sensor)); Assert.Equal(sample, evidence.SourceSample);
        log.Capture(PmLog.Frame(3)); Assert.True(log.Read(sensor)); Assert.NotEqual(sample, evidence.SourceSample);
    }

    [Theory]
    [InlineData("stamp")]
    [InlineData("rate")]
    [InlineData("version")]
    [InlineData("payload")]
    public void TwoDifferentSharedMemoryCopiesInvalidateTheSnapshotAndRequireNewProgress(string difference)
    {
        var sensor = Sensor(new CachedHardware()); var log = new PmLog(PmLog.Edge);
        log.Observe(PmLog.Frame(), 0); log.Observe(PmLog.Frame(2), 1); Assert.True(log.Read(sensor));
        var first = PmLog.Frame(3); var second = difference switch
        { "stamp" => PmLog.Frame(4), "rate" => PmLog.Frame(3, rate: 500), "version" => PmLog.Frame(3, version: 2), _ => PmLog.Frame(3, 80) };
        log.Observe(first, second, 2); Assert.False(log.Read(sensor, reset: true)); Assert.Null(sensor.Value);
        log.Observe(first, 3); Assert.False(log.Read(sensor));
        log.Observe(PmLog.Frame(4), 4); Assert.True(log.Read(sensor));
    }

    [Theory]
    [InlineData("zeroStamp")]
    [InlineData("zeroRate")]
    [InlineData("largeRate")]
    [InlineData("missingValues")]
    [InlineData("shortValues")]
    [InlineData("duplicateSensor")]
    public void MalformedLogCannotBecomeANewSensorAssignment(string fault)
    {
        var sensor = Sensor(new CachedHardware()); var log = new PmLog(PmLog.Edge);
        log.Observe(PmLog.Frame(), 0); log.Observe(PmLog.Frame(2), 1); Assert.True(log.Read(sensor));
        var bad = PmLog.Frame(3);
        switch (fault)
        {
            case "zeroStamp": PmLog.SetField(bad, "ulLastUpdated", 0UL); break;
            case "zeroRate": PmLog.SetField(bad, "ulActiveSampleRate", 0U); break;
            case "largeRate": PmLog.SetField(bad, "ulActiveSampleRate", 86400001U); break;
            case "missingValues": PmLog.SetField(bad, "ulValues", null); break;
            case "shortValues": PmLog.SetField(bad, "ulValues", new uint[2]); break;
            default: var values = new uint[512]; values[0] = values[2] = PmLog.Edge; PmLog.SetField(bad, "ulValues", values); break;
        }
        var stamp = ((ISensorUpdateEvidence)sensor).UpdateSequence;
        log.Observe(bad, 2); Assert.False(log.Read(sensor)); Assert.Equal(stamp, ((ISensorUpdateEvidence)sensor).UpdateSequence);
    }

    [Theory]
    [InlineData("backwards")]
    [InlineData("rate")]
    [InlineData("version")]
    [InlineData("longGap")]
    [InlineData("clockBackwards")]
    public void LogOrObservationDiscontinuityRequiresANewAdvancingFrame(string change)
    {
        var sensor = Sensor(new CachedHardware()); var log = new PmLog(PmLog.Edge);
        log.Observe(PmLog.Frame(10), 0); log.Observe(PmLog.Frame(11), 1); Assert.True(log.Read(sensor));
        var frame = change switch { "backwards" => PmLog.Frame(9), "rate" => PmLog.Frame(12, rate: 500),
            "version" => PmLog.Frame(12, version: 2), _ => PmLog.Frame(12) };
        var seconds = change == "longGap" ? 4 : change == "clockBackwards" ? 0 : 2;
        log.Observe(frame, seconds); Assert.False(log.Read(sensor, reset: true)); Assert.Null(sensor.Value);
        log.Observe(frame, seconds + .1); Assert.False(log.Read(sensor));
        PmLog.SetField(frame, "ulLastUpdated", 13UL); log.Observe(frame, seconds + 1); Assert.True(log.Read(sensor));
    }

    [Fact]
    public void PayloadChangesWithoutANewStampDoNotInventANewMeasurement()
    {
        var sensor = Sensor(new CachedHardware()); var log = new PmLog(PmLog.Edge);
        log.Observe(PmLog.Frame(), 0); log.Observe(PmLog.Frame(2), 1); Assert.True(log.Read(sensor));
        log.Observe(PmLog.Frame(2, 90), 2); Assert.False(log.Read(sensor)); Assert.Equal(60, sensor.Value);
        log.Observe(PmLog.Frame(2, 90), 3); Assert.False(log.Read(sensor));
        log.Observe(PmLog.Frame(3, 90), 4); Assert.True(log.Read(sensor)); Assert.Equal(90, sensor.Value);
    }

    [Fact]
    public void StalledSupportedLoggerCannotBeRejuvenatedThroughTheAlternateQuery()
    {
        var sensor = Sensor(new CachedHardware()); var log = new PmLog(PmLog.Edge);
        log.Observe(PmLog.Frame(), 0); Assert.False(log.Read(sensor, od8Value: 80)); Assert.Null(sensor.Value);
        Set(sensor, 72); // Successful OD5/ODN acquisition during this update stays available.
        Assert.False(log.Read(sensor, od8Value: 80)); Assert.Equal(72, sensor.Value); Assert.Null(((ISensorUpdateEvidence)sensor).SourceSample);
        var unsupported = new PmLog(); Assert.True(unsupported.Read(sensor, od8Value: 80)); Assert.Equal(80, sensor.Value);
    }

    [Fact]
    public void AZeroRpmIsAValidSharedLogValueButAMissingSensorHasNoAssignment()
    {
        var sensor = Sensor(new CachedHardware()); var log = new PmLog(PmLog.Edge, PmLog.Fan, 9);
        log.Observe(PmLog.Frame(), 0); log.Observe(PmLog.Frame(2), 1);
        Assert.True(log.Read(sensor, PmLog.Fan)); Assert.Equal(0, sensor.Value); var stamp = ((ISensorUpdateEvidence)sensor).UpdateSequence;
        Assert.False(log.Read(sensor, 9)); Assert.Equal(stamp, ((ISensorUpdateEvidence)sensor).UpdateSequence);
    }

    [Fact]
    public void RepeatedPmLogDoesNotOverwriteAValidDirectReadingFromTheCurrentUpdate()
    {
        var sensor = Sensor(new CachedHardware()); var log = new PmLog(PmLog.Edge);
        log.Observe(PmLog.Frame(), 0); log.Observe(PmLog.Frame(2), 1); Assert.True(log.Read(sensor));
        log.BeginUpdate(sensor); Set(sensor, 72); log.Observe(PmLog.Frame(2), 2);
        Assert.True(log.Read(sensor)); Assert.Equal(72, sensor.Value); Assert.Null(((ISensorUpdateEvidence)sensor).SourceSample);
        log.BeginUpdate(sensor); log.Observe(PmLog.Frame(2), 3); // Prior direct value alone proves nothing now.
        Assert.True(log.Read(sensor)); Assert.Equal(60, sensor.Value); Assert.NotNull(((ISensorUpdateEvidence)sensor).SourceSample);
        log.BeginUpdate(sensor); Set(sensor, null); log.Observe(PmLog.Frame(3, 75), 4);
        Assert.True(log.Read(sensor)); Assert.Equal(75, sensor.Value);
    }

    [Fact]
    public void DirectReadAndPartialFailureDoNotEraseTheOriginalSharedSampleTime()
    {
        var hardware = new CachedHardware(); var sensor = Sensor(hardware); var log = new PmLog(PmLog.Edge); var freshness = new LhmSensorFreshness();
        log.Observe(PmLog.Frame(), 0); log.Observe(PmLog.Frame(2), 1);
        hardware.OnUpdate = () => { log.Read(sensor); throw new IOException("partial failure"); };
        Assert.NotNull(Read(freshness, hardware, 1000).Problem);
        hardware.OnUpdate = () => Set(sensor, 70); Assert.Equal(2000, Read(freshness, hardware, 2000).Timestamp);
        hardware.OnUpdate = () => log.Read(sensor); var repeated = Read(freshness, hardware, 3000);
        Assert.Equal(60, repeated.Value); Assert.Null(repeated.Problem); Assert.Equal(1000, repeated.Timestamp);
    }

    private sealed class PmBackend(Func<HardwareSensorValue> read) : IHardwareSensorBackend
    {
        public Task<HardwareSensorDevice[]> ReadDevicesAsync(CancellationToken ct) => Task.FromResult(new[]
        {
            new HardwareSensorDevice("/gpu/amd/0", "AMD test", DeviceKind.Graphics, new('b', 64), null,
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", null, [read()])
        });
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RepeatedSharedLogBecomesStaleInTheCommonRuntimeAndRecoversOnlyWhenTheDriverAdvances()
    {
        var clock = new TestClock(); var hardware = new CachedHardware(HardwareType.GpuAmd); var sensor = Sensor(hardware);
        var log = new PmLog(PmLog.Edge); var freshness = new LhmSensorFreshness(); object frame = PmLog.Frame();
        hardware.OnUpdate = () => { log.Observe(frame, clock.GetTimestamp() / 1000d); log.Read(sensor); };
        var backend = new PmBackend(() => Read(freshness, hardware, clock.GetTimestamp()));
        var provider = new HardwareSensorProvider("0123456789abcdef0123456789abcdef", backend);
        await using var runtime = new DeviceControlRuntime([], ["user"], new TestLog(), clock, providers: [provider]);
        await runtime.PollAsync(); Assert.True(Assert.Single(runtime.State().Telemetry!).Stale);
        clock.Advance(1); frame = PmLog.Frame(2); await runtime.PollAsync(); Assert.False(Assert.Single(runtime.State().Telemetry!).Stale);
        for (var i = 1; i <= 4; i++)
        {
            clock.Advance(1); await runtime.PollAsync(); var value = Assert.Single(runtime.State().Telemetry!);
            Assert.Equal(i, value.AgeSeconds); Assert.Equal(i >= 3, value.Stale);
        }
        clock.Advance(1); frame = PmLog.Frame(3); await runtime.PollAsync(); var recovered = Assert.Single(runtime.State().Telemetry!);
        Assert.False(recovered.Stale); Assert.Equal(0, recovered.AgeSeconds); Assert.Equal(60, recovered.Value);
    }
}
