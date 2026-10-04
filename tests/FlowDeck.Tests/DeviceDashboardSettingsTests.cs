using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceDashboardSettingsTests
{
    private static readonly EndpointReference A = new("a", "fan1"), B = new("b", "fan1");
    private static readonly DeviceDashboardConfiguration Saved = new(2, [new(A, DeviceFeature.Rpm, "FAN1", "fan"), new(B, DeviceFeature.Rpm, "FAN1", "pump")]);
    private static readonly DeviceRegistrations Registered = new(2, new[] { A, B }.Select(t => new RegisteredDevice(t.DeviceId, "Hub", "demo",
        DeviceKind.Controller, [new(t.EndpointId, "FAN1", EndpointKind.Cooling)])).ToArray());
    private static DeviceServiceState State(string epoch = "epoch") => new(new(epoch, 1, [], 1), [], false, null);
    private static DeviceDashboardDraft Draft()
    { var draft = new DeviceDashboardDraft(); draft.Update(Registered, Saved, State(), "generation", true); return draft; }
    [Fact] public void UnsentDisplayNameVisibilityRoleAndOrderSurviveTelemetryWithoutChangingIdentity()
    {
        var draft = Draft(); var next = Saved.Items[1] with { Name = "冷却ポンプ", Visible = false };
        draft.Set(next); draft.Move(B, DeviceFeature.Rpm, -1); draft.Update(Registered, Saved, State(), "generation", true);
        Assert.True(draft.CanSave); Assert.Equal(next, draft.Items[0]); Assert.Equal(A, draft.Items[1].Target);
        var edit = draft.Capture(); Assert.Equal("generation", edit.Generation); Assert.Equal("epoch", edit.Epoch);
        Assert.Equal("FAN1", Saved.Items[1].Name); draft.Reset(); Assert.False(draft.Dirty); Assert.Equal(Saved.Items, draft.Items);
    }
    [Theory]
    [InlineData("epoch", "changed")]
    [InlineData("restarted", "generation")]
    public void ChangedConfigurationOrServerRetainsDraftButRefusesToRebase(string epoch, string generation)
    {
        var draft = Draft(); draft.Set(Saved.Items[0] with { Name = "入力中" });
        draft.Update(Registered, Saved, State(epoch), generation, true);
        Assert.True(draft.Conflict); Assert.False(draft.CanSave); Assert.Equal("入力中", draft.Items[0].Name);
        Assert.Throws<InvalidOperationException>(() => draft.Capture());
    }
    [Fact] public void InvalidNamesAndDisconnectedServiceCannotSaveButDraftRemainsEditable()
    {
        var draft = Draft(); draft.Set(Saved.Items[0] with { Name = "" }); Assert.False(draft.CanSave);
        draft.Set(Saved.Items[0] with { Name = "前面" }); Assert.True(draft.CanSave);
        draft.Update(Registered, Saved, State(), "generation", false); Assert.False(draft.CanSave); Assert.Equal("前面", draft.Items[0].Name);
    }
    [Fact] public void ProtocolRejectsDuplicateReferencesAndCannotSmuggleDashboardIntoAnUpload()
    {
        var request = new DeviceConfigurationRequest(DeviceConfigurationOperation.SaveDashboard, ExpectedGeneration: new string('a', 32), Dashboard: Saved);
        Assert.True(DeviceConfigurationProtocol.Valid(request));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { Dashboard = new(2, [Saved.Items[0], Saved.Items[0]]) }));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { Operation = DeviceConfigurationOperation.Snapshot, ExpectedGeneration = null }));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { TopologyRevision = 1 }));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { Dashboard = new(2, [Saved.Items[0] with { Measurement = DeviceFeature.Temperature }]) }));
    }
    [Fact] public async Task PipeSavePreservesRunningFanAndSynchronizedLighting()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var s = client.Snapshot!;
        var target = new EndpointReference(demo.DeviceIds[0], "fan1"); var fan = new DeviceCoolingDraft(target);
        fan.Update(s.State, s.Configuration.Generation, s.Configuration.Configuration.Cooling.Endpoints.Single(e => e.Target == target), s.ElapsedSeconds, s.Connected);
        fan.SetDuty(65); await client.ApplyCoolingAsync(fan.Capture()); s = client.Snapshot!;
        var lighting = new DeviceLightingDraft(); lighting.Update(s.State, s.Configuration.Generation, s.Configuration.Configuration.Lighting, s.ElapsedSeconds, s.Connected);
        lighting.Select(demo.DeviceIds.Select(id => new EndpointReference(id, "led1")));
        lighting.SetProgram(new("breathing", [new(0, 0, 220)])); await client.ApplyLightingAsync(lighting.Capture());
        s = client.Snapshot!; var before = DeviceConfigurationCodec.Write(s.Configuration.Configuration); var state = s.State;
        var config = s.Configuration.Configuration; var items = config.Dashboard.Items.Select(i => i.Target == target ? i with {
            Name = "前面", Role = "pump", Visible = false, Indicator = "circle", DisplayMaximum = 4200 } : i).Reverse().ToArray();
        var result = await client.SaveDashboardAsync(new(s.State.Inventory.Epoch, s.Configuration.Generation, new(2, items)));
        Assert.Equal(Outcome.Confirmed, result.Outcome); Assert.Equal(new[] { "dashboard.json" }, result.ChangedComponents);
        var after = client.Snapshot!; var saved = DeviceConfigurationCodec.Write(after.Configuration.Configuration);
        Assert.Equal(items, after.Configuration.Configuration.Dashboard.Items); Assert.NotEqual(s.Configuration.Generation, after.Configuration.Generation);
        foreach (var name in before.Keys.Where(n => n != "dashboard.json")) Assert.Equal(before[name], saved[name]);
        foreach (var original in state.Endpoints.Where(e => e.Owned))
        {
            var current = after.State.Endpoints.Single(e => e.Target == original.Target);
            Assert.True(current.Owned); Assert.Equal(original.PolicyRevision, current.PolicyRevision);
            if (original.Lighting is { Running: true }) { Assert.True(current.Lighting!.Running); Assert.Equal(original.Lighting.GroupId, current.Lighting.GroupId); }
        }
        Assert.Equal(1, demo.Hubs[0].Writes); Assert.Equal(0, demo.Hubs[1].Writes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SaveDashboardAsync(new(s.State.Inventory.Epoch, s.Configuration.Generation, new(2, items))));
        Assert.True((await client.ReleaseAsync()).Success);
    }
}

