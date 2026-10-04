using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceMonitoringTests
{
    private static readonly string Epoch = Guid.NewGuid().ToString("N"), Generation = Guid.NewGuid().ToString("N");
    private static DeviceServiceState State()
    {
        DeviceCapability Cap(DeviceFeature f) => new(f, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready);
        return new(new(Epoch, 1, [new(new("device", "provider", "Test device", DeviceKind.Motherboard, IdentityConfidence.ConnectionBound, "path", null, ["test"],
            [new("temp", "温度", EndpointKind.Sensor, [Cap(DeviceFeature.Temperature)]),
             new("rpm", "ポンプ", EndpointKind.Cooling, [Cap(DeviceFeature.Rpm)], Role: "pump"),
             new("rgb", "RGB", EndpointKind.Lighting, [Cap(DeviceFeature.FixedColor)])]), 1, true)], 1), [], false, null,
             [new(new("device", "temp"), DeviceFeature.Temperature, 52, 0, false), new(new("device", "rpm"), DeviceFeature.Rpm, 0, 0, false)]);
    }
    [Fact] public void ProposalsContainRealTemperatureAndRpmOnlyAndDoNotSelectAnythingAutomatically()
    {
        var draft = new DeviceMonitoringDraft(); draft.Update(new(2, []), new(2, []), State(), Generation, 0, true);
        Assert.Equal(2, draft.Candidates.Length); Assert.False(draft.Dirty); Assert.False(draft.CanRegister);
        var rpm = draft.Candidates.Single(c => c.Selection.Item.Measurement == DeviceFeature.Rpm);
        Assert.True(rpm.Available); Assert.Equal(0, rpm.Value); Assert.Equal("pump", rpm.Selection.Item.Role);
        draft.Select(rpm, true); Assert.True(draft.CanRegister); Assert.Single(draft.Capture().Items);
    }
    [Fact] public void SelectionSurvivesPollingButConfigurationEpochAndTopologyChangesCannotRebaseIt()
    {
        foreach (var change in new[] { "epoch", "configuration", "topology" })
        {
            var state = State(); var draft = new DeviceMonitoringDraft(); draft.Update(new(2, []), new(2, []), state, Generation, 0, true);
            draft.Select(draft.Candidates[0], true); var edit = draft.Capture();
            draft.Update(new(2, []), new(2, []), state, Generation, 0, true); Assert.Equal(edit, draft.Capture() with { Items = edit.Items });
            var next = state with { Inventory = state.Inventory with { Epoch = change == "epoch" ? Guid.NewGuid().ToString("N") : Epoch,
                TopologyRevision = change == "topology" ? 2 : 1 } };
            draft.Update(new(2, []), new(2, []), next, change == "configuration" ? Guid.NewGuid().ToString("N") : Generation, 0, true);
            Assert.True(draft.Conflict); Assert.False(draft.CanRegister); Assert.Equal(1, draft.SelectedCount); Assert.Throws<InvalidOperationException>(() => draft.Capture());
            draft.Reset(); Assert.False(draft.Dirty);
        }
    }
    [Fact] public void MissingStaleAndDuplicateSamplesStayUnavailableWhileSelectedRowsAreRetained()
    {
        var state = State(); var draft = new DeviceMonitoringDraft(); draft.Update(new(2, []), new(2, []), state, Generation, 0, true);
        var temp = draft.Candidates[0]; draft.Select(temp, true);
        draft.Update(new(2, []), new(2, []), state, Generation, 3, true); Assert.False(draft.CanRegister); Assert.All(draft.Candidates, c => Assert.False(c.Available));
        draft.Update(new(2, []), new(2, []), state with { Telemetry = [.. state.Telemetry!, state.Telemetry![0]] }, Generation, 0, true);
        Assert.False(draft.CanRegister);
        draft.Update(new(2, []), new(2, []), state with { Inventory = state.Inventory with { Devices = [] } }, Generation, 0, true);
        Assert.Equal(temp.Selection, Assert.Single(draft.Candidates).Selection); Assert.False(draft.CanRegister); Assert.Equal(1, draft.SelectedCount);
        draft.Select(draft.Candidates[0], false); Assert.False(draft.Dirty);
    }
    [Fact] public void HiddenSavedItemsAreNotProposedAgainAndDisplayNamesRespectUnicodeBoundaries()
    {
        var state = State(); var saved = new DeviceDashboardItem(new("device", "temp"), DeviceFeature.Temperature, "hidden", "temperature", false);
        var draft = new DeviceMonitoringDraft(); draft.Update(new(2, []), new(2, [saved]), state, Generation, 0, true);
        Assert.Equal(DeviceFeature.Rpm, Assert.Single(draft.Candidates).Selection.Item.Measurement);
        Assert.Equal(new string('a', 79), DeviceMonitoringDraft.DisplayName(new string('a', 79) + "😀"));
    }
    [Fact] public void SameNamedDevicesHaveStableDistinctLabelsIndependentOfEnumerationOrder()
    {
        var first = State().Inventory.Devices[0]; var second = first with { Device = first.Device with { Id = "second-device" } };
        var inventory = State().Inventory with { Devices = [first, second] };
        var names = inventory.Devices.ToDictionary(d => d.Device.Id, d => DeviceMonitoringDraft.DisplayDeviceName(d.Device, inventory));
        Assert.Equal(2, names.Values.Distinct().Count());
        foreach (var device in inventory.Devices.Reverse()) Assert.Equal(names[device.Device.Id],
            DeviceMonitoringDraft.DisplayDeviceName(device.Device, inventory with { Devices = inventory.Devices.Reverse().ToArray() }));
    }
    [Fact] public void RegistrationProtocolRejectsUnrelatedFieldsDuplicatesAndWriteFeaturesWithoutBreakingOlderMessages()
    {
        var item = new DeviceMonitoringSelection(new(new("device", "rpm"), DeviceFeature.Rpm, "fan", "fan"), 1);
        var request = new DeviceConfigurationRequest(DeviceConfigurationOperation.RegisterMonitoring, ExpectedGeneration: Generation, TopologyRevision: 1, Monitoring: [item]);
        Assert.True(DeviceConfigurationProtocol.Valid(request));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { Monitoring = [item, item] }));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { Monitoring = [item with { Generation = 0 }] }));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { Monitoring = [item with { Item = item.Item with { Measurement = DeviceFeature.FixedDuty } }] }));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { Dashboard = new(2, []) }));
        Assert.False(DeviceConfigurationProtocol.Valid(new DeviceConfigurationRequest(DeviceConfigurationOperation.Snapshot, Monitoring: [item])));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new DeviceConfigurationRequest(DeviceConfigurationOperation.Snapshot), Contract.Json));
        Assert.False(json.RootElement.TryGetProperty("monitoring", out _));
    }
    private static async Task<DeviceServiceState> Read(DeviceServicePipeClient client) => (await client.SendAsync(client.Request(DeviceOperation.State))).State!;
    private static DeviceMonitoringEdit Edit(ServiceConfigurationSnapshot config, DeviceServiceState state, params EndpointReference[] targets) =>
        new(state.Inventory.Epoch, config.Generation, state.Inventory.TopologyRevision, targets.Select(t =>
        {
            var device = state.Inventory.Devices.Single(d => d.Device.Id == t.DeviceId); var endpoint = device.Device.Endpoints.Single(e => e.Id == t.EndpointId);
            var feature = endpoint.Capabilities.Any(c => c.Feature == DeviceFeature.Temperature) ? DeviceFeature.Temperature : DeviceFeature.Rpm;
            return new DeviceMonitoringSelection(new(t, feature, endpoint.Name, feature == DeviceFeature.Temperature ? "temperature" : "fan"), device.Generation);
        }).ToArray());
    [Fact] public async Task EmptyInstallationRegistersSelectedReadingsWithoutCreatingAnyOutputPermission()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true);
        await using var client = DeviceServicePipeClient.ForDemo(demo.PipeName, Environment.ProcessId); await client.ConnectAsync();
        var config = await client.ReadConfigurationAsync(); var state = await Read(client); Assert.Empty(config.Configuration.Devices.Devices);
        var temperature = state.Telemetry!.First(t => t.Feature == DeviceFeature.Temperature).Target; var fan = new EndpointReference(demo.DeviceIds[1], "fan3");
        var result = await client.RegisterMonitoringAsync(Edit(config, state, temperature, fan)); Assert.Equal(Outcome.Confirmed, result.Outcome);
        var next = await client.ReadConfigurationAsync(); Assert.Equal(2, next.Configuration.Dashboard.Items.Length);
        Assert.Equal(2, next.Configuration.Devices.Devices.Length); Assert.Empty(next.Configuration.Bindings.Devices); Assert.Empty(next.Configuration.Cooling.Endpoints);
        Assert.Equal(SettingsTransfer.Hash(config.Configuration.Lighting), SettingsTransfer.Hash(next.Configuration.Lighting));
        state = await Read(client); var execution = state.Endpoints.Single(e => e.Target == fan);
        Assert.False((await client.SendAsync(client.Request(DeviceOperation.SetDuty, topology: state.Inventory.TopologyRevision,
            targets: [new(fan, execution.Generation, execution.PolicyRevision)], duty: 60))).Success);
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
        var persisted = DeviceConfigurationCodec.Read(DeviceConfigurationCodec.Store(Path.Combine(dir.Path, "configuration")).ReadActive()!.Files);
        Assert.Equal(SettingsTransfer.Hash(next.Configuration), SettingsTransfer.Hash(persisted));
    }
    [Fact] public async Task AddingMonitoringDoesNotReleaseRunningCoolingOrAlterExistingBindingsAndPolicies()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = DeviceServicePipeClient.ForDemo(demo.PipeName, Environment.ProcessId); await client.ConnectAsync();
        var state = await Read(client); var target = new EndpointReference(demo.DeviceIds[0], "fan1"); var row = state.Endpoints.Single(e => e.Target == target);
        Assert.True((await client.SendAsync(client.Request(DeviceOperation.SetDuty, topology: state.Inventory.TopologyRevision,
            targets: [new(target, row.Generation, row.PolicyRevision)], duty: 62))).Success);
        var config = await client.ReadConfigurationAsync(); var added = new EndpointReference(demo.DeviceIds[1], "fan2");
        await client.SaveDashboardAsync(config.Generation, new(2, config.Configuration.Dashboard.Items.Where(i => i.Target != added).ToArray()));
        config = await client.ReadConfigurationAsync(); state = await Read(client); var writes = demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).ToArray();
        await client.RegisterMonitoringAsync(Edit(config, state, added)); var after = await Read(client); var saved = await client.ReadConfigurationAsync();
        Assert.Equal(writes, demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).ToArray());
        Assert.Equal(62, after.Endpoints.Single(e => e.Target == target).RequestedDuty); Assert.True(after.Endpoints.Single(e => e.Target == target).Owned);
        Assert.Equal(SettingsTransfer.Hash(config.Configuration.Bindings), SettingsTransfer.Hash(saved.Configuration.Bindings));
        Assert.Equal(SettingsTransfer.Hash(config.Configuration.Cooling), SettingsTransfer.Hash(saved.Configuration.Cooling));
        Assert.Equal(SettingsTransfer.Hash(config.Configuration.Lighting), SettingsTransfer.Hash(saved.Configuration.Lighting));
    }
    [Theory] [InlineData("configuration")] [InlineData("topology")] [InlineData("device-generation")] [InlineData("epoch")]
    public async Task StaleSelectionsCannotRegisterAgainstNewState(string changed)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true);
        await using var client = DeviceServicePipeClient.ForDemo(demo.PipeName, Environment.ProcessId); await client.ConnectAsync();
        var config = await client.ReadConfigurationAsync(); var state = await Read(client); var edit = Edit(config, state, new EndpointReference(demo.DeviceIds[0], "fan1"));
        edit = changed switch { "configuration" => edit with { ConfigurationGeneration = Guid.NewGuid().ToString("N") },
            "topology" => edit with { TopologyRevision = edit.TopologyRevision + 1 }, "epoch" => edit with { Epoch = Guid.NewGuid().ToString("N") },
            _ => edit with { Items = [edit.Items[0] with { Generation = edit.Items[0].Generation + 1 }] } };
        await Assert.ThrowsAsync<IOException>(() => client.RegisterMonitoringAsync(edit));
        Assert.Equal(config.Generation, (await client.ReadConfigurationAsync()).Generation); Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
    }
    [Fact] public async Task DisconnectedSelectionIsRejectedAndAlreadyRegisteredItemsAreNotOverwritten()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true);
        await using var client = DeviceServicePipeClient.ForDemo(demo.PipeName, Environment.ProcessId); await client.ConnectAsync();
        var config = await client.ReadConfigurationAsync(); var edit = Edit(config, await Read(client), new EndpointReference(demo.DeviceIds[0], "fan1"));
        demo.Hubs[0].Disconnected = true; await demo.Runtime.PollAsync();
        await Assert.ThrowsAsync<IOException>(() => client.RegisterMonitoringAsync(edit)); Assert.Equal(config.Generation, (await client.ReadConfigurationAsync()).Generation);
        demo.Hubs[0].Disconnected = false; await demo.Runtime.PollAsync(); await demo.Runtime.PollAsync();
        await client.RegisterMonitoringAsync(Edit(config, await Read(client), new EndpointReference(demo.DeviceIds[0], "fan1")));
        config = await client.ReadConfigurationAsync(); var repeated = Edit(config, await Read(client), new EndpointReference(demo.DeviceIds[0], "fan1"));
        await Assert.ThrowsAsync<IOException>(() => client.RegisterMonitoringAsync(repeated));
        Assert.Single((await client.ReadConfigurationAsync()).Configuration.Dashboard.Items);
    }
}
