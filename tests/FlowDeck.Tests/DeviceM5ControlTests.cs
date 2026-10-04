using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceM5ControlTests
{
    private static string Id(char c) => new(c, 32);
    private static readonly EndpointReference Cpu = new("cpu", "temperature"), Gpu = new("gpu", "temperature"), Board = new("board", "temperature"), Pump = new("board", "pump");
    private static readonly PhysicalDeviceBinding Binding = new("hub-a", "native-a", HubDeviceDriver.Definition, new('a', 64));
    private static readonly CoolingTemperatureProfile Profile = new("custom/profile", "任意の温度カーブ", 7, [new(Cpu, [new(30, 30), new(90, 100)], 95, 85)], 3, 2);
    private static readonly CoolingSafety Safety = new(30, 100, 70, CoolingExitAction.FixedDuty, [new(Cpu, 95, 85)]);
    private static DeviceCapability Cap(DeviceFeature feature) => new(feature, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready);
    private static DeviceM5Selection Selection() => new() { Hub = Binding, Fans = ["fan3", "fan1"], Leds = ["led1"], Cpu = Cpu, Gpu = Gpu,
        Profiles = [new("balanced", Profile.Id)], Motherboard = [new(Board, DeviceFeature.Temperature, "マザー温度", "temperature"), new(Pump, DeviceFeature.Rpm, "ポンプ", "pump")] };
    private static DeviceWorkspaceSnapshot Snapshot()
    {
        var endpoints = Enumerable.Range(1, 5).Select(i => new DeviceEndpoint("fan" + i, "FAN" + i, EndpointKind.Cooling,
            [Cap(DeviceFeature.Rpm), Cap(DeviceFeature.DutyRead), Cap(DeviceFeature.FixedDuty), Cap(DeviceFeature.AutomaticDuty), Cap(DeviceFeature.ReleaseCooling)]))
            .Append(new("led1", "LED1", EndpointKind.Lighting, [Cap(DeviceFeature.FixedColor), Cap(DeviceFeature.ColorFrames), Cap(DeviceFeature.RestoreLighting)])).ToArray();
        DeviceInstance Hub(string id) => new(new(id, HubDeviceDriver.Provider, "同じ名前のHub", DeviceKind.Controller, IdentityConfidence.SerialBound,
            id, HubDeviceDriver.Definition, [id], endpoints), 1, true);
        DeviceInstance Sensor(string id, DeviceKind kind, DeviceEndpoint[] values) => new(new(id, "sensors", id, kind, IdentityConfidence.SerialBound, id, null, [id], values), 1, true);
        var devices = new[] { Hub("hub-a"), Hub("hub-b"), Sensor("cpu", DeviceKind.Processor, [new("temperature", "CPU温度", EndpointKind.Sensor, [Cap(DeviceFeature.Temperature)])]),
            Sensor("gpu", DeviceKind.Graphics, [new("temperature", "GPU温度", EndpointKind.Sensor, [Cap(DeviceFeature.Temperature)])]),
            Sensor("board", DeviceKind.Motherboard, [new("temperature", "マザー温度", EndpointKind.Sensor, [Cap(DeviceFeature.Temperature)]),
                new("pump", "ポンプ", EndpointKind.Cooling, [Cap(DeviceFeature.Rpm)])]) };
        var fanTargets = new[] { "hub-a", "hub-b" }.SelectMany(id => Enumerable.Range(1, 5).Select(i => new EndpointReference(id, "fan" + i))).ToArray();
        var config = ServiceRuntimeBootstrap.MonitoringConfiguration(Id('a'));
        config = config with { Devices = new(2, devices.Select(d => new RegisteredDevice(d.Device.Id, d.Device.Name, d.Device.Provider, d.Device.Kind,
            d.Device.Endpoints.Select(e => new RegisteredEndpoint(e.Id, e.Name, e.Kind)).ToArray())).ToArray()),
            Bindings = new(2, Id('a'), [Binding]), Cooling = new(2, fanTargets.Select(t => new CoolingEndpointConfiguration(t, true, Safety, [Profile])).ToArray(), []),
            Lighting = config.Lighting with { Preferences = [new(new("hub-a", "led1"), new("fixed", [new(0, 0, 200)]))] } };
        var state = new DeviceServiceState(new(Id('e'), 1, devices, 1),
            devices.SelectMany(d => d.Device.Endpoints.Select(e => new DeviceExecutionState(new(d.Device.Id, e.Id), 1, 0, Outcome.Unknown))).ToArray(), false, null,
            [.. fanTargets.SelectMany((t, i) => new[] { new DeviceTelemetry(t, DeviceFeature.Rpm, 900 + i, .25, false), new(t, DeviceFeature.DutyRead, 45, .25, false) }),
                new(Cpu, DeviceFeature.Temperature, 52, .5, false), new(Gpu, DeviceFeature.Temperature, 43, .25, false),
                new(Board, DeviceFeature.Temperature, 37, .75, false), new(Pump, DeviceFeature.Rpm, 0, .25, false)]);
        return new(state, new(Id('b'), config, [Binding]), .25, true, null, Session: Id('f'));
    }
    private static DeviceWorkspaceClient EmptyClient() => new(DeviceServicePipeClient.ForDemo("FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N"), Environment.ProcessId));
    private static ControlRequest Request(M5ControlView view, string command = "SetManualDuty", int? duty = 65, string? profile = null, long? revision = null, string? lighting = null) =>
        new(Guid.NewGuid().ToString("N"), "M5GO", view.Snapshot.ControllerEpoch,
            command == "SetLighting" ? view.Snapshot.LightingRevision : view.Snapshot.ControlRevision, command, duty, profile, revision, lighting);

    [Fact] public async Task EmptySetupKeepsThreeFirmwareSlotsWithoutGuessingAHub()
    {
        await using var client = EmptyClient(); var backend = new DeviceM5Control(client); var s = Snapshot(); var view = backend.Project(s);
        Assert.False(view.Snapshot.HubReady); Assert.Equal(3, view.Settings.Profiles.Length); Assert.Empty(view.ReadyProfiles);
        Assert.All(view.Snapshot.Channels, c => Assert.Null(c.Rpm)); Assert.All(view.Snapshot.Sensors, c => Assert.Null(c.Celsius));
        Assert.Throws<InvalidOperationException>(() => backend.Capture(s, Request(view)));
    }
    [Fact] public async Task ExplicitHubSelectionPreservesPhysicalSlotNumbersAndCpuGpuRoles()
    {
        await using var client = EmptyClient(); var backend = new DeviceM5Control(client, Selection()); var s = Snapshot(); var view = backend.Project(s);
        Assert.True(view.Snapshot.HubReady); Assert.Equal("制御可能", view.Snapshot.Readiness); Assert.Equal(new[] { "balanced" }, view.ReadyProfiles);
        Assert.Equal(7, view.Settings.Profiles[1].Revision); Assert.Equal(52, view.Snapshot.Sensors[0].Celsius); Assert.Equal(43, view.Snapshot.Sensors[1].Celsius);
        Assert.Equal(new[] { "fan1", "fan2", "fan3", "fan4", "fan5" }, view.Snapshot.Channels.Select(c => c.Id));
        Assert.Equal(902, view.Snapshot.Channels[2].Rpm); Assert.Equal(45, view.Snapshot.ConfirmedDuty);
        var plan = backend.Capture(s, Request(view)); Assert.Equal(new[] { "fan3", "fan1" }, plan.Targets.Select(t => t.Target.EndpointId));
        Assert.All(plan.Targets, t => Assert.Equal("hub-a", t.Target.DeviceId)); Assert.Equal(65, plan.Duty);
        var reordered = s with { State = s.State with { Inventory = s.State.Inventory with { Devices = s.State.Inventory.Devices.Reverse().ToArray() } } };
        Assert.Equal(view.Snapshot.ControlRevision, backend.Project(reordered).Snapshot.ControlRevision);
    }
    [Fact] public async Task CurrentValuesAreNotRejuvenatedAndZeroRpmRemainsAValidValue()
    {
        await using var client = EmptyClient(); var clock = new TestClock(); var backend = new DeviceM5Control(client, Selection(), clock); var s = Snapshot();
        var view = backend.Project(s); Assert.Equal(.75, clock.GetElapsedTime(view.Snapshot.Sensors[0].Timestamp).TotalSeconds, 5);
        var board = backend.ProjectMotherboard(s); Assert.True(board.Fresh); Assert.Equal(1000, board.AgeMs); Assert.Equal(0, board.Rows[1].Value);
        var stale = backend.Project(s with { ElapsedSeconds = 3 }); Assert.Null(stale.Snapshot.TelemetryTimestamp);
        Assert.All(stale.Snapshot.Sensors, sensor => Assert.Null(sensor.Celsius)); Assert.All(stale.Snapshot.Channels, c => Assert.Null(c.Rpm));
        Assert.False(backend.ProjectMotherboard(s with { Connected = false }).Fresh);
    }
    [Theory][InlineData("nan")][InlineData("duplicate")][InlineData("missing")][InlineData("stale")][InlineData("range")][InlineData("unavailable")]
    public async Task InvalidOrAmbiguousSamplesNeverBecomeZeroOrFresh(string kind)
    {
        await using var client = EmptyClient(); var backend = new DeviceM5Control(client, Selection()); var s = Snapshot(); var readings = s.State.Telemetry!;
        var sample = readings.Single(r => r.Target == Cpu);
        var changed = kind switch { "nan" => sample with { Value = double.NaN }, "stale" => sample with { AgeSeconds = 3 },
            "range" => sample with { Issue = DeviceMeasurementIssue.OutOfRange }, "unavailable" => sample with { Issue = DeviceMeasurementIssue.Unavailable }, _ => sample };
        s = s with { State = s.State with { Telemetry = [.. readings.Where(r => r.Target != Cpu), .. kind == "missing" ? [] : kind == "duplicate" ? new[] { sample, sample } : new[] { changed }] } };
        var view = backend.Project(s); Assert.Null(view.Snapshot.Sensors[0].Celsius); Assert.Empty(view.ReadyProfiles);
    }
    [Fact] public async Task GenericSensorCannotBeRelabeledAsCpuAndUnregisteredSamplesStayHidden()
    {
        await using var client = EmptyClient(); var backend = new DeviceM5Control(client, Selection() with { Cpu = Board }); var s = Snapshot();
        Assert.Null(backend.Project(s).Snapshot.Sensors[0].Celsius);
        backend.Select(Selection()); s = s with { Configuration = s.Configuration with { Configuration = s.Configuration.Configuration with {
            Devices = s.Configuration.Configuration.Devices with { Devices = s.Configuration.Configuration.Devices.Devices.Where(d => d.Id != Cpu.DeviceId).ToArray() } } } };
        Assert.Null(backend.Project(s).Snapshot.Sensors[0].Celsius);
    }
    [Theory][InlineData("session")][InlineData("epoch")][InlineData("generation")][InlineData("topology")][InlineData("configuration")]
    [InlineData("policy")][InlineData("disconnect")][InlineData("binding")][InlineData("candidate")][InlineData("provider")]
    public async Task StaleCommandsCannotFollowAChangedDeviceOrOwner(string change)
    {
        await using var client = EmptyClient(); var backend = new DeviceM5Control(client, Selection()); var s = Snapshot(); var view = backend.Project(s);
        var next = change switch {
            "session" => s with { Session = Id('d') }, "epoch" => s with { State = s.State with { Inventory = s.State.Inventory with { Epoch = Id('d') } } },
            "generation" => s with { State = s.State with { Inventory = s.State.Inventory with { Devices = s.State.Inventory.Devices.Select(d => d.Device.Id == "hub-a" ? d with { Generation = 2 } : d).ToArray() } } },
            "topology" => s with { State = s.State with { Inventory = s.State.Inventory with { TopologyRevision = 2 } } },
            "configuration" => s with { Configuration = s.Configuration with { Generation = Id('d') } },
            "policy" => s with { State = s.State with { Endpoints = s.State.Endpoints.Select(e => e.Target == new EndpointReference("hub-a", "fan1") ? e with { PolicyRevision = 3 } : e).ToArray() } },
            "disconnect" => s with { State = s.State with { Inventory = s.State.Inventory with { Devices = s.State.Inventory.Devices.Select(d => d.Device.Id == "hub-a" ? d with { Connected = false } : d).ToArray() } } },
            "binding" => s with { Configuration = s.Configuration with { Configuration = s.Configuration.Configuration with { Bindings = new(2, Id('a'), []) } } },
            "candidate" => s with { Configuration = s.Configuration with { Candidates = [Binding with { Fingerprint = new('b', 64) }] } },
            _ => s with { State = s.State with { Inventory = s.State.Inventory with { Devices = s.State.Inventory.Devices.Select(d => d.Device.Id == "hub-a" ? d with { Device = d.Device with { Provider = "other" } } : d).ToArray() } } }
        };
        Assert.Throws<InvalidOperationException>(() => backend.Capture(next, Request(view)));
    }
    [Fact] public async Task SensorPollingDoesNotInvalidateTheFanEditButChangingTheExplicitSelectionDoes()
    {
        await using var client = EmptyClient(); var backend = new DeviceM5Control(client, Selection()); var s = Snapshot(); var before = backend.Project(s);
        var refreshed = s with { State = s.State with { Inventory = s.State.Inventory with { Revision = 900 }, Telemetry = s.State.Telemetry!.Select(t => t with { AgeSeconds = 0 }).ToArray() } };
        Assert.Equal(before.Snapshot.ControlRevision, backend.Project(refreshed).Snapshot.ControlRevision); Assert.NotNull(backend.Capture(refreshed, Request(before)));
        backend.Select(Selection() with { Fans = ["fan2"] }); Assert.Throws<InvalidOperationException>(() => backend.Capture(refreshed, Request(before)));
    }
    [Fact] public async Task MixedFanOutputsAreNotPresentedAsOneConfirmedDuty()
    {
        await using var client = EmptyClient(); var backend = new DeviceM5Control(client, Selection()); var s = Snapshot();
        s = s with { State = s.State with { Telemetry = s.State.Telemetry!.Select(t => t.Target == new EndpointReference("hub-a", "fan3") && t.Feature == DeviceFeature.DutyRead ? t with { Value = 70 } : t).ToArray() } };
        var view = backend.Project(s); Assert.Null(view.Snapshot.ConfirmedDuty); Assert.Equal(Outcome.Unknown, view.Snapshot.Confirmation);
        Assert.Equal(45, view.Snapshot.Channels[0].Duty); Assert.Equal(70, view.Snapshot.Channels[2].Duty);
    }
    [Fact] public async Task ProfileSlotsResolveTheSameExactProfileOnEverySelectedFan()
    {
        await using var client = EmptyClient(); var backend = new DeviceM5Control(client, Selection()); var s = Snapshot(); var view = backend.Project(s);
        var plan = backend.Capture(s, Request(view, "SelectAutoProfile", null, "balanced", 7)); Assert.Equal(Profile.Id, plan.Profile!.Id);
        Assert.Equal(DeviceOperation.SetAutomatic, plan.Operation); Assert.Equal(2, plan.Targets.Length);
        Assert.Throws<InvalidOperationException>(() => backend.Capture(s, Request(view, "SelectAutoProfile", null, "balanced", 6)));
        s = s with { Configuration = s.Configuration with { Configuration = s.Configuration.Configuration with { Cooling = s.Configuration.Configuration.Cooling with {
            Endpoints = s.Configuration.Configuration.Cooling.Endpoints.Select(c => c.Target == new EndpointReference("hub-a", "fan3") ? c with {
                Profiles = [Profile with { Curves = [new(Cpu, [new(30, 50), new(90, 100)], 95, 85)] }] } : c).ToArray() } } } };
        var next = backend.Project(s); Assert.Empty(next.ReadyProfiles); Assert.Throws<InvalidOperationException>(() => backend.Capture(s, Request(next, "SelectAutoProfile", null, "balanced", 7)));
    }
    [Theory][InlineData(0)][InlineData(29)][InlineData(101)]
    public async Task InvalidManualDutyDoesNotExploitPerEndpointClamping(int duty)
    {
        await using var client = EmptyClient(); var backend = new DeviceM5Control(client, Selection()); var s = Snapshot();
        Assert.Throws<InvalidOperationException>(() => backend.Capture(s, Request(backend.Project(s), duty: duty)));
    }
    [Fact] public async Task ResumeSelectsOnlyTheFaultedFansWhoseConditionsPermitResume()
    {
        await using var client = EmptyClient(); var backend = new DeviceM5Control(client, Selection()); var s = Snapshot(); var target = new EndpointReference("hub-a", "fan1");
        s = s with { State = s.State with { Endpoints = s.State.Endpoints.Select(e => e.Target == target ? e with {
            Owned = true, Cooling = new(FanMode.Fallback, 100, true, "過熱", true, false) } : e).ToArray() } };
        var view = backend.Project(s); Assert.True(view.Snapshot.Faulted); Assert.True(view.Snapshot.CanResume);
        var plan = backend.Capture(s, Request(view, "ResumeControl", null)); Assert.Equal(target, Assert.Single(plan.Targets).Target);
        Assert.Throws<InvalidOperationException>(() => backend.Capture(s, Request(view)));
    }
    [Fact] public async Task ProjectedArraysCannotMutateTheExplicitSelection()
    {
        await using var client = EmptyClient(); var selected = Selection(); var backend = new DeviceM5Control(client, selected);
        selected.Fans[0] = "fan5"; var copy = backend.Selection; copy.Fans[0] = "fan4";
        var view = backend.Project(Snapshot()); view.Settings.Hardware.Fans![0] = "fan2";
        Assert.Equal("fan3", backend.Selection.Fans[0]);
    }
    [Fact] public async Task WiringMismatchAndRecoveryBothInvalidateTheOpenEdit()
    {
        await using var client = EmptyClient(); var backend = new DeviceM5Control(client, Selection()); var s = Snapshot(); var before = backend.Project(s);
        var changed = s with { Configuration = s.Configuration with { Candidates = [Binding with { Fingerprint = new('b', 64) }] } };
        var failed = backend.Project(changed); Assert.False(failed.Snapshot.HubReady); Assert.True(failed.Snapshot.ControlRevision > before.Snapshot.ControlRevision);
        var recovered = backend.Project(s); Assert.True(recovered.Snapshot.HubReady); Assert.True(recovered.Snapshot.ControlRevision > failed.Snapshot.ControlRevision);
        Assert.Throws<InvalidOperationException>(() => backend.Capture(s, Request(before)));
    }
    [Theory][InlineData("unbound")][InlineData("wrong-driver")][InlineData("duplicate-port")][InlineData("duplicate-slot")]
    public void InvalidMappingsAreRejectedInsteadOfCreatingAnotherControlMeaning(string kind)
    {
        var value = kind switch {
            "unbound" => Selection() with { Hub = null },
            "wrong-driver" => Selection() with { Hub = Binding with { DefinitionId = "motherboard" } },
            "duplicate-port" => Selection() with { Fans = ["fan1", "fan1"] },
            _ => Selection() with { Profiles = [new("balanced", "one"), new("balanced", "two")] }
        };
        Assert.Throws<ArgumentException>(() => DeviceM5Selection.Validate(value));
    }
    [Fact] public async Task DemoCannotConstructRealTransportsEvenBeforeItConnects()
    {
        await using var client = EmptyClient(); var backend = new DeviceM5Control(client); Assert.True(backend.Snapshot.Demo);
        Assert.Throws<InvalidOperationException>(() => new M5Connection(backend, new(), "unused", new NullLog()));
        Assert.Throws<InvalidOperationException>(() => new M5SerialSession(backend, new(Port: "COM14"), new NullLog()));
    }
    private sealed class NullLog : IEventLog { public void Write(string category, object data) { } }
    private static DeviceM5Selection DemoSelection(DeviceWorkspaceSnapshot s, string hub) => new() {
        Hub = s.Configuration.Configuration.Bindings.Devices.Single(b => b.DeviceId == hub), Fans = ["fan3", "fan1"], Leds = ["led1"],
        Cpu = s.Configuration.Configuration.Dashboard.Items.Single(i => i.Name == "CPU").Target,
        Gpu = s.Configuration.Configuration.Dashboard.Items.Single(i => i.Name == "GPU").Target,
        Profiles = [new("balanced", "balanced")] };
    [Fact] public async Task AuthenticatedM5ManualAndAutomaticControlOnlyTheExplicitHubAndPorts()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var backend = new DeviceM5Control(client, DemoSelection(client.Snapshot!, demo.DeviceIds[0]));
        var result = await backend.RequestAsync(Request(backend.ReadM5State(), duty: 68)); Assert.Equal(Outcome.Confirmed, result.Outcome);
        Assert.All(result.Channels!, c => Assert.Equal("confirmed", c.Confirmation));
        Assert.Equal(2, demo.Hubs[0].Writes); Assert.Equal(0, demo.Hubs[1].Writes);
        var channels = backend.Snapshot.Channels; Assert.Equal(68, channels[0].Duty); Assert.Equal(68, channels[2].Duty); Assert.Equal(45, channels[1].Duty);
        var view = backend.ReadM5State(); Assert.Contains("balanced", view.ReadyProfiles);
        var auto = await backend.RequestAsync(Request(view, "SelectAutoProfile", null, "balanced", view.Settings.Profiles[1].Revision));
        Assert.Equal(Outcome.Confirmed, auto.Outcome); Assert.Equal(FanMode.Auto, backend.Snapshot.Mode); Assert.Equal("balanced", backend.Snapshot.LastConfirmed!.Profile!.Id);
        Assert.Equal(0, demo.Hubs[1].Writes);
        var light = await backend.RequestAsync(Request(backend.ReadM5State(), "SetLighting", null, lighting: "white")); Assert.Equal(Outcome.WrittenUnverified, light.Outcome);
        Assert.Equal(1, demo.Hubs[0].LightingWrites); Assert.Equal(0, demo.Hubs[1].LightingWrites);
        Assert.True((await client.ReleaseAsync()).Success);
    }
    [Fact] public async Task QueuedFanCommandCancelledByPanelDisconnectNeverWrites()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var backend = new DeviceM5Control(client, DemoSelection(client.Snapshot!, demo.DeviceIds[0]));
        var view = backend.ReadM5State(); var request = Request(view, duty: 75);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        demo.Hubs[0].BeforeFanWrite = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        var t = new EndpointReference(demo.DeviceIds[0], "fan2"); var s = client.Snapshot!; var e = s.State.Endpoints.Single(e => e.Target == t);
        var pc = client.ApplyCoolingAsync(new(new(s.State.Inventory.Epoch, s.State.Inventory.TopologyRevision, s.Configuration.Generation, new(t, e.Generation, e.PolicyRevision)), DeviceOperation.SetDuty, 55));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); using var cancel = new CancellationTokenSource();
            var pending = backend.RequestFromM5Async(request, cancel.Token); Assert.True(backend.Snapshot.FanBusy);
            Assert.Throws<InvalidOperationException>(() => backend.Select(new())); cancel.Cancel();
            Assert.Equal(Outcome.Rejected, (await pending).Outcome); Assert.False(backend.Snapshot.FanBusy);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(Outcome.Confirmed, (await pc).Outcome); Assert.Equal(1, demo.Hubs[0].Writes); Assert.Equal(0, demo.Hubs[1].Writes);
        demo.Hubs[0].BeforeFanWrite = null;
    }
    [Fact] public async Task RealV2ProjectionFitsFirmwareFramesAndDuplicateManualCommandsExecuteOnce()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var backend = new DeviceM5Control(client, DemoSelection(client.Snapshot!, demo.DeviceIds[0]));
        var protocol = new M5ProtocolSession(backend, motherboard: backend.ReadMotherboard, lighting: new DeviceM5Lighting(client));
        var frames = new ConcurrentQueue<JsonElement>(); protocol.Outgoing += frame => { using var json = JsonDocument.Parse(frame); frames.Enqueue(json.RootElement.Clone()); };
        protocol.Receive(JsonSerializer.Serialize(new { v = 1, type = "hello", deviceType = "M5GO", deviceId = "panel", bootId = "boot", firmwareVersion = "1.3.0",
            capabilities = new[] { "motherboard-v1", "lighting-v1" } }));
        var state = frames.Single(f => f.GetProperty("type").GetString() == "state"); Assert.Equal(3, state.GetProperty("profiles").GetArrayLength());
        Assert.Equal(5, state.GetProperty("fan").GetProperty("channels").GetArrayLength()); Assert.True(state.GetProperty("temperature").GetProperty("cpu").GetProperty("valid").GetBoolean());
        var command = JsonSerializer.Serialize(new { v = 1, type = "command", sessionId = protocol.SessionId, controllerEpoch = backend.Epoch, id = "a",
            commandSequence = 1, expectedRevision = state.GetProperty("controlRevision").GetInt64(), command = "SetManualDuty", payload = new { duty = 65 } });
        protocol.Receive(command); protocol.Receive(command);
        for (var i = 0; i < 300 && !frames.Any(f => f.GetProperty("type").GetString() == "result"); i++) await Task.Delay(10);
        Assert.Contains(frames, f => f.GetProperty("type").GetString() == "result" && f.GetProperty("outcome").GetString() == "confirmed");
        protocol.Receive(command); Assert.Equal(2, demo.Hubs[0].Writes); Assert.Equal(0, demo.Hubs[1].Writes);
        Assert.All(frames, frame => Assert.InRange(Encoding.UTF8.GetByteCount(frame.GetRawText()) + 1, 1, 4096));
    }
    [Fact] public async Task MotherboardProjectionKeepsEightUtf8BoundedRowsAndPerRowMissingValues()
    {
        await using var client = EmptyClient(); var s = Snapshot(); var items = Enumerable.Range(0, 8)
            .Select(i => new DeviceDashboardItem(new("board", "extra" + i), DeviceFeature.Temperature, string.Concat(Enumerable.Repeat("温度🌡️", 30)), "temperature")).ToArray();
        var board = s.State.Inventory.Devices.Single(d => d.Device.Id == "board"); var extra = items.Select(i => new DeviceEndpoint(i.Target.EndpointId, "温度", EndpointKind.Sensor, [Cap(DeviceFeature.Temperature)])).ToArray();
        s = s with { State = s.State with { Inventory = s.State.Inventory with { Devices = s.State.Inventory.Devices.Select(d => d == board ? d with { Device = d.Device with { Endpoints = [.. d.Device.Endpoints, .. extra] } } : d).ToArray() },
            Telemetry = [.. s.State.Telemetry!, new(items[0].Target, DeviceFeature.Temperature, 40, 0, false)] },
            Configuration = s.Configuration with { Configuration = s.Configuration.Configuration with { Devices = s.Configuration.Configuration.Devices with {
                Devices = s.Configuration.Configuration.Devices.Devices.Select(d => d.Id == "board" ? d with { Endpoints = [.. d.Endpoints, .. extra.Select(e => new RegisteredEndpoint(e.Id, e.Name, e.Kind))] } : d).ToArray() } } } };
        var backend = new DeviceM5Control(client, Selection() with { Motherboard = items }); var panel = backend.ProjectMotherboard(s);
        Assert.True(panel.Fresh); Assert.Equal(8, panel.Rows.Length); Assert.Equal(40, panel.Rows[0].Value); Assert.All(panel.Rows.Skip(1), r => Assert.Null(r.Value));
        Assert.All(panel.Rows, r => { Assert.InRange(Encoding.UTF8.GetByteCount(r.Name), 1, 72); Assert.DoesNotContain('\uFFFD', r.Name); });
        Assert.Throws<ArgumentException>(() => backend.Select(Selection() with { Motherboard = [.. items, new(Board, DeviceFeature.Rpm, "9個目", "fan")] }));
    }
}
