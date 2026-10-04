using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using LibreHardwareMonitor.Hardware;

namespace FlowDeck.Tests;

public class DeviceMeasurementTests
{
    [Theory]
    [InlineData(DeviceFeature.Temperature, 60d, 0d, false, DeviceMeasurementIssue.None, DeviceMeasurementState.Valid)]
    [InlineData(DeviceFeature.Temperature, null, 0d, true, DeviceMeasurementIssue.Unavailable, DeviceMeasurementState.Unavailable)]
    [InlineData(DeviceFeature.Temperature, null, 0d, true, DeviceMeasurementIssue.OutOfRange, DeviceMeasurementState.OutOfRange)]
    [InlineData(DeviceFeature.Temperature, 0d, 0d, false, DeviceMeasurementIssue.None, DeviceMeasurementState.OutOfRange)]
    [InlineData(DeviceFeature.Temperature, 150d, 0d, false, DeviceMeasurementIssue.None, DeviceMeasurementState.OutOfRange)]
    [InlineData(DeviceFeature.Rpm, -1d, 0d, false, DeviceMeasurementIssue.None, DeviceMeasurementState.OutOfRange)]
    [InlineData(DeviceFeature.Rpm, 0d, 0d, false, DeviceMeasurementIssue.None, DeviceMeasurementState.Valid)]
    [InlineData(DeviceFeature.Rpm, double.NaN, 0d, false, DeviceMeasurementIssue.None, DeviceMeasurementState.OutOfRange)]
    [InlineData(DeviceFeature.Rpm, double.PositiveInfinity, 0d, false, DeviceMeasurementIssue.None, DeviceMeasurementState.OutOfRange)]
    [InlineData(DeviceFeature.Rpm, 900d, 3d, false, DeviceMeasurementIssue.None, DeviceMeasurementState.Stale)]
    [InlineData(DeviceFeature.Rpm, 900d, -1d, false, DeviceMeasurementIssue.None, DeviceMeasurementState.Stale)]
    [InlineData(DeviceFeature.Rpm, 900d, 0d, true, DeviceMeasurementIssue.None, DeviceMeasurementState.Stale)]
    [InlineData(DeviceFeature.DutyRead, 101d, 0d, false, DeviceMeasurementIssue.None, DeviceMeasurementState.OutOfRange)]
    [InlineData(DeviceFeature.DutyRead, 0d, 0d, false, DeviceMeasurementIssue.None, DeviceMeasurementState.Valid)]
    public void ValuesAgeAndAcquisitionFailuresRemainDistinct(DeviceFeature feature, double? value, double age, bool stale,
        DeviceMeasurementIssue issue, DeviceMeasurementState expected)
    {
        var sample = new DeviceTelemetry(new("device", "endpoint"), feature, value, age, stale, Issue: issue);
        Assert.Equal(expected, DeviceMeasurement.State(sample, 0));
        Assert.Equal(expected == DeviceMeasurementState.Valid, DeviceDashboard.Fresh(sample, 0));
    }

