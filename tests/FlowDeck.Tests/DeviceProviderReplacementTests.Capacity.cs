using System.Collections.Concurrent;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class DeviceProviderReplacementTests
{
    private sealed class CapacityProvider(string id, string prefix, int count, ConcurrentQueue<string> events, bool deferred = false) : IDeviceDriverProvider
    {
        public string ProviderId => id;
        public string[] ResourceDomains => ["route:" + id];
        public Driver[] All { get; } = Enumerable.Range(0, count).Select(i => new Driver(prefix + i, id, events)).ToArray();
        private bool discovered = !deferred;
        public bool FailDispose, Disposed;
        public IDeviceControlDriver[] CachedDrivers => discovered ? All : [];
        public Task PollAsync(CancellationToken ct) { Assert.False(Disposed); discovered = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync()
        {
            if (FailDispose) throw new IOException("provider still open");
            Disposed = true; return ValueTask.CompletedTask;
        }
    }
    private sealed class CapacityRig : IAsyncDisposable
    {
        public readonly ConcurrentQueue<string> Events = new();
        public CapacityProvider Current;
        public readonly Provider Independent;
        public readonly DeviceControlRuntime Runtime;
        public readonly DeviceServiceHello Hello;
        private long sequence;
        public CapacityRig()
        {
            Current = new("direct", "old-", 31, Events); Independent = new("independent", "cooler", Events);
            Runtime = new([], ["user"], new TestLog(), new TestClock(), providers: [Current, Independent],
                physicalBindings: Current.All.Select(d => d.BindingCandidate).Append(Independent.Driver.BindingCandidate));
            Hello = Runtime.OpenSession("user");
        }
        public CapacityProvider Next(string prefix = "next-", int count = 31, bool deferred = false) =>
            new(Current.ProviderId == "direct" ? "sdk" : "direct", prefix, count, Events, deferred);
        public DeviceServiceRequest Request(string id)
        {
            var s = Runtime.State(); var e = s.Endpoints.Single(e => e.Target == new EndpointReference(id, "fan"));
            return new(2, Hello.Epoch, Hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", DeviceOperation.SetDuty,
                s.Inventory.TopologyRevision, [new(e.Target, e.Generation, e.PolicyRevision)], Duty: 60);
        }
        public async Task Change(CapacityProvider next)
        {
            var scope = new DeviceProviderIsolation([Current.ProviderId, next.ProviderId],
                Current.ResourceDomains.Concat(next.ResourceDomains).Concat(Current.All.Select(d => d.Domain)).Concat(next.All.Select(d => d.Domain)).Distinct().ToArray());
            await Runtime.ReconfigureAsync(Hello.Session, Runtime.State().Inventory.TopologyRevision, async (_, ct) =>
                (await Runtime.ReplaceProvidersAsync(Hello.Session, [Current.ProviderId], () => [next], ct, [next.ProviderId]))
                    .OfType<IPhysicalDeviceDriver>().Select(d => d.BindingCandidate!).ToArray(), scope);
            Current = next;
        }
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FullRegistryAdmitsAReplacementAfterReleaseAndHandleClosure(bool deferred)
    {
        await using var r = new CapacityRig(); var old = r.Current;
        Assert.Equal(32, r.Runtime.State().Inventory.Devices.Length);
        Assert.True((await r.Runtime.ExecuteAsync(r.Request("cooler"))).Success);
        Assert.True((await r.Runtime.ExecuteAsync(r.Request("old-0"))).Success);
        var stale = r.Request("old-0"); var independentReleases = r.Independent.Driver.Releases;
        var config = ServiceRuntimeBootstrap.MonitoringConfiguration(new string('a',32));
        config = config with { Devices = new(2, [new("old-0", "保存した機器名", "direct", DeviceKind.Controller,
            [new("fan", "保存したファン名", EndpointKind.Cooling)])]) };
        var before = DeviceConfigurationCodec.SerializePackage(config);
        await r.Change(r.Next(deferred: deferred));
        var state = r.Runtime.State(); Assert.Equal(32, state.Inventory.Devices.Length);
        Assert.All(state.Inventory.Devices, d => Assert.True(d.Connected));
        Assert.All(old.All, d => { Assert.True(d.Disposed); Assert.False(d.Owned); }); Assert.True(old.Disposed);
        Assert.All(r.Current.All, d => { Assert.True(d.Polls > 0); Assert.Equal(0,d.Writes); Assert.True(d.Releases > 0); });
        Assert.True(r.Independent.Driver.Owned); Assert.Equal(independentReleases,r.Independent.Driver.Releases);
        Assert.False((await r.Runtime.ExecuteAsync(stale)).Success);
        var catalog = DeviceCatalogPresentation.Build(new(state,new("generation",config,[]),0,true,null));
        Assert.DoesNotContain(catalog,d=>d.Id=="old-0");
        Assert.Equal(state.Inventory.Devices.Select(d=>d.Device.Id),catalog.Select(d=>d.Id));
        Assert.Equal(before,DeviceConfigurationCodec.SerializePackage(config));
        Assert.True((await r.Runtime.ExecuteAsync(r.Request("next-0"))).Success);
    }
    [Fact]
    public async Task RepeatedFullRegistryReplacementAndReturningIdentityNeverReuseAnOldGeneration()
    {
        await using var r = new CapacityRig(); var stale = r.Request("old-0");
        for (var i=0;i<3;i++)
        {
            await r.Change(r.Next(i==2 ? "old-" : "round-"+i+"-",deferred: i%2==0));
            Assert.Equal(32,r.Runtime.State().Inventory.Devices.Length);
        }
        var e=r.Runtime.State().Endpoints.Single(e=>e.Target==new EndpointReference("old-0","fan"));
        Assert.True(e.Generation>stale.Targets![0].Generation); Assert.False((await r.Runtime.ExecuteAsync(stale)).Success);
        Assert.True((await r.Runtime.ExecuteAsync(r.Request("old-0"))).Success);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CapacityReusesOnlyRequiredSlotsAndRetainsOtherDisconnectedHistory(bool sameIdentity)
    {
        await using var r = new CapacityRig(); var before=r.Runtime.State();
        await r.Change(r.Next(sameIdentity ? "old-" : "next-",count:1));
        var after=r.Runtime.State(); Assert.Equal(32,after.Inventory.Devices.Length);
        Assert.Equal(2,after.Inventory.Devices.Count(d=>d.Connected));
        Assert.Equal(sameIdentity ? 31 : 30,after.Inventory.Devices.Count(d=>d.Device.Id.StartsWith("old-")));
        if(sameIdentity)
            Assert.True(after.Inventory.Devices.Single(d=>d.Device.Id=="old-0").Generation>before.Inventory.Devices.Single(d=>d.Device.Id=="old-0").Generation);
        else Assert.DoesNotContain(after.Inventory.Devices,d=>d.Device.Id=="old-0");
        Assert.Contains(after.Inventory.Devices,d=>d.Device.Id=="old-30"&&!d.Connected);
    }
    [Theory] [InlineData("release")] [InlineData("driver-close")] [InlineData("provider-close")]
    public async Task UnfinishedReleaseOrClosureCannotReclaimRegisteredDevices(string failure)
    {
        await using var r = new CapacityRig(); var old=r.Current; var next=r.Next();
        Assert.True((await r.Runtime.ExecuteAsync(r.Request("old-0"))).Success);
        old.All[0].FailRelease=failure=="release"; old.All[0].FailDispose=failure=="driver-close"; old.FailDispose=failure=="provider-close";
        try
        {
            await Assert.ThrowsAsync<IOException>(()=>r.Change(next));
            var s=r.Runtime.State(); Assert.Equal(32,s.Inventory.Devices.Length);
            Assert.Contains(s.Inventory.Devices,d=>d.Device.Id=="old-0"); Assert.DoesNotContain(s.Inventory.Devices,d=>d.Device.Id.StartsWith("next-"));
            Assert.All(next.All,d=>Assert.Equal(0,d.Polls));
            if(failure=="release") { Assert.True(old.All[0].Owned); Assert.False(old.Disposed); }
            Assert.NotNull(s.LifecycleError);
        }
        finally { old.All[0].FailRelease=false; old.All[0].FailDispose=false; old.FailDispose=false; }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task TrulyOverCapacityNewRouteDoesNotPartiallyForgetOldRegistrations(bool deferred)
    {
        await using var r = new CapacityRig(); var next=r.Next(count:32,deferred:deferred);
        await Assert.ThrowsAsync<IOException>(()=>r.Change(next));
        var s=r.Runtime.State(); Assert.Equal(32,s.Inventory.Devices.Length);
        Assert.Equal(31,s.Inventory.Devices.Count(d=>d.Device.Id.StartsWith("old-")));
        Assert.DoesNotContain(s.Inventory.Devices,d=>d.Device.Id.StartsWith("next-"));
        Assert.False(r.Independent.Driver.Disposed);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ConfigurationCommitAndRollbackAtCapacityPreserveStoredNamesAndIndependentControl(bool failTarget)
    {
        await using var r=await RoutingRig.Create();
        var history=new CapacityProvider("history","history-",30,r.Events);
        await r.Startup.Runtime.ReconfigureAsync(r.Hello.Session,r.Startup.Runtime.State().Inventory.TopologyRevision,async (_,ct)=> {
            await r.Startup.Runtime.ReplaceProvidersAsync(r.Hello.Session,[],()=>[history],ct,["history"]);
            return r.Startup.Configuration.Bindings.Devices;
        });
        Assert.Equal(32,r.Startup.Runtime.State().Inventory.Devices.Length);
        r.Policy.Planner=(_,_)=>new(["direct"],["sdk"],Isolation.ResourceDomains);
        r.Policy.Factory=(_,ids)=>ids.Select(id=> { var p=r.Policy.New(id); p.FailPoll=failTarget&&id=="sdk"; return (IDeviceDriverProvider)p; }).ToArray();
        var state=r.Startup.Runtime.State(); var fan=state.Endpoints.Single(e=>e.Target.DeviceId=="cooler");
        var request=r.Request(new(DeviceConfigurationOperation.Snapshot)) with {
            Operation=DeviceOperation.SetDuty,Configuration=null,TopologyRevision=state.Inventory.TopologyRevision,
            Targets=[new(fan.Target,fan.Generation,fan.PolicyRevision)],Duty=60
        };
        Assert.True((await r.Startup.Runtime.ExecuteAsync(request)).Success);
        var releases=r.Independent.Driver.Releases; var originalGeneration=r.Store.ReadActivePointer();
        var savedNames=System.Text.Json.JsonSerializer.Serialize(r.Startup.Configuration.Devices,Contract.Json);
        var upload=await r.Upload(r.Target()); var result=await r.Apply(upload);
        Assert.Equal(!failTarget,result.Success); Assert.Null(r.Journal.Pending);
        Assert.Equal(failTarget?"rolled-back":"applied",r.Application.Phase);
        var stored=DeviceConfigurationCodec.Read(r.Store.ReadActive()!.Files);
        Assert.Equal(savedNames,System.Text.Json.JsonSerializer.Serialize(stored.Devices,Contract.Json));
        Assert.Equal(failTarget,originalGeneration==r.Store.ReadActivePointer());
        Assert.True(r.Independent.Driver.Owned); Assert.Equal(releases,r.Independent.Driver.Releases); Assert.False(history.Disposed);
        state=r.Startup.Runtime.State(); Assert.Equal(32,state.Inventory.Devices.Length); Assert.All(state.Inventory.Devices,d=>Assert.True(d.Connected));
        var catalog=DeviceCatalogPresentation.Build(new(state,new(r.Startup.Generation.Id,stored,[]),0,true,null));
        if(failTarget) {
            var old=Assert.Single(catalog,d=>d.Id=="rgb");
            Assert.Equal("rgb",old.Name); Assert.Equal("Fan",Assert.Single(old.Endpoints).Name);
        }
        else Assert.DoesNotContain(catalog,d=>d.Id=="rgb");
    }
    [Fact]
    public async Task FullLiveRegistryCannotBePrunedToMakeRoomForAnotherProvider()
    {
        await using var r = new CapacityRig(); var next=r.Next(count:1);
        await Assert.ThrowsAsync<IOException>(()=>r.Runtime.ReconfigureAsync(r.Hello.Session,r.Runtime.State().Inventory.TopologyRevision,async (_,ct)=>
            (await r.Runtime.ReplaceProvidersAsync(r.Hello.Session,[],()=>[next],ct,[next.ProviderId])).OfType<IPhysicalDeviceDriver>().Select(d=>d.BindingCandidate!).ToArray()));
        Assert.Equal(32,r.Runtime.State().Inventory.Devices.Length); Assert.All(r.Current.All,d=>Assert.False(d.Disposed));
        Assert.True(next.Disposed); Assert.DoesNotContain(r.Runtime.State().Inventory.Devices,d=>d.Device.Id.StartsWith("next-"));
    }
}
