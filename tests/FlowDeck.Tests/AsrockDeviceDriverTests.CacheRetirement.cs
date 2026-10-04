using FlowDeck.Core;
namespace FlowDeck.Tests;
public sealed partial class AsrockDeviceDriverTests
{
    [Fact] public async Task CacheRetirementPreservesSharedMotherboardBackendAndOtherDevice()
    {
        var a=new Cell(1,true); var b=new Cell(2,true); await using var r=await new Rig(new(a,b)).Open();
        var old=r.Driver(a); var other=r.Driver(b); r.Backend.Cells.Remove(a); await r.Runtime.PollAsync();
        var state=r.Runtime.State(); var generation=state.Inventory.Devices.Single(d=>d.Device.Id==a.Id).Generation;
        await r.Runtime.RetireDisconnectedAsync(r.Owner,state.Inventory.TopologyRevision,a.Id,generation);
        Assert.Same(other,Assert.Single(r.Provider.CachedDrivers)); Assert.False(r.Backend.Disposed);
        r.Backend.Cells.Add(a); await r.Runtime.PollAsync(); await r.Runtime.PollAsync();
        Assert.NotSame(old,r.Driver(a)); Assert.True(r.Runtime.State().Inventory.Devices.Single(d=>d.Device.Id==a.Id).Generation>generation);
        Assert.All(r.Driver(a).DescribeSetup(),s=>Assert.NotNull(s)); Assert.False(r.Backend.Disposed);
    }
}
