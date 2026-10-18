using FlowDeck.Core;

namespace FlowDeck.Tests;

public sealed class DeviceCoolingAcquisitionTests
{
    private static readonly CoolingSafety Safety = new(20, 100, null, CoolingExitAction.ReleaseToDevice, []);
    private class Driver : IDeviceCoolingDriver, IDeviceTelemetrySource
    {
        public string DeviceId => "acquisition-board";
        public string Connection = "same-board";
        public int Duty = 40, Writes, Attempts;
        public bool Drifting = true;
        public string? AcquisitionError;
        public DeviceObservation Observe() => new(DeviceId, "test", "Board", DeviceKind.Motherboard, IdentityConfidence.ConnectionBound,
            Connection, "test-board", ["board-bus"], [new("fan", "Fan", EndpointKind.Cooling,
                new[] { DeviceFeature.FixedDuty, DeviceFeature.ReleaseCooling, DeviceFeature.DutyRead, DeviceFeature.Rpm }
                    .Select(f => new DeviceCapability(f, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)).ToArray())]);
        public DeviceSample[] ReadCachedSamples()
        {
            // BIOS control advances independently of the service's cached reads.
            if (Drifting) Duty = Duty >= 80 ? 40 : Duty + 1;
            var now = TimeProvider.System.GetTimestamp();
            return [new("fan", DeviceFeature.DutyRead, Duty, now), new("fan", DeviceFeature.Rpm, 900, now)];
        }
        public CoolingEndpointConfiguration? CoolingConfiguration(string endpointId) => new(new(DeviceId, "fan"), true, Safety, []);
        public string? ValidateCoolingSetup(string endpointId) => null;
        public string? Validate(DeviceEndpointAction action) => action.Operation == DeviceOperation.ReleaseCooling ? null : "policy required";
        public Task<DeviceDriverResult> ApplyCoolingAsync(CoolingActuation actuation, CancellationToken ct)
        {
            Attempts++;
            if (AcquisitionError is { } conflict) return Task.FromResult(new DeviceDriverResult(Outcome.Rejected, conflict));
            if (actuation.Authorize().Error is { } error) return Task.FromResult(new DeviceDriverResult(Outcome.Rejected, error));
            Writes++; Drifting = false; Duty = actuation.Duty;
            return Task.FromResult(new DeviceDriverResult(Outcome.Confirmed, "confirmed", Duty));
        }
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) =>
            Task.FromResult(new DeviceDriverResult(Outcome.Confirmed, "released", Duty));
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) =>
            Task.FromResult(new[] { new DeviceDriverReleaseResult("fan", new(Outcome.Confirmed, "released", Duty)) });
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    // This test driver represents an acquisition guarded by mode/command validation.
    // Hardware-level acceptance and rejection are covered by the register driver tests.
    private sealed class GuardedDriver : Driver, IDeviceGuardedCoolingAcquisition { }
    private sealed class Rig : IAsyncDisposable
    {
        public readonly Driver Driver;
        public readonly DeviceControlRuntime Runtime;
        private readonly DeviceServiceHello hello;
        private long sequence;
        public Rig(bool guarded)
        {
            Driver = guarded ? new GuardedDriver() : new Driver();
            Runtime = new([Driver], ["user"], new TestLog());
            hello = Runtime.OpenSession("user");
        }
        public DeviceExecutionState State => Runtime.State().Endpoints.Single();
        public Task<DeviceServiceReply> Set(int duty)
        {
            var state = Runtime.State(); var endpoint = state.Endpoints.Single();
            return Runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", DeviceOperation.SetDuty,
                state.Inventory.TopologyRevision, [new(endpoint.Target, endpoint.Generation, endpoint.PolicyRevision)], duty));
        }
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }
    [Fact]
    public async Task FirstGuardedAcquisitionDoesNotTreatChangingBiosTelemetryAsConfirmedSoftwareOutput()
    {
        await using var rig = new Rig(guarded: true);
        Assert.False(rig.State.Owned);
        var result = await rig.Set(65);
        Assert.Equal(Outcome.Confirmed, result.Result!.Outcome);
        Assert.Equal(1, rig.Driver.Writes);
        Assert.Equal(65, rig.Driver.Duty);
        Assert.True(rig.State.Owned);
        Assert.False(rig.State.Cooling!.Faulted);
    }
    [Fact]
    public async Task DriversWithoutGuardedAcquisitionKeepTheExistingInitialOutputCheck()
    {
        await using var rig = new Rig(guarded: false);
        var result = await rig.Set(65);
        Assert.Equal(Outcome.Rejected, result.Result!.Outcome);
        Assert.Equal(0, rig.Driver.Attempts);
        Assert.Equal(0, rig.Driver.Writes);
        Assert.False(rig.State.Owned);
        Assert.Null(rig.State.Cooling);
    }
    [Fact]
    public async Task AReplacementOfAnOwnedPolicyStillRejectsOutputDriftBeforeCallingTheDriver()
    {
        await using var rig = new Rig(guarded: true);
        Assert.Equal(Outcome.Confirmed, (await rig.Set(65)).Result!.Outcome);
        rig.Driver.Drifting = true;
        Assert.Equal(Outcome.Rejected, (await rig.Set(70)).Result!.Outcome);
        Assert.Equal(1, rig.Driver.Attempts);
        Assert.Equal(1, rig.Driver.Writes);
        Assert.True(rig.State.Cooling!.Faulted);
    }
    [Fact]
    public async Task LaterExternalOutputChangesBlockEvenAnInitiallyGuardedPolicy()
    {
        await using var rig = new Rig(guarded: true);
        Assert.Equal(Outcome.Confirmed, (await rig.Set(65)).Result!.Outcome);
        rig.Driver.Duty = 90;
        await rig.Runtime.TickCoolingAsync();
        Assert.Equal(1, rig.Driver.Writes);
        Assert.Equal(90, rig.Driver.Duty);
        Assert.True(rig.State.Cooling!.Faulted);
        Assert.Equal(FanMode.Unknown, rig.State.Cooling.Mode);
        Assert.False(rig.State.Cooling.CanResume);
    }
    [Theory]
    [InlineData("external mode/command changed")]
    [InlineData("pending recovery record")]
    public async Task GuardedAcquisitionFailureStaysBlockedAndIsNeverAutomaticallyRetried(string failure)
    {
        await using var rig = new Rig(guarded: true);
        rig.Driver.AcquisitionError = failure;
        Assert.Equal(Outcome.Rejected, (await rig.Set(65)).Result!.Outcome);
        Assert.Equal(1, rig.Driver.Attempts);
        Assert.Equal(0, rig.Driver.Writes);
        rig.Driver.AcquisitionError = null;
        await rig.Runtime.TickCoolingAsync();
        Assert.Equal(Outcome.Rejected, (await rig.Set(70)).Result!.Outcome);
        Assert.Equal(1, rig.Driver.Attempts);
        Assert.Equal(0, rig.Driver.Writes);
        Assert.True(rig.State.Cooling!.Faulted);
    }
    [Fact]
    public async Task AChangedConnectionWithExistingOwnershipDoesNotReenterInitialAcquisition()
    {
        await using var rig = new Rig(guarded: true);
        Assert.Equal(Outcome.Confirmed, (await rig.Set(65)).Result!.Outcome);
        rig.Driver.Connection = "reconnected-board";
        rig.Driver.Drifting = true;
        Assert.Equal(Outcome.Rejected, (await rig.Set(70)).Result!.Outcome);
        Assert.Equal(1, rig.Driver.Attempts);
        Assert.Equal(1, rig.Driver.Writes);
    }
    [Fact]
    public void DefaultPolicyConstructionStillTreatsTheSuppliedDutyAsConfirmed()
    {
        var policy = new DeviceCoolingPolicy(new(FanMode.Manual, 65), Safety, 40);
        var decision = policy.Decide(new Dictionary<EndpointReference, double>(), 41, 900, true);
        Assert.True(policy.OutputBlocked);
        Assert.False(decision.Write);
    }
}
