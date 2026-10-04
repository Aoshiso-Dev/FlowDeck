using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class LightingNameTests
{
    [Fact] public void NamesBindToExactIndividualDevicesAndPortsWithoutMutatingTheInput()
    {
        var address = new LightAddress(Hub: new("one", "led1"));
        var original = new LightingNameCollection(Names: []);
        var saved = LightingNames.Set(original, address, "  前面ファン  ");
        Assert.Empty(original.Names!); Assert.Equal("前面ファン", LightingNames.Find(saved, address));
        Assert.Null(LightingNames.Find(saved, new(Hub: new("two", "led1"))));
        Assert.Null(LightingNames.Find(saved, new(Hub: new("one", "led2"))));
        Assert.Null(LightingNames.Find(saved, new(Board: new("one", 1))));
        Assert.Empty(LightingNames.Set(saved, address, " ").Names!);
    }
    [Fact] public void IdentificationAddressesCannotMeanAllDevicesOrMoreThanOneTarget()
    {
        Assert.False(new LightAddress().Valid);
        Assert.False(new LightAddress(new("hub", "led1"), new("board", 2)).Valid);
        Assert.False(new LightAddress(Hub: new("hub", "all")).Valid);
        Assert.False(new LightAddress(Hub: new("", "led1")).Valid);
        Assert.False(new LightAddress(Board: new("board", 8)).Valid);
        Assert.False(new LightAddress(Board: new("board\n", 2)).Valid);
        Assert.True(new LightAddress(Board: new("usb-device-path", 7)).Valid);
    }
    [Fact] public void MalformedVersionsNamesDuplicateAddressesAndExcessiveCollectionsAreRejected()
    {
        var item = new LightingName(new(Hub: new("hub", "led1")), "前面");
        Assert.NotNull(LightingNames.Validate(new(Names: [item, item with { Name = "別名" }])));
        Assert.NotNull(LightingNames.Validate(new(Version: 2, Names: [])));
        Assert.NotNull(LightingNames.Validate(new(Names: [item with { Name = "a\nb" }])));
        Assert.NotNull(LightingNames.Validate(new(Names: [item with { Name = new string('x', 41) }])));
        Assert.NotNull(LightingNames.Validate(new(Names: [item with { Name = " " }])));
        Assert.NotNull(LightingNames.Validate(new(Names: [null!])));
        var many = Enumerable.Range(0,257).Select(i => new LightingName(new(Hub: new(i.ToString(), "led1")), "同じ表示名")).ToArray();
        Assert.NotNull(LightingNames.Validate(new(Names: many)));
        Assert.Null(LightingNames.Validate(new(Names: many.Take(256).ToArray())));
    }
    [Fact] public async Task AliasStoreRoundTripsJapaneseAndDisconnectedTargetsWithAtomicBackup()
    {
        var folder = Path.Combine(Path.GetTempPath(), "FlowDeck-names-" + Guid.NewGuid().ToString("N"));
        var store = new LightingNameStore(folder); var address = new LightAddress(Board: new("disconnected-board", 5));
        var original = LightingNames.Set(new(Names: []), address, "背面・LED"); await store.SaveAsync(original);
        var before = await File.ReadAllTextAsync(store.PathName);
        var loaded = await new LightingNameStore(folder).LoadAsync(); Assert.Equal("背面・LED", LightingNames.Find(loaded, address));
        await store.SaveAsync(LightingNames.Set(loaded, address, ""));
        Assert.Equal(before, await File.ReadAllTextAsync(store.PathName + ".backup")); Assert.Empty((await store.LoadAsync()).Names!);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new(Names: [new(address, "")] )));
        Assert.Empty((await store.LoadAsync()).Names!);
    }
    [Fact] public async Task CorruptAliasFileIsRejectedWithoutSilentlyReplacingIt()
    {
        var folder = Path.Combine(Path.GetTempPath(), "FlowDeck-names-invalid-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var store = new LightingNameStore(folder); await File.WriteAllTextAsync(store.PathName, "{broken");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => store.LoadAsync());
        Assert.Equal("{broken", await File.ReadAllTextAsync(store.PathName));
        await File.WriteAllTextAsync(store.PathName, "{\"version\":999,\"names\":[]}");
        await Assert.ThrowsAsync<IOException>(() => store.LoadAsync());
    }
}
