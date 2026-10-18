using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceEndpointSetupTests
{
    [Theory] [InlineData("fan1")] [InlineData("led1")]
    public async Task SavedActualSettingsRemainEditableAfterRestartWithoutAcknowledgement(string endpoint)
    {
        using var dir = new ServiceTestDirectory();
        EndpointReference target; string epoch;
        await using (var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, persistent: true))
        {
            await using var client = demo.CreateClient(); await client.ConnectAsync();
            target = new(demo.DeviceIds[0], endpoint); epoch = client.Snapshot!.State.Inventory.Epoch;
            Assert.Null(new DeviceEndpointSetupDraft(client.Snapshot, target).Problem);
        }
        await using var restarted = await DemoDeviceWorkspace.CreateAsync(dir.Path, persistent: true);
        await using var next = restarted.CreateClient(); await next.ConnectAsync();
        Assert.NotEqual(epoch, next.Snapshot!.State.Inventory.Epoch);
        var draft = new DeviceEndpointSetupDraft(next.Snapshot, target);
        Assert.Null(draft.Problem);
        draft.ChangeInputs(draft.Values with { Name = "保存済みの端子", Confirmed = false });
        Assert.NotNull(draft.Capture());
        Assert.All(restarted.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
    }

    [Fact] public async Task LightingExitEditsNeedNoAcknowledgementAndInternalPreviewDoesNotWrite()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var draft = new DeviceEndpointSetupDraft(client.Snapshot!, new(demo.DeviceIds[0], "led1"));
        foreach (var exit in new[] { new LightingExitPolicy(LightingExitAction.Off), new LightingExitPolicy(LightingExitAction.FixedColor, new(4, 8, 16)) })
        {
            draft.ChangeInputs(draft.Values with { LightingExit = exit, Confirmed = false });
            Assert.Null(draft.Problem);
            await client.PreviewSetupAsync(draft.Capture());
        }
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
    }

    [Theory] [InlineData("minimum")] [InlineData("fallback")] [InlineData("exit")]
    [InlineData("exit-action")] [InlineData("protection")] [InlineData("stop")]
    public async Task CoolingSettingsValidateActualValuesWithoutAcknowledgement(string field)
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
        draft.ChangeInputs(saved with { Safety = changed, Confirmed = false });
        if (field == "exit-action") Assert.Throws<InvalidOperationException>(() => draft.Capture());
        else Assert.NotNull(draft.Capture());
        draft.ChangeInputs(Contract.Copy(saved) with { Confirmed = false });
        Assert.Null(draft.Problem);
    }

    [Fact] public async Task ReportedLedCountBoundsRemainRequiredWithoutConfirmation()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var draft = new DeviceEndpointSetupDraft(client.Snapshot!, new(demo.DeviceIds[0], "led1"));
        var saved = draft.Values.LedCount;
        draft.ChangeInputs(draft.Values with { LedCount = saved + 1 });
        Assert.Throws<InvalidOperationException>(() => draft.Capture());
        draft.ChangeInputs(draft.Values with { LedCount = saved, Confirmed = false });
        Assert.Null(draft.Problem);
    }

    [Theory] [InlineData("fingerprint")] [InlineData("identity")] [InlineData("definition")] [InlineData("missing")]
    public async Task CurrentDeviceSettingsCanBeSavedWithoutRevokingOtherPortsMetadata(string field)
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
        draft.ChangeInputs(draft.Values with { Name = "名前を変更", LightingExit = new(LightingExitAction.Off), Confirmed = false });
        var next = draft.Capture().Configuration;
        Assert.Equal(SettingsTransfer.Hash(config.Cooling), SettingsTransfer.Hash(next.Cooling));
        Assert.Equal(snapshot.Configuration.Candidates.Single(b => b.DeviceId == target.DeviceId), next.Bindings.Devices.Single(b => b.DeviceId == target.DeviceId));
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
    }

    [Fact] public async Task NewKnownLightingSettingsNeedNoAcknowledgementAndDoNotPromoteLegacyFlags()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var draft = new DeviceEndpointSetupDraft(client.Snapshot!, new(demo.DeviceIds[0], "led1"));
        draft.ChangeInputs(draft.Values with { LightingExit = new(LightingExitAction.FixedColor, new(10, 20, 30)) });
        Assert.False(draft.Values.Confirmed); Assert.Null(draft.Problem);
        var saved = draft.Capture().Configuration;
        Assert.False(Assert.Single(saved.Lighting.Endpoints!).WiringConfirmed);
        Assert.False(Assert.Single(Assert.Single(saved.Devices.Devices).Endpoints).Setup!.LedCountConfirmed);
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
        Assert.True(draft.Conflict);
        Assert.Throws<InvalidOperationException>(() => draft.Capture());
    }

    [Fact] public async Task UnknownCoolingValuesAndLedCountAreNotGuessedFromDefaultsOrAllowedMinimum()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var snapshot = client.Snapshot!;
        var cooling = new DeviceEndpointSetupDraft(snapshot, new(demo.DeviceIds[0], "fan1"));
        Assert.Null(cooling.Values.Safety!.Minimum); Assert.Null(cooling.Values.Safety.Fallback); Assert.Null(cooling.Values.Safety.Exit);
        Assert.Throws<InvalidOperationException>(() => cooling.Capture());
        var target = new EndpointReference(demo.DeviceIds[0], "led1");
        snapshot = snapshot with { Configuration = snapshot.Configuration with { Setup = snapshot.Configuration.Setup!.Select(c => c.Target == target
            ? c with { Options = c.Options with { MinimumLeds = 1, MaximumLeds = 40, DetectedLeds = null } } : c).ToArray() } };
        var lighting = new DeviceEndpointSetupDraft(snapshot, target); Assert.Null(lighting.Values.LedCount);
        Assert.Throws<InvalidOperationException>(() => lighting.Capture());
        lighting.ChangeInputs(lighting.Values with { LedCount = 16 }); Assert.NotNull(lighting.Capture());
    }

    [Fact] public async Task AChangedPhysicalBindingPreservesSettingsButDoesNotTransferSavedStartupIntent()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var snapshot = client.Snapshot!;
        var target = new EndpointReference(demo.DeviceIds[0], "led1"); var config = snapshot.Configuration.Configuration;
        config = config with { Cooling = config.Cooling with { Endpoints = config.Cooling.Endpoints.Select(e => e with {
            SavedPolicy = new(FanMode.Manual, 60, SafetySnapshot: e.Safety), ResumeOnStart = true }).ToArray() } };
        snapshot = snapshot with { Configuration = snapshot.Configuration with { Configuration = config,
            Candidates = snapshot.Configuration.Candidates.Select(b => b.DeviceId == target.DeviceId ? b with { Fingerprint = new('f', 64) } : b).ToArray() } };
        var next = new DeviceEndpointSetupDraft(snapshot, target).Capture().Configuration;
        foreach (var before in config.Cooling.Endpoints)
        {
            var after = next.Cooling.Endpoints.Single(e => e.Target == before.Target);
            Assert.Equal(SettingsTransfer.Hash(before with { ResumeOnStart = after.ResumeOnStart }), SettingsTransfer.Hash(after));
            Assert.Equal(before.Target.DeviceId != target.DeviceId, after.ResumeOnStart);
        }
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task CurrentKnownLedExtentKeepsActualSettingsEditableWithoutSavingTheStaleValue(bool detected)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var snapshot = client.Snapshot!;
        var target = new EndpointReference(demo.DeviceIds[0], "led1"); var config = snapshot.Configuration.Configuration;
        config = config with { Devices = config.Devices with { Devices = config.Devices.Devices.Select(d => d.Id != target.DeviceId ? d : d with {
            Endpoints = d.Endpoints.Select(e => e.Id != target.EndpointId ? e : e with { Setup = e.Setup! with { LedCount = 7 } }).ToArray() }).ToArray() } };
        snapshot = snapshot with { Configuration = snapshot.Configuration with { Configuration = config,
            Setup = snapshot.Configuration.Setup!.Select(c => c.Target != target ? c : c with { Options = c.Options with {
                MinimumLeds = detected ? 1 : 8, MaximumLeds = detected ? 40 : 8, DetectedLeds = detected ? 8 : null } }).ToArray() } };
        var before = SettingsTransfer.Hash(snapshot.Configuration.Configuration);
        var draft = new DeviceEndpointSetupDraft(snapshot, target); Assert.Equal(8, draft.Values.LedCount); Assert.Null(draft.Problem);
        draft.ChangeInputs(draft.Values with { Name = "現在の照明", LightingExit = new(LightingExitAction.Off) });
        Assert.Equal(8, draft.Capture().Configuration.Devices.Devices.Single(d => d.Id == target.DeviceId).Endpoints.Single(e => e.Id == target.EndpointId).Setup!.LedCount);
        Assert.Equal(before, SettingsTransfer.Hash(snapshot.Configuration.Configuration));
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
    }
}
