using FlowDeck.Core;

namespace FlowDeck.Tests;

public sealed class DeviceLightingBatchTests
{
    private sealed class Driver(string id) : IDeviceLightingBatchDriver
    {
        public string DeviceId => id;
        public string[] Last = [];
        public int Batches;
        public bool Missing, Duplicate, UnknownSecond, Unavailable;
        public DeviceObservation Observe() => new(id, "batch-test", "RGB", DeviceKind.Controller, IdentityConfidence.SerialBound,
            "port-" + id, "test-v1", ["batch:" + id], new[] { "a", "b" }.Select(p => new DeviceEndpoint(p, p, EndpointKind.Lighting,
                [new(DeviceFeature.FixedColor, ImplementationSupport.Supported, VerificationEvidence.Automated,
                    Unavailable && p == "b" ? DeviceAvailability.NeedsSetup : DeviceAvailability.Ready),
                 new(DeviceFeature.RestoreLighting, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)])).ToArray());
        public string? Validate(DeviceEndpointAction action) => action.Operation == DeviceOperation.SetLighting ? "use the complete batch" : null;
        public string? ValidateLightingBatch(EndpointLightingProgram[] programs) => DeviceLightingBatch.Valid(programs) ? null : "invalid batch";
        public Task<DeviceDriverReleaseResult[]> ApplyLightingBatchAsync(EndpointLightingProgram[] programs, CancellationToken ct)
        {
            Batches++; Last = programs.Select(p => p.EndpointId).ToArray();
            var results = programs.Select(p => new DeviceDriverReleaseResult(p.EndpointId,
                new(UnknownSecond && p.EndpointId == "b" ? Outcome.Unknown : Outcome.WrittenUnverified, "batch result", ReportedLighting: p.Program))).ToArray();
            return Task.FromResult(Missing ? results.Take(1).ToArray() : Duplicate ? [results[0], results[0]] : results);
        }
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => throw new InvalidOperationException("not a per-endpoint write");
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) =>
            Task.FromResult(new[] { "a", "b" }.Select(p => new DeviceDriverReleaseResult(p, new(Outcome.Confirmed, "released"))).ToArray());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Rig(params Driver[] drivers) : IAsyncDisposable
    {
        public readonly DeviceResourceScheduler Scheduler = new();
        public DeviceControlRuntime Runtime = null!;
        private DeviceServiceHello hello = null!;
        private long sequence;
        public Rig Open()
        {
            Runtime = new(drivers, ["user"], new TestLog(), new TestClock(), Scheduler); hello = Runtime.OpenSession("user"); return this;
        }
        public DeviceServiceRequest Request(params EndpointReference[] targets)
        {
            var state = Runtime.State();
            return new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", DeviceOperation.SetLighting,
                state.Inventory.TopologyRevision, targets.Select(t => state.Endpoints.Single(e => e.Target == t))
                    .Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray(), Lighting: new("fixed", [new(25, 50, 100)]));
        }
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrDuplicateEndpointResultsCannotBePublishedAsSuccess(bool duplicate)
    {
        var driver = new Driver("rgb") { Missing = !duplicate, Duplicate = duplicate };
        await using var r = new Rig(driver).Open();
        var reply = await r.Runtime.ExecuteAsync(r.Request(new("rgb", "a"), new("rgb", "b")));
        Assert.Equal(Outcome.Unknown, reply.Result!.Outcome);
        Assert.All(reply.Result.Targets, t => Assert.Equal(Outcome.Unknown, t.Outcome));
        Assert.All(r.Runtime.State().Endpoints, e => Assert.True(e.Owned));
    }

    [Fact]
    public async Task AQueuedBatchCannotWriteAfterAnySelectedEndpointWasSuperseded()
    {
        var driver = new Driver("rgb"); await using var r = new Rig(driver).Open();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = r.Scheduler.RunAsync(["batch:rgb"], HardwarePriority.Manual, async _ => { entered.SetResult(); await unblock.Task; return true; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<DeviceServiceReply> old, latest;
        try
        {
            old = r.Runtime.ExecuteAsync(r.Request(new("rgb", "a"), new("rgb", "b")));
            latest = r.Runtime.ExecuteAsync(r.Request(new EndpointReference("rgb", "a")));
        }
        finally { unblock.TrySetResult(); }
        await hold;
        Assert.Equal(Outcome.Rejected, (await old.WaitAsync(TimeSpan.FromSeconds(5))).Result!.Outcome);
        Assert.Equal(Outcome.WrittenUnverified, (await latest.WaitAsync(TimeSpan.FromSeconds(5))).Result!.Outcome);
        Assert.Equal(1, driver.Batches); Assert.Equal(new[] { "a" }, driver.Last);
    }

    [Fact]
    public async Task AnUnavailableMemberRejectsTheWholeSelectionBeforeOtherDevicesWrite()
    {
        var a = new Driver("one"); var b = new Driver("two") { Unavailable = true };
        await using var r = new Rig(a, b).Open();
        var reply = await r.Runtime.ExecuteAsync(r.Request(new("one", "a"), new("two", "b")));
        Assert.False(reply.Success); Assert.Equal(0, a.Batches); Assert.Equal(0, b.Batches);
    }

    [Fact]
    public async Task BatchPartialFailurePreservesIndividualResultsAndIndependentDevices()
    {
        var a = new Driver("one") { UnknownSecond = true }; var b = new Driver("two");
        await using var r = new Rig(a, b).Open();
        var reply = await r.Runtime.ExecuteAsync(r.Request(new("one", "a"), new("one", "b"), new("two", "a")));
        Assert.Equal(Outcome.Partial, reply.Result!.Outcome); Assert.Equal(1, a.Batches); Assert.Equal(1, b.Batches);
        Assert.Equal(Outcome.Unknown, Assert.Single(reply.Result.Targets, t => t.Target == new EndpointReference("one", "b")).Outcome);
        Assert.All(reply.Result.Targets.Where(t => t.Target != new EndpointReference("one", "b")), t => Assert.Equal(Outcome.WrittenUnverified, t.Outcome));
    }
}
