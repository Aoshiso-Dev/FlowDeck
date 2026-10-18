using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceEndpointSetupTests
{
    [Fact]
    public async Task KnownLightAcceptsExplicitColorWithoutAnySetupSave()
    {
        using var dir = new ServiceTestDirectory();
        await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var before = SettingsTransfer.Hash(client.Snapshot!.Configuration.Configuration);
        var generation = client.Snapshot.Configuration.Generation;
        await client.PrepareFirstUseAsync();
        Assert.Equal(before, SettingsTransfer.Hash(client.Snapshot!.Configuration.Configuration));
        Assert.Equal(generation, client.Snapshot.Configuration.Generation);
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
        var target = new EndpointReference(demo.DeviceIds[0], "led1");
        var draft = new DeviceLightingDraft();
        draft.Update(client.Snapshot.State, client.Snapshot.Configuration.Generation,
            client.Snapshot.Configuration.Configuration.Lighting, client.Snapshot.ElapsedSeconds, true);
        draft.Select([target]); draft.SetProgram(new("fixed", [new(11, 22, 33)]));
        Assert.True(draft.CanApply);
        Assert.Equal(Outcome.WrittenUnverified, (await client.ApplyLightingAsync(draft.Capture())).Outcome);
        Assert.Equal(1, demo.Hubs[0].LightingWrites); Assert.Equal(0, demo.Hubs[1].LightingWrites);
        Assert.Equal(new[] { new RgbColor(11, 22, 33) }, demo.Hubs[0].LightEffects["led1"].Colors);
        Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
    }

    [Fact]
    public async Task ReadingReadinessDoesNotRecoverAnUncertainLightOrApplyItsSavedColor()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var target = new EndpointReference(demo.DeviceIds[0], "led1");
        var draft = new DeviceLightingDraft();
        draft.Update(client.Snapshot!.State, client.Snapshot.Configuration.Generation,
            client.Snapshot.Configuration.Configuration.Lighting, 0, true);
        draft.Select([target]); draft.SetProgram(new("fixed", [new(11, 22, 33)]));
        demo.Hubs[0].FailNextLightingWrite = true;
        Assert.Equal(Outcome.Unknown, (await client.ApplyLightingAsync(draft.Capture())).Outcome);
        var writes = demo.Hubs.Select(h => h.LightingWrites).ToArray();
        var saved = SettingsTransfer.Hash(client.Snapshot.Configuration.Configuration);
        Assert.Equal(1, (await client.PrepareFirstUseAsync()).PreparedLighting);
        var capability = client.Snapshot!.State.Inventory.Devices.Single(d => d.Device.Id == target.DeviceId)
            .Device.Endpoints.Single(e => e.Id == target.EndpointId).Capabilities.Single(c => c.Feature == DeviceFeature.FixedColor);
        Assert.Equal(DeviceAvailability.Failed, capability.Availability);
        Assert.Equal(writes, demo.Hubs.Select(h => h.LightingWrites).ToArray());
        Assert.Equal(saved, SettingsTransfer.Hash(client.Snapshot.Configuration.Configuration));
        Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
    }

    [Fact]
    public async Task SavingActualLightingSettingsDoesNotInventConfirmationOrChangeFanSafety()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var target = new EndpointReference(demo.DeviceIds[0], "led1"); var before = client.Snapshot!.Configuration.Configuration;
        var draft = new DeviceEndpointSetupDraft(client.Snapshot, target);
        Assert.False(draft.Values.Confirmed); Assert.Equal(24, draft.Values.LedCount); Assert.Null(draft.Problem);
        draft.ChangeInputs(draft.Values with { Name = "ケース照明", LightingExit = new(LightingExitAction.FixedColor, new(1, 2, 3)) });
        Assert.Equal(Outcome.Confirmed, (await client.ApplySetupAsync(await client.PreviewSetupAsync(draft.Capture()))).Outcome);
        var after = client.Snapshot!.Configuration.Configuration;
        var endpoint = after.Devices.Devices.Single(d => d.Id == target.DeviceId).Endpoints.Single(e => e.Id == target.EndpointId);
        Assert.Equal("ケース照明", endpoint.Name); Assert.Equal(24, endpoint.Setup!.LedCount); Assert.False(endpoint.Setup.LedCountConfirmed);
        var setting = after.Lighting.Endpoints!.Single(e => e.Target == target);
        Assert.False(setting.WiringConfirmed); Assert.Equal(new RgbColor(1, 2, 3), setting.Exit!.Color);
        Assert.Equal(SettingsTransfer.Hash(before.Cooling), SettingsTransfer.Hash(after.Cooling));
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
    }
}
