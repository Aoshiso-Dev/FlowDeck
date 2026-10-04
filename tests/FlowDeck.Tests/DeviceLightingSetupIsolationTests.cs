using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceEndpointSetupTests
{
    [Fact] public async Task LightweightConnectionChecksRetainFreshnessAndExitGuards()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        var clock = new TestClock();
        await using var client = new DeviceWorkspaceClient(DeviceServicePipeClient.ForDemo(demo.PipeName, Environment.ProcessId), clock);
        Assert.False(client.Connected); Assert.False(client.HasFreshState);
        await client.ConnectAsync(); Assert.True(client.Connected); Assert.True(client.HasFreshState);
        clock.Advance(3); Assert.True(client.Connected); Assert.False(client.HasFreshState);
        await client.RefreshAsync(); Assert.True(client.HasFreshState);
        await client.ReleaseAsync(); Assert.False(client.Connected); Assert.False(client.HasFreshState);
    }

    private static DeviceEndpointSetupDraft Light(DeviceWorkspaceClient client, EndpointReference target)
    {
        var draft = new DeviceEndpointSetupDraft(client.Snapshot!, target);
        draft.Change(draft.Values with { Confirmed = true, LightingExit = new(LightingExitAction.FixedColor, new(255, 255, 255)) });
        return draft;
    }

    private async Task LightProgram(DeviceWorkspaceClient client, EndpointReference[] targets, string effect)
    {
        await client.RefreshAsync(true); var snapshot = client.Snapshot!; var draft = new DeviceLightingDraft();
        draft.Update(snapshot.State, snapshot.Configuration.Generation, snapshot.Configuration.Configuration.Lighting, 0, true);
        draft.Select(targets); draft.SetProgram(new(effect, [new(128, 64, 32)], Brightness: 50));
        await LightingTestDiagnostics.ApplyAsync(output, client, draft.Capture());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LightingSetupRetainsFixedAndAutomaticFansAndTheirExitObligations(bool remove)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var light = new EndpointReference(demo.DeviceIds[0], "led1");
        var fixedFan = new EndpointReference(light.DeviceId, "fan1");
        var autoFans = new[] { new EndpointReference(light.DeviceId, "fan2"), new EndpointReference(demo.DeviceIds[1], "fan1") };
        await Duty(client, fixedFan, 62);
        foreach (var fan in autoFans) await Automatic(demo, client, fan);
        var before = client.Snapshot!.State.Endpoints.Where(e => e.Owned).ToDictionary(e => e.Target);
        var edit = remove ? DeviceControlSetupRemoval.Capture(client.Snapshot!, light) : Light(client, light).Capture();
        var writes = demo.Hubs.Select(h => h.Writes).ToArray();
        Assert.Equal(Outcome.Confirmed, (await client.ApplySetupAsync(await client.PreviewSetupAsync(edit))).Outcome);
        Assert.Equal(writes, demo.Hubs.Select(h => h.Writes));
        foreach (var fan in autoFans.Append(fixedFan))
        {
            var after = client.Snapshot!.State.Endpoints.Single(e => e.Target == fan);
            Assert.True(after.Owned); Assert.Equal(before[fan].PolicyRevision, after.PolicyRevision);
        }
        Assert.Equal(62, client.Snapshot.State.Endpoints.Single(e => e.Target == fixedFan).ReportedDuty);
        demo.CpuTemperature = 88; await demo.Runtime.PollAsync(); await demo.Runtime.TickCoolingAsync(); await client.RefreshAsync(true);
        foreach (var fan in autoFans) Assert.True(client.Snapshot!.State.Endpoints.Single(e => e.Target == fan).ReportedDuty >= 90);
        await client.ReleaseAsync();
        foreach (var fan in autoFans.Append(fixedFan)) Assert.Equal(70, (await demo.Hubs[Array.IndexOf(demo.DeviceIds, fan.DeviceId)].ReadAsync()).Channels.Single(c => c.Id == fan.EndpointId).Duty);
    }

    [Theory] [InlineData("fixed")] [InlineData("breathing")]
    public async Task LightingSetupEndsOnlyItsOwnDeviceAndPreservesOtherLightingAndFans(string effect)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var lights = demo.DeviceIds.Select(id => new EndpointReference(id, "led1")).ToArray();
        var fan = new EndpointReference(lights[0].DeviceId, "fan1"); await Duty(client, fan, 62);
        foreach (var light in lights) await LightProgram(client, [light], effect);
        await demo.Runtime.TickLightingAsync(); await client.RefreshAsync(true);
        var other = client.Snapshot!.State.Endpoints.Single(e => e.Target == lights[1]);
        Assert.Equal(Outcome.Confirmed, (await client.ApplySetupAsync(await client.PreviewSetupAsync(Light(client, lights[0]).Capture()))).Outcome);
        Assert.True(client.Snapshot.State.Endpoints.Single(e => e.Target == fan).Owned);
        var ended = client.Snapshot.State.Endpoints.Single(e => e.Target == lights[0]);
        Assert.False(ended.Owned); Assert.Equal("off", ended.ReportedLighting!.EffectId); // previously saved exit
        await demo.Runtime.TickLightingAsync(); await client.RefreshAsync(true);
        var after = client.Snapshot.State.Endpoints.Single(e => e.Target == lights[1]);
        Assert.True(after.Owned); Assert.Equal(other.PolicyRevision, after.PolicyRevision);
        if (effect == "breathing") Assert.True(after.Lighting!.Running);
        await LightProgram(client, [lights[0]], "fixed");
        Assert.Equal(new RgbColor(64, 32, 16), demo.Hubs[0].LightEffects["led1"].Colors.Single());
        await client.ReleaseAsync();
        Assert.Equal(new RgbColor(255, 255, 255), demo.Hubs[0].LightEffects["led1"].Colors.Single());
    }

    [Fact] public async Task LightingSetupRejectsCrossDeviceSyncBeforeOutputThenAllowsSelectedDeviceStopAndSave()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var lights = demo.DeviceIds.Select(id => new EndpointReference(id, "led1")).ToArray();
        await LightProgram(client, lights, "breathing");
        await demo.Runtime.TickLightingAsync(); await client.RefreshAsync(true);
        var writes = demo.Hubs.Select(h => h.LightingNonFrameWrites).ToArray();
        var groups = client.Snapshot!.State.Endpoints.Where(e => lights.Contains(e.Target)).ToDictionary(e => e.Target, e => e.Lighting!.GroupId);
        var error = await Assert.ThrowsAsync<IOException>(() => client.PreviewSetupAsync(Light(client, lights[0]).Capture()));
        // Animation is allowed to keep writing while the rejected preview runs.
        // A restore/static write or a replaced group would be an unintended change.
        await Task.Delay(180); await client.RefreshAsync(true);
        Assert.Contains("照明同期中", error.Message); Assert.Equal(writes, demo.Hubs.Select(h => h.LightingNonFrameWrites));
        Assert.All(lights, light => Assert.Equal(groups[light], client.Snapshot!.State.Endpoints.Single(e => e.Target == light).Lighting!.GroupId));
        Assert.All(lights, light => Assert.True(client.Snapshot!.State.Endpoints.Single(e => e.Target == light).Owned));
        var snapshot = client.Snapshot!; var draft = new DeviceLightingDraft();
        draft.Update(snapshot.State, snapshot.Configuration.Generation, snapshot.Configuration.Configuration.Lighting, 0, true); draft.Select([lights[0]]);
        var stopped = await client.ApplyLightingAsync(draft.CaptureStop());
        Assert.True(stopped.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified, stopped.Message);
        Assert.Equal(Outcome.Confirmed, (await client.ApplySetupAsync(await client.PreviewSetupAsync(Light(client, lights[0]).Capture()))).Outcome);
        Assert.True(client.Snapshot!.State.Endpoints.Single(e => e.Target == lights[1]).Lighting!.Running);
    }

    [Fact] public async Task FailedLightingExitKeepsFanControlAndCanRetryWithoutDiscardingRecovery()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var light = new EndpointReference(demo.DeviceIds[0], "led1"); var fan = new EndpointReference(light.DeviceId, "fan1");
        await Duty(client, fan, 62); await LightProgram(client, [light], "fixed");
        var preview = await client.PreviewSetupAsync(Light(client, light).Capture());
        demo.Hubs[0].FailNextLightingWrite = true;
        await Assert.ThrowsAsync<IOException>(() => client.ApplySetupAsync(preview));
        Assert.True(client.Snapshot!.State.Endpoints.Single(e => e.Target == fan).Owned);
        Assert.Equal(62, client.Snapshot.State.Endpoints.Single(e => e.Target == fan).ReportedDuty);
        Assert.NotEmpty(new HubLightingRecoveryJournal(dir.Path, light.DeviceId).Pending);
        Assert.Equal(Outcome.Confirmed, (await client.ApplySetupAsync(await client.PreviewSetupAsync(Light(client, light).Capture()))).Outcome);
        Assert.Null(client.Snapshot!.State.LifecycleError); Assert.Empty(new HubLightingRecoveryJournal(dir.Path, light.DeviceId).Pending);
        Assert.True(client.Snapshot.State.Endpoints.Single(e => e.Target == fan).Owned);
        await client.ReleaseAsync(); Assert.Equal(70, (await demo.Hubs[0].ReadAsync()).Channels.Single(c => c.Id == "fan1").Duty);
    }

    [Theory] [InlineData("fan")] [InlineData("other-light")] [InlineData("other-binding")]
    public async Task LightingSetupRejectsChangesOutsideTheSelectedPort(string extra)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var target = new EndpointReference(demo.DeviceIds[0], "led1"); var edit = Light(client, target).Capture(); var config = edit.Configuration;
        config = extra switch {
            "fan" => config with { Cooling = config.Cooling with { Endpoints = config.Cooling.Endpoints.Select(e => e with { Safety = e.Safety with { Minimum = 40 } }).ToArray() } },
            "other-light" => config with { Lighting = config.Lighting with { Endpoints = config.Lighting.Endpoints!.Select(e => e.Target.DeviceId != target.DeviceId ? e with { Exit = new(LightingExitAction.FixedColor, new(1, 2, 3)) } : e).ToArray() } },
            _ => config with { Bindings = config.Bindings with { Devices = config.Bindings.Devices.Where(b => b.DeviceId == target.DeviceId).ToArray() } }
        };
        await Assert.ThrowsAsync<IOException>(() => client.PreviewSetupAsync(edit with { Configuration = config }));
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
    }

    [Fact] public void LightingSetupWireRequestRequiresExactTargetAndRejectsOtherOperationPayloads()
    {
        var request = new DeviceConfigurationRequest(DeviceConfigurationOperation.ValidateLightingSetup, Guid.NewGuid().ToString("N"), LightingSetupTarget: new("device", "led1"));
        Assert.True(DeviceConfigurationProtocol.Valid(request));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { LightingSetupTarget = null }));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { Operation = DeviceConfigurationOperation.Validate }));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { CoolingSetupTarget = new("device", "fan1") }));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { Data = [1] }));
    }
}
