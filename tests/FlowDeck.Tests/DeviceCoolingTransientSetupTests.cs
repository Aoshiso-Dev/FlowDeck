using FlowDeck.Core;

namespace FlowDeck.Tests;

public sealed class DeviceCoolingTransientSetupTests
{
    private sealed class Driver : IIndividualCoolingConfiguration, IDeviceTelemetrySource, IDeviceGuardedCoolingAcquisition
    {
        public string DeviceId => "transient-setup-board";
        public PhysicalDeviceBinding BindingCandidate => new(DeviceId, "test", "test-board", new('a', 64));
        public Action? Observing;
        public int Attempts, Writes, Duty = 40;
        public bool Pending;
        public DeviceObservation Observe()
        {
            Observing?.Invoke();
            return new(DeviceId, "test", "Board", DeviceKind.Motherboard, IdentityConfidence.ConnectionBound, "same-board", "test-board",
                ["transient-setup-bus"], new[] { "fan", "other" }.Select(id => new DeviceEndpoint(id, id, EndpointKind.Cooling,
                    new[] { DeviceFeature.FixedDuty, DeviceFeature.ReleaseCooling, DeviceFeature.DutyRead, DeviceFeature.Rpm }
                        .Select(f => new DeviceCapability(f, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)).ToArray())).ToArray());
        }
        public DeviceSample[] ReadCachedSamples() => new[] { "fan", "other" }.SelectMany(id => new DeviceSample[]
        {
            new(id, DeviceFeature.DutyRead, Duty, TimeProvider.System.GetTimestamp()),
            new(id, DeviceFeature.Rpm, 900, TimeProvider.System.GetTimestamp())
        }).ToArray();
        public CoolingEndpointConfiguration? CoolingConfiguration(string id) => new(new(DeviceId, id), true,
            new(20, 100, null, CoolingExitAction.ReleaseToDevice, []), []);
        public string? ValidateCoolingSetup(string id) => null;
        public string? Validate(DeviceEndpointAction action) => null;
        public void Configure(PhysicalDeviceBinding? binding, IEnumerable<CoolingEndpointConfiguration> configuration) { }
        public void ConfigureCoolingEndpoint(PhysicalDeviceBinding binding, string id, CoolingEndpointConfiguration? configuration) { }
        public Task<DeviceDriverResult> ApplyCoolingAsync(CoolingActuation actuation, CancellationToken ct)
        {
            Attempts++; Pending = true;
            if (actuation.Authorize().Error is { } denied) return Task.FromResult(new DeviceDriverResult(Outcome.Rejected, denied));
            Writes++; Duty = actuation.Duty; return Task.FromResult(new DeviceDriverResult(Outcome.Confirmed, "confirmed", Duty));
        }
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) =>
            Task.FromResult(new DeviceDriverResult(Outcome.Confirmed, "released", Duty));
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct)
        {
            if (!Pending) return Task.FromResult(Array.Empty<DeviceDriverReleaseResult>());
            Pending = false; return Task.FromResult(new[] { new DeviceDriverReleaseResult("fan", new(Outcome.Confirmed, "released", Duty)) });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task OtherPortSetupCannotLeaveAnUnstartedPolicyToRunAfterTheTransientBlockEnds()
    {
        var rejectedBeforeEntry = 0; var invoked = 0;
        // Exercise each observation boundary, including the first policy step, without
        // a production hook or relying on the exact number of refreshes in the runtime.
        for (var boundary = 1; boundary <= 32; boundary++)
        {
            var driver = new Driver(); await using var runtime = new DeviceControlRuntime([driver], ["user"], new TestLog());
            var hello = runtime.OpenSession("user"); var before = runtime.State();
            var fan = before.Endpoints.Single(e => e.Target.EndpointId == "fan");
            var other = before.Endpoints.Single(e => e.Target.EndpointId == "other");
            Task? setup = null; var observations = 0;
            driver.Observing = () =>
            {
                if (++observations != boundary) return;
                driver.Observing = null;
                setup = runtime.ReconfigureAsync(hello.Session, before.Inventory.TopologyRevision,
                    (_, _) => Task.FromResult(new[] { driver.BindingCandidate }), endpointSetup: new(other.Target, []));
            };
            var reply = await runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1,
                "transient-setup", DeviceOperation.SetDuty, before.Inventory.TopologyRevision,
                [new(fan.Target, fan.Generation, fan.PolicyRevision)], 65)).WaitAsync(TimeSpan.FromSeconds(5));
            driver.Observing = null;
            if (setup is not null) await setup.WaitAsync(TimeSpan.FromSeconds(5));
            var after = runtime.State(); Assert.False(after.ConfigurationChanging);
            if (driver.Attempts == 0)
            {
                rejectedBeforeEntry++;
                Assert.Equal(Outcome.Rejected, reply.Result!.Outcome);
                var rejected = after.Endpoints.Single(e => e.Target == fan.Target);
                Assert.False(rejected.Owned); Assert.Null(rejected.Cooling);
                var revision = rejected.PolicyRevision;
                await runtime.TickCoolingAsync(); await runtime.TickCoolingAsync();
                Assert.Equal(0, driver.Attempts); Assert.Equal(0, driver.Writes);
                Assert.Equal(revision, runtime.State().Endpoints.Single(e => e.Target == fan.Target).PolicyRevision);
            }
            else invoked++;
            var released = await runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 2,
                "transient-setup", DeviceOperation.ReleaseSession));
            Assert.True(released.Success, released.Error); Assert.Null(runtime.State().LifecycleError);
        }
        Assert.True(rejectedBeforeEntry > 0); Assert.True(invoked > 0);
    }
}
