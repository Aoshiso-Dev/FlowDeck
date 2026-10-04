using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;
public class MemoryRgbStreamingTests
{
    private static MemoryRgbTarget[] Targets(RgbInventory inventory) => inventory.Devices.Select(d => new MemoryRgbTarget(d.ControlIdentity!,d.Zones[0].Configuration!)).ToArray();
    [Fact] public void FastPixelTransferKeepsLengthRgbOrderingAndKnownAddressBounds()
    {
        var request=EneMemoryRgbBus.PixelTransfer(0x71,0x12,0x34,0x56);
        Assert.Equal(new long[] {0x71,0,3,5,0x56341203,0,0,0,0},request);
        Assert.Throws<ArgumentOutOfRangeException>(() => EneMemoryRgbBus.PixelTransfer(0x50,1,2,3));
    }
    [Fact] public async Task DirectFramesDoNotRestartEffectsAndNormalStopSettlesAtLastColor()
    {
        var bus = new MemoryRgbControlTests.Bus(); var control = new EneMemoryRgbControl(() => bus);
        var before = await control.ReadAsync(); var lease = await control.BeginAsync(Targets(before));
        await control.FrameAsync(lease,new(30,70,110)); var count = bus.Writes.Count;
        await control.FrameAsync(lease,new(20,40,80));
        Assert.All(bus.Writes.Skip(count), w => Assert.InRange(w.Register,0x8100,0x8117));
        Assert.Equal(new byte[] {20,80,40},Enumerable.Range(0,3).Select(i => bus.Registers[(0x71,(ushort)(0x8100+i))]));
        Assert.Equal(0,bus.Registers[(0x71,0x8160)]);
        await control.EndAsync(lease,true);
        Assert.All((await control.ReadAsync()).Devices,d => Assert.Equal(new BoardLightSetting(1,20,40,80),d.Zones[0].Configuration));
        await Assert.ThrowsAsync<IOException>(() => control.FrameAsync(lease,new(1,2,3)));
    }
    [Fact] public async Task FailedPartialFrameRestoresOriginalPaletteAndNeverReplays()
    {
        var bus = new MemoryRgbControlTests.Bus(); bus.Registers[(0x71,0x8164)] = 90;
        var control = new EneMemoryRgbControl(() => bus); var before = await control.ReadAsync(); var lease = await control.BeginAsync(Targets(before));
        await control.FrameAsync(lease,new(10,20,30)); bus.FailWrite = bus.Attempts + 7;
        await Assert.ThrowsAsync<IOException>(() => control.FrameAsync(lease,new(40,50,60)));
        var count = bus.Attempts; await Assert.ThrowsAsync<IOException>(() => control.FrameAsync(lease,new(40,50,60))); Assert.Equal(count,bus.Attempts);
        await control.EndAsync(lease,true); // A failed stream cannot retain a partially written frame.
        Assert.Equal(before.Devices[0].Zones[0].Configuration,(await control.ReadAsync()).Devices[0].Zones[0].Configuration);
        Assert.Equal(0,bus.Registers[(0x73,0x8020)]);
    }
    [Fact] public async Task CrashJournalRecoversBothDimmsButDoesNotOverwriteAnExternalPalette()
    {
        var directory = Path.Combine(Path.GetTempPath(),"flowdeck-memory-stream-"+Guid.NewGuid().ToString("N"));
        try
        {
            var bus = new MemoryRgbControlTests.Bus(); var control = new EneMemoryRgbControl(() => bus,directory);
            var before = await control.ReadAsync(); var lease = await control.BeginAsync(Targets(before));
            await control.FrameAsync(lease,new(9,19,29));
            var recovered = await new EneMemoryRgbControl(() => bus,directory).ReadAsync();
            Assert.Equal(before.Devices[0].Zones[0].Configuration,recovered.Devices[0].Zones[0].Configuration);
            Assert.False(File.Exists(Path.Combine(directory,"memory-rgb-stream.json")));
            var next = new EneMemoryRgbControl(() => bus,directory); var token = await next.BeginAsync(Targets(recovered));
            await next.FrameAsync(token,new(9,19,29)); bus.Registers[(0x71,0x8105)] = 200;
            var writes = bus.Writes.Count; await Assert.ThrowsAsync<IOException>(() => new EneMemoryRgbControl(() => bus,directory).ReadAsync());
            Assert.DoesNotContain(bus.Writes.Skip(writes),w => w.Address == 0x71);
            Assert.True(File.Exists(Path.Combine(directory,"memory-rgb-stream.json")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory,true); }
    }
    [Fact] public async Task UnknownTargetsAndChangedSettingsFailBeforeWrites()
    {
        var bus = new MemoryRgbControlTests.Bus(); var control = new EneMemoryRgbControl(() => bus);
        await Assert.ThrowsAsync<ArgumentException>(() => control.BeginAsync([new("other",new(1,0,0,0))]));
        await Assert.ThrowsAsync<IOException>(() => control.BeginAsync([new(MemoryLighting.Identity(0x71),new(1,9,9,9))]));
        Assert.Empty(bus.Writes);
    }
    [Fact] public async Task ServiceExpiresIdleMemoryStreamIndependentlyOfGuiHeartbeatsAndFans()
    {
        var bus = new MemoryRgbControlTests.Bus(); var control = new EneMemoryRgbControl(() => bus);
        await using var f = new ServiceFixture(memoryRgb:control); await f.Initialize();
        Assert.Contains("memory-stream-v1",f.Hello.Capabilities!);
        var begin = await f.Call("memory-stream-begin",new MemoryRgbBegin(Targets(await control.ReadAsync())));
        var lease = begin.Payload.Deserialize<string>(Contract.Json)!;
        var frame = f.Request("memory-stream-frame",new MemoryRgbFrame(lease,new(11,22,33)));
        Assert.True((await f.Runtime.ExecuteAsync(frame)).Success); var count=bus.Writes.Count;
        Assert.True((await f.Runtime.ExecuteAsync(frame)).Success); Assert.Equal(count,bus.Writes.Count);
        Assert.False((await f.Call("memory-stream-frame",new MemoryRgbFrame("other",new(4,5,6)))).Success);
        f.LeaseClock.Advance(3); await f.Call<object?>("heartbeat",null); await f.Runtime.SweepAsync();
        Assert.True(f.Runtime.HasOwner); Assert.Equal(0,bus.Registers[(0x71,0x8020)]);
        Assert.False((await f.Call("memory-stream-frame",new MemoryRgbFrame(lease,new(1,2,3)))).Success);
        Assert.Equal(0,f.Hub.Writes); Assert.All(f.Drivers.Values,d => Assert.Equal(0,d.Writes));
    }
    [Fact] public async Task ServiceDisconnectRestoresSelectedMemoryWithoutTouchingOtherModule()
    {
        var bus = new MemoryRgbControlTests.Bus(); var control = new EneMemoryRgbControl(() => bus);
        await using var f = new ServiceFixture(memoryRgb:control); await f.Initialize();
        var begin = await f.Call("memory-stream-begin",new MemoryRgbBegin(Targets(await control.ReadAsync()).Take(1).ToArray()));
        var token = begin.Payload.Deserialize<string>(Contract.Json)!;
        await f.Call("memory-stream-frame",new MemoryRgbFrame(token,new(3,4,5)));
        await f.Runtime.DisconnectAsync(f.Hello.Session);
        Assert.Equal(0,bus.Registers[(0x71,0x8020)]); Assert.All(bus.Writes,w => Assert.Equal(0x71,w.Address));
    }
    [Fact] public void SynchronizedMemorySurvivesScenesGroupsAndBrightnessWithoutLeakingTargets()
    {
        var id=MemoryLighting.Identity(0x71); var other=MemoryLighting.Identity(0x73);
        var sync = new SceneSyncLight("hub",["led1"],"board",[2],"breathing",new(100,80,60),2,[id,other]);
        var scene = new LightingScene(Guid.NewGuid().ToString("N"),"全体",[],[],sync);
        Assert.Null(LightingScenes.Validate(Contract.Copy(scene)));
        var group = new LightingGroup(Guid.NewGuid().ToString("N"),"ファンとメモリ",[new("hub","led1")],[new(id,0)]);
        Assert.False(LightingGroups.Contains(group,sync));
        var selected=LightingGroups.Select(scene,group); Assert.Equal(new[] {id},selected.Sync!.Memory); Assert.Empty(selected.Sync.Zones); Assert.Equal("",selected.Sync.BoardIdentity);
        Assert.Single(LightingGroups.BoardTargets(selected));
        Assert.Equal(new RgbColor(50,40,30),LightingBrightness.Build(selected,50).Sync!.Color);
        Assert.NotNull(LightingScenes.Validate(scene with {Board=[new(id,0,new(1,0,0,0))]}));
        Assert.NotNull(LightingScenes.Validate(scene with {Sync=sync with {Memory=[id,id]}}));
        var fixedScene=selected with {Sync=selected.Sync with {Mode="fixed"}};
        Assert.True(LightingBrightness.SameOutput(fixedScene,new(scene.Id,scene.Name,[new("hub","led1",new("fixed",[sync.Color]))],[new(id,0,new(1,100,80,60))])));
    }
}
