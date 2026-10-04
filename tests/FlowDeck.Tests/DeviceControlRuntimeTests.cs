using System.Text;
using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Tests;

public sealed class GenericTestDriver(string id, string? domain = null) : IDeviceControlDriver
{
    public string DeviceId => id;
    public int Writes, Releases, Duty = 50;
    public bool Connected = true, Fail, Throw, RestoreFails, OmitRelease, HideEndpoint, ObserveThrows;
    public string Locator = "port1";
    public ImplementationSupport Support = ImplementationSupport.Supported;
    public DeviceAvailability Availability = DeviceAvailability.Ready;
    public TaskCompletionSource? Entered, Continue;
    public TaskCompletionSource? ReleaseEntered, ReleaseContinue;
    public int ReportedReleaseDuty = 65;
    public string? ReleaseFailureMessage;
    public DeviceObservation? Observe() => ObserveThrows ? throw new IOException("observer unavailable") : !Connected ? null : new(id, "test", "Same model", DeviceKind.Controller,
        IdentityConfidence.SerialBound, Locator, "test-v1", [domain ?? "usb:" + id],
        HideEndpoint ? [] : [new("fan1", "同じ名前", EndpointKind.Cooling,
            [new(DeviceFeature.FixedDuty, Support, VerificationEvidence.Automated, Availability),
             new(DeviceFeature.ReleaseCooling, ImplementationSupport.Supported, VerificationEvidence.Automated, Availability)])]);
    public string? Validate(DeviceEndpointAction action) => action.Operation == DeviceOperation.SetDuty && action.Duty < 20 ? "最低出力は20%です" : null;
    public async Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct)
    {
        Interlocked.Increment(ref Writes); Entered?.TrySetResult(); if (Continue is not null) await Continue.Task;
        if (Throw) throw new IOException("write outcome uncertain");
        if (Fail) return new(Outcome.Failed, "write failed");
        Duty = action.Operation == DeviceOperation.ReleaseCooling ? 65 : action.Duty!.Value;
        return new(Outcome.Confirmed, "readback", Duty);
    }
    public async Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct)
    {
        Interlocked.Increment(ref Releases); if (RestoreFails) throw new IOException("restore failed");
        ReleaseEntered?.TrySetResult(); if (ReleaseContinue is not null) await ReleaseContinue.Task;
        if (OmitRelease) return [];
        if (ReleaseFailureMessage is not null) return [new("fan1", new(Outcome.Unknown, ReleaseFailureMessage))];
        Duty = 65; return [new("fan1", new(Outcome.Confirmed, "restored", ReportedReleaseDuty))];
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
public sealed class GenericRig : IAsyncDisposable
{
    public TestClock Clock { get; } = new();
    public DeviceControlRuntime Runtime { get; }
    public DeviceServiceHello Hello { get; }
    public GenericTestDriver[] Drivers { get; }
    private long sequence;
    public GenericRig(params GenericTestDriver[] drivers)
    {
        Drivers = drivers; Runtime = new(drivers, [ServiceFixture.Sid], new TestLog(), Clock); Hello = Runtime.OpenSession(ServiceFixture.Sid);
    }
    public DeviceServiceRequest Request(int duty = 60, params string[] devices)
    {
        var state = Runtime.State();
        return new(2, Hello.Epoch, Hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "PC", DeviceOperation.SetDuty,
            state.Inventory.TopologyRevision, (devices.Length == 0 ? Drivers.Select(d => d.DeviceId).ToArray() : devices).Select(id =>
            {
                var endpoint = state.Endpoints.Single(s => s.Target == new EndpointReference(id, "fan1"));
                return new DeviceTargetVersion(endpoint.Target, endpoint.Generation, endpoint.PolicyRevision);
            }).ToArray(), Duty: duty);
    }
    public DeviceServiceRequest Read(DeviceOperation operation = DeviceOperation.State, string? result = null) =>
        new(2, Hello.Epoch, Hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "PC", operation, ResultId: result);
    public ValueTask DisposeAsync() => Runtime.DisposeAsync();
}

public class DeviceControlRuntimeTests
{
    [Fact] public async Task ReleaseOfAnOldConnectionEndsItsObligationWithoutReplacingNewConnectionReadings()
    {
        var driver = new GenericTestDriver("a") { ReleaseEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseContinue = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var rig = new GenericRig(driver); await rig.Runtime.ExecuteAsync(rig.Request());
        var release = rig.Runtime.ExecuteAsync(rig.Read(DeviceOperation.ReleaseSession));
        await driver.ReleaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        driver.Connected = false; rig.Runtime.Refresh(); driver.Connected = true; rig.Runtime.Refresh();
        driver.ReleaseContinue.TrySetResult(); Assert.True((await release).Success);
        var state = Assert.Single(rig.Runtime.State().Endpoints);
        Assert.False(state.Owned); Assert.Null(state.ReportedDuty); Assert.Equal(Outcome.Unknown, state.Outcome);
        Assert.Null(rig.Runtime.State().LifecycleError);
    }
    [Fact] public async Task InvalidRestorationReadbackCannotClearAnOwnedEndpoint()
    {
        var driver = new GenericTestDriver("a") { ReportedReleaseDuty = 200 }; await using var rig = new GenericRig(driver);
        await rig.Runtime.ExecuteAsync(rig.Request()); Assert.False((await rig.Runtime.ExecuteAsync(rig.Read(DeviceOperation.ReleaseSession))).Success);
        Assert.True(Assert.Single(rig.Runtime.State().Endpoints).Owned); Assert.NotNull(rig.Runtime.State().LifecycleError);
    }
    [Fact] public async Task RemovingAnOwnedEndpointCannotHideAnOmittedRestoration()
    {
        var driver = new GenericTestDriver("a"); await using var rig = new GenericRig(driver);
        await rig.Runtime.ExecuteAsync(rig.Request()); driver.HideEndpoint = true; driver.OmitRelease = true; rig.Runtime.Refresh();
        Assert.True(Assert.Single(rig.Runtime.State().Endpoints).Owned);
        var result = await rig.Runtime.ExecuteAsync(rig.Read(DeviceOperation.ReleaseSession));
        Assert.False(result.Success); Assert.NotNull(rig.Runtime.State().LifecycleError);
        driver.OmitRelease = false; var hello = rig.Runtime.OpenSession(ServiceFixture.Sid);
        Assert.True((await rig.Runtime.ExecuteAsync(rig.Read(DeviceOperation.ReleaseSession) with { Session = hello.Session })).Success);
        Assert.Empty(rig.Runtime.State().Endpoints);
    }
    [Fact] public async Task ReconnectedOwnedDeviceRequiresRestorationBeforeANewOutput()
    {
        var driver = new GenericTestDriver("a"); await using var rig = new GenericRig(driver);
        await rig.Runtime.ExecuteAsync(rig.Request()); driver.Connected = false; rig.Runtime.Refresh(); driver.Connected = true; rig.Runtime.Refresh();
        Assert.False((await rig.Runtime.ExecuteAsync(rig.Request(85))).Success); Assert.Equal(1, driver.Writes);
        var release = rig.Request() with { Operation = DeviceOperation.ReleaseCooling, Duty = null };
        Assert.Equal(Outcome.Confirmed, (await rig.Runtime.ExecuteAsync(release)).Result!.Outcome);
        Assert.Equal(Outcome.Confirmed, (await rig.Runtime.ExecuteAsync(rig.Request(75))).Result!.Outcome);
    }
    [Fact] public async Task FaultingObserverDoesNotStopOwnershipExpirationOrOtherDevices()
    {
        var a = new GenericTestDriver("a"); var b = new GenericTestDriver("b"); await using var rig = new GenericRig(a, b);
        await rig.Runtime.ExecuteAsync(rig.Request(70)); a.ObserveThrows = true;
        var state = rig.Runtime.State(); Assert.False(state.Inventory.Devices.Single(d => d.Device.Id == "a").Connected);
        Assert.True(state.Inventory.Devices.Single(d => d.Device.Id == "b").Connected);
        rig.Runtime.Start(); rig.Clock.Advance(13);
        await Task.Run(async () => { while (Volatile.Read(ref b.Releases) == 0) await Task.Delay(25); }).WaitAsync(TimeSpan.FromSeconds(5));
        await rig.Runtime.SweepAsync(); Assert.Equal(1, a.Releases); Assert.Equal(1, b.Releases);
    }
    [Fact]
    public async Task SameModelDevicesAreAddressedIndividuallyAndPollingDoesNotInvalidateTopology()
    {
        var a = new GenericTestDriver("a"); var b = new GenericTestDriver("b"); await using var r = new GenericRig(a, b);
        var request = r.Request(73, "b"); r.Runtime.Refresh(); r.Runtime.Refresh();
        var result = await r.Runtime.ExecuteAsync(request); Assert.Equal(Outcome.Confirmed, result.Result!.Outcome);
        Assert.Equal(0, a.Writes); Assert.Equal(1, b.Writes); Assert.Equal(73, b.Duty);
        Assert.Equal(new EndpointReference("b", "fan1"), Assert.Single(result.Result.Targets).Target);
    }
    [Fact]
    public async Task ChangedGenerationPolicyOrSessionIsRejectedWithoutWriting()
    {
        var driver = new GenericTestDriver("a"); await using var r = new GenericRig(driver);
        var old = r.Request(); driver.Locator = "port2"; r.Runtime.Refresh();
        Assert.False((await r.Runtime.ExecuteAsync(old)).Success);
        var wrongSession = r.Request() with { Session = Guid.NewGuid().ToString("N") };
        Assert.False((await r.Runtime.ExecuteAsync(wrongSession)).Success);
        var wrongRevision = r.Request(); wrongRevision.Targets![0] = wrongRevision.Targets[0] with { ExpectedPolicyRevision = 900 };
        Assert.False((await r.Runtime.ExecuteAsync(wrongRevision)).Success); Assert.Equal(0, driver.Writes);
    }
    [Fact]
    public async Task BatchPreflightRejectsAllWhenOneTargetIsUnconfiguredOrUnsupported()
    {
        var a = new GenericTestDriver("a"); var b = new GenericTestDriver("b") { Support = ImplementationSupport.Unsupported };
        await using var r = new GenericRig(a, b);
        Assert.False((await r.Runtime.ExecuteAsync(r.Request())).Success); Assert.Equal(0, a.Writes); Assert.Equal(0, b.Writes);
        b.Support = ImplementationSupport.Supported; r.Runtime.Refresh();
        Assert.False((await r.Runtime.ExecuteAsync(r.Request(10))).Success); Assert.Equal(0, a.Writes);
    }
    [Fact]
    public async Task RepeatedRequestDoesNotWriteTwiceAndChangedContentsCannotReuseAnId()
    {
        var driver = new GenericTestDriver("a"); await using var r = new GenericRig(driver); var request = r.Request(77);
        var a = await r.Runtime.ExecuteAsync(request); var b = await r.Runtime.ExecuteAsync(request);
        Assert.Equal(a.Result!.Time, b.Result!.Time); Assert.Equal(1, driver.Writes);
        Assert.False((await r.Runtime.ExecuteAsync(request with { Duty = 88 })).Success);
        b.Result.Targets[0] = b.Result.Targets[0] with { ReportedDuty = 1 };
        Assert.Equal(77, (await r.Runtime.ExecuteAsync(request)).Result!.Targets[0].ReportedDuty);
    }
    [Fact]
    public async Task InflightRetrySharesTheOperationAndReportsPendingTargets()
    {
        var driver = new GenericTestDriver("a") { Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Continue = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var r = new GenericRig(driver); var request = r.Request(); var first = r.Runtime.ExecuteAsync(request);
        await driver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retry = r.Runtime.ExecuteAsync(request); var pending = await r.Runtime.ExecuteAsync(r.Read(DeviceOperation.Result, request.Id));
        Assert.Equal(Outcome.Pending, pending.Result!.Outcome); Assert.Single(pending.Result.Targets); Assert.False(retry.IsCompleted);
        driver.Continue.SetResult(); await Task.WhenAll(first, retry); Assert.Equal(1, driver.Writes);
    }
    [Fact]
    public async Task PartialResultsPreserveSuccessfulEndpointsAndUncertainWritesAreNotConfirmed()
    {
        var a = new GenericTestDriver("a"); var b = new GenericTestDriver("b") { Fail = true }; var c = new GenericTestDriver("c") { Throw = true };
        await using var r = new GenericRig(a, b, c); var result = (await r.Runtime.ExecuteAsync(r.Request())).Result!;
        Assert.Equal(Outcome.Partial, result.Outcome);
        Assert.Equal(Outcome.Confirmed, result.Targets.Single(t => t.Target.DeviceId == "a").Outcome);
        Assert.Equal(Outcome.Failed, result.Targets.Single(t => t.Target.DeviceId == "b").Outcome);
        Assert.Equal(Outcome.Unknown, result.Targets.Single(t => t.Target.DeviceId == "c").Outcome);
    }
    [Fact]
    public async Task ASlowSharedResourceRechecksTargetAvailabilityBeforeTheNextWrite()
    {
        var a = new GenericTestDriver("a", "bus") { Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Continue = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var b = new GenericTestDriver("b", "bus"); await using var r = new GenericRig(a, b);
        var first = r.Runtime.ExecuteAsync(r.Request(65, "a")); await a.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiting = r.Runtime.ExecuteAsync(r.Request(80, "b")); b.Availability = DeviceAvailability.Conflict; r.Runtime.Refresh();
        a.Continue.SetResult(); await first;
        Assert.Equal(Outcome.Rejected, (await waiting).Result!.Outcome); Assert.Equal(0, b.Writes);
    }
    [Fact]
    public async Task ADelayedReplyDoesNotReplaceTheStateOfAReconnectedDevice()
    {
        var driver = new GenericTestDriver("a") { Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Continue = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var r = new GenericRig(driver); var task = r.Runtime.ExecuteAsync(r.Request());
        await driver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); driver.Connected = false; r.Runtime.Refresh(); driver.Connected = true; r.Runtime.Refresh();
        driver.Continue.SetResult(); Assert.Equal(Outcome.Unknown, (await task).Result!.Outcome);
        var state = Assert.Single(r.Runtime.State().Endpoints); Assert.Null(state.ReportedDuty); Assert.Equal(Outcome.Unknown, state.Outcome);
    }
    [Fact]
    public async Task LeaseExpirationRejectsQueuedWritesAndRestoresAfterTheActiveWriteSettles()
    {
        var a = new GenericTestDriver("a", "bus") { Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Continue = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var b = new GenericTestDriver("b", "bus"); await using var r = new GenericRig(a, b);
        var writing = r.Runtime.ExecuteAsync(r.Request(75, "a")); await a.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = r.Runtime.ExecuteAsync(r.Request(85, "b")); r.Clock.Advance(13); var release = r.Runtime.SweepAsync();
        Assert.False(release.IsCompleted); Assert.Equal(0, a.Releases);
        a.Continue.SetResult(); await Task.WhenAll(writing, queued, release);
        Assert.Equal(0, b.Writes); Assert.Equal(65, a.Duty); Assert.Equal(1, a.Releases); Assert.Equal(1, b.Releases);
        Assert.Equal(Outcome.Rejected, (await queued).Result!.Outcome);
        Assert.False((await r.Runtime.ExecuteAsync(r.Read(DeviceOperation.Renew))).Success);
    }
    [Fact]
    public async Task RenewalDoesNotWaitForHardwareAndDoesNotAllowOwnerTheft()
    {
        var driver = new GenericTestDriver("a") { Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Continue = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var r = new GenericRig(driver); var writing = r.Runtime.ExecuteAsync(r.Request());
        await driver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); r.Clock.Advance(10);
        Assert.True((await r.Runtime.ExecuteAsync(r.Read(DeviceOperation.Renew)).WaitAsync(TimeSpan.FromSeconds(1))).Success);
        Assert.Throws<IOException>(() => r.Runtime.OpenSession(ServiceFixture.Sid)); Assert.Throws<UnauthorizedAccessException>(() => r.Runtime.OpenSession("different-user"));
        r.Clock.Advance(5); await r.Runtime.SweepAsync(); Assert.Equal(0, driver.Releases);
        driver.Continue.SetResult(); await writing;
    }
    [Fact]
    public async Task FailedOrMissingRestorationCannotBeReportedAsSuccessfulRelease()
    {
        var a = new GenericTestDriver("a") { RestoreFails = true }; var b = new GenericTestDriver("b") { OmitRelease = true };
        await using var r = new GenericRig(a, b); await r.Runtime.ExecuteAsync(r.Request());
        var release = await r.Runtime.ExecuteAsync(r.Read(DeviceOperation.ReleaseSession));
        Assert.False(release.Success); Assert.NotNull(r.Runtime.State().LifecycleError); Assert.Equal(1, a.Releases); Assert.Equal(1, b.Releases);
        // A new authenticated owner can inspect the fault and retry release, but cannot start new output while recovery is unresolved.
        var recovery = r.Runtime.OpenSession(ServiceFixture.Sid);
        var blockedWrite = r.Request() with { Session = recovery.Session, Sequence = 1 };
        Assert.False((await r.Runtime.ExecuteAsync(blockedWrite)).Success);
        a.RestoreFails = false; b.OmitRelease = false;
        var retry = r.Read(DeviceOperation.ReleaseSession) with { Session = recovery.Session, Sequence = 2 };
        Assert.True((await r.Runtime.ExecuteAsync(retry)).Success); Assert.Null(r.Runtime.State().LifecycleError);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DetailedReleaseFailuresStayWithinTheReplyProtocolLimit(bool unicode)
    {
        var detail = unicode ? string.Concat(Enumerable.Repeat("\U0001F9CA", 5000)) : new string('x', 10000);
        await using var r = new GenericRig(new GenericTestDriver("a") { ReleaseFailureMessage = detail },
            new GenericTestDriver("b") { ReleaseFailureMessage = detail });
        await r.Runtime.ExecuteAsync(r.Request());
        var reply = await r.Runtime.ExecuteAsync(r.Read(DeviceOperation.ReleaseSession));
        Assert.False(reply.Success); Assert.InRange(reply.Error!.Length, 16383, 16384); Assert.EndsWith("…", reply.Error);
        Assert.Contains("fan1: " + detail, reply.Error);
        var decoded = DeviceServiceProtocol.ReadReply(JsonSerializer.SerializeToUtf8Bytes(reply, Contract.Json), r.Hello);
        Assert.Equal(reply.Error, decoded.Error);
        Assert.All(r.Runtime.State().Endpoints, e => Assert.True(e.Owned));
    }
    [Fact]
    public async Task RenewalIdsCannotBeRepurposedAndTheirReplayCannotKeepAnOwnerAlive()
    {
        var driver = new GenericTestDriver("a"); await using var r = new GenericRig(driver);
        var renewal = r.Read(DeviceOperation.Renew); Assert.True((await r.Runtime.ExecuteAsync(renewal)).Success);
        Assert.False((await r.Runtime.ExecuteAsync(renewal with { Sequence = renewal.Sequence + 1 })).Success);
        r.Clock.Advance(10); Assert.True((await r.Runtime.ExecuteAsync(renewal)).Success);
        r.Clock.Advance(3); await r.Runtime.SweepAsync(); Assert.Equal(1, driver.Releases);
        Assert.False((await r.Runtime.ExecuteAsync(renewal)).Success);
    }
    [Fact]
    public async Task EvictedResultsCannotBeReplayedUsingAnOldSequence()
    {
        var driver = new GenericTestDriver("a"); await using var r = new GenericRig(driver); var request = r.Request();
        await r.Runtime.ExecuteAsync(request);
        for (var i = 0; i < 300; i++) await r.Runtime.ExecuteAsync(r.Read());
        Assert.False((await r.Runtime.ExecuteAsync(request)).Success); Assert.Equal(1, driver.Writes);
    }
    [Theory]
    [InlineData("\"executePath\":\"arbitrary.exe\",")] [InlineData("\"Duty\":50,")]
    public async Task UnknownExecutableFieldsAndDuplicateJsonMembersAreRejected(string injected)
    {
        await using var r = new GenericRig(new GenericTestDriver("a"));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(r.Request(), Contract.Json); var json = Encoding.UTF8.GetString(bytes);
        Assert.Throws<IOException>(() => DeviceServiceProtocol.Read(Encoding.UTF8.GetBytes("{" + injected + json[1..])));
    }
    [Fact]
    public async Task VersionOneCannotBeSentThroughVersionTwoAndUnrelatedParametersAreRejected()
    {
        await using var r = new GenericRig(new GenericTestDriver("a"));
        Assert.Throws<IOException>(() => DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(r.Request() with { Version = 1 }, Contract.Json)));
        Assert.Throws<IOException>(() => DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(r.Read() with { Duty = 50 }, Contract.Json)));
        Assert.NotEqual(CoolingServiceProtocol.PipeName, DeviceServiceProtocol.PipeName);
    }
}
