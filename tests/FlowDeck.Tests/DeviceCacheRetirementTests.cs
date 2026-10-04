using FlowDeck.Core;

namespace FlowDeck.Tests;

public sealed class DeviceCacheRetirementTests
{
    private sealed class Driver(string id) : IPhysicalDeviceDriver
    {
        public string DeviceId => id;
        public PhysicalDeviceBinding BindingCandidate => new(id, "serial:" + id, "test-v1", new('a',64));
        public bool Connected = true, Owned, Disposed, FailDispose, FailRelease;
        public int Disposes, Releases, Writes;
        public Func<Task>? Closing;
        public DeviceObservation? Observe() => !Connected || Disposed ? null : new(id,"fixture",id,DeviceKind.Controller,
            IdentityConfidence.SerialBound,"connection","test-v1",["fixture"],
            [new("fan","Fan",EndpointKind.Cooling,[new(DeviceFeature.FixedDuty,ImplementationSupport.Supported,
                VerificationEvidence.Automated,DeviceAvailability.Ready)])]);
        public string? Validate(DeviceEndpointAction action) => Disposed ? "closed" : null;
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action,CancellationToken ct)
        { Writes++; Owned=true; return Task.FromResult(new DeviceDriverResult(Outcome.Confirmed,"written",action.Duty)); }
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason,CancellationToken ct)
        {
            Releases++; if(!FailRelease) Owned=false;
            return Task.FromResult(new[] { new DeviceDriverReleaseResult("fan",new(FailRelease ? Outcome.Unknown : Outcome.Confirmed,"release",50)) });
        }
        public async ValueTask DisposeAsync()
        { Disposes++; if(Closing is not null) await Closing(); if(FailDispose) throw new IOException("handle open"); Disposed=true; }
    }
    private sealed class Provider : IDeviceCacheRetirement
    {
        public string ProviderId => "fixture";
        public string[] ResourceDomains => ["fixture"];
        public readonly List<Driver> Drivers = [new("gone"),new("other")];
        public bool FailPoll, Retain, Disposed;
        public IDeviceControlDriver[] CachedDrivers => Drivers.ToArray();
        public bool IsConfirmedAbsent(IDeviceControlDriver driver) => !FailPoll && Drivers.Contains((Driver)driver) && !((Driver)driver).Connected;
        public Task PollAsync(CancellationToken ct) => FailPoll ? Task.FromException(new IOException("enumeration failed")) : Task.CompletedTask;
        public async Task RetireDisconnectedAsync(IDeviceControlDriver driver,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Assert.Null(driver.Observe());
            await driver.DisposeAsync(); if(!Retain) Drivers.Remove((Driver)driver);
        }
        public ValueTask DisposeAsync() { Disposed=true; return ValueTask.CompletedTask; }
    }
    private sealed class Rig : IAsyncDisposable
    {
        public readonly Provider Provider=new();
        public readonly TestClock Clock=new();
        public readonly DeviceControlRuntime Runtime;
        public readonly DeviceServiceHello Hello;
        public Driver Gone => Provider.Drivers.Single(d=>d.DeviceId=="gone");
        public Driver Other => Provider.Drivers.Single(d=>d.DeviceId=="other");
        private long sequence;
        public Rig()
        { Runtime=new([], ["user"],new TestLog(),Clock,providers:[Provider],physicalBindings:Provider.Drivers.Select(d=>d.BindingCandidate)); Hello=Runtime.OpenSession("user"); }
        public DeviceServiceRequest Request(string id)
        {
            var s=Runtime.State(); var e=s.Endpoints.Single(e=>e.Target==new EndpointReference(id,"fan"));
            return new(2,Hello.Epoch,Hello.Session,Guid.NewGuid().ToString("N"),++sequence,"test",DeviceOperation.SetDuty,
                s.Inventory.TopologyRevision,[new(e.Target,e.Generation,e.PolicyRevision)],Duty:60);
        }
        public long Generation => Runtime.State().Inventory.Devices.Single(d=>d.Device.Id=="gone").Generation;
        public Task Retire(string? owner=null,long? topology=null,long? generation=null) => Runtime.RetireDisconnectedAsync(
            owner??Hello.Session,topology??Runtime.State().Inventory.TopologyRevision,"gone",generation??Generation);
        public ValueTask DisposeAsync()=>Runtime.DisposeAsync();
    }
    [Fact] public async Task CleanupReleasesOtherControlPreservesProviderAndReturningIdentityGetsNewGeneration()
    {
        await using var r=new Rig(); var gone=r.Gone; var oldGeneration=r.Generation;
        var stale=r.Request("gone"); Assert.True((await r.Runtime.ExecuteAsync(r.Request("other"))).Success);
        gone.Connected=false; Assert.Null(r.Runtime.CacheRetirementProblem("gone",r.Generation));
        await r.Retire(); Assert.True(gone.Disposed); Assert.Equal(1,gone.Disposes); Assert.False(r.Other.Owned);
        Assert.False(r.Provider.Disposed); Assert.False(r.Other.Disposed);
        Assert.DoesNotContain(r.Runtime.State().Inventory.Devices,d=>d.Device.Id=="gone");
        var returned=new Driver("gone"); r.Provider.Drivers.Add(returned); await r.Runtime.PollAsync();
        Assert.True(r.Generation>oldGeneration); Assert.False((await r.Runtime.ExecuteAsync(stale)).Success);
        Assert.False((await r.Runtime.ExecuteAsync(r.Request("gone"))).Success); Assert.Equal(0,returned.Writes);
        Assert.True((await r.Runtime.ExecuteAsync(r.Request("other"))).Success);
    }
    [Theory] [InlineData("connected")] [InlineData("generation")] [InlineData("owner")] [InlineData("topology")] [InlineData("poll")]
    public async Task InvalidReviewDoesNotReleaseIndependentControlOrDispose(string reason)
    {
        await using var r=new Rig(); Assert.True((await r.Runtime.ExecuteAsync(r.Request("other"))).Success);
        if(reason!="connected") r.Gone.Connected=false;
        if(reason=="poll") { r.Provider.FailPoll=true; await r.Runtime.PollAsync(); }
        var releases=r.Other.Releases;
        await Assert.ThrowsAsync<IOException>(()=>r.Retire(reason=="owner" ? "other-user" : null,
            reason=="topology" ? -1 : null,reason=="generation" ? r.Generation+1 : null));
        Assert.Equal(releases,r.Other.Releases); Assert.True(r.Other.Owned); Assert.Equal(0,r.Gone.Disposes);
        r.Provider.FailPoll=false;
    }
    [Fact] public async Task OwnedDisconnectedTargetCannotBeForgottenEvenWhenItsDriverClaimsReleaseWouldSucceed()
    {
        await using var r=new Rig(); Assert.True((await r.Runtime.ExecuteAsync(r.Request("gone"))).Success);
        r.Gone.Connected=false; var releases=r.Other.Releases;
        Assert.NotNull(r.Runtime.CacheRetirementProblem("gone",r.Generation));
        await Assert.ThrowsAsync<IOException>(()=>r.Retire()); Assert.Equal(0,r.Gone.Disposes); Assert.Equal(releases,r.Other.Releases);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FailedReleaseOrCloseRetainsHistoryAndCanBeRetried(bool release)
    {
        await using var r=new Rig(); r.Gone.Connected=false; r.Gone.FailRelease=release; r.Gone.FailDispose=!release;
        await Assert.ThrowsAsync<IOException>(()=>r.Retire()); Assert.Contains(r.Runtime.State().Inventory.Devices,d=>d.Device.Id=="gone");
        Assert.Contains(r.Gone,r.Provider.CachedDrivers); Assert.False(r.Gone.Disposed);
        r.Gone.FailRelease=false; r.Gone.FailDispose=false;
        // A recovery failure must first be cleared through the normal release/recovery path.
        if(release) { r.Clock.Advance(13); await r.Runtime.SweepAsync(); var hello=r.Runtime.OpenSession("user"); await r.Retire(owner:hello.Session); }
        else await r.Retire();
        Assert.DoesNotContain(r.Runtime.State().Inventory.Devices,d=>d.Device.Id=="gone");
    }
    [Fact] public async Task ProviderThatRetainsTheClosedObjectCannotMakeRuntimeForgetIt()
    {
        await using var r=new Rig(); r.Gone.Connected=false; r.Provider.Retain=true;
        await Assert.ThrowsAsync<IOException>(()=>r.Retire()); Assert.Contains(r.Runtime.State().Inventory.Devices,d=>d.Device.Id=="gone");
        Assert.Single(r.Provider.CachedDrivers,d=>d.DeviceId=="gone");
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CancellationAndShutdownWaitUntilTheActualCloseHasSettled(bool shutdown)
    {
        var r=new Rig(); var gone=r.Gone; gone.Connected=false;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gone.Closing=async()=> { entered.TrySetResult(); await finish.Task; };
        Task? closing=null; var retire=r.Retire();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if(shutdown) closing=r.Runtime.DisposeAsync().AsTask();
            else { r.Clock.Advance(13); closing=r.Runtime.SweepAsync(); }
            Assert.False(retire.IsCompleted); Assert.False(gone.Disposed); Assert.Contains(gone,r.Provider.CachedDrivers);
            if(shutdown) Assert.False(closing.IsCompleted);
            finish.TrySetResult(); await Assert.ThrowsAnyAsync<Exception>(()=>retire.WaitAsync(TimeSpan.FromSeconds(3)));
            await closing.WaitAsync(TimeSpan.FromSeconds(3)); Assert.True(gone.Disposed);
            Assert.DoesNotContain(r.Provider.CachedDrivers,d=>ReferenceEquals(d,gone));
            Assert.DoesNotContain(r.Runtime.State().Inventory.Devices,d=>d.Device.Id=="gone");
        }
        finally { finish.TrySetResult(); await r.DisposeAsync(); }
    }
    [Fact] public async Task FullRegistryCanAdmitAnotherDeviceAfterIndividualRetirement()
    {
        await using var r=new Rig(); for(var i=0;i<30;i++) r.Provider.Drivers.Add(new("filler"+i));
        Assert.Equal(32,r.Runtime.State().Inventory.Devices.Length); r.Gone.Connected=false; await r.Retire();
        r.Provider.Drivers.Add(new("fresh")); await r.Runtime.PollAsync();
        Assert.Equal(32,r.Runtime.State().Inventory.Devices.Length); Assert.Contains(r.Runtime.State().Inventory.Devices,d=>d.Device.Id=="fresh"&&d.Connected);
    }
    [Fact] public async Task CacheRetirementRecoversGlobalCapacityWithoutAcceptingAMalformedProvider()
    {
        var provider=new Provider();for(var i=0;i<29;i++) provider.Drivers.Add(new("cached-"+i));
        var independent=new Driver("independent");await using var runtime=new DeviceControlRuntime([independent],["user"],new TestLog(),providers:[provider]);
        var hello=runtime.OpenSession("user");Assert.Equal(32,runtime.State().Inventory.Devices.Length);
        provider.Drivers[0].Connected=false;provider.Drivers.Add(new("fresh"));
        var blocked=runtime.State();var gone=blocked.Inventory.Devices.Single(d=>d.Device.Id=="gone");
        Assert.Null(runtime.CacheRetirementProblem("gone",gone.Generation));
        await runtime.RetireDisconnectedAsync(hello.Session,blocked.Inventory.TopologyRevision,"gone",gone.Generation);
        var after=runtime.State();Assert.Equal(32,after.Inventory.Devices.Length);Assert.Contains(after.Inventory.Devices,d=>d.Device.Id=="fresh");
        Assert.False(independent.Disposed);Assert.Equal(0,independent.Writes);
        provider.Drivers.Single(d=>d.DeviceId=="other").Connected=false;provider.Drivers.Add(new("fresh"));
        var generation=runtime.State().Inventory.Devices.Single(d=>d.Device.Id=="other").Generation;
        Assert.NotNull(runtime.CacheRetirementProblem("other",generation));
        await Assert.ThrowsAsync<IOException>(()=>runtime.RetireDisconnectedAsync(hello.Session,runtime.State().Inventory.TopologyRevision,"other",generation));
        provider.Drivers.RemoveAt(provider.Drivers.Count-1);
    }

}
