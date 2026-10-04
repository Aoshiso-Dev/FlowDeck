using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceSavedCoolingTests
{
    private sealed class Sensors(TestClock clock) : IPhysicalDeviceDriver, IDeviceTelemetrySource
    {
        public string DeviceId => "cpu";
        public PhysicalDeviceBinding Binding = new("cpu", "cpu-serial", "test-cpu", new('a', 64));
        public PhysicalDeviceBinding? BindingCandidate => Binding;
        public DeviceObservation Observe() => new(DeviceId, "test", "CPU", DeviceKind.Processor, IdentityConfidence.SerialBound,
            "same-location", "test-cpu", ["sensor-bus"], [new("temperature", "温度", EndpointKind.Sensor,
                [new(DeviceFeature.Temperature, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)])]);
        public DeviceSample[] ReadCachedSamples() => [new("temperature", DeviceFeature.Temperature, 60, clock.GetTimestamp())];
        public string? Validate(DeviceEndpointAction action) => "read only";
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => throw new InvalidOperationException();
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => Task.FromResult(Array.Empty<DeviceDriverReleaseResult>());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    // Keep the observation stable to exercise the runtime's binding check independently of registry generations and driver checks.
    private sealed class PhysicalHub(HubDeviceDriver inner) : IDeviceCoolingDriver, IPhysicalDeviceDriver, IDeviceTelemetrySource
    {
        public string DeviceId => inner.DeviceId;
        public PhysicalDeviceBinding? Override;
        public PhysicalDeviceBinding? BindingCandidate => Override ?? inner.BindingCandidate;
        public DeviceObservation? Observe() => inner.Observe();
        public DeviceSample[] ReadCachedSamples() => inner.ReadCachedSamples();
        public CoolingEndpointConfiguration? CoolingConfiguration(string endpointId) => inner.CoolingConfiguration(endpointId);
        public string? ValidateCoolingSetup(string endpointId) => inner.ValidateCoolingSetup(endpointId);
        public string? Validate(DeviceEndpointAction action) => inner.Validate(action);
        public Task<DeviceDriverResult> ApplyCoolingAsync(CoolingActuation actuation, CancellationToken ct) => inner.ApplyCoolingAsync(actuation, ct);
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => inner.ApplyAsync(action, ct);
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => inner.ReleaseAsync(reason, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceTestDirectory directory = new();
        public readonly TestClock Clock = new();
        public readonly DemoHub Hub = new() { DelayMs = 0 };
        public readonly Sensors Sensors;
        public readonly HubDeviceDriver Driver;
        public readonly PhysicalHub Physical;
        public DeviceControlRuntime Runtime = null!;
        public DeviceServiceHello Hello = null!;
        private long sequence;
        private Rig()
        {
            Sensors = new(Clock);
            Driver = new("0123456789abcdef0123456789abcdef", DemoHub.Identity.Id, Hub, directory.Path, Clock);
            Physical = new(Driver);
        }
        public static async Task<Rig> Create(bool automatic = false, bool resume = true, bool changedSafety = false, string? omitBinding = null)
        {
            var rig = new Rig(); await rig.Driver.InitializeAsync();
            var safety = new CoolingSafety(30, 100, 50, CoolingExitAction.FixedDuty, []);
            var profile = new CoolingTemperatureProfile("saved", "保存カーブ", 1,
                [new(new("cpu", "temperature"), [new(20, 30), new(80, 90)], 95, 85)], 0, 100);
            rig.Driver.Configure(rig.Driver.BindingCandidate, [new(new(rig.Driver.DeviceId, "fan1"), true, safety, [profile],
                new(automatic ? FanMode.Auto : FanMode.Manual, automatic ? null : 65, automatic ? profile : null,
                    changedSafety ? safety with { Minimum = 40 } : safety), resume)]);
            var bindings = new[] { rig.Driver.BindingCandidate!, rig.Sensors.Binding }.Where(b => omitBinding != "hub" || b.DeviceId != rig.Driver.DeviceId)
                .Where(b => omitBinding != "sensor" || b.DeviceId != "cpu").ToArray();
            rig.Runtime = new([rig.Physical, rig.Sensors], ["user"], new TestLog(), rig.Clock, physicalBindings: bindings);
            return rig;
        }
        public DeviceServiceRequest Request(DeviceOperation operation, int? duty = null)
        {
            var state = Runtime.State(); var row = state.Endpoints.Single(s => s.Target == new EndpointReference(Driver.DeviceId, "fan1"));
            return new(2, Hello.Epoch, Hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", operation,
                state.Inventory.TopologyRevision, [new(row.Target, row.Generation, row.PolicyRevision)], duty);
        }
        public DeviceExecutionState State => Runtime.State().Endpoints.Single(s => s.Target == new EndpointReference(Driver.DeviceId, "fan1"));
        public async ValueTask DisposeAsync() { await Runtime.DisposeAsync(); directory.Dispose(); }
    }
    [Theory]
    [InlineData(false, 65)]
    [InlineData(true, 70)]
    public async Task StartupAndOpeningTheGuiDoNotWriteUntilTheOwnerRestoresASavedPolicy(bool automatic, int expected)
    {
        await using var r = await Rig.Create(automatic);
        await r.Runtime.RecoverStartupAsync(); Assert.Equal(0, r.Hub.Writes);
        r.Hello = r.Runtime.OpenSession("user"); Assert.Contains("restore-saved-cooling-v2", r.Hello.Features);
        Assert.Equal(0, r.Hub.Writes);
        var request = r.Request(DeviceOperation.RestoreSavedCooling);
        var reply = await r.Runtime.ExecuteAsync(request);
        Assert.Equal(Outcome.Confirmed, reply.Result!.Outcome); Assert.Equal(expected, reply.Result.Targets[0].ReportedDuty);
        Assert.Equal(automatic ? null : (int?)65, reply.Result.Targets[0].RequestedDuty);
        Assert.Equal(automatic ? FanMode.Auto : FanMode.Manual, r.State.Cooling!.Mode);
        Assert.Equal(automatic ? null : (int?)65, r.State.RequestedDuty);
        await r.Runtime.ExecuteAsync(request); Assert.Equal(1, r.Hub.Writes);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task DisabledResumeOrChangedSafetyCannotRestore(bool resume, bool changedSafety)
    {
        await using var r = await Rig.Create(resume: resume, changedSafety: changedSafety); r.Hello = r.Runtime.OpenSession("user");
        Assert.False((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.RestoreSavedCooling))).Success);
        Assert.Equal(0, r.Hub.Writes);
    }
    [Fact]
    public async Task SavedPolicyCannotReplaceAnActiveUserChoice()
    {
        await using var r = await Rig.Create(); r.Hello = r.Runtime.OpenSession("user");
        Assert.True((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetDuty, 80))).Success);
        Assert.False((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.RestoreSavedCooling))).Success);
        Assert.Equal(80, r.State.ReportedDuty); Assert.Equal(1, r.Hub.Writes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => r.Runtime.RecoverStartupAsync());
    }
    [Theory]
    [InlineData("hub")]
    [InlineData("sensor")]
    public async Task SavedPolicyRequiresExplicitPhysicalBindingsForOutputAndRequiredSensors(string omitted)
    {
        await using var r = await Rig.Create(automatic: true, omitBinding: omitted); r.Hello = r.Runtime.OpenSession("user");
        Assert.NotEmpty(r.Runtime.State().Telemetry!);
        Assert.False((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.RestoreSavedCooling))).Success);
        Assert.Equal(0, r.Hub.Writes);
    }
    [Fact]
    public async Task SensorReplacementFallsBackButDoesNotAuthorizeNewControl()
    {
        await using var r = await Rig.Create(automatic: true); r.Hello = r.Runtime.OpenSession("user");
        await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.RestoreSavedCooling));
        r.Sensors.Binding = r.Sensors.Binding with { Fingerprint = new('b', 64) };
        await r.Runtime.TickCoolingAsync(); Assert.Equal(100, r.State.ReportedDuty); Assert.True(r.State.Cooling!.Faulted);
        Assert.False((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.ResumeCooling))).Success);
        Assert.True((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.ReleaseCooling))).Success);
        Assert.False((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.RestoreSavedCooling))).Success);
    }
    [Fact]
    public async Task OutputReplacementBlocksEvenFallbackButStillPermitsVerifiedRelease()
    {
        await using var r = await Rig.Create(automatic: true); r.Hello = r.Runtime.OpenSession("user");
        await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.RestoreSavedCooling));
        r.Physical.Override = r.Physical.BindingCandidate! with { Fingerprint = new('c', 64) };
        await r.Runtime.TickCoolingAsync(); Assert.Equal(1, r.Hub.Writes); Assert.Equal(Outcome.Unknown, r.State.Outcome);
        Assert.True(r.State.Cooling!.Faulted);
        Assert.False((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetDuty, 80))).Success);
        Assert.True((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.ReleaseCooling))).Success);
        Assert.Equal(50, r.State.ReportedDuty);
    }
    [Fact]
    public async Task RestoreWireRequestCannotSupplyAnAlternativePolicy()
    {
        await using var r = await Rig.Create(); r.Hello = r.Runtime.OpenSession("user");
        var request = r.Request(DeviceOperation.RestoreSavedCooling);
        Assert.Equal(request.Operation, DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(request, Contract.Json)).Operation);
        Assert.Throws<IOException>(() => DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(request with { Duty = 1 }, Contract.Json)));
    }
}
