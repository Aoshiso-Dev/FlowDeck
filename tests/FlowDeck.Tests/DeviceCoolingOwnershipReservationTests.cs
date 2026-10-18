using FlowDeck.Core;

namespace FlowDeck.Tests;

public sealed class DeviceCoolingOwnershipReservationTests
{
    private sealed class Clock : TimeProvider
    {
        private long timestamp = 10000;
        public int Step;
        public long Now => Interlocked.Read(ref timestamp);
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Add(ref timestamp, Step);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(Now);
    }
    private class SparseReleaseDriver(Clock clock) : IDeviceCoolingDriver, IDeviceTelemetrySource
    {
        public string DeviceId => "sparse-release-board";
        public int Duty = 40, Attempts, Writes, ReleasedEndpoints, ReleaseCalls;
        public bool Drifting, ReleaseFails, Pending, FreezeSamples;
        public long SampleTimestamp;
        public string Behavior = "confirm";
        public TaskCompletionSource? Entered, Continue;
        public DeviceObservation Observe() => new(DeviceId, "test", "Board", DeviceKind.Motherboard, IdentityConfidence.ConnectionBound,
            "same-board", "test-board", ["sparse-board-bus"], [new("fan", "Fan", EndpointKind.Cooling,
                new[] { DeviceFeature.FixedDuty, DeviceFeature.ReleaseCooling, DeviceFeature.DutyRead, DeviceFeature.Rpm }
                    .Select(f => new DeviceCapability(f, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)).ToArray())]);
        public DeviceSample[] ReadCachedSamples()
        {
            if (Drifting) Duty = Duty >= 80 ? 40 : Duty + 1;
            var timestamp = FreezeSamples ? SampleTimestamp : clock.GetTimestamp();
            return [new("fan", DeviceFeature.DutyRead, Duty, timestamp), new("fan", DeviceFeature.Rpm, 900, timestamp)];
        }
        public CoolingEndpointConfiguration? CoolingConfiguration(string endpointId) =>
            new(new(DeviceId, "fan"), true, new(20, 100, null, CoolingExitAction.ReleaseToDevice, []), []);
        public string? ValidateCoolingSetup(string endpointId) => null;
        public string? Validate(DeviceEndpointAction action) => action.Operation == DeviceOperation.ReleaseCooling ? null : "policy required";
        public async Task<DeviceDriverResult> ApplyCoolingAsync(CoolingActuation actuation, CancellationToken ct)
        {
            // Real drivers report only attempted or durably pending endpoints during whole-session release.
            Pending = true; Attempts++; Entered?.TrySetResult();
            if (Continue is { } pause) await pause.Task;
            if (Behavior == "throw") throw new IOException("read or write result uncertain");
            if (Behavior == "invalid") return new(Outcome.Confirmed, "invalid report", 101);
            if (Behavior == "reject") return new(Outcome.Rejected, "guard refused acquisition");
            if (actuation.Authorize().Error is { } error) return new(Outcome.Rejected, error);
            Writes++; Drifting = false; Duty = actuation.Duty;
            return new(Outcome.Confirmed, "confirmed", Duty);
        }
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct)
        {
            ReleaseCalls++;
            if (!Pending) return Task.FromResult(Array.Empty<DeviceDriverReleaseResult>());
            if (ReleaseFails) return Task.FromResult(new[] { new DeviceDriverReleaseResult("fan", new(Outcome.Rejected, "external owner; recovery retained")) });
            Pending = false; ReleasedEndpoints++;
            return Task.FromResult(new[] { new DeviceDriverReleaseResult("fan", new(Outcome.Confirmed, "released", Duty)) });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class GuardedDriver(Clock clock) : SparseReleaseDriver(clock), IDeviceGuardedCoolingAcquisition { }
    private sealed class Rig : IAsyncDisposable
    {
        public readonly Clock Clock = new();
        public readonly SparseReleaseDriver Driver;
        public readonly DeviceControlRuntime Runtime;
        private DeviceServiceHello hello;
        private long sequence;
        public Rig(bool guarded = false)
        {
            Driver = guarded ? new GuardedDriver(Clock) : new SparseReleaseDriver(Clock);
            Runtime = new([Driver], ["user"], new TestLog(), Clock);
            hello = Runtime.OpenSession("user");
        }
        public DeviceExecutionState State => Runtime.State().Endpoints.Single();
        public DeviceServiceRequest Request(int duty)
        {
            var state = Runtime.State(); var endpoint = state.Endpoints.Single();
            return new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", DeviceOperation.SetDuty,
                state.Inventory.TopologyRevision, [new(endpoint.Target, endpoint.Generation, endpoint.PolicyRevision)], duty);
        }
        public Task<DeviceServiceReply> Set(int duty) => Runtime.ExecuteAsync(Request(duty));
        public Task<DeviceServiceReply> Release() => Runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session,
            Guid.NewGuid().ToString("N"), ++sequence, "test", DeviceOperation.ReleaseSession));
        public void Reopen() { hello = Runtime.OpenSession("user"); sequence = 0; }
        public ValueTask DisposeAsync() { Driver.ReleaseFails = false; Driver.Continue?.TrySetResult(); return Runtime.DisposeAsync(); }
    }

    [Fact]
    public async Task InitialCoreRejectionCreatesNeitherReleaseObligationNorBackgroundPolicy()
    {
        await using var r = new Rig(); r.Driver.Drifting = true;
        Assert.Equal(Outcome.Rejected, (await r.Set(65)).Result!.Outcome);
        Assert.Equal(Outcome.Rejected, r.State.Outcome);
        Assert.Equal("端子の外部変更を確認してください", r.State.Message);
        Assert.Equal(0, r.Driver.Attempts); Assert.Equal(0, r.Driver.Writes);
        Assert.False(r.State.Owned); Assert.Null(r.State.Cooling);
        r.Driver.Drifting = false;
        await r.Runtime.TickCoolingAsync();
        Assert.Equal(0, r.Driver.Attempts); Assert.Equal(0, r.Driver.Writes);
        Assert.True((await r.Release()).Success);
        Assert.Equal(0, r.Driver.ReleasedEndpoints); Assert.Null(r.Runtime.State().LifecycleError);
        r.Reopen();
        Assert.Equal(Outcome.Confirmed, (await r.Set(65)).Result!.Outcome);
        Assert.Equal(1, r.Driver.Attempts); Assert.True(r.State.Owned);
    }

    [Fact]
    public async Task NormalExitDoesNotDemandAResultForAnEndpointNeverPassedToTheDriver()
    {
        await using var r = new Rig(); r.Driver.Drifting = true;
        Assert.Equal(Outcome.Rejected, (await r.Set(65)).Result!.Outcome);
        Assert.Equal(0, r.Driver.Attempts);
        var release = await r.Release();
        Assert.True(release.Success, release.Error);
        Assert.Null(r.Runtime.State().LifecycleError);
        Assert.Equal(0, r.Driver.ReleasedEndpoints);
    }

    [Fact]
    public async Task TelemetryExpiryAcrossLocalValidationCannotInventOwnershipOrLaterRetry()
    {
        var localRejected = 0; var localUnknown = 0; var entered = 0;
        // Move a cached sample through the 3-second freshness boundary without tying
        // the regression to an exact number of internal reads or adding production hooks.
        for (var freshMilliseconds = 1; freshMilliseconds <= 80; freshMilliseconds++)
        {
            await using var r = new Rig(guarded: true);
            var request = r.Request(65);
            r.Driver.FreezeSamples = true;
            r.Driver.SampleTimestamp = r.Clock.Now - 3000 + freshMilliseconds;
            r.Clock.Step = 1;
            var reply = await r.Runtime.ExecuteAsync(request);
            r.Clock.Step = 0; r.Driver.FreezeSamples = false;
            if (r.Driver.Attempts == 0)
            {
                if (reply.Result!.Outcome == Outcome.Unknown) localUnknown++;
                else { Assert.Equal(Outcome.Rejected, reply.Result!.Outcome); localRejected++; }
                Assert.False(r.State.Owned); Assert.Null(r.State.Cooling);
                await r.Runtime.TickCoolingAsync();
                Assert.Equal(0, r.Driver.Attempts); Assert.Equal(0, r.Driver.Writes);
                Assert.True((await r.Release()).Success);
                Assert.Null(r.Runtime.State().LifecycleError);
            }
            else { entered++; Assert.True(r.State.Owned); }
        }
        Assert.True(localRejected > 0); Assert.True(localUnknown > 0); Assert.True(entered > 0);
    }

    [Theory]
    [InlineData("reject", Outcome.Rejected)]
    [InlineData("throw", Outcome.Unknown)]
    [InlineData("invalid", Outcome.Unknown)]
    public async Task EveryInvokedDriverRetainsItsReleaseObligationEvenWithoutConfirmedWrites(string behavior, Outcome outcome)
    {
        await using var r = new Rig(guarded: true); r.Driver.Behavior = behavior;
        Assert.Equal(outcome, (await r.Set(65)).Result!.Outcome);
        Assert.Equal(1, r.Driver.Attempts); Assert.Equal(0, r.Driver.Writes);
        Assert.True(r.State.Owned); Assert.True(r.Driver.Pending); Assert.NotNull(r.State.Cooling);
        Assert.True((await r.Release()).Success);
        Assert.Equal(1, r.Driver.ReleasedEndpoints); Assert.False(r.State.Owned); Assert.Null(r.State.Cooling);
    }

    [Fact]
    public async Task AnOwnedReplacementRejectedBeforeDriverEntryStillRequiresConfirmedRelease()
    {
        await using var r = new Rig(guarded: true);
        Assert.Equal(Outcome.Confirmed, (await r.Set(65)).Result!.Outcome);
        r.Driver.Drifting = true;
        Assert.Equal(Outcome.Rejected, (await r.Set(70)).Result!.Outcome);
        Assert.Equal(1, r.Driver.Attempts); Assert.True(r.State.Owned); Assert.NotNull(r.State.Cooling);
        r.Driver.Drifting = false; await r.Runtime.TickCoolingAsync();
        Assert.Equal(1, r.Driver.Attempts);
        r.Driver.ReleaseFails = true;
        Assert.False((await r.Release()).Success);
        Assert.True(r.State.Owned); Assert.True(r.Driver.Pending); Assert.NotNull(r.Runtime.State().LifecycleError);
        r.Driver.ReleaseFails = false; r.Reopen();
        Assert.True((await r.Release()).Success); Assert.False(r.State.Owned); Assert.False(r.Driver.Pending);
    }

    [Fact]
    public async Task PendingDriverEntryIsOwnedBeforeAwaitAndNormalReleaseWaitsForIt()
    {
        await using var r = new Rig(guarded: true);
        r.Driver.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        r.Driver.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = r.Set(65);
        await r.Driver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(r.State.Owned); Assert.True(r.Driver.Pending);
        var release = r.Release();
        Assert.Equal(0, r.Driver.ReleaseCalls); Assert.False(release.IsCompleted);
        r.Driver.Continue.SetResult();
        await command; Assert.True((await release).Success);
        Assert.Equal(1, r.Driver.ReleasedEndpoints); Assert.False(r.State.Owned);
    }
}
