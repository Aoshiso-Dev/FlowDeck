using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceEndpointSetupTests
{
    [Fact] public async Task DetectedLightingFirstUseSavesTheReportedCountWithoutWritingOrChangingFanControl()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var fan = new EndpointReference(demo.DeviceIds[0], "fan1"); var light = new EndpointReference(fan.DeviceId, "led1");
        Assert.Null(DeviceDetectedLightingSetup.Create(client.Snapshot!, fan));
        await client.ApplySetupAsync(await client.PreviewSetupAsync(Fan(client, fan.DeviceId).Capture()));
        Assert.Equal(Outcome.Confirmed, (await Duty(client, fan, 62)).Outcome);
        var writes = demo.Hubs[0].Writes; var before = client.Snapshot!.Configuration.Configuration;
        var detected = DeviceDetectedLightingSetup.Create(client.Snapshot, light);
        Assert.NotNull(detected); Assert.True(detected.Values.Confirmed); Assert.Equal(24, detected.Values.LedCount);
        Assert.Equal(LightingExitAction.Off, detected.Values.LightingExit!.Action);
        Assert.Equal(Outcome.Confirmed, (await client.ApplySetupAsync(await client.PreviewSetupAsync(detected.Capture()))).Outcome);
        var after = client.Snapshot!.Configuration.Configuration;
        Assert.Equal(SettingsTransfer.Hash(before.Cooling), SettingsTransfer.Hash(after.Cooling));
        Assert.True(client.Snapshot.State.Endpoints.Single(e => e.Target == fan).Owned);
        Assert.Equal(62, client.Snapshot.State.Endpoints.Single(e => e.Target == fan).RequestedDuty);
        Assert.Equal(writes, demo.Hubs[0].Writes); Assert.All(demo.Hubs, h => Assert.Equal(0, h.LightingWrites));
        Assert.Equal(24, after.Devices.Devices.Single(d => d.Id == light.DeviceId).Endpoints.Single(e => e.Id == light.EndpointId).Setup!.LedCount);
        Assert.Single(after.Lighting.Endpoints!);
        Assert.Null(DeviceDetectedLightingSetup.Create(client.Snapshot, light));
    }

    [Theory] [InlineData("unknown-count")] [InlineData("invalid-count")] [InlineData("fixed-only-exit")]
    [InlineData("stale")] [InlineData("disconnected")] [InlineData("releasing")] [InlineData("changing")]
    [InlineData("fault")] [InlineData("generation")] [InlineData("missing-identity")]
    [InlineData("changed-binding")] [InlineData("owned")] [InlineData("unsupported")]
    public async Task AutomaticFirstUseRejectsUnknownStaleChangedOrUnsafeCandidates(string condition)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var snapshot = client.Snapshot!;
        var target = new EndpointReference(demo.DeviceIds[0], "led1");
        Assert.NotNull(DeviceDetectedLightingSetup.Describe(snapshot, target));
        if (condition is "unknown-count" or "invalid-count" or "fixed-only-exit" or "generation")
            snapshot = snapshot with { Configuration = snapshot.Configuration with { Setup = snapshot.Configuration.Setup!.Select(c => c.Target != target ? c :
                c with { Generation = c.Generation + (condition == "generation" ? 1 : 0), Options = c.Options with {
                    DetectedLeds = condition == "unknown-count" ? null : condition == "invalid-count" ? 4097 : c.Options.DetectedLeds,
                    LightingExitActions = condition == "fixed-only-exit" ? [LightingExitAction.FixedColor] : c.Options.LightingExitActions } }).ToArray() } };
        if (condition == "stale") snapshot = snapshot with { ElapsedSeconds = 3 };
        if (condition == "disconnected") snapshot = snapshot with { Connected = false };
        if (condition == "releasing") snapshot = snapshot with { State = snapshot.State with { Releasing = true } };
        if (condition == "changing") snapshot = snapshot with { State = snapshot.State with { ConfigurationChanging = true } };
        if (condition == "fault") snapshot = snapshot with { State = snapshot.State with { LifecycleError = "復旧が必要" } };
        if (condition == "missing-identity") snapshot = snapshot with { Configuration = snapshot.Configuration with { Candidates = [] } };
        if (condition == "changed-binding")
        {
            var config = snapshot.Configuration.Configuration;
            snapshot = snapshot with { Configuration = snapshot.Configuration with { Configuration = config with { Bindings = config.Bindings with {
                Devices = [snapshot.Configuration.Candidates.Single(b => b.DeviceId == target.DeviceId) with { Fingerprint = new('f', 64) }] } } } };
        }
        if (condition == "owned") snapshot = snapshot with { State = snapshot.State with { Endpoints = snapshot.State.Endpoints.Select(e => e.Target == target ? e with { Owned = true } : e).ToArray() } };
        if (condition == "unsupported") snapshot = snapshot with { State = snapshot.State with { Inventory = snapshot.State.Inventory with {
            Devices = snapshot.State.Inventory.Devices.Select(d => d.Device.Id != target.DeviceId ? d : d with { Device = d.Device with {
                Endpoints = d.Device.Endpoints.Select(e => e.Id != target.EndpointId ? e : e with { Capabilities = e.Capabilities.Select(c => c with { Support = ImplementationSupport.Unsupported }).ToArray() }).ToArray() } }).ToArray() } } };
        Assert.Null(DeviceDetectedLightingSetup.Describe(snapshot, target)); Assert.Null(DeviceDetectedLightingSetup.Create(snapshot, target));
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
    }

    [Fact] public async Task FirstUsePrefersRestoringOriginalLightingWhenTheDriverSupportsIt()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var snapshot = client.Snapshot!;
        var target = new EndpointReference(demo.DeviceIds[0], "led1");
        snapshot = snapshot with { Configuration = snapshot.Configuration with { Setup = snapshot.Configuration.Setup!.Select(c => c.Target != target ? c :
            c with { Options = c.Options with { LightingExitActions = [LightingExitAction.FixedColor, LightingExitAction.Off, LightingExitAction.RestoreOriginal] } }).ToArray() } };
        Assert.Equal(LightingExitAction.RestoreOriginal, DeviceDetectedLightingSetup.Create(snapshot, target)!.Values.LightingExit!.Action);
    }

    [Fact] public async Task RemovedLightingSetupIsNotSilentlyReapprovedAsFirstUse()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var target = new EndpointReference(demo.DeviceIds[0], "led1");
        await client.ApplySetupAsync(await client.PreviewSetupAsync(DeviceControlSetupRemoval.Capture(client.Snapshot!, target)));
        Assert.Null(DeviceDetectedLightingSetup.Create(client.Snapshot!, target));
        Assert.False(new DeviceEndpointSetupDraft(client.Snapshot!, target).Values.Confirmed);
    }

    [Fact] public async Task SetupChangedAfterAutomaticDraftCreationIsRejectedBeforeSaving()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var target = new EndpointReference(demo.DeviceIds[0], "led1");
        var edit = DeviceDetectedLightingSetup.Create(client.Snapshot!, target)!.Capture();
        demo.Hubs[0].Disconnected = true; await demo.Runtime.PollAsync();
        await Assert.ThrowsAsync<IOException>(() => client.PreviewSetupAsync(edit));
        Assert.Empty(client.Snapshot!.Configuration.Configuration.Lighting.Endpoints ?? []);
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
    }
}
