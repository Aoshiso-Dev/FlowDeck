using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceEndpointSetupTests
{
    [Theory] [InlineData("fan1")] [InlineData("led1")]
    public async Task SavedConfirmationIsReusedAfterServiceRestartWithTheSamePhysicalDevice(string endpoint)
    {
        using var dir = new ServiceTestDirectory();
        EndpointReference target; string epoch;
        await using (var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, persistent: true))
        {
            await using var client = demo.CreateClient(); await client.ConnectAsync();
            target = new(demo.DeviceIds[0], endpoint); epoch = client.Snapshot!.State.Inventory.Epoch;
            Assert.True(new DeviceEndpointSetupDraft(client.Snapshot, target).ReusesSavedConfirmation);
        }
        await using var restarted = await DemoDeviceWorkspace.CreateAsync(dir.Path, persistent: true);
        await using var next = restarted.CreateClient(); await next.ConnectAsync();
        Assert.NotEqual(epoch, next.Snapshot!.State.Inventory.Epoch);
        var draft = new DeviceEndpointSetupDraft(next.Snapshot, target);
        Assert.True(draft.ReusesSavedConfirmation); Assert.Null(draft.Problem);
        draft.ChangeInputs(draft.Values with { Name = "保存済みの端子" });
        Assert.True(draft.ReusesSavedConfirmation); Assert.NotNull(draft.Capture());
        Assert.All(restarted.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
    }

    [Fact] public async Task LightingExitEditsReuseSavedPhysicalConfirmationAndPreviewDoesNotWrite()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var draft = new DeviceEndpointSetupDraft(client.Snapshot!, new(demo.DeviceIds[0], "led1"));
        foreach (var exit in new[] { new LightingExitPolicy(LightingExitAction.Off), new LightingExitPolicy(LightingExitAction.FixedColor, new(4, 8, 16)) })
        {
            draft.ChangeInputs(draft.Values with { LightingExit = exit });
            Assert.True(draft.ReusesSavedConfirmation); Assert.Null(draft.Problem);
            await client.PreviewSetupAsync(draft.Capture());
        }
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
    }

    [Theory] [InlineData("minimum")] [InlineData("fallback")] [InlineData("exit")]
    [InlineData("exit-action")] [InlineData("protection")] [InlineData("stop")]
    public async Task ChangedCoolingSafetyNeedsConfirmationButRestoringSavedValuesReusesIt(string field)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var draft = new DeviceEndpointSetupDraft(client.Snapshot!, new(demo.DeviceIds[0], "fan1"));
        var saved = Contract.Copy(draft.Values);
        var safety = saved.Safety!;
        var changed = field switch {
            "minimum" => safety with { Minimum = 40 },
            "fallback" => safety with { Fallback = 90 },
            "exit" => safety with { Exit = 80 },
            "exit-action" => safety with { ExitAction = CoolingExitAction.ReleaseToDevice },
            "protection" => safety with { ManualProtection = [] },
            _ => safety with { Stop = new([new(safety.ManualProtection[0].Sensor, 40)], 50, 3) }
        };
        draft.ChangeInputs(saved with { Safety = changed });
        Assert.False(draft.Values.Confirmed); Assert.False(draft.ReusesSavedConfirmation);
        Assert.Throws<InvalidOperationException>(() => draft.Capture());
        draft.ChangeInputs(Contract.Copy(saved) with { Confirmed = false });
        Assert.True(draft.ReusesSavedConfirmation); Assert.Null(draft.Problem);
    }

    [Fact] public async Task LedCountChangesNeedConfirmationAndReturningToTheSavedCountReusesIt()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var draft = new DeviceEndpointSetupDraft(client.Snapshot!, new(demo.DeviceIds[0], "led1"));
        var saved = draft.Values.LedCount;
        draft.ChangeInputs(draft.Values with { LedCount = saved + 1 });
        Assert.False(draft.Values.Confirmed); Assert.Throws<InvalidOperationException>(() => draft.Capture());
        draft.ChangeInputs(draft.Values with { LedCount = saved });
        Assert.True(draft.ReusesSavedConfirmation); Assert.Null(draft.Problem);
    }

    [Theory] [InlineData("fingerprint")] [InlineData("identity")] [InlineData("definition")] [InlineData("missing")]
    public async Task AChangedOrMissingPhysicalBindingCannotReuseSavedConfirmation(string field)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var snapshot = client.Snapshot!;
        var target = new EndpointReference(demo.DeviceIds[0], "led1");
        var config = snapshot.Configuration.Configuration;
        var bindings = config.Bindings.Devices.Where(b => field != "missing" || b.DeviceId != target.DeviceId).Select(b => b.DeviceId != target.DeviceId ? b : field switch {
            "fingerprint" => b with { Fingerprint = new('f', 64) },
            "identity" => b with { ProviderIdentity = "another-device" },
            "definition" => b with { DefinitionId = "another-definition" },
            _ => b
        }).ToArray();
        snapshot = snapshot with { Configuration = snapshot.Configuration with { Configuration = config with { Bindings = config.Bindings with { Devices = bindings } } } };
        var draft = new DeviceEndpointSetupDraft(snapshot, target);
        draft.ChangeInputs(draft.Values with { Name = "名前を変更", LightingExit = new(LightingExitAction.Off), Confirmed = true });
        Assert.False(draft.Values.Confirmed); Assert.False(draft.ReusesSavedConfirmation);
        Assert.Throws<InvalidOperationException>(() => draft.Capture());
    }

    [Fact] public async Task NewPortStillRequiresExplicitConfirmationAndLightingColorDoesNotInvalidateIt()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var draft = new DeviceEndpointSetupDraft(client.Snapshot!, new(demo.DeviceIds[0], "led1"));
        draft.ChangeInputs(draft.Values with { LightingExit = new(LightingExitAction.FixedColor, new(10, 20, 30)), Confirmed = true });
        Assert.False(draft.Values.Confirmed); Assert.False(draft.ReusesSavedConfirmation);
        Assert.Throws<InvalidOperationException>(() => draft.Capture());
        draft.Change(draft.Values with { Confirmed = true });
        draft.ChangeInputs(draft.Values with { LightingExit = new(LightingExitAction.FixedColor, new(30, 20, 10)) });
        Assert.True(draft.Values.Confirmed); Assert.False(draft.ReusesSavedConfirmation); Assert.Null(draft.Problem);
    }

    [Fact] public async Task ConfigurationConflictCannotBeClearedByRestoringSavedInputs()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var snapshot = client.Snapshot!;
        var draft = new DeviceEndpointSetupDraft(snapshot, new(demo.DeviceIds[0], "fan1"));
        var saved = draft.Values;
        draft.ChangeInputs(saved with { Safety = saved.Safety! with { Minimum = 40 } });
        draft.Update(snapshot with { Configuration = snapshot.Configuration with { Generation = Guid.NewGuid().ToString("N") } });
        draft.ChangeInputs(saved);
        Assert.True(draft.Conflict); Assert.False(draft.ReusesSavedConfirmation);
        Assert.Throws<InvalidOperationException>(() => draft.Capture());
    }
}
