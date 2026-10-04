using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceCoolingPolicyTests
{
    private static readonly EndpointReference Cpu = new("sensors", "cpu"), Gpu = new("sensors", "gpu"), Water = new("sensors", "water");
    private static CoolingSafety Safety => new(20, 100, 50, CoolingExitAction.FixedDuty, []);
    private static CoolingTemperatureProfile Profile => new("mixed", "3センサー", 1,
        [new(Cpu, [new(30, 30), new(90, 60)], 95, 85), new(Gpu, [new(20, 20), new(80, 100)], 90, 80),
         new(Water, [new(20, 30), new(40, 90)], 50, 45)], 2, 10);
    private static CoolingSafety StopSafety => Safety with { Stop = new([new(Cpu, 45), new(Gpu, 45)], 70, 2) };
    private sealed class Sensors(TestClock clock) : IDeviceControlDriver, IDeviceTelemetrySource
    {
        public string DeviceId => "sensors";
        public double? Cpu = 60, Gpu = 50, Water = 30;
        public DeviceMeasurementIssue CpuIssue;
        public string Locator = "sensor-connection";
        public long Timestamp = clock.GetTimestamp();
        public void Fresh() => Timestamp = clock.GetTimestamp();
        public DeviceObservation Observe() => new(DeviceId, "test", "温度", DeviceKind.Other, IdentityConfidence.ConnectionBound,
            Locator, "test", ["sensor-bus"], new[] { "cpu", "gpu", "water" }.Select(id => new DeviceEndpoint(id, id, EndpointKind.Sensor,
                [new(DeviceFeature.Temperature, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)])).ToArray());
        public DeviceSample[] ReadCachedSamples() => [new("cpu", DeviceFeature.Temperature, Cpu, Timestamp, Issue: CpuIssue),
            new("gpu", DeviceFeature.Temperature, Gpu, Timestamp), new("water", DeviceFeature.Temperature, Water, Timestamp)];
        public string? Validate(DeviceEndpointAction action) => "read only";
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => throw new InvalidOperationException("No sensor writes");
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => Task.FromResult(Array.Empty<DeviceDriverReleaseResult>());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Hub : IHubEndpointAdapter
    {
        public DemoHub Inner = new() { DelayMs = 0 };
        public Action? AfterRead, BeforeWrite;
        public Func<Task>? WriteWait;
        public HubEndpointExpectation? LastExpectation;
        public bool ExpireNext;
        public bool ZeroRpm;
        public bool IsDemo => true;
        public bool SupportsFanEndpoints => true;
        public bool SupportsFanDeadlines => true;
        public string Version => "policy test";
        private HubStatus Transform(HubStatus status) => !ZeroRpm ? status : status with
        { Channels = status.Channels.Select(c => c.Id == "fan1" ? c with { Rpm = 0 } : c).ToArray() };
        public Task<HubIdentity[]> ProbeAsync(CancellationToken ct = default) => Inner.ProbeAsync(ct);
        public async Task<HubStatus> OpenAsync(HubIdentity identity, long generation, CancellationToken ct = default) => Transform(await Inner.OpenAsync(identity, generation, ct));
        public async Task<HubStatus> ReadAsync(CancellationToken ct = default) { var status = Transform(await Inner.ReadAsync(ct)); AfterRead?.Invoke(); return status; }
        public async Task<HubResult> SetFanEndpointAsync(string channel, int duty, HubEndpointExpectation expected, CancellationToken ct = default)
        {
            if (WriteWait is { } wait) await wait(); BeforeWrite?.Invoke(); LastExpectation = expected;
            if (ExpireNext) { ExpireNext = false; expected = expected with { ValidUntilTick64 = HardwareWriteDeadline.Now - 1 }; }
            var result = await Inner.SetFanEndpointAsync(channel, duty, expected, ct);
            return result with { Status = result.Status is null ? null : Transform(result.Status) };
        }
        public Task<HubResult> SetFanGroupAsync(string[] channels, int duty, CancellationToken ct = default) => throw new InvalidOperationException("No whole-Hub writes");
        public Task<HubResult> SetLightingAsync(string[] channels, string preset, CancellationToken ct = default) => throw new InvalidOperationException("No lighting writes");
        public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Rig : IAsyncDisposable
    {
        public ServiceTestDirectory Directory = new();
        public TestClock Clock = new();
        public Hub Hub = new();
        public Sensors Sensors;
        public HubDeviceDriver Driver;
        public DeviceControlRuntime Runtime;
        public DeviceServiceHello Hello;
        private long sequence;
        private Rig(CoolingSafety? safety)
        {
            Sensors = new(Clock);
            Driver = new("0123456789abcdef0123456789abcdef", DemoHub.Identity.Id, Hub, Directory.Path, Clock);
            Runtime = new([Driver, Sensors], ["user"], new TestLog(), Clock);
            Hello = Runtime.OpenSession("user");
        }
        public static async Task<Rig> Create(CoolingSafety? safety = null)
        {
            var rig = new Rig(safety); await rig.Driver.InitializeAsync();
            rig.Driver.Configure(rig.Driver.BindingCandidate,
                [new(new(rig.Driver.DeviceId, "fan1"), true, safety ?? Safety, []),
                 new(new(rig.Driver.DeviceId, "fan2"), true, (safety ?? Safety) with { Minimum = 75, Fallback = 100, Exit = 80 }, [])]);
            return rig;
        }
        public DeviceServiceRequest Request(DeviceOperation operation, int? duty = null, CoolingTemperatureProfile? profile = null, string[]? channels = null)
        {
            var state = Runtime.State(); var targets = (channels ?? ["fan1"]).Select(id =>
            {
                var s = state.Endpoints.Single(e => e.Target == new EndpointReference(Driver.DeviceId, id));
                return new DeviceTargetVersion(s.Target, s.Generation, s.PolicyRevision);
            }).ToArray();
            return new(2, Hello.Epoch, Hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", operation, state.Inventory.TopologyRevision, targets, duty, profile);
        }
        public async Task<DeviceServiceReply> Set(DeviceOperation operation, int? duty = null, CoolingTemperatureProfile? profile = null, string[]? channels = null) =>
            await Runtime.ExecuteAsync(Request(operation, duty, profile, channels));
        public async Task Tick(double seconds = 0, bool freshSensors = true)
        {
            Clock.Advance(seconds); if (freshSensors) Sensors.Fresh(); await Driver.RefreshAsync();
            await Runtime.ExecuteAsync(new(2, Hello.Epoch, Hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", DeviceOperation.Renew));
            await Runtime.TickCoolingAsync();
        }
        public DeviceExecutionState State => Runtime.State().Endpoints.Single(e => e.Target == new EndpointReference(Driver.DeviceId, "fan1"));
        public int Duty => Driver.CachedStatus!.Channels.Single(c => c.Id == "fan1").Duty!.Value;
        public async ValueTask DisposeAsync() { await Runtime.DisposeAsync(); Directory.Dispose(); }
    }
    [Fact] public async Task AutomaticDemandUsesTheMaximumOfThreeCurvesAndKeepsOtherFansUnchanged()
    {
        await using var r = await Rig.Create(); r.Sensors.Cpu = 80; r.Sensors.Gpu = 60; r.Sensors.Water = 38;
        var result = await r.Set(DeviceOperation.SetAutomatic, profile: Profile);
        Assert.Equal(Outcome.Confirmed, result.Result!.Outcome); Assert.Equal(84, r.Duty);
        Assert.Equal(FanMode.Auto, r.State.Cooling!.Mode); Assert.Equal("mixed", r.State.Cooling.ProfileId);
        Assert.All(r.Driver.CachedStatus!.Channels.Where(c => c.Id != "fan1"), c => Assert.Equal(45, c.Duty));
    }
    [Fact] public async Task OneSharedCurveIsClampedToEachEndpointsOwnMinimum()
    {
        await using var r = await Rig.Create(); r.Sensors.Cpu = 20;
        var profile = new CoolingTemperatureProfile("group", "group", 1, [new(Cpu, [new(20, 25), new(90, 100)], 95, 85)], 0, 100);
        Assert.Equal(Outcome.Confirmed, (await r.Set(DeviceOperation.SetAutomatic, profile: profile, channels: ["fan1", "fan2"])).Result!.Outcome);
        await r.Tick(1); Assert.Equal(25, r.Duty); Assert.Equal(75, r.Driver.CachedStatus!.Channels.Single(c => c.Id == "fan2").Duty);
    }
    [Fact] public async Task ThreeSecondOldInputFallsBackAndRequiresExplicitHysteresisCheckedResume()
    {
        await using var r = await Rig.Create(); await r.Set(DeviceOperation.SetAutomatic, profile: Profile);
        await r.Tick(3, freshSensors: false); Assert.Equal(100, r.Duty); Assert.True(r.State.Cooling!.Faulted);
        var writes = r.Hub.Inner.Writes; r.Sensors.Cpu = 87; await r.Tick(1);
        Assert.False(r.State.Cooling!.CanResume); Assert.Equal(writes, r.Hub.Inner.Writes);
        Assert.False((await r.Set(DeviceOperation.ResumeCooling)).Success);
        Assert.False((await r.Set(DeviceOperation.SetDuty, 40)).Success);
        r.Sensors.Cpu = 60; await r.Tick(1); Assert.True(r.State.Cooling!.CanResume);
        Assert.Equal(Outcome.Confirmed, (await r.Set(DeviceOperation.ResumeCooling)).Result!.Outcome);
        Assert.False(r.State.Cooling!.Faulted); Assert.Equal(FanMode.Auto, r.State.Cooling.Mode);
    }
    [Theory] [InlineData(DeviceMeasurementIssue.OutOfRange)] [InlineData(DeviceMeasurementIssue.Unavailable)]
    public async Task AcquisitionIssueCannotReuseAPlausibleCachedTemperatureOrAutomaticallyResume(DeviceMeasurementIssue issue)
    {
        await using var r = await Rig.Create(); Assert.True((await r.Set(DeviceOperation.SetAutomatic, profile: Profile)).Success);
        r.Sensors.CpuIssue = issue; await r.Tick(1);
        Assert.Equal(60, r.Sensors.Cpu); Assert.Equal(100, r.Duty); Assert.True(r.State.Cooling!.Faulted);
        Assert.False(r.State.Cooling.CanResume); Assert.False((await r.Set(DeviceOperation.ResumeCooling)).Success);
        Assert.Null(r.Runtime.State().Telemetry!.Single(t => t.Target == Cpu).Value);
        r.Sensors.CpuIssue = DeviceMeasurementIssue.None; await r.Tick(1);
        Assert.Equal(100, r.Duty); Assert.True(r.State.Cooling!.Faulted); Assert.True(r.State.Cooling.CanResume);
        Assert.True((await r.Set(DeviceOperation.ResumeCooling)).Success); Assert.Equal(FanMode.Auto, r.State.Cooling!.Mode);
    }
    [Fact] public async Task AReplacementTemperatureSourceIsNeverSilentlyReboundDuringResume()
    {
        await using var r = await Rig.Create(); await r.Set(DeviceOperation.SetAutomatic, profile: Profile);
        r.Sensors.Locator = "replacement-source"; r.Runtime.Refresh(); await r.Tick(1);
        Assert.True(r.State.Cooling!.Faulted); Assert.Equal(100, r.Duty);
        Assert.False((await r.Set(DeviceOperation.ResumeCooling)).Success);
        Assert.Equal(Outcome.Confirmed, (await r.Set(DeviceOperation.ReleaseCooling)).Result!.Outcome);
        Assert.Equal(Outcome.Confirmed, (await r.Set(DeviceOperation.SetAutomatic, profile: Profile)).Result!.Outcome);
    }
    [Fact] public async Task DescentHoldsThenUsesElapsedTimeAndRetainsFractionsWhileRisesAreImmediate()
    {
        await using var r = await Rig.Create(); await r.Set(DeviceOperation.SetDuty, 100); r.Sensors.Cpu = 20;
        var profile = new CoolingTemperatureProfile("slope", "slope", 1, [new(Cpu, [new(20, 30), new(90, 100)], 95, 85)], 2, 10);
        await r.Set(DeviceOperation.SetAutomatic, profile: profile); await r.Tick(1); Assert.Equal(100, r.Duty);
        await r.Tick(2); Assert.Equal(90, r.Duty); await r.Tick(.05); Assert.Equal(90, r.Duty); await r.Tick(.05); Assert.Equal(89, r.Duty);
        r.Sensors.Cpu = 90; await r.Tick(.05); Assert.Equal(100, r.Duty);
    }
    [Fact] public async Task StopChecksAllSelectedTemperaturesAndRestartHoldsBoostUntilTheConfiguredDuration()
    {
        await using var r = await Rig.Create(StopSafety); r.Sensors.Cpu = 50; r.Sensors.Gpu = 20;
        Assert.False((await r.Set(DeviceOperation.SetDuty, 0)).Success); Assert.Equal(0, r.Hub.Inner.Writes);
        r.Sensors.Cpu = 20; Assert.Equal(Outcome.Confirmed, (await r.Set(DeviceOperation.SetDuty, 0)).Result!.Outcome); Assert.Equal(0, r.Duty);
        await r.Set(DeviceOperation.SetDuty, 30); Assert.Equal(70, r.Duty); Assert.True(r.State.Cooling!.Restarting);
        await r.Tick(1); Assert.Equal(70, r.Duty); await r.Tick(1); Assert.Equal(30, r.Duty); Assert.False(r.State.Cooling!.Restarting);
    }
    [Fact] public async Task RestartTimerBeginsAtTheConfirmedBoostRatherThanBeforeTheIoWait()
    {
        await using var r = await Rig.Create(StopSafety); r.Sensors.Cpu = r.Sensors.Gpu = 20;
        await r.Set(DeviceOperation.SetDuty, 0);
        r.Hub.BeforeWrite = () => { r.Clock.Advance(2); r.Hub.BeforeWrite = null; };
        await r.Set(DeviceOperation.SetDuty, 30); await r.Tick(1); Assert.Equal(70, r.Duty);
        await r.Tick(1); Assert.Equal(30, r.Duty);
    }
    [Fact] public async Task AutomaticOutputDoesNotJumpBackUpAfterTheRestartBoostEnds()
    {
        await using var r = await Rig.Create(StopSafety); r.Sensors.Cpu = r.Sensors.Gpu = 20;
        await r.Set(DeviceOperation.SetDuty, 0);
        var profile = new CoolingTemperatureProfile("low", "low", 1, [new(Cpu, [new(20, 20), new(90, 20)], 95, 85)], 0, 10);
        await r.Set(DeviceOperation.SetAutomatic, profile: profile); Assert.Equal(70, r.Duty);
        await r.Tick(2); Assert.Equal(20, r.Duty); await r.Tick(1); Assert.Equal(20, r.Duty);
    }
    [Fact] public async Task ThermalFallbackNeverLowersAnAlreadyHigherOutput()
    {
        await using var r = await Rig.Create(Safety with { Fallback = 50, ManualProtection = [new(Cpu, 80, 70)] });
        await r.Set(DeviceOperation.SetDuty, 90); r.Sensors.Cpu = 85; await r.Tick(1);
        Assert.Equal(90, r.Duty); Assert.True(r.State.Cooling!.Faulted); Assert.Equal(FanMode.Fallback, r.State.Cooling.Mode);
    }
    [Fact] public async Task OneInvalidThermalPolicyRejectsTheWholeGroupBeforeAnyWrite()
    {
        await using var r = await Rig.Create();
        r.Driver.Configure(r.Driver.BindingCandidate,
            [new(new(r.Driver.DeviceId, "fan1"), true, Safety, []),
             new(new(r.Driver.DeviceId, "fan2"), true, Safety with { ManualProtection = [new(Cpu, 80, 70)] }, [])]);
        r.Sensors.Cpu = 85;
        Assert.False((await r.Set(DeviceOperation.SetDuty, 60, channels: ["fan1", "fan2"])).Success); Assert.Equal(0, r.Hub.Inner.Writes);
    }
    [Fact] public async Task ANewManualPolicyWinsAfterAnAlreadyStartedAutomaticWriteCompletes()
    {
        await using var r = await Rig.Create(); await r.Set(DeviceOperation.SetAutomatic, profile: Profile);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        r.Hub.WriteWait = async () => { r.Hub.WriteWait = null; entered.TrySetResult(); await finish.Task; };
        r.Sensors.Water = 40; var old = r.Tick();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var next = r.Set(DeviceOperation.SetDuty, 40);
            Assert.False(next.IsCompleted); finish.TrySetResult(); await Task.WhenAll(old, next).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(40, r.Duty); Assert.Equal(FanMode.Manual, r.State.Cooling!.Mode);
            var writes = r.Hub.Inner.Writes; await r.Tick(1); Assert.Equal(writes, r.Hub.Inner.Writes);
        }
        finally { finish.TrySetResult(); }
    }
    [Fact] public async Task FailedRestartLatchesFallbackAndCannotResumeWithoutRotation()
    {
        await using var r = await Rig.Create(StopSafety); r.Sensors.Cpu = r.Sensors.Gpu = 20;
        await r.Set(DeviceOperation.SetDuty, 0); r.Hub.ZeroRpm = true;
        await r.Set(DeviceOperation.SetDuty, 30); await r.Tick(2);
        Assert.True(r.State.Cooling!.Faulted); Assert.Equal(100, r.Duty); Assert.False(r.State.Cooling.CanResume);
        var writes = r.Hub.Inner.Writes; await r.Tick(1); Assert.Equal(writes, r.Hub.Inner.Writes);
    }
    [Fact] public async Task ManualProtectionCannotBeBypassedThroughTheRawDriverAndFallsBackWhenHot()
    {
        await using var r = await Rig.Create(Safety with { ManualProtection = [new(Cpu, 80, 70)] }); r.Sensors.Cpu = 50;
        Assert.Equal(Outcome.Rejected, (await r.Driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 40), default)).Outcome);
        Assert.Equal(Outcome.Confirmed, (await r.Set(DeviceOperation.SetDuty, 40)).Result!.Outcome);
        r.Sensors.Cpu = 85; await r.Tick(1); Assert.Equal(100, r.Duty); Assert.True(r.State.Cooling!.Faulted);
        r.Sensors.Cpu = 75; await r.Tick(1); Assert.False((await r.Set(DeviceOperation.ResumeCooling)).Success);
        r.Sensors.Cpu = 65; await r.Tick(1); Assert.Equal(Outcome.Confirmed, (await r.Set(DeviceOperation.ResumeCooling)).Result!.Outcome); Assert.Equal(40, r.Duty);
    }
    [Fact] public async Task ALastMomentTemperatureFailurePreventsTheRequestedWriteThenTriggersFallback()
    {
        await using var r = await Rig.Create(Safety with { ManualProtection = [new(Cpu, 80, 70)] }); r.Sensors.Cpu = 50;
        r.Hub.AfterRead = () => { r.Sensors.Cpu = 85; r.Hub.AfterRead = null; };
        Assert.Equal(Outcome.Rejected, (await r.Set(DeviceOperation.SetDuty, 40)).Result!.Outcome); Assert.Equal(0, r.Hub.Inner.Writes);
        await r.Tick(1); Assert.Equal(100, r.Duty); Assert.Equal(1, r.Hub.Inner.Writes);
    }
    [Fact] public async Task UnchangedAutomaticDemandDoesNotContinuouslyRewriteTheFanOrJournal()
    {
        await using var r = await Rig.Create(); r.Sensors.Water = 38; await r.Set(DeviceOperation.SetAutomatic, profile: Profile);
        var writes = r.Hub.Inner.Writes; for (var i = 0; i < 5; i++) await r.Tick(1);
        Assert.Equal(writes, r.Hub.Inner.Writes);
    }
    [Fact] public async Task ANewSampleCannotExtendTheValidityOfAnAlreadyCalculatedOutput()
    {
        await using var r = await Rig.Create();
        r.Hub.AfterRead = () => { r.Clock.Advance(3); r.Sensors.Fresh(); r.Hub.AfterRead = null; };
        Assert.Equal(Outcome.Rejected, (await r.Set(DeviceOperation.SetAutomatic, profile: Profile)).Result!.Outcome);
        Assert.Equal(0, r.Hub.Inner.Writes); Assert.True(r.State.Cooling!.Faulted);
        await r.Tick(); Assert.Equal(100, r.Duty); Assert.Equal(1, r.Hub.Inner.Writes);
    }
    [Fact] public async Task TheOldestRequiredSampleBoundsTheWorkersDeadline()
    {
        await using var r = await Rig.Create(); r.Clock.Advance(2); await r.Driver.RefreshAsync();
        var start = HardwareWriteDeadline.Now;
        Assert.Equal(Outcome.Confirmed, (await r.Set(DeviceOperation.SetAutomatic, profile: Profile)).Result!.Outcome);
        var deadline = r.Hub.LastExpectation!.ValidUntilTick64;
        Assert.NotNull(deadline); Assert.InRange(deadline.Value, start, HardwareWriteDeadline.Now + 1000 - HardwareWriteDeadline.ClockMarginMilliseconds);
    }
    [Fact] public async Task AWorkerExpiredStopDoesNotStopTheFanAndFallsBackWithoutRetryingTheStop()
    {
        await using var r = await Rig.Create(StopSafety); r.Sensors.Cpu = r.Sensors.Gpu = 20; r.Hub.ExpireNext = true;
        Assert.Equal(Outcome.Rejected, (await r.Set(DeviceOperation.SetDuty, 0)).Result!.Outcome);
        Assert.NotNull(r.Hub.LastExpectation!.ValidUntilTick64); Assert.Equal(45, r.Duty); Assert.Equal(0, r.Hub.Inner.Writes);
        await r.Tick(); Assert.Equal(100, r.Duty); Assert.Equal(1, r.Hub.Inner.Writes); Assert.Null(r.Hub.LastExpectation!.ValidUntilTick64);
        Assert.True(r.State.Cooling!.Faulted); await r.Tick(1); Assert.Equal(1, r.Hub.Inner.Writes);
        Assert.Equal(Outcome.Confirmed, (await r.Set(DeviceOperation.ResumeCooling)).Result!.Outcome); Assert.Equal(0, r.Duty);
    }
    [Fact] public async Task ExternalOutputChangesStopAutomaticWritesInsteadOfOverwritingAnotherController()
    {
        await using var r = await Rig.Create(); await r.Set(DeviceOperation.SetAutomatic, profile: Profile);
        await r.Hub.Inner.SetFanGroupAsync(["fan1"], 99); var writes = r.Hub.Inner.Writes;
        await r.Tick(1); Assert.Equal(writes, r.Hub.Inner.Writes); Assert.Equal(99, r.Duty);
        Assert.Equal(FanMode.Unknown, r.State.Cooling!.Mode); Assert.False(r.State.Cooling.CanResume);
        Assert.Equal(Outcome.Unknown, r.State.Outcome); Assert.Equal(99, r.State.ReportedDuty);
    }
}
