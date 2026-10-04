using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceM5SettingsTests
{
    private static DeviceM5Settings Settings(DeviceWorkspaceSnapshot s, string hub) => new(new("COM14", "panel", Transport: "auto", WifiAddress: "192.168.1.19"), new() {
        Hub = s.Configuration.Configuration.Bindings.Devices.Single(b => b.DeviceId == hub), Fans = ["fan1", "fan3"], Leds = ["led1"],
        Cpu = s.Configuration.Configuration.Dashboard.Items.Single(i => i.Name == "CPU").Target,
        Gpu = s.Configuration.Configuration.Dashboard.Items.Single(i => i.Name == "GPU").Target, Profiles = [new("balanced", "balanced")] }, true);
    private static DeviceM5SettingsEdit Edit(DeviceWorkspaceSnapshot s, DeviceM5Settings value) => new(s.State.Inventory.Epoch, s.Session!, s.Configuration.Generation, s.State.Inventory.TopologyRevision, value);
    private static DeviceConfigurationPackage With(DeviceConfigurationPackage c, DeviceM5Settings settings) => c with { Preferences = DeviceM5Configuration.Apply(c.Preferences, settings) };
    [Theory]
    [InlineData("port")][InlineData("zero-port")][InlineData("layout")][InlineData("loopback")][InlineData("ipv6")][InlineData("multicast")]
    [InlineData("transport")][InlineData("missing-id")][InlineData("duplicate-port")][InlineData("duplicate-slot")][InlineData("unbound")]
    public void InvalidConnectionAndSelectionNeverReachSave(string problem)
    {
        var value = new DeviceM5Settings(new(), new());
        value = problem switch {
            "port" => value with { Connection = new("/dev/COM1") }, "zero-port" => value with { Connection = new("COM0") },
            "layout" => value with { Connection = new(Layout: "C") }, "loopback" => value with { Connection = new(WifiAddress: "127.0.0.1") },
            "ipv6" => value with { Connection = new(WifiAddress: "::1") }, "multicast" => value with { Connection = new(WifiAddress: "224.0.0.1") },
            "transport" => value with { Connection = new(Transport: "network") }, "missing-id" => value with { Connection = new("COM14"), ConnectOnStart = true },
            "duplicate-port" => value with { Selection = new() { Fans = ["fan1", "fan1"] } },
            "duplicate-slot" => value with { Selection = new() { Profiles = [new("quiet", "one"), new("quiet", "two")] } },
            _ => value with { Selection = new() { Leds = ["led1"] } } };
        Assert.NotNull(DeviceM5Settings.Problem(value));
        Assert.False(DeviceConfigurationProtocol.Valid(new DeviceConfigurationRequest(DeviceConfigurationOperation.SaveM5, ExpectedGeneration: Guid.NewGuid().ToString("N"), TopologyRevision: 1, M5: value)));
    }
    [Fact] public void M5BodyCannotBeSmuggledIntoAnotherOperationOrUpload()
    {
        var good = new DeviceConfigurationRequest(DeviceConfigurationOperation.SaveM5, ExpectedGeneration: Guid.NewGuid().ToString("N"), TopologyRevision: 0, M5: new(new(), new()));
        Assert.True(DeviceConfigurationProtocol.Valid(good));
        Assert.False(DeviceConfigurationProtocol.Valid(good with { Operation = DeviceConfigurationOperation.Snapshot }));
        Assert.False(DeviceConfigurationProtocol.Valid(good with { Data = [1] })); Assert.False(DeviceConfigurationProtocol.Valid(good with { M5 = null }));
        Assert.False(DeviceConfigurationProtocol.Valid(good with { TopologyRevision = null })); Assert.False(DeviceConfigurationProtocol.Valid(good with { Notifications = new() }));
    }
    [Fact] public async Task SavingMappingsPreservesCoolingOwnerDutyAndPolicyWithoutHardwareWrites()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); Assert.True(client.SupportsM5Settings);
        var target = new EndpointReference(demo.DeviceIds[0], "fan1"); var s = client.Snapshot!; var e = s.State.Endpoints.Single(e => e.Target == target);
        var result = await client.ApplyCoolingAsync(new(new(s.State.Inventory.Epoch, s.State.Inventory.TopologyRevision, s.Configuration.Generation, new(target, e.Generation, e.PolicyRevision)), DeviceOperation.SetDuty, 55));
        Assert.Equal(Outcome.Confirmed, result.Outcome); s = client.Snapshot!; e = s.State.Endpoints.Single(e => e.Target == target); var writes = demo.Hubs[0].Writes;
        var value = Settings(s, demo.DeviceIds[0]); var reply = await client.SaveM5Async(Edit(s, value));
        Assert.Equal(Outcome.Confirmed, reply.Outcome); Assert.Equal(SettingsTransfer.Hash(value), SettingsTransfer.Hash(DeviceM5Configuration.Read(client.Snapshot!.Configuration.Configuration.Preferences)));
        var after = client.Snapshot.State.Endpoints.Single(e => e.Target == target);
        Assert.Equal(e.PolicyRevision, after.PolicyRevision); Assert.Equal(e.Generation, after.Generation); Assert.Equal(55, (await demo.Hubs[0].ReadAsync()).Channels[0].Duty);
        Assert.Equal(writes, demo.Hubs[0].Writes); Assert.Equal(0, demo.Hubs[1].Writes); Assert.All(demo.Hubs, h => Assert.Equal(0, h.LightingWrites));
        Assert.NotEqual(s.Configuration.Generation, client.Snapshot.Configuration.Generation);
    }
    [Theory][InlineData("epoch")][InlineData("session")][InlineData("generation")][InlineData("topology")][InlineData("fingerprint")][InlineData("sensor-role")][InlineData("profile")]
    public async Task StaleOrIncorrectTargetEditsAreRejectedBeforeWriting(string change)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var s = client.Snapshot!; var value = Settings(s, demo.DeviceIds[0]); var edit = Edit(s, value);
        edit = change switch {
            "epoch" => edit with { Epoch = Guid.NewGuid().ToString("N") }, "session" => edit with { Session = Guid.NewGuid().ToString("N") },
            "generation" => edit with { Generation = Guid.NewGuid().ToString("N") }, "topology" => edit with { Topology = edit.Topology + 1 },
            "fingerprint" => edit with { Settings = value with { Selection = value.Selection with { Hub = value.Selection.Hub! with { Fingerprint = new('f', 64) } } } },
            "sensor-role" => edit with { Settings = value with { Selection = value.Selection with { Cpu = value.Selection.Gpu, Gpu = null } } },
            _ => edit with { Settings = value with { Selection = value.Selection with { Profiles = [new("balanced", "absent")] } } } };
        await Assert.ThrowsAsync<IOException>(() => client.SaveM5Async(edit)); Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
        Assert.Equal(s.Configuration.Generation, client.Snapshot!.Configuration.Generation);
    }
    [Theory][InlineData("generation")][InlineData("session")][InlineData("epoch")][InlineData("topology")][InlineData("candidate")]
    public async Task DraftRetainsUserInputAndStickyConflictUntilReload(string change)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var s = client.Snapshot!; var draft = new DeviceM5SettingsDraft(); draft.Update(s);
        draft.Change(Settings(s, demo.DeviceIds[0])); Assert.True(draft.CanSave); var hash = SettingsTransfer.Hash(draft.Settings); draft.Update(s);
        Assert.Equal(hash, SettingsTransfer.Hash(draft.Settings)); Assert.True(draft.CanSave);
        var changed = change switch {
            "generation" => s with { Configuration = s.Configuration with { Generation = Guid.NewGuid().ToString("N") } },
            "session" => s with { Session = Guid.NewGuid().ToString("N") }, "epoch" => s with { State = s.State with { Inventory = s.State.Inventory with { Epoch = Guid.NewGuid().ToString("N") } } },
            "topology" => s with { State = s.State with { Inventory = s.State.Inventory with { TopologyRevision = s.State.Inventory.TopologyRevision + 1 } } },
            _ => s with { Configuration = s.Configuration with { Candidates = [] } } };
        draft.Update(changed); draft.Update(s); Assert.True(draft.Conflict); Assert.False(draft.CanSave); Assert.Equal(hash, SettingsTransfer.Hash(draft.Settings));
        Assert.Throws<InvalidOperationException>(() => draft.Capture()); draft.Reset(s); Assert.False(draft.Conflict); Assert.False(draft.Dirty);
    }
    [Fact] public async Task DraftChoicesRespectRolesAndSwitchingHubClearsControlsButKeepsSensors()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var s = client.Snapshot!; var draft = new DeviceM5SettingsDraft(); draft.Update(s);
        Assert.Null(draft.Settings.Selection.Hub); Assert.Empty(draft.Ports(false)); Assert.Equal(3, draft.Hubs().Length);
        var settings = Settings(s, demo.DeviceIds[0]); draft.Change(settings);
        Assert.Contains(draft.Sensors(DeviceKind.Processor), c => c.Target == settings.Selection.Cpu);
        Assert.DoesNotContain(draft.Sensors(DeviceKind.Processor), c => c.Target == settings.Selection.Gpu);
        Assert.Single(draft.Profiles()); Assert.Contains(draft.Ports(true), c => c.Id == "led1" && c.Available);
        draft.SelectHub(draft.Hubs().Single(h => h.Binding?.DeviceId == demo.DeviceIds[1]).Binding);
        Assert.Empty(draft.Settings.Selection.Fans); Assert.Empty(draft.Settings.Selection.Leds); Assert.Empty(draft.Settings.Selection.Profiles); Assert.Equal(settings.Selection.Cpu, draft.Settings.Selection.Cpu);
        draft.SelectHub(null); Assert.True(draft.CanSave); Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task BackupAndPortableTransferPreserveMonitoringButOnlyLocalBackupKeepsHubAuthority(bool portable)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var s = client.Snapshot!; var settings = Settings(s, demo.DeviceIds[0]);
        var config = With(s.Configuration.Configuration, settings); var archive = DeviceSettingsArchives.Read(DeviceSettingsArchives.Write(DeviceSettingsArchives.Create(config, portable)));
        var saved = DeviceM5Configuration.Read(archive.Configuration.Preferences);
        Assert.Equal(settings.Selection.Cpu, saved.Selection.Cpu); Assert.Equal(settings.Selection.Gpu, saved.Selection.Gpu);
        Assert.Equal(!portable, saved.ConnectOnStart); Assert.Equal(portable ? null : settings.Selection.Hub, saved.Selection.Hub);
        if (portable) { Assert.Null(saved.Connection.Port); Assert.Null(saved.Connection.DeviceId); Assert.Null(saved.Connection.WifiAddress); Assert.Empty(saved.Selection.Fans); }
        var draft = new DeviceSettingsImportDraft(s, archive); draft.Confirmed = true; var restored = draft.Capture().Configuration;
        Assert.False(restored.Preferences.M5ConnectOnStart); Assert.Equal(portable ? null : settings.Selection.Hub, restored.Preferences.M5Selection!.Hub);
        var cpu = draft.Endpoints.Single(e => e.Source == settings.Selection.Cpu); Assert.DoesNotContain(cpu.Choices, c => c.Target == settings.Selection.Gpu);
        Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
    }
    [Theory][InlineData("binding")][InlineData("remap")]
    public async Task RestoreCannotRetainPanelControlAfterPhysicalOrEndpointRemapping(string change)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var s = client.Snapshot!; var value = Settings(s, demo.DeviceIds[0]); var source = With(s.Configuration.Configuration, value);
        if (change == "binding") s = s with { Configuration = s.Configuration with { Candidates = s.Configuration.Candidates.Where(c => c.DeviceId != demo.DeviceIds[0]).ToArray() } };
        var draft = new DeviceSettingsImportDraft(s, DeviceSettingsArchives.Create(source, false));
        if (change == "remap") draft.Map(new(demo.DeviceIds[0], "fan1"), null);
        draft.Confirmed = true; var saved = draft.Capture().Configuration.Preferences;
        Assert.Null(saved.M5Selection!.Hub); Assert.Empty(saved.M5Selection.Fans); Assert.False(saved.M5ConnectOnStart);
    }
    [Fact] public async Task StaleSavedBindingsRemainReadableButNeverBecomeReady()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var s = client.Snapshot!; var value = Settings(s, demo.DeviceIds[0]);
        value = value with { Selection = value.Selection with { Hub = value.Selection.Hub! with { Fingerprint = new('f', 64) } } };
        var config = With(s.Configuration.Configuration, value); DeviceConfigurationCodec.Validate(config);
        var roundtrip = DeviceConfigurationCodec.Read(DeviceConfigurationCodec.Write(config)); Assert.Equal(value.Selection.Hub, roundtrip.Preferences.M5Selection!.Hub);
        Assert.NotNull(DeviceM5Configuration.LiveProblem(value, roundtrip, s.State, s.Configuration.Candidates));
        var draft = new DeviceM5SettingsDraft(); draft.Update(s with { Configuration = s.Configuration with { Configuration = roundtrip } });
        Assert.Contains(draft.Hubs(), h => h.Binding == value.Selection.Hub && h.Name.Contains("再確認"));
    }
    [Fact] public async Task SavingM5InvalidatesOlderRestorePreviewWithoutReleasingCooling()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var s = client.Snapshot!;
        var draft = new DeviceSettingsImportDraft(s, DeviceSettingsArchives.Create(s.Configuration.Configuration, false)); draft.Confirmed = true;
        var preview = await client.PreviewImportAsync(draft.Capture()); await client.SaveM5Async(Edit(client.Snapshot!, Settings(client.Snapshot!, demo.DeviceIds[0])));
        await Assert.ThrowsAsync<IOException>(() => client.ApplyImportAsync(preview)); Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
    }
}