    private sealed class Driver(TestClock clock) : IDeviceControlDriver, IDeviceTelemetrySource
    {
        public string DeviceId => "sensor";
        public DeviceSample[] Samples = [new("rpm", DeviceFeature.Rpm, 900, clock.GetTimestamp())];
        public DeviceObservation Observe() => new(DeviceId, "test", "同名センサー", DeviceKind.Other, IdentityConfidence.SerialBound,
            "memory", null, ["memory"], [new("rpm", "回転数", EndpointKind.Sensor,
                [new(DeviceFeature.Rpm, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)])]);
        public DeviceSample[] ReadCachedSamples() => Samples;
        public string? Validate(DeviceEndpointAction action) => "monitoring only";
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => throw new InvalidOperationException("No writes");
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => Task.FromResult<DeviceDriverReleaseResult[]>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(-1d, DeviceMeasurementIssue.None, DeviceMeasurementIssue.OutOfRange)]
    [InlineData(double.NaN, DeviceMeasurementIssue.None, DeviceMeasurementIssue.OutOfRange)]
    [InlineData(null, DeviceMeasurementIssue.None, DeviceMeasurementIssue.Unavailable)]
    [InlineData(900d, DeviceMeasurementIssue.OutOfRange, DeviceMeasurementIssue.OutOfRange)]
    [InlineData(900d, DeviceMeasurementIssue.Unavailable, DeviceMeasurementIssue.Unavailable)]
    public async Task RuntimeMasksInvalidValuesButRetainsTheReasonThroughWireAndDiagnostics(double? raw,
        DeviceMeasurementIssue declared, DeviceMeasurementIssue expected)
    {
        var clock = new TestClock(); var driver = new Driver(clock) { Samples = [new("rpm", DeviceFeature.Rpm, raw, clock.GetTimestamp(), Issue: declared)] };
        await using var runtime = new DeviceControlRuntime([driver], ["user"], new TestLog(), clock);
        var hello = runtime.OpenSession("user"); var state = runtime.State();
        var sample = Assert.Single(state.Telemetry!); Assert.Equal(expected, sample.Issue); Assert.Null(sample.Value); Assert.True(sample.Stale);
        var reply = new DeviceServiceReply(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), true, State: state);
        var decoded = DeviceServiceProtocol.ReadReply(JsonSerializer.SerializeToUtf8Bytes(reply, Contract.Json), hello);
        Assert.Equal(sample, Assert.Single(decoded.State!.Telemetry!));
        var target = new EndpointReference("sensor", "rpm");
        var item = new DeviceDashboardItem(target, DeviceFeature.Rpm, "ポンプ", "pump");
        var reading = DeviceDashboard.Read(item, new(2, []), decoded.State, 0, true);
        Assert.Equal(expected == DeviceMeasurementIssue.OutOfRange ? "範囲外" : "未取得", reading.State);
        Assert.Null(reading.Value); Assert.Equal("—", reading.DisplayValue);
        var clocked = new DashboardHistory(clock); clocked.Add([reading]); Assert.Null(Assert.Single(clocked.Get(reading.Key)).Value);
        var disconnected = decoded.State with { Inventory = decoded.State.Inventory with { Devices = [decoded.State.Inventory.Devices[0] with { Connected = false }] } };
        Assert.Equal("未接続", DeviceDashboard.Read(item, new(2, []), disconnected, 0, true).State);
        Assert.Equal("サービス未接続", DeviceDashboard.Read(item, new(2, []), decoded.State, 0, false).State);
        var diagnostic = await runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "PC",
            DeviceOperation.ReadDiagnostics, Diagnostics: new(DeviceDiagnosticScope.Measurements)));
        Assert.True(diagnostic.Success, diagnostic.Error);
        Assert.Equal(diagnostic.Diagnostics!.TotalBytes, diagnostic.Diagnostics.Data.Length);
        var report = JsonSerializer.Deserialize<DeviceDiagnosticReport>(diagnostic.Diagnostics.Data, Contract.Json)!;
        var measured = Assert.Single(Assert.Single(Assert.Single(report.Devices).Endpoints).Measurements!);
        Assert.Equal(expected, measured.Issue); Assert.Null(measured.Value);
    }

    [Theory] [InlineData("unknown")] [InlineData("value")] [InlineData("fresh")]
    public async Task WireRejectsUnknownIssueAndClaimsOfUsableOutOfRangeData(string scenario)
    {
        var clock = new TestClock(); await using var runtime = new DeviceControlRuntime([new Driver(clock)], ["user"], new TestLog(), clock);
        var hello = runtime.OpenSession("user"); var state = runtime.State();
        var invalid = state.Telemetry![0] with { Value = null, Stale = true, Issue = DeviceMeasurementIssue.OutOfRange };
        invalid = scenario switch { "unknown" => invalid with { Issue = (DeviceMeasurementIssue)99 },
            "value" => invalid with { Value = 0 }, _ => invalid with { Stale = false } };
        var reply = new DeviceServiceReply(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), true, State: state with { Telemetry = [invalid] });
        Assert.Throws<IOException>(() => DeviceServiceProtocol.ReadReply(JsonSerializer.SerializeToUtf8Bytes(reply, Contract.Json), hello));
        var oldJson = JsonSerializer.SerializeToUtf8Bytes(reply with { State = state }, Contract.Json);
        Assert.DoesNotContain("\"issue\"", System.Text.Encoding.UTF8.GetString(oldJson));
        Assert.Equal(DeviceMeasurementIssue.None, DeviceServiceProtocol.ReadReply(oldJson, hello).State!.Telemetry![0].Issue);
    }
}

