using FlowDeck.Core;
using Rack = FlowDeck.Tests.EneMemoryBackendTests.Rack;
namespace FlowDeck.Tests;
public sealed partial class EneMemoryDeviceDriverTests
{
    [Fact] public async Task CacheRetirementOfAbsentMemoryPreservesOtherModuleAndDoesNotWrite()
    {
        var rack=new Rack(0x71,0x73); await using var r=await new Rig(rack).Open();
        var old=r.Driver(); var other=r.Driver(0x73); rack.Present.Remove(0x71); await r.Runtime.PollAsync();
        var state=r.Runtime.State(); var generation=state.Inventory.Devices.Single(d=>d.Device.Id==old.DeviceId).Generation;
        await r.Runtime.RetireDisconnectedAsync(r.Owner,state.Inventory.TopologyRevision,old.DeviceId,generation);
        Assert.Same(other,Assert.Single(r.Provider.CachedDrivers)); Assert.Empty(rack.Writes);
        rack.Present.Add(0x71); await r.Runtime.PollAsync(); await r.Runtime.PollAsync(); Assert.NotSame(old,r.Driver());
        Assert.True(r.Runtime.State().Inventory.Devices.Single(d=>d.Device.Id==old.DeviceId).Generation>generation); Assert.Empty(rack.Writes);
    }
}
