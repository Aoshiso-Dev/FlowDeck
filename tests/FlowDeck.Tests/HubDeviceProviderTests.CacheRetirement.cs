using FlowDeck.Core;
namespace FlowDeck.Tests;
public partial class HubDeviceProviderTests
{
    [Fact] public async Task CacheRetirementClosesOneHubAndRemovesRememberedIdentityWithoutClosingDiscovery()
    {
        await using var f=new Fixture([A,B],[A.Id]); await f.DiscoverAndOpen();
        var old=f.Driver(A.Id); var other=f.Driver(B.Id); var adapter=f.Adapters[A.Id];
        await Assert.ThrowsAsync<IOException>(()=>f.Provider.RetireDisconnectedAsync(old,default));
        f.Discovery.Found=[B]; await f.Provider.PollAsync(default); Assert.Null(old.Observe());
        await f.Provider.RetireDisconnectedAsync(old,default);
        Assert.True(adapter.Closed); Assert.Equal(1,adapter.Disposes); Assert.Equal(0,f.Discovery.Disposes);
        Assert.False(f.Adapters[B.Id].Closed); Assert.Same(other,Assert.Single(f.Provider.CachedDrivers));
        await f.Provider.PollAsync(default); Assert.Single(f.Provider.CachedDrivers);
        f.Adapters.Remove(A.Id); f.Discovery.Found=[A,B]; await f.DiscoverAndOpen();
        Assert.NotSame(old,f.Driver(A.Id)); Assert.NotSame(adapter,f.Adapters[A.Id]); Assert.Equal(0,f.Adapters[A.Id].Inner.Writes);
    }
    [Fact] public async Task CacheRetirementRetainsHubWhenHandleCloseFailsAndRetriesTheSameObject()
    {
        await using var f=new Fixture([A]); await f.DiscoverAndOpen(); var old=f.Driver(A.Id); var adapter=f.Adapters[A.Id];
        f.Discovery.Found=[]; await f.Provider.PollAsync(default); adapter.FailDispose=true;
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Provider.RetireDisconnectedAsync(old,default));
        Assert.Same(old,Assert.Single(f.Provider.CachedDrivers)); Assert.False(adapter.Closed);
        adapter.FailDispose=false; await f.Provider.RetireDisconnectedAsync(old,default); Assert.Empty(f.Provider.CachedDrivers); Assert.True(adapter.Closed);
    }
    [Fact] public async Task CacheRetirementCanRecoverAFullHubCacheAfterAnotherIdentityArrives()
    {
        var found=Enumerable.Range(0,32).Select(i=>new HubIdentity("hub-"+i,"Hub","SERIAL-"+i,"usb:"+i)).ToArray();
        await using var f=new Fixture(found);await f.DiscoverAndOpen();
        // This tests cache capacity, not elapsed owner lifetime; parallel suites can exceed the lease.
        await using var runtime=new DeviceControlRuntime([], ["user"],new TestLog(),new TestClock(),providers:[f.Provider]);
        var hello=runtime.OpenSession("user");var old=f.Driver(found[0].Id);Assert.Equal(32,runtime.State().Inventory.Devices.Length);
        f.Discovery.Found=[..found.Skip(1),new("fresh","Hub","SERIAL-FRESH","usb:fresh")];
        await runtime.PollAsync();var blocked=runtime.State();
        Assert.False(blocked.Inventory.Devices.Single(d=>d.Device.Id==old.DeviceId).Connected);
        var generation=blocked.Inventory.Devices.Single(d=>d.Device.Id==old.DeviceId).Generation;
        Assert.Null(runtime.CacheRetirementProblem(old.DeviceId,generation));
        await runtime.RetireDisconnectedAsync(hello.Session,blocked.Inventory.TopologyRevision,old.DeviceId,generation);
        await runtime.PollAsync();await runtime.PollAsync();var after=runtime.State();
        Assert.Equal(32,after.Inventory.Devices.Length);Assert.All(after.Inventory.Devices,d=>Assert.True(d.Connected));
        Assert.DoesNotContain(after.Inventory.Devices,d=>d.Device.Id==old.DeviceId);Assert.Equal(32,f.Provider.CachedDrivers.Length);
        Assert.All(f.Adapters.Values,a=>Assert.Equal(0,a.Inner.Writes));
    }

}
