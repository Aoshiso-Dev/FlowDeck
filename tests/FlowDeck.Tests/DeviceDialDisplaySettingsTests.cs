using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceDialDisplaySettingsTests
{
    private static DeviceDialDisplayEdit Edit(DeviceWorkspaceSnapshot snapshot)
    {
        EndpointReference Target(DeviceKind kind)
        {
            var device = snapshot.Configuration.Configuration.Devices.Devices.Single(d => d.Kind == kind);
            return new(device.Id, device.Endpoints.Single(e => e.Kind == EndpointKind.Sensor).Id);
        }
        return new(snapshot.State.Inventory.Epoch, snapshot.Session!, snapshot.Configuration.Generation,
            snapshot.State.Inventory.TopologyRevision, new(Target(DeviceKind.Processor), Target(DeviceKind.Graphics)));
    }
    [Fact]
    public async Task TemperatureSelectionsPersistOnlyPreferencesWithoutWritingHardware()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); Assert.True(client.SupportsDialDisplay);
        var snapshot = client.Snapshot!; var edit = Edit(snapshot);
        var before = DeviceConfigurationCodec.Write(snapshot.Configuration.Configuration);
        var reply = await client.SaveDialDisplayAsync(edit);
        Assert.Equal(Outcome.Confirmed, reply.Outcome); Assert.Equal(new[] { "preferences.json" }, reply.ChangedComponents);
        var saved = client.Snapshot!.Configuration.Configuration;
        Assert.Equal(edit.Settings, saved.Preferences.DialDisplay);
        var after = DeviceConfigurationCodec.Write(saved);
        foreach (var name in before.Keys.Where(n => n != "preferences.json")) Assert.Equal(before[name], after[name]);
        var persisted = DeviceConfigurationCodec.Store(Path.Combine(directory.Path, "configuration")).ReadActive()!;
        Assert.Equal(edit.Settings, DeviceConfigurationCodec.Read(persisted.Files).Preferences.DialDisplay);
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
        await Assert.ThrowsAsync<IOException>(() => client.SaveDialDisplayAsync(edit));
    }
    [Theory]
    [InlineData("epoch")] [InlineData("session")] [InlineData("generation")] [InlineData("topology")]
    [InlineData("wrong-kind")] [InlineData("missing")]
    public async Task StaleOrInvalidSelectionsCannotChangePreferencesOrHardware(string change)
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var snapshot = client.Snapshot!;
        var edit = Edit(snapshot);
        edit = change switch
        {
            "epoch" => edit with { Epoch = new('b', 32) }, "session" => edit with { Session = new('b', 32) },
            "generation" => edit with { Generation = new('b', 32) }, "topology" => edit with { Topology = edit.Topology + 1 },
            "wrong-kind" => edit with { Settings = new(edit.Settings.Gpu, edit.Settings.Cpu) },
            _ => edit with { Settings = new(new("missing", "temperature"), edit.Settings.Gpu) }
        };
        await Assert.ThrowsAsync<IOException>(() => client.SaveDialDisplayAsync(edit));
        Assert.Equal(snapshot.Configuration.Generation, client.Snapshot!.Configuration.Generation);
        Assert.Equal(snapshot.Configuration.Configuration.Preferences.DialDisplay, client.Snapshot.Configuration.Configuration.Preferences.DialDisplay);
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
    }
}
