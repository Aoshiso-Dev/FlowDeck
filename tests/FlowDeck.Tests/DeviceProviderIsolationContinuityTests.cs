using FlowDeck.Core;

namespace FlowDeck.Tests;

public sealed class DeviceProviderIsolationContinuityTests
{
    internal sealed class Sensor(TestClock clock) : IPhysicalDeviceDriver, IDevicePollingDriver, IDeviceTelemetrySource
    {
        public string DeviceId => "sensor";
        public double Temperature = 40;
        public int Polls, Releases;
        private long timestamp = clock.GetTimestamp();
        public PhysicalDeviceBinding BindingCandidate => new(DeviceId, "sensor-serial", "sensor-v1", new('a', 64));
        public DeviceObservation Observe() => new(DeviceId, "sensors", "CPU", DeviceKind.Processor, IdentityConfidence.SerialBound, "connection", "sensor-v1",
            ["bus:sensors"], [new("temperature", "Temperature", EndpointKind.Sensor, [Capability(DeviceFeature.Temperature)])]);
        public Task PollAsync(CancellationToken ct) { timestamp = clock.GetTimestamp(); Polls++; return Task.CompletedTask; }
        public DeviceSample[] ReadCachedSamples() => [new("temperature", DeviceFeature.Temperature, Temperature, timestamp)];
        public string? Validate(DeviceEndpointAction action) => "read-only";
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => throw new InvalidOperationException();
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct)
        { Releases++; return Task.FromResult(Array.Empty<DeviceDriverReleaseResult>()); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    internal sealed class Fan(TestClock clock) : IPhysicalDeviceDriver, IDeviceCoolingDriver, IDevicePollingDriver, IDeviceTelemetrySource
    {
        public string DeviceId => "fan";
        public int Duty = 30, Writes, Releases, Polls;
        private long timestamp = clock.GetTimestamp();
        public PhysicalDeviceBinding BindingCandidate => new(DeviceId, "fan-serial", "fan-v1", new('b', 64));
        public DeviceObservation Observe() => new(DeviceId, "fans", "Fan", DeviceKind.Controller, IdentityConfidence.SerialBound, "connection", "fan-v1",
            ["bus:fan"], [new("fan1", "Fan", EndpointKind.Cooling, new[] { DeviceFeature.FixedDuty, DeviceFeature.AutomaticDuty,
                DeviceFeature.ReleaseCooling, DeviceFeature.DutyRead, DeviceFeature.Rpm }.Select(Capability).ToArray())]);
        public CoolingEndpointConfiguration CoolingConfiguration(string endpointId) => new(new(DeviceId, "fan1"), true,
            new(20, 100, 50, CoolingExitAction.FixedDuty, []), []);
        public string? ValidateCoolingSetup(string endpointId) => null;
        public Task<DeviceDriverResult> ApplyCoolingAsync(CoolingActuation actuation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var permit = actuation.Authorize();
            if (permit.Error is not null) return Task.FromResult(new DeviceDriverResult(Outcome.Rejected, permit.Error, Failure: permit.Failure));
            Duty = actuation.Duty; Writes++; timestamp = clock.GetTimestamp();
            return Task.FromResult(new DeviceDriverResult(Outcome.Confirmed, "applied", Duty));
        }
        public Task PollAsync(CancellationToken ct) { timestamp = clock.GetTimestamp(); Polls++; return Task.CompletedTask; }
        public DeviceSample[] ReadCachedSamples() => [new("fan1", DeviceFeature.DutyRead, Duty, timestamp), new("fan1", DeviceFeature.Rpm, 1000, timestamp)];
        public string? Validate(DeviceEndpointAction action) => null;
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => throw new InvalidOperationException("Use cooling engine");
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct)
        { Releases++; Duty = 50; return Task.FromResult<DeviceDriverReleaseResult[]>([new("fan1", new(Outcome.Confirmed, "released", Duty))]); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static DeviceCapability Capability(DeviceFeature feature) => new(feature, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready);
    private sealed class Provider(string id, string[] domains, params IDeviceControlDriver[] drivers) : IDeviceDriverProvider
    {
        public string ProviderId => id;
        public string[] ResourceDomains => domains;
        public IDeviceControlDriver[] CachedDrivers => drivers;
        public TaskCompletionSource? Closing, ResumeClose;
        public bool FailPoll;
        public Task PollAsync(CancellationToken ct) => FailPoll ? Task.FromException(new IOException("SDK disconnected")) : Task.CompletedTask;
        public async ValueTask DisposeAsync() { Closing?.TrySetResult(); if (ResumeClose is not null) await ResumeClose.Task; }
    }
    private sealed class Rig : IAsyncDisposable
    {
        public readonly TestClock Clock = new();
        public readonly Sensor Sensor;
        public readonly Fan Fan;
        public readonly DeviceLightingRuntimeTests.Driver Light = new("rgb", "bus:rgb"), IndependentLight = new("other", "bus:other");
        public readonly Provider Direct, Sensors;
        public readonly DeviceControlRuntime Runtime;
        public readonly DeviceServiceHello Hello;
        private long sequence;
        public Rig(bool sensorOnChangedProvider = false)
        {
            Sensor = new(Clock); Fan = new(Clock);
            Direct = new("direct", sensorOnChangedProvider ? ["bus:rgb", "bus:sensors"] : ["bus:rgb"],
                sensorOnChangedProvider ? [Light, Sensor] : [Light]);
            Sensors = new("sensors", ["bus:sensors"], sensorOnChangedProvider ? [] : [Sensor]);
            Runtime = new([Fan, IndependentLight], ["user"], new TestLog(), Clock, providers: [Direct, Sensors],
                physicalBindings: [Fan.BindingCandidate, Sensor.BindingCandidate, Light.BindingCandidate, IndependentLight.BindingCandidate]);
            Hello = Runtime.OpenSession("user");
        }
        public DeviceProviderIsolation Scope => new(["direct", "sdk"], ["bus:rgb", "bus:sdk"]);
        public DeviceServiceRequest Request(DeviceOperation operation, EndpointReference[] targets, LightingProgram? light = null)
        {
            var state = Runtime.State();
            return new(2, Hello.Epoch, Hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", operation, state.Inventory.TopologyRevision,
                targets.Select(t => { var e = state.Endpoints.Single(s => s.Target == t); return new DeviceTargetVersion(t, e.Generation, e.PolicyRevision); }).ToArray(), Lighting: light);
        }
        public async Task StartCooling()
        {
            var request = Request(DeviceOperation.SetAutomatic, [new("fan", "fan1")]) with { Profile = new("curve", "CPU", 1,
                [new(new("sensor", "temperature"), [new(30, 30), new(80, 80)], 95, 85)], 0, 100) };
            var result = await Runtime.ExecuteAsync(request); Assert.True(result.Success, result.Error); Assert.Equal(40, Fan.Duty);
        }
        public Task Change(bool fail = false) => Runtime.ReconfigureAsync(Hello.Session, Runtime.State().Inventory.TopologyRevision, async (_, ct) =>
        {
            var drivers = await Runtime.ReplaceProvidersAsync(Hello.Session, ["direct"], () =>
                [new Provider("sdk", ["bus:sdk"]) { FailPoll = fail }], ct, ["sdk"]);
            return drivers.OfType<IPhysicalDeviceDriver>().Select(d => d.BindingCandidate!).ToArray();
        }, Scope);
        public async Task Tick(double seconds)
        {
            Clock.Advance(seconds);
            Assert.True((await Runtime.ExecuteAsync(Request(DeviceOperation.Renew, []) with { TopologyRevision = 0, Targets = null })).Success);
            await Runtime.PollAsync(); await Runtime.TickCoolingAsync(); await Runtime.TickLightingAsync();
        }
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CoolingTracksTemperatureAndProtectsAgainstOverheatWhileRgbCloseIsDelayed(bool failTarget)
    {
        await using var r = new Rig(); await r.StartCooling();
        r.Direct.Closing = new(); r.Direct.ResumeClose = new(); var change = r.Change(failTarget);
        await r.Direct.Closing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            r.Sensor.Temperature = 75; await r.Tick(1); Assert.Equal(75, r.Fan.Duty);
            r.Sensor.Temperature = 98; await r.Tick(1); Assert.Equal(100, r.Fan.Duty);
            Assert.Equal(0, r.Fan.Releases); Assert.Equal(0, r.Sensor.Releases); Assert.True(r.Sensor.Polls >= 2);
        }
        finally { r.Direct.ResumeClose.TrySetResult(); }
        if (failTarget) await Assert.ThrowsAsync<IOException>(() => change); else await change;
        var state = r.Runtime.State().Endpoints.Single(e => e.Target.DeviceId == "fan");
        Assert.True(state.Owned); Assert.True(state.Cooling!.Faulted); Assert.Equal(100, r.Fan.Duty);
        await r.Tick(1); Assert.Equal(100, r.Fan.Duty); Assert.Equal(0, r.Fan.Releases);
    }

    [Fact]
    public async Task ATemperatureDependencyOnTheChangingProviderCannotBeCalledIndependent()
    {
        await using var r = new Rig(sensorOnChangedProvider: true); await r.StartCooling();
        var scope = r.Scope with { ResourceDomains = ["bus:rgb", "bus:sdk", "bus:sensors"] };
        // Remove the empty discovery provider, which would itself also share the sensor bus.
        var error = await Assert.ThrowsAsync<IOException>(() => r.Runtime.ReconfigureAsync(r.Hello.Session, r.Runtime.State().Inventory.TopologyRevision,
            (_, _) => throw new InvalidOperationException(), scope with { ProviderIds = ["direct", "sdk", "sensors"] }));
        Assert.Contains("温度センサー", error.Message); Assert.Equal(0, r.Fan.Releases);
    }

    [Fact]
    public async Task AnIndependentLightingStreamPreservesItsOriginAndOwnershipAcrossReplacement()
    {
        await using var r = new Rig(); var program = new LightingProgram("color-cycle", [new(255, 0, 0)], 4);
        Assert.True((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetLighting, [new("other", "light1")], program))).Success);
        var before = r.Runtime.State().Endpoints.Single(e => e.Target.DeviceId == "other");
        r.Direct.Closing = new(); r.Direct.ResumeClose = new(); var change = r.Change();
        await r.Direct.Closing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await r.Tick(.5); Assert.Equal(DeviceLighting.Frame(program, .5), r.IndependentLight.Color); Assert.Equal(0, r.IndependentLight.Ends); }
        finally { r.Direct.ResumeClose.TrySetResult(); }
        await change; await r.Tick(.5);
        var after = r.Runtime.State().Endpoints.Single(e => e.Target.DeviceId == "other");
        Assert.Equal(before.PolicyRevision, after.PolicyRevision); Assert.Equal(before.Lighting!.GroupId, after.Lighting!.GroupId);
        Assert.Equal(DeviceLighting.Frame(program, 1), r.IndependentLight.Color); Assert.Equal(1, r.IndependentLight.Opens); Assert.Equal(0, r.IndependentLight.Ends);
    }

    [Fact]
    public async Task ASynchronizationGroupAcrossBothRoutesRequiresAnExplicitStopBeforeSwitching()
    {
        await using var r = new Rig();
        Assert.True((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetLighting, [new("other", "light1"), new("rgb", "light1")],
            new("breathing", [new(10, 20, 30)], 2)))).Success);
        var error = await Assert.ThrowsAsync<IOException>(() => r.Change());
        Assert.Contains("同じ照明同期", error.Message); Assert.Equal(0, r.Light.Ends); Assert.Equal(0, r.IndependentLight.Ends);
    }

    [Fact]
    public async Task OwnerDisconnectStillEndsEveryIndependentControlDuringAStalledSwitch()
    {
        await using var r = new Rig(); await r.StartCooling();
        r.Direct.Closing = new(); r.Direct.ResumeClose = new(); var change = r.Change();
        await r.Direct.Closing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disconnect = r.Runtime.DisconnectAsync(r.Hello.Session);
        r.Direct.ResumeClose.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => change);
        await disconnect.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(50, r.Fan.Duty); Assert.True(r.Fan.Releases > 0);
        Assert.DoesNotContain(r.Runtime.State().Endpoints, e => e.Owned);
    }
}
