using FlowDeck.Core;
using FlowDeck.Infrastructure;
namespace FlowDeck.Tests;
public sealed class DeviceCacheRetirementServiceTests
{
    private const string Generation="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static DeviceConfigurationRequest Request(DeviceConfigurationOperation operation=DeviceConfigurationOperation.PreviewCacheRetirement)=>
        new(operation,ExpectedGeneration:Generation,TopologyRevision:3,CacheRetirement:new("device",4));
    [Fact] public void WireRequiresAnExactStandaloneTargetAndReviewVersions()
    {
        var r=Request();Assert.True(DeviceConfigurationProtocol.Valid(r));Assert.True(DeviceConfigurationProtocol.Valid(r with { Operation=DeviceConfigurationOperation.RetireDisconnected }));
        DeviceConfigurationRequest[] invalid=[r with { CacheRetirement=null },r with { CacheRetirement=new("device",0) },r with { CacheRetirement=new("",4) },
            r with { TopologyRevision=null },r with { TopologyRevision=-1 },r with { ExpectedGeneration=null },r with { TransferId=Generation },
            r with { Data=[1] },r with { Offset=0 },r with { DeviceName=new("device","name") },r with { Operation=DeviceConfigurationOperation.Snapshot }];
        Assert.All(invalid,x=>Assert.False(DeviceConfigurationProtocol.Valid(x)));
        var reply=new DeviceConfigurationReply(Outcome.Confirmed,"ok",Generation,CacheRetirement:r.CacheRetirement);
        Assert.True(DeviceConfigurationProtocol.Valid(reply));Assert.False(DeviceConfigurationProtocol.Valid(reply with { Outcome=Outcome.Unknown }));
        Assert.False(DeviceConfigurationProtocol.Valid(reply with { Candidates=[] }));Assert.False(DeviceConfigurationProtocol.Valid(reply with { TransferId=Generation }));
    }
    [Theory] [InlineData("epoch")] [InlineData("session")] [InlineData("configuration")] [InlineData("topology")] [InlineData("generation")] [InlineData("connected")] [InlineData("exit")]
    public async Task ReviewedHistoryCannotBeAppliedAfterItsContextChanges(string change)
    {
        using var dir=new ServiceTestDirectory();await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient();await client.ConnectAsync();demo.GpuConnected=false;await demo.Runtime.PollAsync();await client.RefreshAsync(true);
        var s=client.Snapshot!;var id=s.State.Inventory.Devices.Single(d=>d.Device.Kind==DeviceKind.Graphics).Device.Id;
        var edit=await client.PreviewCacheRetirementAsync(DeviceCacheRetirementEdit.Capture(s,id));Assert.True(edit.Matches(client.Snapshot));
        var changed=change switch {
            "epoch"=>s with { State=s.State with { Inventory=s.State.Inventory with { Epoch="changed" } } },
            "session"=>s with { Session="changed" },"configuration"=>s with { Configuration=s.Configuration with { Generation="changed" } },
            "topology"=>s with { State=s.State with { Inventory=s.State.Inventory with { TopologyRevision=s.State.Inventory.TopologyRevision+1 } } },
            "generation"=>s with { State=s.State with { Inventory=s.State.Inventory with { Devices=s.State.Inventory.Devices.Select(d=>d.Device.Id==id?d with { Generation=d.Generation+1 }:d).ToArray() } } },
            "connected"=>s with { State=s.State with { Inventory=s.State.Inventory with { Devices=s.State.Inventory.Devices.Select(d=>d.Device.Id==id?d with { Connected=true }:d).ToArray() } } },
            _=>s with { ExitAttempted=true }
        };
        Assert.False(edit.Matches(changed));Assert.All(demo.Hubs,h=>Assert.Equal(0,h.Writes));
    }
    [Fact] public async Task ServiceRetirementPreservesConfigurationAndReappearingDashboardNames()
    {
        using var dir=new ServiceTestDirectory();await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient();await client.ConnectAsync();var gpu=client.Snapshot!.State.Inventory.Devices.Single(d=>d.Device.Kind==DeviceKind.Graphics);
        demo.GpuConnected=false;await demo.Runtime.PollAsync();await client.RefreshAsync(true);
        var before=DeviceConfigurationCodec.SerializePackage(client.Snapshot!.Configuration.Configuration);var generation=client.Snapshot.Configuration.Generation;
        var edit=await client.PreviewCacheRetirementAsync(DeviceCacheRetirementEdit.Capture(client.Snapshot,gpu.Device.Id));
        Assert.Contains(client.Snapshot!.State.Inventory.Devices,d=>d.Device.Id==gpu.Device.Id);
        await client.RetireDisconnectedAsync(edit);Assert.DoesNotContain(client.Snapshot!.State.Inventory.Devices,d=>d.Device.Id==gpu.Device.Id);
        Assert.Equal(generation,client.Snapshot.Configuration.Generation);Assert.Equal(before,DeviceConfigurationCodec.SerializePackage(client.Snapshot.Configuration.Configuration));
        demo.GpuConnected=true;await demo.Runtime.PollAsync();await client.RefreshAsync(true);
        Assert.True(client.Snapshot!.State.Inventory.Devices.Single(d=>d.Device.Id==gpu.Device.Id).Generation>gpu.Generation);
        Assert.Contains(client.Snapshot.Configuration.Configuration.Devices.Devices,d=>d.Id==gpu.Device.Id);
        await Assert.ThrowsAsync<IOException>(()=>client.RetireDisconnectedAsync(edit));Assert.All(demo.Hubs,h=>{Assert.Equal(0,h.Writes);Assert.Equal(0,h.LightingWrites);});
    }
    [Theory] [InlineData("configuration")] [InlineData("topology")] [InlineData("generation")] [InlineData("connected")] [InlineData("unknown")]
    public async Task AuthenticatedWireStillRejectsStaleOrInvalidRetirement(string change)
    {
        using var dir=new ServiceTestDirectory();await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        var pipe=DeviceServicePipeClient.ForDemo(demo.PipeName,Environment.ProcessId);await using var client=new DeviceWorkspaceClient(pipe);await client.ConnectAsync();
        demo.GpuConnected=false;await demo.Runtime.PollAsync();await client.RefreshAsync(true);var s=client.Snapshot!;
        var d=s.State.Inventory.Devices.Single(d=>d.Device.Kind==DeviceKind.Graphics);
        var request=new DeviceConfigurationRequest(DeviceConfigurationOperation.RetireDisconnected,ExpectedGeneration:s.Configuration.Generation,
            TopologyRevision:s.State.Inventory.TopologyRevision,CacheRetirement:new(d.Device.Id,d.Generation));
        request=change switch {
            "configuration"=>request with { ExpectedGeneration=Generation },"topology"=>request with { TopologyRevision=request.TopologyRevision+1 },
            "generation"=>request with { CacheRetirement=request.CacheRetirement! with { Generation=d.Generation+1 } },
            "unknown"=>request with { CacheRetirement=new("not-cached",d.Generation) },_=>request };
        if(change=="connected") { demo.GpuConnected=true;await demo.Runtime.PollAsync(); }
        var result=await pipe.SendAsync(pipe.Request(DeviceOperation.Configuration,configuration:request));Assert.False(result.Success);
        Assert.Contains(demo.Runtime.State().Inventory.Devices,x=>x.Device.Id==d.Device.Id);Assert.All(demo.Hubs,h=>Assert.Equal(0,h.Writes));
    }
    [Fact] public void CacheRetirementSmokeRequiresAnExclusiveEmbeddedMockEnvironment()
    {
        string[] common=["--devices-preview","--demo","--data-dir",Path.Combine(Path.GetTempPath(),"flowdeck-cache-options")];
        Assert.True(DeviceWorkspaceOptions.Parse([..common,"--smoke","--cache-retirement"]).CacheRetirement);
        Assert.Throws<ArgumentException>(()=>DeviceWorkspaceOptions.Parse([..common,"--cache-retirement"]));
        Assert.Throws<ArgumentException>(()=>DeviceWorkspaceOptions.Parse([..common,"--smoke","--cache-retirement","--registration-removal"]));
        Assert.Throws<ArgumentException>(()=>DeviceWorkspaceOptions.Parse([..common,"--smoke","--cache-retirement","--devices-pipe","FlowDeck.Devices.Demo."+Generation,"--server-pid","1"]));
    }
}
