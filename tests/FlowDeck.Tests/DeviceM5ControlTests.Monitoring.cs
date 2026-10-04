using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceM5ControlTests
{
    private static DeviceWorkspaceSnapshot MonitoringSnapshot()
    {
        var s = Snapshot(); var c = s.Configuration.Configuration;
        return s with { Configuration = s.Configuration with { Configuration = c with {
            Bindings = c.Bindings with { Devices = [] }, Cooling = c.Cooling with { Endpoints = [] } } } };
    }
    private static DeviceM5Selection MonitoringSelection() => Selection() with { Fans = [], Leds = [], Profiles = [] };

    [Fact]
    public async Task MonitoringOnlyHubCanBeSelectedAndDisplayedWithoutCreatingControlAuthority()
    {
        var s = MonitoringSnapshot(); var selection = MonitoringSelection();
        var draft = new DeviceM5SettingsDraft(); draft.Update(s);
        Assert.Contains(draft.Hubs(), h => h.Binding == Binding);
        draft.Change(draft.Settings with { Selection = selection });
        Assert.True(draft.CanSave);
        Assert.Null(DeviceM5Configuration.LiveProblem(draft.Settings, s.Configuration.Configuration, s.State, s.Configuration.Candidates));
        await using var client = EmptyClient(); var backend = new DeviceM5Control(client, selection);
        var view = backend.Project(s);
        Assert.True(view.Snapshot.HubReady); Assert.Equal(902, view.Snapshot.Channels[2].Rpm);
        Assert.Equal(52, view.Snapshot.Sensors[0].Celsius); Assert.Equal(43, view.Snapshot.Sensors[1].Celsius);
        Assert.Empty(view.Settings.Hardware.Fans!); Assert.Empty(view.Settings.Hardware.Leds!);
        Assert.Empty(view.ReadyProfiles); Assert.NotEqual("制御可能", view.Snapshot.Readiness);
        Assert.Throws<InvalidOperationException>(() => backend.Capture(s, Request(view)));
        Assert.Throws<InvalidOperationException>(() => backend.Capture(s, Request(view, "SelectAutoProfile", null, "balanced", 7)));
        Assert.Throws<InvalidOperationException>(() => backend.Capture(s, Request(view, "ResumeControl", null)));
        Assert.Throws<InvalidOperationException>(() => backend.Capture(s, Request(view, "SetLighting", null, lighting: "blue")));
    }

    [Theory]
    [InlineData("fan")]
    [InlineData("light")]
    [InlineData("profile")]
    public async Task AddingAnyHubControlStillRequiresThePhysicalControlBinding(string kind)
    {
        var s = MonitoringSnapshot(); var selection = MonitoringSelection();
        selection = kind switch {
            "fan" => selection with { Fans = ["fan1"] },
            "light" => selection with { Leds = ["led1"] },
            _ => selection with { Profiles = [new("balanced", Profile.Id)] }
        };
        Assert.NotNull(DeviceM5Configuration.LiveProblem(new(new(), selection), s.Configuration.Configuration, s.State, s.Configuration.Candidates));
        await using var client = EmptyClient(); var backend = new DeviceM5Control(client, selection);
        var view = backend.Project(s); Assert.False(view.Snapshot.HubReady);
        Assert.Throws<InvalidOperationException>(() => backend.Capture(s, Request(view)));
        Assert.Throws<InvalidOperationException>(() => backend.Capture(s, Request(view, "SetLighting", null, lighting: "blue")));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("disconnected")]
    [InlineData("unregistered")]
    [InlineData("candidate")]
    [InlineData("fingerprint")]
    public async Task MonitoringOnlySelectionNeverFollowsAStaleOrReplacedHub(string change)
    {
        var s = MonitoringSnapshot();
        var changed = change switch {
            "stale" => s with { ElapsedSeconds = 3 },
            "disconnected" => s with { State = s.State with { Inventory = s.State.Inventory with { Devices = s.State.Inventory.Devices.Select(d => d.Device.Id == Binding.DeviceId ? d with { Connected = false } : d).ToArray() } } },
            "unregistered" => s with { Configuration = s.Configuration with { Configuration = s.Configuration.Configuration with { Devices = s.Configuration.Configuration.Devices with { Devices = s.Configuration.Configuration.Devices.Devices.Where(d => d.Id != Binding.DeviceId).ToArray() } } } },
            "candidate" => s with { Configuration = s.Configuration with { Candidates = [] } },
            _ => s with { Configuration = s.Configuration with { Candidates = [Binding with { Fingerprint = new('f', 64) }] } }
        };
        await using var client = EmptyClient(); var backend = new DeviceM5Control(client, MonitoringSelection());
        Assert.True(backend.Project(s).Snapshot.HubReady);
        var view = backend.Project(changed); Assert.False(view.Snapshot.HubReady);
        Assert.All(view.Snapshot.Channels, c => Assert.Null(c.Rpm));
    }

    [Fact]
    public async Task RegisteredMonitoringFlowsThroughV2AndFirmware13FramesAcrossUsbWifiWithoutOutputs()
    {
        using var dir = new ServiceTestDirectory();
        await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var s = client.Snapshot!;
        var cpu = s.State.Inventory.Devices.Single(d => d.Device.Kind == DeviceKind.Processor);
        var gpu = s.State.Inventory.Devices.Single(d => d.Device.Kind == DeviceKind.Graphics);
        var targets = new[] { new EndpointReference(cpu.Device.Id, cpu.Device.Endpoints[0].Id),
            new EndpointReference(gpu.Device.Id, gpu.Device.Endpoints[0].Id), new(demo.DeviceIds[0], "fan1"), new(demo.DeviceIds[0], "fan3") };
        var entries = targets.Select(t => {
            var d = s.State.Inventory.Devices.Single(d => d.Device.Id == t.DeviceId);
            var e = d.Device.Endpoints.Single(e => e.Id == t.EndpointId);
            var f = e.Capabilities.Any(c => c.Feature == DeviceFeature.Temperature) ? DeviceFeature.Temperature : DeviceFeature.Rpm;
            return new DeviceMonitoringSelection(new(t, f, e.Name, f == DeviceFeature.Temperature ? "temperature" : "fan"), d.Generation);
        }).ToArray();
        Assert.Equal(Outcome.Confirmed, (await client.RegisterMonitoringAsync(new(s.State.Inventory.Epoch, s.Configuration.Generation, s.State.Inventory.TopologyRevision, entries))).Outcome);
        s = client.Snapshot!;
        var selection = new DeviceM5Selection { Hub = s.Configuration.Candidates.Single(b => b.DeviceId == demo.DeviceIds[0]), Cpu = targets[0], Gpu = targets[1] };
        var settings = new DeviceM5Settings(new("COM14", "recording-panel", Transport: "auto"), selection);
        Assert.Equal(Outcome.Confirmed, (await client.SaveM5Async(new(s.State.Inventory.Epoch, s.Session!, s.Configuration.Generation, s.State.Inventory.TopologyRevision, settings))).Outcome);
        Assert.Empty(client.Snapshot!.Configuration.Configuration.Bindings.Devices);
        Assert.Empty(client.Snapshot.Configuration.Configuration.Cooling.Endpoints);
        var backend = new DeviceM5Control(client, selection);
        var router = new M5TransportRouter(backend, settings.Connection, motherboard: backend.ReadMotherboard);
        var frames = new ConcurrentQueue<(string Route, JsonElement Frame)>();
        router.Outgoing += (route, frame) => { Assert.InRange(Encoding.UTF8.GetByteCount(frame) + 1, 1, 4096); using var json = JsonDocument.Parse(frame); frames.Enqueue((route, json.RootElement.Clone())); };
        var hello = JsonSerializer.Serialize(new { v = 1, type = "hello", deviceType = "M5GO", deviceId = "recording-panel", bootId = "boot", firmwareVersion = "1.3.0", capabilities = new[] { "motherboard-v1", "lighting-v1" } });
        JsonElement LatestState() => frames.Last(f => f.Frame.GetProperty("type").GetString() == "state").Frame;
        void Acknowledge(string route)
        {
            var state = LatestState();
            router.Receive(route, JsonSerializer.Serialize(new { v = 1, type = "stateApplied", sessionId = router.Protocol.SessionId, controllerEpoch = backend.Epoch,
                snapshotSequence = state.GetProperty("snapshotSequence").GetInt64(), controlRevision = state.GetProperty("controlRevision").GetInt64(), lightingRevision = state.GetProperty("lightingRevision").GetInt64() }));
            Assert.True(router.Protocol.Synchronized);
        }
        router.Receive("wifi", hello); Acknowledge("wifi"); var firstSession = router.Protocol.SessionId;
        var first = LatestState(); Assert.Equal("ready", first.GetProperty("hub").GetProperty("state").GetString());
        Assert.False(first.GetProperty("fan").GetProperty("ready").GetBoolean());
        Assert.True(first.GetProperty("temperature").GetProperty("cpu").GetProperty("valid").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, first.GetProperty("fan").GetProperty("channels")[0].GetProperty("rpm").ValueKind);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("fan").GetProperty("channels")[1].GetProperty("rpm").ValueKind);
        demo.CpuTemperature = 61; await demo.Runtime.PollAsync(); await client.RefreshAsync(); router.Tick();
        Assert.Equal(61, LatestState().GetProperty("temperature").GetProperty("cpu").GetProperty("celsius").GetDouble());
        router.Receive("usb", hello); Assert.Equal("usb", router.ActiveRoute); Assert.NotEqual(firstSession, router.Protocol.SessionId); Acknowledge("usb");
        router.Receive("wifi", hello); Assert.Equal("usb", router.ActiveRoute);
        var state = LatestState(); var command = JsonSerializer.Serialize(new { v = 1, type = "command", sessionId = router.Protocol.SessionId, controllerEpoch = backend.Epoch,
            id = "blocked-output", commandSequence = 1, expectedRevision = state.GetProperty("controlRevision").GetInt64(), command = "SetManualDuty", payload = new { duty = 65 } });
        router.Receive("usb", command); router.Receive("usb", command);
        for (var i = 0; i < 100 && !frames.Any(f => f.Frame.GetProperty("type").GetString() == "result"); i++) await Task.Delay(10);
        Assert.Contains(frames, f => f.Frame.GetProperty("type").GetString() == "result" && f.Frame.GetProperty("outcome").GetString() == "rejected");
        router.Disconnect("usb"); router.Receive("wifi", hello); Assert.Equal("wifi", router.ActiveRoute); Acknowledge("wifi"); router.Close();
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
        Assert.All(client.Snapshot.State.Endpoints, e => Assert.False(e.Owned));
    }
}
