using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceNameSettingsTests
{
    private static DeviceWorkspaceSnapshot Snapshot()
    {
        var package = ServiceRuntimeBootstrap.MonitoringConfiguration(new string('a', 32));
        package = package with { Devices = new(2, [new("hub-a", "Hub", "demo", DeviceKind.Controller, [])]) };
        return new(new(new("epoch", 1, [], 1), [], false, null), new("generation", package, []), 0, true, null, Session: "session");
    }
    [Fact] public void DraftKeepsJapaneseInputAndIdentityAcrossTelemetryAndAllowsDisconnectedRegisteredDevice()
    {
        var s = Snapshot(); var draft = new DeviceNameDraft("hub-a"); draft.Update(s);
        draft.Name = "前面ファン用ハブ"; draft.Update(s with { ElapsedSeconds = 10 });
        Assert.True(draft.CanSave); Assert.False(draft.Conflict);
        Assert.Equal(new DeviceNameEdit("epoch", "session", "generation", new("hub-a", "前面ファン用ハブ")), draft.Capture());
        Assert.Equal("Hub", s.Configuration.Configuration.Devices.Devices[0].Name);
    }
    [Theory]
    [InlineData("generation")]
    [InlineData("epoch")]
    [InlineData("session")]
    [InlineData("removed")]
    public void DraftConflictsRemainStickyAndKeepTextUntilExplicitReload(string change)
    {
        var s = Snapshot(); var draft = new DeviceNameDraft("hub-a"); draft.Update(s); draft.Name = "入力途中";
        var next = change switch {
            "generation" => s with { Configuration = s.Configuration with { Generation = "changed" } },
            "epoch" => s with { State = s.State with { Inventory = s.State.Inventory with { Epoch = "changed" } } },
            "session" => s with { Session = "new-session" },
            _ => s with { Configuration = s.Configuration with { Configuration = s.Configuration.Configuration with { Devices = new(2, []) } } }
        };
        draft.Update(next); Assert.True(draft.Conflict); Assert.False(draft.CanSave); Assert.Equal("入力途中", draft.Name);
        Assert.Throws<InvalidOperationException>(() => draft.Capture());
        draft.Name = "Hub"; draft.Update(s); Assert.True(draft.Conflict);
        draft.Reset(s); Assert.False(draft.Conflict); Assert.Equal("Hub", draft.Name); Assert.False(draft.Dirty);
    }
    [Theory]
    [InlineData("disconnected")]
    [InlineData("exiting")]
    [InlineData("releasing")]
    [InlineData("configuring")]
    [InlineData("no-session")]
    public void UnavailableServiceRetainsInputButCannotSave(string condition)
    {
        var s = Snapshot(); var draft = new DeviceNameDraft("hub-a"); draft.Update(s); draft.Name = "保持する名前";
        var next = condition switch {
            "disconnected" => s with { Connected = false }, "exiting" => s with { ExitAttempted = true },
            "releasing" => s with { State = s.State with { Releasing = true } },
            "configuring" => s with { State = s.State with { ConfigurationChanging = true } }, _ => s with { Session = null }
        };
        draft.Update(next); Assert.False(draft.CanSave); Assert.Equal("保持する名前", draft.Name);
        Assert.Throws<InvalidOperationException>(() => draft.Capture());
    }
    [Fact] public void NameProtocolAllowsDisplayTextOnlyAndRejectsUnrelatedRequestFields()
    {
        var request = new DeviceConfigurationRequest(DeviceConfigurationOperation.SaveDeviceName,
            ExpectedGeneration: new string('a', 32), DeviceName: new("hub-a", "前面ハブ"));
        Assert.True(DeviceConfigurationProtocol.Valid(request));
        Assert.True(DeviceConfigurationProtocol.Valid(request with { DeviceName = new("hub-a", new string('名', 160)) }));
        foreach (var name in new[] { null, "", "  ", "改行\n", new string('名', 161) })
            Assert.False(DeviceConfigurationProtocol.Valid(request with { DeviceName = new("hub-a", name!) }));
        foreach (var invalid in new[] { request with { DeviceName = null }, request with { DeviceName = new("", "name") },
            request with { TransferId = new string('b', 32) }, request with { ExpectedGeneration = null }, request with { TopologyRevision = 1 },
            request with { Dashboard = new(2, []) }, request with { Monitoring = [] }, request with { Data = [1] },
            request with { TotalBytes = 1 }, request with { Offset = 0 }, request with { Sha256 = new string('a', 64) }, request with { OpenRgbPort = 6742 } })
            Assert.False(DeviceConfigurationProtocol.Valid(invalid));
        foreach (var operation in Enum.GetValues<DeviceConfigurationOperation>().Where(o => o != DeviceConfigurationOperation.SaveDeviceName))
            Assert.False(DeviceConfigurationProtocol.Valid(request with { Operation = operation }));
    }
    [Fact] public async Task HandlerWithoutNameEditingDoesNotAdvertiseTheFeature()
    {
        await using var runtime = new DeviceControlRuntime([], [ServiceFixture.Sid], new TestLog(), demo: true);
        Assert.DoesNotContain(DeviceNameChange.Feature, runtime.OpenSession(ServiceFixture.Sid).Features);
    }
    [Fact] public async Task PipeRenamePreservesRunningCoolingLightingBindingsAndAllOtherSettings()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); Assert.True(client.SupportsDeviceNames);
        var s = client.Snapshot!; var target = new EndpointReference(demo.DeviceIds[0], "fan1"); var fan = new DeviceCoolingDraft(target);
        fan.Update(s.State, s.Configuration.Generation, s.Configuration.Configuration.Cooling.Endpoints.Single(e => e.Target == target), s.ElapsedSeconds, s.Connected);
        fan.SetDuty(65); await client.ApplyCoolingAsync(fan.Capture()); s = client.Snapshot!;
        var light = new DeviceLightingDraft(); light.Update(s.State, s.Configuration.Generation, s.Configuration.Configuration.Lighting, s.ElapsedSeconds, s.Connected);
        light.Select(demo.DeviceIds.Select(id => new EndpointReference(id, "led1"))); light.SetProgram(new("breathing", [new(0, 0, 220)]));
        await client.ApplyLightingAsync(light.Capture()); s = client.Snapshot!;
        var draft = new DeviceNameDraft(demo.DeviceIds[0]); draft.Update(s); draft.Name = "前面のハブ"; var edit = draft.Capture();
        var before = DeviceConfigurationCodec.Write(s.Configuration.Configuration);
        var reply = await client.SaveDeviceNameAsync(edit); var after = client.Snapshot!;
        Assert.Equal(Outcome.Confirmed, reply.Outcome); Assert.Equal(new[] { "devices.json" }, reply.ChangedComponents);
        Assert.Equal(draft.Name, after.Configuration.Configuration.Devices.Devices.Single(d => d.Id == draft.DeviceId).Name);
        Assert.Equal(demo.DeviceIds, after.Configuration.Configuration.Devices.Devices.Where(d => demo.DeviceIds.Contains(d.Id)).Select(d => d.Id));
        var originalName = s.Configuration.Configuration.Devices.Devices.Single(d => d.Id == draft.DeviceId).Name;
        var normalized = after.Configuration.Configuration with { Devices = after.Configuration.Configuration.Devices with {
            Devices = after.Configuration.Configuration.Devices.Devices.Select(d => d.Id == draft.DeviceId ? d with { Name = originalName } : d).ToArray() } };
        foreach (var file in before.Keys) Assert.Equal(before[file], DeviceConfigurationCodec.Write(normalized)[file]);
        var original = s.Configuration.Configuration.Devices.Devices.Single(d => d.Id == draft.DeviceId);
        Assert.Equal(original.Endpoints, after.Configuration.Configuration.Devices.Devices.Single(d => d.Id == draft.DeviceId).Endpoints);
        foreach (var owned in s.State.Endpoints.Where(e => e.Owned))
        {
            var current = after.State.Endpoints.Single(e => e.Target == owned.Target);
            Assert.True(current.Owned); Assert.Equal(owned.PolicyRevision, current.PolicyRevision);
            if (owned.Lighting is { Running: true }) { Assert.True(current.Lighting!.Running); Assert.Equal(owned.Lighting.GroupId, current.Lighting.GroupId); }
        }
        Assert.Equal(1, demo.Hubs[0].Writes); Assert.Equal(0, demo.Hubs[1].Writes);
        await Assert.ThrowsAsync<IOException>(() => client.SaveDeviceNameAsync(edit));
        foreach (var invalid in new[] { edit with { Generation = after.Configuration.Generation, Session = "stale" },
            edit with { Generation = after.Configuration.Generation, Epoch = "stale" } })
            await Assert.ThrowsAsync<IOException>(() => client.SaveDeviceNameAsync(invalid));
        Assert.True((await client.ReleaseAsync()).Success);
    }
    [Fact] public async Task DisconnectedDeviceCanBeRenamedToTheSameLabelWithoutMergingIdentities()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var before = client.Snapshot!;
        demo.Hubs[0].Disconnected = true; await demo.Runtime.PollAsync(); await client.RefreshAsync(); var s = client.Snapshot!;
        Assert.False(s.State.Inventory.Devices.Single(d => d.Device.Id == demo.DeviceIds[0]).Connected);
        var otherName = s.Configuration.Configuration.Devices.Devices.Single(d => d.Id == demo.DeviceIds[1]).Name;
        await client.SaveDeviceNameAsync(new(s.State.Inventory.Epoch, s.Session!, s.Configuration.Generation, new(demo.DeviceIds[0], otherName)));
        var after = client.Snapshot!; Assert.Equal(2, after.Configuration.Configuration.Devices.Devices.Count(d => d.Name == otherName));
        Assert.Equal(before.Configuration.Configuration.Devices.Devices.Length, after.Configuration.Configuration.Devices.Devices.Length);
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
        Assert.True((await client.ReleaseAsync()).Success);
    }
}