public partial class HardwareSensorProviderTests
{
    [Theory]
    [InlineData(SensorType.Temperature, null, DeviceMeasurementIssue.Unavailable)]
    [InlineData(SensorType.Temperature, 0f, DeviceMeasurementIssue.OutOfRange)]
    [InlineData(SensorType.Temperature, 150f, DeviceMeasurementIssue.OutOfRange)]
    [InlineData(SensorType.Fan, 30001f, DeviceMeasurementIssue.OutOfRange)]
    [InlineData(SensorType.Fan, 0f, DeviceMeasurementIssue.None)]
    [InlineData(SensorType.Control, 101f, DeviceMeasurementIssue.OutOfRange)]
    [InlineData(SensorType.Temperature, float.NaN, DeviceMeasurementIssue.OutOfRange)]
    public async Task CapturePreservesAcquisitionValidityThroughTheHardwareProvider(SensorType type, float? raw, DeviceMeasurementIssue expected)
    {
        var clock = new TestClock(); var hardware = new Hardware(); hardware.Sensors = [new Sensor(hardware, type, 0, raw)];
        var values = new List<HardwareSensorValue>(); var timestamp = clock.GetTimestamp();
        HardwareSensorCapture.AddValues(hardware, values, hardware.Sensors.ToDictionary(s => s, _ => new SensorAcquisition(timestamp, null)));
        Assert.Equal(expected, values[0].Issue); Assert.Equal(expected == DeviceMeasurementIssue.None ? (double?)raw : null, values[0].Value);
        var cpu = Cpu(clock) with { Sensors = values.ToArray() }; var backend = new Backend { Devices = [cpu] };
        await using var runtime = Runtime(backend, clock, out _); await runtime.PollAsync();
        var state = runtime.State(); var sample = Assert.Single(state.Telemetry!); Assert.Equal(expected, sample.Issue);
        Assert.Equal(expected != DeviceMeasurementIssue.None, sample.Stale);
        var readings = DeviceCatalogPresentation.Build(new(state, new("generation", ServiceRuntimeBootstrap.MonitoringConfiguration(Installation), []), 0, true, null));
        var cap = Assert.Single(Assert.Single(Assert.Single(readings).Endpoints).Capabilities);
        Assert.Equal(expected == DeviceMeasurementIssue.None ? "利用可" : expected == DeviceMeasurementIssue.OutOfRange ? "範囲外" : "未取得", cap.Availability);
        var generation = state.Inventory.Devices[0].Generation;
        backend.Devices = [cpu with { Sensors = [values[0] with { Value = 50, Problem = null, Issue = DeviceMeasurementIssue.None }] }];
        await runtime.PollAsync(); Assert.Equal(generation, runtime.State().Inventory.Devices[0].Generation);
        Assert.Equal(DeviceMeasurementIssue.None, Assert.Single(runtime.State().Telemetry!).Issue);
        backend.Fail = true; await runtime.PollAsync();
        Assert.Equal(DeviceMeasurementIssue.Unavailable, Assert.Single(runtime.State().Telemetry!).Issue);
    }

    [Fact]
    public void FailedAcquisitionDoesNotTreatAnOlderCachedInvalidNumberAsANewOutOfRangeSample()
    {
        var hardware = new Hardware(); hardware.Sensors = [new Sensor(hardware, SensorType.Temperature, 0, 999)];
        var values = new List<HardwareSensorValue>();
        HardwareSensorCapture.AddValues(hardware, values, hardware.Sensors.ToDictionary(s => s, _ => new SensorAcquisition(123, "update failed")));
        var value = Assert.Single(values); Assert.Equal(DeviceMeasurementIssue.Unavailable, value.Issue); Assert.Null(value.Value);
        Assert.Equal(123, value.Timestamp); Assert.Equal("update failed", value.Problem);
    }
}
