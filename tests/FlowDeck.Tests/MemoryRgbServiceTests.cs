using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class MemoryRgbServiceTests
{
    [Fact] public async Task MemoryCommandsUseOwnerSessionAndDeduplicateWrites()
    {
        var bus = new MemoryRgbControlTests.Bus();
        await using var f = new ServiceFixture(memoryRgb: new EneMemoryRgbControl(() => bus)); await f.Initialize();
        Assert.Contains("memory-rgb-v1", f.Hello.Capabilities!);
        var read = await f.Call<object?>("memory-rgb-read", null); Assert.True(read.Success);
        var current = read.Payload.Deserialize<RgbInventory>(Contract.Json)!.Devices[0].Zones[0].Configuration!;
        var request = f.Request("memory-rgb-apply", new ServiceMemoryLightChange(MemoryLighting.Identity(0x71), [new(0, current, new(1, 8, 16, 24))]));
        Assert.False((await f.Runtime.ExecuteAsync(request with { Session = Guid.NewGuid().ToString("N") })).Success); Assert.Empty(bus.Writes);
        Assert.False((await f.Runtime.ExecuteAsync(request with { Epoch = Guid.NewGuid().ToString("N") })).Success); Assert.Empty(bus.Writes);
        Assert.True((await f.Runtime.ExecuteAsync(request)).Success); var count = bus.Writes.Count; Assert.True(count > 0);
        Assert.True((await f.Runtime.ExecuteAsync(request)).Success); Assert.Equal(count, bus.Writes.Count);
        Assert.Equal(0, f.Hub.Writes); Assert.All(f.Drivers.Values, d => Assert.Equal(0, d.Writes));
    }
    [Fact] public async Task MissingProviderDoesNotAdvertiseOrExecuteMemoryCommands()
    {
        await using var f = new ServiceFixture(); await f.Initialize();
        Assert.DoesNotContain("memory-rgb-v1", f.Hello.Capabilities ?? []);
        Assert.False((await f.Call<object?>("memory-rgb-read", null)).Success); Assert.Equal(0, f.Hub.Writes);
    }
}

