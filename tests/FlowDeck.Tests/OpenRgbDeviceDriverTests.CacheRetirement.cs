using FlowDeck.Core;
using FlowDeck.Infrastructure;
namespace FlowDeck.Tests;
public sealed partial class OpenRgbDeviceDriverTests
{
    [Fact] public async Task CacheRetirementKeepsSdkConnectionAndReappearingIdentityGetsANewDriver()
    {
        await using var r=await new Rig(Device(),Device(42,"SECOND")).Open();
        var old=r.Driver(); var other=r.Driver(42); var device=r.Server.Devices[41];
        r.Server.Devices.Remove(41); r.Server.Wire.Devices.Remove(41);
        var revision=r.Connection!.InventoryRevision; await r.Server.Wire.SendAsync(0,100,[]);
        using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            while(r.Connection.InventoryRevision==revision) await Task.Delay(10,timeout.Token);
        await r.Runtime.PollAsync(); var state=r.Runtime.State(); var generation=state.Inventory.Devices.Single(d=>d.Device.Id==old.DeviceId).Generation;
        await r.Runtime.RetireDisconnectedAsync(r.Owner,state.Inventory.TopologyRevision,old.DeviceId,generation);
        Assert.Same(other,Assert.Single(r.Provider.CachedDrivers)); var connection=r.Connection;
        r.Server.Devices.Add(41,device); r.Server.Wire.Devices.Add(41,OpenRgbSdkModelServer.Encode(device));
        revision=connection.InventoryRevision; await r.Server.Wire.SendAsync(0,100,[]);
        using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            while(connection.InventoryRevision==revision) await Task.Delay(10,timeout.Token);
        await r.Runtime.PollAsync(); await r.Runtime.PollAsync(); Assert.Same(connection,r.Connection); Assert.NotSame(old,r.Driver());
        Assert.True(r.Runtime.State().Inventory.Devices.Single(d=>d.Device.Id==old.DeviceId).Generation>generation);
    }
}