public partial class DeviceConfigurationAuthorityTests
{
    private static DeviceConfigurationRequest DashboardRequest(Rig r, string name = "前面") => new(DeviceConfigurationOperation.SaveDashboard,
        ExpectedGeneration: r.Startup.Generation.Id, Dashboard: new(2, [new(r.Startup.Configuration.Cooling.Endpoints[0].Target, DeviceFeature.Rpm, name, "fan")]));
    [Fact] public async Task DashboardSaveDoesNotConfigureDriversAndReplayedRequestDoesNotCreateAnotherGeneration()
    {
        await using var r = await Rig.Create(); await r.Startup.Runtime.ExecuteAsync(r.Duty(80));
        r.Driver.AfterConfigure = _ => throw new InvalidOperationException("display save must not configure hardware");
        var old = r.Startup.Generation; var request = r.Request(DashboardRequest(r));
        var reply = await r.Startup.Runtime.ExecuteAsync(request); Assert.True(reply.Success, reply.Error); var saved = r.Startup.Generation;
        Assert.NotEqual(old, saved); Assert.Equal(1, r.Hub.Writes);
        Assert.True((await r.Startup.Runtime.ExecuteAsync(request)).Success); Assert.Equal(saved, r.Startup.Generation);
        Assert.False((await r.Send(DashboardRequest(r, "古い変更") with { ExpectedGeneration = old.Id })).Success);
        Assert.True((await r.Send(DashboardRequest(r))).Success); Assert.Equal(saved, r.Startup.Generation);
        Assert.All(r.Startup.Runtime.State().Endpoints.Where(e => e.Target.EndpointId == "fan1"), e => Assert.True(e.Owned));
    }
    [Theory]
    [InlineData("before-switch")]
    [InlineData("file:dashboard.json")]
    public async Task DashboardSaveFailurePreservesActualPointerWithoutStoppingFan(string phase)
    {
        await using var r = await Rig.Create(); await r.Startup.Runtime.ExecuteAsync(r.Duty(80)); var before = r.Startup.Generation;
        r.Store.Checkpoint = at => { if (at == phase) throw new IOException("injected save failure"); };
        var result = await r.Send(DashboardRequest(r)); Assert.False(result.Success);
        Assert.Equal(before, r.Startup.Generation); Assert.Equal(r.Store.ReadActive()!.Pointer, r.Startup.Generation);
        Assert.Equal(1, r.Hub.Writes); Assert.True(r.Startup.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "fan1").Owned);
        r.Store.Checkpoint = null; Assert.True((await r.Send(DashboardRequest(r, "再保存"))).Success);
    }
    [Fact] public async Task DashboardSaveCannotAdoptUnregisteredDevicesOrChangeEndpointKind()
    {
        await using var r = await Rig.Create(); var before = r.Startup.Generation;
        var request = DashboardRequest(r); var item = request.Dashboard!.Items[0];
        Assert.False((await r.Send(request with { Dashboard = new(2, [item with { Target = new("unknown", "fan1") }]) })).Success);
        Assert.False((await r.Send(request with { Dashboard = new(2, [item with { Measurement = DeviceFeature.Temperature, Role = "temperature" }]) })).Success);
        Assert.Equal(before, r.Startup.Generation); Assert.Equal(0, r.Hub.Writes);
    }
}
