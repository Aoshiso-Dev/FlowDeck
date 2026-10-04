using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Tests;

public sealed class DeviceShutdownRecoveryTests
{
    private sealed class Log : IEventLog
    {
        public bool FailReleaseLog;
        public readonly List<(string Category, string Data)> Events = [];
        public void Write(string category, object data)
        {
            if (FailReleaseLog && category == "deviceRelease") throw new IOException("audit file locked");
            lock (Events) Events.Add((category, JsonSerializer.Serialize(data, Contract.Json)));
        }
    }
    private sealed class Driver(string id) : IDeviceControlDriver
    {
        public string DeviceId => id;
        public string Locator = "original-connection";
        public bool Closed, CanClose, ThrowRelease;
        public int Writes, Releases, Closes;
        public DeviceObservation? Observe() => Closed ? null : new(id, "owner", id, DeviceKind.Controller, IdentityConfidence.ConnectionBound,
            Locator, "fixture-v1", ["fixture:" + id], [.. new[] { "fan-a", "fan-b" }.Select(endpoint => new DeviceEndpoint(endpoint, endpoint,
                EndpointKind.Cooling, [new(DeviceFeature.FixedDuty, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready),
                    new(DeviceFeature.ReleaseCooling, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)])),
                new("temperature", "temperature", EndpointKind.Sensor,
                    [new(DeviceFeature.Temperature, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)])]);
        public string? Validate(DeviceEndpointAction action) => Closed ? "closed" : null;
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct)
        { Writes++; return Task.FromResult(new DeviceDriverResult(Outcome.Confirmed, "written", action.Duty)); }
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct)
        {
            Releases++;
            if (ThrowRelease) throw new IOException("SDK disconnected before reply");
            return Task.FromResult(new[] { new DeviceDriverReleaseResult("fan-a", new(Outcome.Unknown, "SDK unavailable")),
                new DeviceDriverReleaseResult("fan-b", new(Outcome.Unknown, "SDK unavailable")) });
        }
        public ValueTask DisposeAsync()
        { Closes++; if (!CanClose) throw new IOException("requires native owner"); Closed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Provider(params Driver[] drivers) : IDeviceProviderShutdownCoordinator
    {
        public string ProviderId => "owner";
        public string[] ResourceDomains => ["fixture-owner"];
        public IDeviceControlDriver[] CachedDrivers => drivers;
        public DeviceShutdownRecovery[] Reports = [];
        public bool FailClose;
        public int Closes, Reads;
        public bool Closed;
        public Task PollAsync(CancellationToken ct) => Task.CompletedTask;
        public bool CanCompleteDriverShutdown(IDeviceControlDriver[] pending) => pending.All(d => drivers.Contains(d));
        public ValueTask DisposeAsync()
        {
            Closes++; if (FailClose) throw new IOException("native handle open");
            foreach (var driver in drivers) driver.CanClose = true;
            Closed = true; return ValueTask.CompletedTask;
        }
        public DeviceShutdownRecovery[] ReadShutdownRecoveries()
        { Assert.True(Closed); Assert.All(drivers, d => Assert.True(d.Closed)); Reads++; return Contract.Copy(Reports); }
    }
    private sealed class Rig : IAsyncDisposable
    {
        public readonly Driver Driver = new("rgb"), Sibling = new("other");
        public readonly Provider Provider;
        public readonly Log Log = new();
        public readonly DeviceControlRuntime Runtime;
        public Rig(bool sibling = false)
        {
            Provider = sibling ? new(Driver, Sibling) : new(Driver);
            Provider.Reports = [Proof(Driver)];
            Runtime = new([], ["user"], Log, new TestClock(), providers: [Provider]);
        }
        public static DeviceShutdownRecovery Proof(Driver driver) => new(Guid.NewGuid().ToString("N"), driver.DeviceId, driver.Locator, "fixture-v1",
            [new("fan-a", new(Outcome.Confirmed, "restored")), new("fan-b", new(Outcome.Confirmed, "restored"))]);
        public async Task<Rig> Own()
        {
            var hello = Runtime.OpenSession("user"); var state = Runtime.State();
            var targets = state.Endpoints.Where(e => e.Target.EndpointId.StartsWith("fan-", StringComparison.Ordinal))
                .Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray();
            var reply = await Runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "test", DeviceOperation.SetDuty,
                state.Inventory.TopologyRevision, targets, Duty: 60));
            Assert.Equal(Outcome.Confirmed, reply.Result!.Outcome); Assert.All(Fans(), s => Assert.True(s.Owned));
            return this;
        }
        public DeviceExecutionState[] Fans() => Runtime.State().Endpoints.Where(e => e.Target.EndpointId.StartsWith("fan-", StringComparison.Ordinal)).ToArray();
        public async ValueTask DisposeAsync()
        {
            Provider.FailClose = false; Log.FailReleaseLog = false;
            Provider.Reports = Provider.CachedDrivers.Cast<Driver>().Select(Proof).ToArray();
            await Runtime.DisposeAsync();
        }
    }
    [Fact] public async Task VerifiedNativeRecoveryEndsOwnershipWithoutInventingValuesAndPreservesTheFailureHistory()
    {
        await using var r = await new Rig().Own();
        await r.Runtime.DisposeAsync(); Assert.True(r.Runtime.ResourcesClosed); Assert.Null(r.Runtime.State().LifecycleError);
        var state = r.Runtime.State();
        Assert.All(r.Fans(), endpoint =>
        {
            Assert.False(endpoint.Owned); Assert.Null(endpoint.ReportedDuty); Assert.Null(endpoint.RequestedDuty);
            Assert.Equal(Outcome.Unknown, endpoint.Outcome); Assert.Contains("以前の接続", endpoint.Message);
        });
        Assert.All(state.Inventory.Devices, instance => Assert.False(instance.Connected));
        Assert.Contains(r.Log.Events, e => e.Category == "deviceRelease" && e.Data.Contains("SDK unavailable", StringComparison.Ordinal));
        Assert.Contains(r.Log.Events, e => e.Category == "deviceShutdownRecovery" && e.Data.Contains("restored", StringComparison.Ordinal));
        var revisions = r.Fans().Select(e => e.PolicyRevision).ToArray();
        await r.Runtime.DisposeAsync(); Assert.Equal(1, r.Provider.Reads); Assert.Equal(revisions, r.Fans().Select(e => e.PolicyRevision));
    }
    [Fact] public async Task APartialVerifiedResultLeavesTheOtherEndpointUnresolved()
    {
        await using var r = await new Rig().Own();
        r.Provider.Reports[0] = r.Provider.Reports[0] with { Results = [r.Provider.Reports[0].Results[0]] };
        await r.Runtime.DisposeAsync(); var state = r.Runtime.State();
        Assert.False(state.Endpoints.Single(e => e.Target.EndpointId == "fan-a").Owned);
        Assert.True(state.Endpoints.Single(e => e.Target.EndpointId == "fan-b").Owned); Assert.NotNull(state.LifecycleError);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task HistoricalEvidenceForAnotherConnectionOrDefinitionCannotClearCurrentOwnership(bool locator)
    {
        await using var r = await new Rig().Own(); var proof = r.Provider.Reports[0];
        r.Provider.Reports[0] = locator ? proof with { Locator = "earlier-connection" } : proof with { DefinitionId = "other-definition" };
        await r.Runtime.DisposeAsync(); Assert.True(r.Runtime.ResourcesClosed);
        Assert.All(r.Fans(), endpoint => Assert.True(endpoint.Owned)); Assert.NotNull(r.Runtime.State().LifecycleError);
    }
    [Fact] public async Task OneDevicesRecoveryCannotClearAnotherDevicesReleaseFailure()
    {
        await using var r = await new Rig(sibling: true).Own();
        await r.Runtime.DisposeAsync(); var state = r.Runtime.State();
        Assert.All(r.Fans().Where(e => e.Target.DeviceId == r.Driver.DeviceId), endpoint => Assert.False(endpoint.Owned));
        Assert.All(r.Fans().Where(e => e.Target.DeviceId == r.Sibling.DeviceId), endpoint => Assert.True(endpoint.Owned));
        Assert.Contains("other", state.LifecycleError); Assert.DoesNotContain("rgb:", state.LifecycleError);
    }
    [Theory]
    [InlineData("duplicate-device")] [InlineData("duplicate-endpoint")] [InlineData("unknown-device")]
    [InlineData("unknown-endpoint")] [InlineData("sensor")] [InlineData("live-value")] [InlineData("live-lighting")] [InlineData("not-confirmed")] [InlineData("bad-id")]
    public async Task InvalidFinalEvidenceIsRejectedAtomicallyAndMetadataCanBeRetriedWithoutRepeatingHardware(string change)
    {
        await using var r = await new Rig().Own(); var proof = r.Provider.Reports[0]; var row = proof.Results[0];
        r.Provider.Reports = change switch
        {
            "duplicate-device" => [proof, proof with { Id = Guid.NewGuid().ToString("N") }],
            "duplicate-endpoint" => [proof with { Results = [row, row] }],
            "unknown-device" => [proof, proof with { Id = Guid.NewGuid().ToString("N"), DeviceId = "unowned" }],
            "unknown-endpoint" => [proof with { Results = [row, new("unknown", new(Outcome.Confirmed, "restored"))] }],
            "sensor" => [proof with { Results = [row, new("temperature", new(Outcome.Confirmed, "restored"))] }],
            "live-value" => [proof with { Results = [row with { Result = row.Result with { ReportedDuty = 45 } }] }],
            "live-lighting" => [proof with { Results = [row with { Result = row.Result with { ReportedLighting = new("fixed", [new(0, 0, 255)]) } }] }],
            "not-confirmed" => [proof with { Results = [row with { Result = row.Result with { Outcome = Outcome.Unknown } }] }],
            _ => [proof with { Id = "not-a-receipt" }]
        };
        await Assert.ThrowsAsync<AggregateException>(() => r.Runtime.DisposeAsync().AsTask());
        Assert.False(r.Runtime.ResourcesClosed); Assert.True(r.Provider.Closed); Assert.All(r.Fans(), e => Assert.True(e.Owned));
        var closes = r.Driver.Closes; r.Provider.Reports = [proof];
        await r.Runtime.DisposeAsync(); Assert.True(r.Runtime.ResourcesClosed); Assert.Null(r.Runtime.State().LifecycleError);
        Assert.Equal(closes, r.Driver.Closes); Assert.Equal(1, r.Provider.Closes); Assert.All(r.Fans(), e => Assert.False(e.Owned));
    }
    [Fact] public async Task NativeOrJournalClosureFailureCannotPublishRecoveryBeforeRetrySucceeds()
    {
        await using var r = await new Rig().Own(); r.Provider.FailClose = true;
        await Assert.ThrowsAsync<AggregateException>(() => r.Runtime.DisposeAsync().AsTask());
        Assert.Equal(0, r.Provider.Reads); Assert.All(r.Fans(), e => Assert.True(e.Owned));
        r.Provider.FailClose = false; await r.Runtime.DisposeAsync(); Assert.Equal(1, r.Provider.Reads);
        Assert.All(r.Fans(), e => Assert.False(e.Owned)); Assert.Null(r.Runtime.State().LifecycleError);
    }
    [Fact] public async Task ConfirmedRecoveryDoesNotHideAnUnrelatedAuditFailure()
    {
        await using var r = await new Rig().Own(); r.Log.FailReleaseLog = true;
        await r.Runtime.DisposeAsync(); Assert.All(r.Fans(), e => Assert.False(e.Owned));
        Assert.Contains("audit file locked", r.Runtime.State().LifecycleError);
    }
    [Fact] public async Task AnSdkReleaseExceptionRequiresControlRecoveryButNeverARecoveryResultForTemperatureSensors()
    {
        await using var r = await new Rig().Own(); r.Driver.ThrowRelease = true;
        await r.Runtime.DisposeAsync(); Assert.Null(r.Runtime.State().LifecycleError);
        Assert.All(r.Fans(), endpoint => Assert.False(endpoint.Owned));
        var sensor = r.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "temperature");
        Assert.False(sensor.Owned); Assert.Equal(Outcome.Unknown, sensor.Outcome); Assert.DoesNotContain("restored", sensor.Message);
    }
}