public partial class DeviceConfigurationAuthorityTests
{
    private static DeviceConfigurationRequest NameRequest(Rig r, string name = "前面ハブ") => new(DeviceConfigurationOperation.SaveDeviceName,
        ExpectedGeneration: r.Startup.Generation.Id, DeviceName: new(r.Startup.Configuration.Devices.Devices[0].Id, name));
    [Fact] public async Task NameSaveDoesNotReconfigureDriversOrRepeatOnReplayAndUnchangedName()
    {
        await using var r = await Rig.Create(); Assert.Contains(DeviceNameChange.Feature, r.Hello.Features);
        Assert.True((await r.Startup.Runtime.ExecuteAsync(r.Duty(80))).Success);
        r.Driver.AfterConfigure = _ => throw new InvalidOperationException("Name editing must not configure hardware");
        var old = r.Startup.Generation; var request = r.Request(NameRequest(r));
        Assert.True((await r.Startup.Runtime.ExecuteAsync(request)).Success); var saved = r.Startup.Generation; Assert.NotEqual(old, saved);
        Assert.True((await r.Startup.Runtime.ExecuteAsync(request)).Success); Assert.Equal(saved, r.Startup.Generation);
        Assert.True((await r.Send(NameRequest(r))).Success); Assert.Equal(saved, r.Startup.Generation);
        Assert.False((await r.Send(NameRequest(r, "古い変更") with { ExpectedGeneration = old.Id })).Success);
        Assert.Equal(1, r.Hub.Writes); Assert.True(r.Startup.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "fan1").Owned);
        var loaded = DeviceConfigurationCodec.Read(r.Store.ReadActive()!.Files);
        Assert.Equal("前面ハブ", loaded.Devices.Devices[0].Name);
    }
    [Theory]
    [InlineData("before-switch")]
    [InlineData("file:devices.json")]
    public async Task NameSaveFailureLeavesTheOldGenerationAndRunningCooling(string phase)
    {
        await using var r = await Rig.Create(); await r.Startup.Runtime.ExecuteAsync(r.Duty(80)); var before = r.Startup.Generation;
        r.Store.Checkpoint = at => { if (at == phase) throw new IOException("injected name save failure"); };
        Assert.False((await r.Send(NameRequest(r))).Success);
        Assert.Equal(before, r.Startup.Generation); Assert.Equal(before, r.Store.ReadActive()!.Pointer);
        Assert.Equal(1, r.Hub.Writes); Assert.True(r.Startup.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "fan1").Owned);
        r.Store.Checkpoint = null; Assert.True((await r.Send(NameRequest(r))).Success);
    }
    [Fact] public async Task NamingCannotRegisterUnknownHardwareOrUseDeviceNamesAsIdentity()
    {
        await using var r = await Rig.Create(); var before = r.Startup.Generation; var request = NameRequest(r);
        foreach (var id in new[] { "unknown", r.Startup.Configuration.Devices.Devices[0].Name })
            Assert.False((await r.Send(request with { DeviceName = new(id, "new name") })).Success);
        Assert.Equal(before, r.Startup.Generation); Assert.Equal(0, r.Hub.Writes);
    }
}
