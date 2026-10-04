using System.Collections.Concurrent;
using FlowDeck.Core;

namespace FlowDeck.Tests;

public sealed partial class DeviceProviderReplacementTests
{
    private sealed class Driver(string id, string provider, ConcurrentQueue<string> events) : IPhysicalDeviceDriver, IDevicePollingDriver
    {
        public string DeviceId => id;
        public string Domain { get; set; } = "device:" + id;
        public PhysicalDeviceBinding BindingCandidate => new(id, "serial:" + id, "test-v1", new('a', 64));
        public int Polls, Writes, Releases, Disposes, Observes;
        public bool Owned, FailRelease, OmitRelease, FailDispose, Disposed;
        public Func<Task>? Closing;
        public Func<DeviceReleaseReason, bool>? RejectRelease;
        public DeviceObservation? Observe()
        {
            Observes++;
            return Disposed ? null : new(id, provider, id, DeviceKind.Controller, IdentityConfidence.SerialBound,
            "stable-connection", "test-v1", [Domain], [new("fan", "Fan", EndpointKind.Cooling,
                [new(DeviceFeature.FixedDuty, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready),
                 new(DeviceFeature.ReleaseCooling, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)])]);
        }
        public string? Validate(DeviceEndpointAction action) => Disposed ? "closed" : null;
        public Task PollAsync(CancellationToken ct)
        { Assert.False(Disposed); Polls++; events.Enqueue(id + ":read"); return Task.CompletedTask; }
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct)
        { Assert.False(Disposed); Writes++; Owned = true; events.Enqueue(id + ":write"); return Task.FromResult(new DeviceDriverResult(Outcome.Confirmed, "written", action.Duty)); }
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct)
        {
            Assert.False(Disposed); Releases++; events.Enqueue(id + ":release");
            if (OmitRelease) return Task.FromResult(Array.Empty<DeviceDriverReleaseResult>());
            var failed = FailRelease || RejectRelease?.Invoke(reason) == true;
            if (!failed) Owned = false;
            return Task.FromResult(new[] { new DeviceDriverReleaseResult("fan", new(failed ? Outcome.Unknown : Outcome.Confirmed, "release", 50)) });
        }
        public async ValueTask DisposeAsync()
        {
            Disposes++; events.Enqueue(id + ":closing"); if (Closing is not null) await Closing();
            if (FailDispose) throw new IOException("handle still open");
            Disposed = true; events.Enqueue(id + ":closed");
        }
    }
    private sealed class Provider(string id, string device, ConcurrentQueue<string> events) : IDeviceDriverProvider
    {
        public string ProviderId => id;
        public readonly Driver Driver = new(device, id, events);
        public string[] ResourceDomains => [Driver.Domain];
        public IDeviceControlDriver[] CachedDrivers { get { Enumerations++; if (FailEnumeration) throw new IOException("cached enumeration failed"); return HideDriver ? [] : [Driver]; } }
        public int Polls, Disposes, Enumerations;
        public bool FailPoll, FailDispose, Disposed, HideDriver, FailEnumeration;
        public Func<Task>? Reading;
        public async Task PollAsync(CancellationToken ct)
        {
            Assert.False(Disposed); Polls++; events.Enqueue(id + ":discover"); if (Reading is not null) await Reading();
            if (FailPoll) throw new IOException("SDK unavailable");
        }
        public ValueTask DisposeAsync()
        {
            Disposes++; events.Enqueue(id + ":close"); if (FailDispose) throw new IOException("provider handle still open");
            Disposed = true; return ValueTask.CompletedTask;
        }
    }
    private sealed class Rig : IAsyncDisposable
    {
        public readonly ConcurrentQueue<string> Events = new();
        public readonly Provider Direct, Independent;
        public readonly DeviceControlRuntime Runtime;
        public readonly DeviceServiceHello Hello;
        private long sequence;
        public Rig()
        {
            Direct = new("direct", "rgb", Events); Independent = new("independent", "cooler", Events);
            Runtime = new([], ["user"], new TestLog(), new TestClock(), providers: [Direct, Independent],
                physicalBindings: [Direct.Driver.BindingCandidate, Independent.Driver.BindingCandidate]);
            Hello = Runtime.OpenSession("user");
        }
        public Provider New(string provider = "sdk", string device = "external") => new(provider, device, Events);
        public Task Change(string[] remove, Func<IDeviceDriverProvider[]> create) => Runtime.ReconfigureAsync(Hello.Session,
            Runtime.State().Inventory.TopologyRevision, async (_, ct) =>
                (await Runtime.ReplaceProvidersAsync(Hello.Session, remove, create, ct)).OfType<IPhysicalDeviceDriver>().Select(d => d.BindingCandidate!).ToArray());
        public DeviceServiceRequest Request(string id)
        {
            var state = Runtime.State(); var endpoint = state.Endpoints.Single(e => e.Target == new EndpointReference(id, "fan"));
            return new(2, Hello.Epoch, Hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", DeviceOperation.SetDuty,
                state.Inventory.TopologyRevision, [new(endpoint.Target, endpoint.Generation, endpoint.PolicyRevision)], Duty: 60);
        }
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    [Fact]
    public async Task ReplacementIsAllowedOnlyInsideAReleasedConfigurationScope()
    {
        await using var r = new Rig(); var factories = 0;
        await Assert.ThrowsAsync<IOException>(() => r.Runtime.ReplaceProvidersAsync(r.Hello.Session, ["direct"], () => { factories++; return []; }, default));
        await r.Runtime.ReconfigureAsync(r.Hello.Session, r.Runtime.State().Inventory.TopologyRevision, async (_, ct) =>
        {
            await Assert.ThrowsAsync<IOException>(() => r.Runtime.ReplaceProvidersAsync("wrong-owner", ["direct"], () => { factories++; return []; }, ct));
            return new[] { r.Direct.Driver.BindingCandidate, r.Independent.Driver.BindingCandidate };
        });
        Assert.Equal(0, factories); Assert.Equal(0, r.Direct.Disposes);
    }

    [Fact]
    public async Task OldControlAndHandlesEndBeforeNewDiscoveryAndOnlyTheNewPathPolls()
    {
        await using var r = new Rig(); Assert.True((await r.Runtime.ExecuteAsync(r.Request("rgb"))).Success);
        var next = r.New();
        await r.Change(["direct"], () =>
        {
            Assert.False(r.Direct.Driver.Owned); Assert.True(r.Direct.Driver.Disposed); Assert.True(r.Direct.Disposed);
            r.Events.Enqueue("new:factory"); return [next];
        });
        var history = r.Events.ToArray(); Assert.True(Array.IndexOf(history, "rgb:release") < Array.IndexOf(history, "rgb:closed"));
        Assert.True(Array.IndexOf(history, "direct:close") < Array.IndexOf(history, "new:factory"));
        Assert.True(Array.IndexOf(history, "new:factory") < Array.IndexOf(history, "sdk:discover"));
        Assert.Equal(0, next.Driver.Writes); Assert.Equal(1, next.Driver.Releases);
        var oldReads = r.Direct.Driver.Polls; var oldDiscovery = r.Direct.Polls;
        await r.Runtime.PollAsync(); await r.Runtime.PollAsync();
        Assert.Equal(oldReads, r.Direct.Driver.Polls); Assert.Equal(oldDiscovery, r.Direct.Polls);
        Assert.False(r.Runtime.State().Inventory.Devices.Single(d => d.Device.Id == "rgb").Connected);
        Assert.True(r.Runtime.State().Inventory.Devices.Single(d => d.Device.Id == "external").Connected);
        Assert.Equal(0, r.Independent.Disposes); Assert.Equal(0, r.Independent.Driver.Disposes);
        Assert.True((await r.Runtime.ExecuteAsync(r.Request("external"))).Success);
    }

    [Fact]
    public async Task InFlightDiscoveryMustActuallyFinishBeforeItsProviderCanClose()
    {
        await using var r = new Rig(); var entered = new TaskCompletionSource(); var resume = new TaskCompletionSource();
        r.Direct.Reading = async () => { entered.TrySetResult(); await resume.Task; };
        var poll = r.Runtime.PollAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var factories = 0; var next = r.New();
        var change = r.Change(["direct"], () => { factories++; return [next]; });
        try
        {
            Assert.False(change.IsCompleted); Assert.Equal(0, factories); Assert.Equal(0, r.Direct.Disposes);
            await Assert.ThrowsAsync<IOException>(() => r.Runtime.ReplaceProvidersAsync(r.Hello.Session, ["direct"], () => [next], default));
        }
        finally { resume.TrySetResult(); }
        await poll; await change.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, factories);
    }

    [Fact]
    public async Task ASlowCloseCannotBeBypassedByPollingOrASecondReplacement()
    {
        await using var r = new Rig(); var entered = new TaskCompletionSource(); var resume = new TaskCompletionSource();
        r.Direct.Driver.Closing = async () => { entered.TrySetResult(); await resume.Task; };
        var next = r.New(); var factories = 0; var change = r.Change(["direct"], () => { factories++; return [next]; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var polls = r.Direct.Polls; await r.Runtime.PollAsync(); Assert.Equal(polls, r.Direct.Polls);
            Assert.Equal(0, factories); Assert.Equal(0, next.Polls);
            await Assert.ThrowsAsync<IOException>(() => r.Runtime.ReplaceProvidersAsync(r.Hello.Session, [], () => [r.New("other", "other")], default));
        }
        finally { resume.TrySetResult(); }
        await change.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnconfirmedReleasePreventsDisposalAndNewFactoryCreation(bool omitted)
    {
        await using var r = new Rig(); await r.Runtime.ExecuteAsync(r.Request("rgb"));
        r.Direct.Driver.FailRelease = !omitted; r.Direct.Driver.OmitRelease = omitted;
        var factories = 0;
        try
        {
            await Assert.ThrowsAsync<IOException>(() => r.Change(["direct"], () => { factories++; return [r.New()]; }));
            Assert.Equal(0, factories); Assert.Equal(0, r.Direct.Disposes); Assert.True(r.Direct.Driver.Owned);
        }
        finally { r.Direct.Driver.FailRelease = r.Direct.Driver.OmitRelease = false; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHandleCloseFailurePermanentlyBlocksNewRoutesUntilServiceRestart(bool providerFails)
    {
        await using var r = new Rig(); r.Direct.FailDispose = providerFails; r.Direct.Driver.FailDispose = !providerFails;
        var factories = 0;
        try
        {
            await Assert.ThrowsAsync<IOException>(() => r.Change(["direct"], () => { factories++; return [r.New()]; }));
            Assert.Contains("ハンドル", r.Runtime.State().LifecycleError); Assert.Equal(0, factories);
            r.Direct.FailDispose = r.Direct.Driver.FailDispose = false;
            await Assert.ThrowsAsync<IOException>(() => r.Change(["direct"], () => { factories++; return [r.New()]; }));
            Assert.Equal(0, factories); var polls = r.Direct.Polls; await r.Runtime.PollAsync(); Assert.Equal(polls, r.Direct.Polls);
        }
        finally { r.Direct.FailDispose = r.Direct.Driver.FailDispose = false; }
    }

    [Fact]
    public async Task RecreatingTheSameProviderAndDeviceStillInvalidatesOldTargets()
    {
        await using var r = new Rig(); var before = r.Request("rgb"); var next = r.New("direct", "rgb");
        await r.Change(["direct"], () => [next]);
        var after = r.Runtime.State(); Assert.NotEqual(before.Targets![0].Generation, after.Endpoints.Single(e => e.Target.DeviceId == "rgb").Generation);
        Assert.False((await r.Runtime.ExecuteAsync(before)).Success); Assert.Equal(0, next.Driver.Writes);
        Assert.True((await r.Runtime.ExecuteAsync(r.Request("rgb"))).Success); Assert.Equal(1, next.Driver.Writes);
    }

    [Fact]
    public async Task ConstructionFailureKeepsTheOldRouteClosedAndAllowsAnExplicitFreshRetry()
    {
        await using var r = new Rig();
        await Assert.ThrowsAsync<IOException>(() => r.Change(["direct"], () => throw new IOException("factory failed")));
        Assert.True(r.Direct.Disposed); var count = r.Direct.Polls; await r.Runtime.PollAsync(); Assert.Equal(count, r.Direct.Polls);
        var next = r.New(); await r.Change(["direct"], () => [next]); Assert.True(next.Polls >= 2);
    }

    [Fact]
    public async Task DuplicateRegistrationCannotDisposeOrReplaceAnIndependentProvider()
    {
        await using var r = new Rig(); var invalid = r.New("independent", "duplicate");
        await Assert.ThrowsAsync<IOException>(() => r.Change(["direct"], () => [invalid]));
        Assert.True(invalid.Disposed); Assert.True(invalid.Driver.Disposed); Assert.Equal(0, invalid.Polls);
        Assert.False(r.Independent.Disposed); Assert.False(r.Independent.Driver.Disposed);
    }

    [Fact]
    public async Task ANewProviderCannotClaimTheDeviceOfAnIndependentProvider()
    {
        await using var r = new Rig(); var invalid = r.New("sdk", "cooler");
        await Assert.ThrowsAsync<IOException>(() => r.Change(["direct"], () => [invalid]));
        Assert.Equal(0, invalid.Polls); Assert.True(invalid.Disposed); Assert.True(invalid.Driver.Disposed);
        Assert.False(r.Independent.Disposed); Assert.False(r.Independent.Driver.Disposed);
    }

    [Fact]
    public async Task OwnerLossDuringCloseCannotStartTheNewRouteOrDeadlockSessionRelease()
    {
        await using var r = new Rig(); var entered = new TaskCompletionSource(); var resume = new TaskCompletionSource();
        r.Direct.Driver.Closing = async () => { entered.TrySetResult(); await resume.Task; };
        var factories = 0; var change = r.Change(["direct"], () => { factories++; return [r.New()]; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disconnected = r.Runtime.DisconnectAsync(r.Hello.Session); resume.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => change.WaitAsync(TimeSpan.FromSeconds(5)));
        await disconnected.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, factories);
    }

    [Fact]
    public async Task AStoppedExternalServerDoesNotStopAnIndependentCoolingDevice()
    {
        await using var r = new Rig(); var next = r.New(); await r.Change(["direct"], () => [next]);
        Assert.True((await r.Runtime.ExecuteAsync(r.Request("cooler"))).Success); var releases = r.Independent.Driver.Releases;
        next.FailPoll = true; await r.Runtime.PollAsync();
        Assert.True(r.Independent.Driver.Owned); Assert.Equal(releases, r.Independent.Driver.Releases);
        Assert.Null(r.Runtime.State().LifecycleError); Assert.True((await r.Runtime.ExecuteAsync(r.Request("cooler"))).Success);
    }

    [Fact]
    public async Task TheConfigurationScopeWaitsForAnAccidentallyUnawaitedReplacement()
    {
        await using var r = new Rig(); var entered = new TaskCompletionSource(); var resume = new TaskCompletionSource();
        r.Direct.Driver.Closing = async () => { entered.TrySetResult(); await resume.Task; };
        var next = r.New();
        var change = r.Runtime.ReconfigureAsync(r.Hello.Session, r.Runtime.State().Inventory.TopologyRevision, (current, ct) =>
        {
            _ = r.Runtime.ReplaceProvidersAsync(r.Hello.Session, ["direct"], () => [next], ct);
            return Task.FromResult(Array.Empty<PhysicalDeviceBinding>());
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Assert.False(change.IsCompleted); Assert.Equal(0, next.Polls); }
        finally { resume.TrySetResult(); }
        await change.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(next.Polls >= 2);
    }

    [Fact]
    public async Task AFailedConfigurationCancelsItsUnawaitedReplacementBeforeTheNewFactory()
    {
        await using var r = new Rig(); var entered = new TaskCompletionSource(); var resume = new TaskCompletionSource();
        r.Direct.Driver.Closing = async () => { entered.TrySetResult(); await resume.Task; };
        var factories = 0;
        var change = r.Runtime.ReconfigureAsync(r.Hello.Session, r.Runtime.State().Inventory.TopologyRevision, async (current, ct) =>
        {
            _ = r.Runtime.ReplaceProvidersAsync(r.Hello.Session, ["direct"], () => { factories++; return [r.New()]; }, ct);
            await entered.Task; throw new IOException("configuration commit failed");
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The callback will fail, but its replacement still owns the closing handle until it finishes.
        try
        {
            Assert.False(change.IsCompleted);
            async Task WaitForFailure() { while (r.Runtime.State().LifecycleError is null) await Task.Delay(1); }
            await WaitForFailure().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { resume.TrySetResult(); }
        await Assert.ThrowsAsync<IOException>(() => change.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Equal(0, factories);
    }

    [Fact]
    public async Task NewRouteRecoveryMustSucceedBeforeConfigurationCanUseItsDrivers()
    {
        await using var r = new Rig(); var next = r.New(); next.Driver.FailRelease = true;
        try
        {
            await Assert.ThrowsAsync<IOException>(() => r.Change(["direct"], () => [next]));
            Assert.True(r.Direct.Disposed); Assert.Equal(1, next.Driver.Releases); Assert.Equal(0, next.Driver.Writes);
            Assert.NotNull(r.Runtime.State().LifecycleError);
        }
        finally { next.Driver.FailRelease = false; }
    }
}
