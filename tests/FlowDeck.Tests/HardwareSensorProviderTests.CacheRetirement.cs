using FlowDeck.Core;
namespace FlowDeck.Tests;
public partial class HardwareSensorProviderTests
{
    [Fact] public async Task CacheRetirementClosesOnlyAbsentSensorAndRediscoveryCreatesFreshDriver()
    {
        var clock=new TestClock(); var cpu=Cpu(clock); var gpu=Gpu(clock); var backend=new Backend { Devices=[cpu,gpu] };
        await using var runtime=Runtime(backend,clock,out var provider); await runtime.PollAsync(); var hello=runtime.OpenSession("user");
        var original=runtime.State().Inventory.Devices.Single(d=>d.Device.Kind==DeviceKind.Processor);
        var old=provider.CachedDrivers.Single(d=>d.DeviceId==original.Device.Id);
        var retained=provider.CachedDrivers.Single(d=>d.DeviceId!=original.Device.Id);
        await Assert.ThrowsAsync<IOException>(()=>provider.RetireDisconnectedAsync(old,default));
        backend.Devices=[gpu]; await runtime.PollAsync();
        await runtime.RetireDisconnectedAsync(hello.Session,runtime.State().Inventory.TopologyRevision,old.DeviceId,runtime.State().Inventory.Devices.Single(d=>d.Device.Id==old.DeviceId).Generation);
        Assert.Equal(0,backend.Disposals); Assert.Same(retained,Assert.Single(provider.CachedDrivers));
        backend.Devices=[cpu,gpu]; await runtime.PollAsync();
        Assert.NotSame(old,provider.CachedDrivers.Single(d=>d.DeviceId==old.DeviceId));
        Assert.True(runtime.State().Inventory.Devices.Single(d=>d.Device.Id==old.DeviceId).Generation>original.Generation);
        Assert.Equal(0,backend.Disposals);
    }
}
