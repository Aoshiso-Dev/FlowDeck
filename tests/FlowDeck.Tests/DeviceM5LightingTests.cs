using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

[Collection("Wall-clock transport")]
public partial class DeviceM5LightingTests
{
    private static readonly EndpointReference A = new("a", "led1"), B = new("b", "led1");
    private static readonly LightingProgram Blue = new("breathing", [new(0, 0, 200)], Brightness: 60);
    private static string Id(char c) => new(c, 32);
    private static DeviceCapability Cap(DeviceFeature f) => new(f, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready);
    private static DeviceWorkspaceSnapshot Snapshot()
    {
        var config = ServiceRuntimeBootstrap.MonitoringConfiguration(Id('d'));
        config = config with {
            Devices = new(2, [new("a", "照明A", "demo", DeviceKind.Controller, [new("led1", "LED", EndpointKind.Lighting)]),
                new("b", "照明B", "demo", DeviceKind.Memory, [new("led1", "LED", EndpointKind.Lighting)])]),
            Lighting = config.Lighting with { Groups = [new("one", "片側", EndpointKind.Lighting, [A])],
                Scenes = [new("one", "青", [new(A, Blue)], []), new("both", "両方", [], [new([A, B], Blue)])],
                Preferences = [new(A, Blue), new(B, Blue)] }
        };
        var state = new DeviceServiceState(new(Id('e'), 1, new[] { A, B }.Select(t => new DeviceInstance(new(t.DeviceId,
            "demo", "照明", DeviceKind.Controller, IdentityConfidence.SerialBound, t.DeviceId, "demo", [t.DeviceId],
            [new("led1", "LED", EndpointKind.Lighting, [Cap(DeviceFeature.FixedColor), Cap(DeviceFeature.ColorFrames), Cap(DeviceFeature.RestoreLighting)])]), 1, true)).ToArray(), 1),
            [new(A, 1, 1, Outcome.WrittenUnverified), new(B, 1, 1, Outcome.WrittenUnverified)], false, null);
        return new(state, new(Id('a'), config, []), 0, true, null, Session: Id('f'));
    }
    private static DeviceWorkspaceSnapshot Library(DeviceWorkspaceSnapshot s, LightingConfiguration library) => s with {
        Configuration = s.Configuration with { Configuration = s.Configuration.Configuration with { Lighting = library } } };
    private static M5LightingCommand Command(DeviceWorkspaceSnapshot s, string name = "LightingOff", string scope = "all", string? scene = null, int? brightness = null) =>
        new("m5/request", name, scope, DeviceM5Lighting.Read(s, scope).Stamp, scene, brightness);

    [Fact] public void CatalogMapsArbitraryIdsWithoutDependingOnTheirNamesOrPositions()
    {
        var s = Snapshot(); var library = s.Configuration.Configuration.Lighting;
        s = Library(s, library with { Groups = Enumerable.Range(0, 32).Select(i => new EndpointGroup("日本語/" + i, new string('光', 80), EndpointKind.Lighting, [A])).ToArray(),
            Scenes = Enumerable.Range(0, 32).Select(i => new DeviceLightingScene("日本語/" + i, new string('色', 80), [new(A, Blue)], [])).ToArray() });
        var view = DeviceM5Lighting.Read(s, "all"); Assert.True(view.Ready); Assert.Equal(33, view.Groups.Length); Assert.Equal(32, view.Scenes.Length);
        Assert.All(view.Groups.Skip(1), g => Assert.True(M5LightingCommand.ValidId(g.Id))); Assert.All(view.Scenes, g => Assert.True(M5LightingCommand.ValidId(g.Id)));
        Assert.Equal(64, view.Groups.Skip(1).Concat(view.Scenes).Select(i => i.Id).Distinct().Count());
        var reversed = Library(s, s.Configuration.Configuration.Lighting with { Groups = s.Configuration.Configuration.Lighting.Groups.Reverse().Select(g => g with { Name = "改名" }).ToArray() });
        Assert.Equal(view.Groups[1].Id, DeviceM5Lighting.Read(reversed, "all").Groups[^1].Id);
    }
    [Fact] public void ScopeOnlyOffersWholeScenesAndForgedOutOfScopeSceneIsRejected()
    {
        var s = Snapshot(); var scope = DeviceM5Lighting.WireId(LightingPresetKind.Group, "one"); var view = DeviceM5Lighting.Read(s, scope);
        Assert.Single(view.Scenes); Assert.Equal(DeviceM5Lighting.WireId(LightingPresetKind.Scene, "one"), view.Scenes[0].Id);
        var edit = DeviceM5Lighting.Capture(s, Command(s, "ApplyLightingScene", scope, view.Scenes[0].Id));
        Assert.Equal(A, Assert.Single(edit.Version.Targets).Target); Assert.Equal("one", edit.Preset!.Id);
        Assert.Throws<InvalidOperationException>(() => DeviceM5Lighting.Capture(s, Command(s, "ApplyLightingScene", scope, DeviceM5Lighting.WireId(LightingPresetKind.Scene, "both"))));
    }
    [Fact] public void SamplingAndAnimationFramesDoNotInvalidateAnOpenEdit()
    {
        var s = Snapshot(); var running = new DeviceLightingStatus(Id('b'), true, 1, 0, new(0, 0, 1), 2);
        s = s with { State = s.State with { Endpoints = s.State.Endpoints.Select(e => e with { Owned = true, Lighting = running }).ToArray() } };
        var before = DeviceM5Lighting.Read(s, "all").Stamp;
        var next = s with { ElapsedSeconds = 1, State = s.State with { Inventory = s.State.Inventory with { Revision = 500 },
            Endpoints = s.State.Endpoints.Select(e => e with { Lighting = running with { Frames = 900, LastColor = new(0, 0, 200), MaximumWriteMilliseconds = 5 } }).ToArray(),
            Telemetry = [new(new("cpu", "temp"), DeviceFeature.Temperature, 90, 0, false)] } };
        Assert.Equal(before, DeviceM5Lighting.Read(next, "all").Stamp); Assert.NotNull(DeviceM5Lighting.Capture(next, Command(s)));
    }
    [Theory]
    [InlineData("session")][InlineData("epoch")][InlineData("topology")][InlineData("configuration")][InlineData("generation")]
    [InlineData("policy")][InlineData("disconnect")][InlineData("owner")][InlineData("fault")][InlineData("capability")][InlineData("scene")]
    public void OldStampCannotFollowAChangedSelectionOrService(string change)
    {
        var s = Snapshot(); var first = s.State.Endpoints[0]; var device = s.State.Inventory.Devices[0];
        var next = change switch {
            "session" => s with { Session = Id('b') }, "epoch" => s with { State = s.State with { Inventory = s.State.Inventory with { Epoch = Id('b') } } },
            "topology" => s with { State = s.State with { Inventory = s.State.Inventory with { TopologyRevision = 2 } } },
            "configuration" => s with { Configuration = s.Configuration with { Generation = Id('b') } },
            "generation" => s with { State = s.State with { Endpoints = [first with { Generation = 2 }, s.State.Endpoints[1]] } },
            "policy" => s with { State = s.State with { Endpoints = [first with { PolicyRevision = 2 }, s.State.Endpoints[1]] } },
            "disconnect" => s with { State = s.State with { Inventory = s.State.Inventory with { Devices = [device with { Connected = false }, s.State.Inventory.Devices[1]] } } },
            "owner" => s with { State = s.State with { Endpoints = [first with { Owned = true }, s.State.Endpoints[1]] } },
            "fault" => s with { State = s.State with { Endpoints = [first with { Lighting = new(Id('b'), false, 0, 0, null, 0, "失敗") }, s.State.Endpoints[1]] } },
            "capability" => s with { State = s.State with { Inventory = s.State.Inventory with { Devices = [device with { Device = device.Device with {
                Endpoints = [device.Device.Endpoints[0] with { Capabilities = [Cap(DeviceFeature.FixedColor) with { Availability = DeviceAvailability.NeedsSetup }] }] } }, s.State.Inventory.Devices[1]] } } },
            _ => Library(s, s.Configuration.Configuration.Lighting with { Scenes = [new("one", "変更", [new(A, Blue)], [], 2)] })
        };
        Assert.NotEqual(DeviceM5Lighting.Read(s, "all").Stamp, DeviceM5Lighting.Read(next, "all").Stamp);
        Assert.Throws<InvalidOperationException>(() => DeviceM5Lighting.Capture(next, Command(s)));
    }
    [Theory][InlineData(3, true)][InlineData(double.NaN, true)][InlineData(0, false)]
    public void LostFreshnessCannotBecomeAReadyPanel(double elapsed, bool connected)
    {
        var s = Snapshot() with { ElapsedSeconds = elapsed, Connected = connected };
        Assert.False(DeviceM5Lighting.Read(s, "all").Ready); Assert.Throws<InvalidOperationException>(() => DeviceM5Lighting.Capture(s, Command(s)));
    }
    [Fact] public void BrightnessUsesTheExactSavedGroupLayerAndRevision()
    {
        var s = Snapshot(); var scope = DeviceM5Lighting.WireId(LightingPresetKind.Group, "one");
        var edit = DeviceM5Lighting.Capture(s, Command(s, "SetLightingBrightness", scope, brightness: 35));
        Assert.Equal(A, Assert.Single(edit.Version.Targets).Target); Assert.Equal(LightingAdjustmentScope.Group, edit.Adjustment!.Scope);
        Assert.Equal("one", edit.Adjustment.GroupId); Assert.Equal(1, edit.Adjustment.GroupRevision); Assert.Equal(35, edit.Adjustment.Brightness);
        var all = DeviceM5Lighting.Capture(s, Command(s)); Assert.Equal(2, all.Version.Targets.Length); Assert.Equal(LightingAdjustmentScope.All, all.Adjustment!.Scope);
    }
    [Fact] public void MissingBaselineIsNotGuessedFromAnAnimationFrame()
    {
        var s = Snapshot(); s = Library(s, s.Configuration.Configuration.Lighting with { Preferences = [] });
        Assert.True(DeviceM5Lighting.Read(s, "all").Ready); // A saved scene may establish the first baseline.
        Assert.Throws<IOException>(() => DeviceM5Lighting.Capture(s, Command(s)));
        Assert.NotNull(DeviceM5Lighting.Capture(s, Command(s, "ApplyLightingScene", scene: DeviceM5Lighting.WireId(LightingPresetKind.Scene, "both"))));
    }
    [Theory][InlineData(0)][InlineData(30)]
    public void UnrepresentableBrightnessIsExplicitlyUnavailable(int brightness)
    {
        var s = Snapshot(); s = Library(s, s.Configuration.Configuration.Lighting with {
            TargetAdjustments = [new(A, Blue, new(brightness), new(), new()), new(B, Blue, new(), new(), new())] });
        var view = DeviceM5Lighting.Read(s, "all"); Assert.False(view.Ready); Assert.Contains("明るさ", view.Error);
        Assert.Throws<InvalidOperationException>(() => DeviceM5Lighting.Capture(s, Command(s)));
    }
    [Fact] public void PartialSyncSelectionCannotStopOrReplaceOutsideLightsButCanAdjustItsGroup()
    {
        var s = Snapshot(); s = s with { State = s.State with { Endpoints = s.State.Endpoints.Select(e => e with {
            Owned = true, Lighting = new(Id('b'), true, 1, 0, new(0, 0, 20), 1) }).ToArray() } };
        var scope = DeviceM5Lighting.WireId(LightingPresetKind.Group, "one");
        Assert.Throws<InvalidOperationException>(() => DeviceM5Lighting.Capture(s, Command(s, "StopLightingSync", scope)));
        Assert.Throws<InvalidOperationException>(() => DeviceM5Lighting.Capture(s, Command(s, "ApplyLightingScene", scope, DeviceM5Lighting.WireId(LightingPresetKind.Scene, "one"))));
        Assert.NotNull(DeviceM5Lighting.Capture(s, Command(s, "SetLightingBrightness", scope, brightness: 50)));
    }
    [Fact] public void SyncStopPersistsNormalBaseColorAndLeavesNonRunningLightsAlone()
    {
        var s = Snapshot(); s = s with { State = s.State with { Endpoints = [s.State.Endpoints[0] with {
            Owned = true, Lighting = new(Id('b'), true, 1, 0, new(255, 0, 0), 1) }, s.State.Endpoints[1]] } };
        var edit = DeviceM5Lighting.Capture(s, Command(s, "StopLightingSync"));
        Assert.Equal(A, Assert.Single(edit.Version.Targets).Target); Assert.Equal(DeviceOperation.SetLighting, edit.Operation);
        Assert.Equal("fixed", edit.Program!.EffectId); Assert.Equal(new RgbColor(0, 0, 120), Assert.Single(edit.Program.Colors));
    }
    [Fact] public void FirmwareCannotSilentlyFlattenDifferentNormalStopColors()
    {
        var s = Snapshot(); s = s with { State = s.State with { Endpoints = s.State.Endpoints.Select(e => e with {
            Owned = true, Lighting = new(Id('b'), true, 1, 0, new(0, 0, 20), 1) }).ToArray() } };
        s = Library(s, s.Configuration.Configuration.Lighting with { Preferences = [new(A, Blue), new(B, Blue with { Colors = [new(200, 0, 0)] })] });
        Assert.Throws<InvalidOperationException>(() => DeviceM5Lighting.Capture(s, Command(s, "StopLightingSync")));
    }
    [Fact] public void UnknownScopeDoesNotFallbackToAllLights()
    {
        var s = Snapshot(); var view = DeviceM5Lighting.Read(s, Id('0')); Assert.False(view.Ready); Assert.Empty(view.Scenes);
        Assert.Throws<InvalidOperationException>(() => DeviceM5Lighting.Capture(s, Command(s, scope: Id('0'))));
    }

    [Fact] public async Task AuthenticatedBridgeSharesPcOwnershipAndRoundTripsSceneBrightnessOffAndRestore()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        EndpointReference[] targets = demo.DeviceIds.Select(id => new EndpointReference(id, "led1")).ToArray();
        await client.SaveLightingAsync(new(LightingLibraryOperation.SaveGroup, client.Snapshot!.Configuration.Generation, "両方", 0,
            Group: new("両方", "すべて", EndpointKind.Lighting, targets)));
        await client.SaveLightingAsync(new(LightingLibraryOperation.SaveScene, client.Snapshot!.Configuration.Generation, "青の呼吸", 0,
            Scene: new("青の呼吸", "ブルー", [], [new(targets, Blue)])));
        var backend = new DeviceM5Lighting(client); var scope = DeviceM5Lighting.WireId(LightingPresetKind.Group, "両方");
        async Task<OperationResult> Send(string command, int? brightness = null, string? scene = null)
        {
            var view = backend.Read(scope); Assert.True(view.Ready, view.Error);
            var result = await backend.ExecuteAsync(new(Guid.NewGuid().ToString("N"), command, scope, view.Stamp, scene, brightness), CancellationToken.None);
            Assert.True(result.Outcome == Outcome.WrittenUnverified,
                $"{command}: {JsonSerializer.Serialize(result, Contract.Json)}; state={JsonSerializer.Serialize(client.Snapshot, Contract.Json)}");
            return result;
        }
        var scene = Assert.Single(backend.Read(scope).Scenes).Id;
        var applied = await Send("ApplyLightingScene", scene: scene); Assert.Equal(Outcome.WrittenUnverified, applied.Outcome); Assert.Equal("M5GO", applied.Source);
        Assert.True(backend.Read(scope).Synchronized); Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
        var mirror = backend.ReadBodyLed(scope); Assert.NotNull(mirror.Frame); Assert.Equal("breathing", mirror.Frame.Effect);
        Assert.Equal(60, mirror.Frame.Brightness); Assert.True(mirror.Frame.Valid);
        Assert.Equal(Outcome.WrittenUnverified, (await Send("SetLightingBrightness", 50)).Outcome); Assert.Equal(50, backend.Read(scope).Brightness);
        Assert.Equal(30, backend.ReadBodyLed(scope).Frame!.Brightness);
        Assert.Equal(Outcome.WrittenUnverified, (await Send("LightingOff")).Outcome); Assert.True(backend.Read(scope).Off); Assert.True(backend.Read(scope).CanRestore);
        var dark = backend.ReadBodyLed(scope).Frame!; Assert.True(dark.Effect == "off" || dark.Brightness == 0 || dark.Color == new RgbColor(0, 0, 0));
        Assert.Equal(Outcome.WrittenUnverified, (await Send("RestoreLighting")).Outcome); Assert.False(backend.Read(scope).Off);
        Assert.Equal("breathing", backend.ReadBodyLed(scope).Frame!.Effect);
        Assert.All(client.Snapshot!.Configuration.Configuration.Lighting.Preferences!, p => Assert.Equal(30, p.Program.Brightness));
        Assert.Equal(Outcome.WrittenUnverified, (await Send("StopLightingSync")).Outcome); Assert.False(backend.Read(scope).Synchronized);
        Assert.Equal("fixed", backend.ReadBodyLed(scope).Frame!.Effect);
        Assert.All(client.Snapshot!.Configuration.Configuration.Lighting.Preferences!, p => Assert.Equal("fixed", p.Program.EffectId));
        Assert.True((await client.ReleaseAsync()).Success);
    }
    [Fact] public async Task CancelledOrSupersededRequestsDoNotWriteAndBusyEnds()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var target = new EndpointReference(demo.DeviceIds[0], "led1");
        await client.SaveLightingAsync(new(LightingLibraryOperation.SaveGroup, client.Snapshot!.Configuration.Generation, "one", 0,
            Group: new("one", "片側", EndpointKind.Lighting, [target])));
        await client.SaveLightingAsync(new(LightingLibraryOperation.SaveScene, client.Snapshot!.Configuration.Generation, "one", 0,
            Scene: new("one", "青", [new(target, Blue with { EffectId = "fixed" })], [])));
        var backend = new DeviceM5Lighting(client); var scope = DeviceM5Lighting.WireId(LightingPresetKind.Group, "one"); var view = backend.Read(scope);
        var request = new M5LightingCommand("m5/request", "ApplyLightingScene", scope, view.Stamp, view.Scenes[0].Id);
        Assert.Equal(Outcome.Rejected, (await backend.ExecuteAsync(request, new CancellationToken(true))).Outcome); Assert.False(backend.Read(scope).Busy);
        await client.SaveLightingAsync(new(LightingLibraryOperation.SaveScene, client.Snapshot!.Configuration.Generation, "one", 1,
            Scene: new("one", "変更", [new(target, Blue)], [])));
        Assert.Equal(Outcome.Rejected, (await backend.ExecuteAsync(request, CancellationToken.None)).Outcome);
        Assert.All(demo.Hubs, h => Assert.Equal(0, h.LightingWrites)); Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
    }
    [Fact] public async Task AnM5CommandQueuedBehindAPcEditIsValidatedAfterTheEditCompletes()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var target = new EndpointReference(demo.DeviceIds[0], "led1");
        await client.SaveLightingAsync(new(LightingLibraryOperation.SaveGroup, client.Snapshot!.Configuration.Generation, "one", 0,
            Group: new("one", "片側", EndpointKind.Lighting, [target])));
        await client.SaveLightingAsync(new(LightingLibraryOperation.SaveScene, client.Snapshot!.Configuration.Generation, "one", 0,
            Scene: new("one", "青", [new(target, Blue with { EffectId = "fixed" })], [])));
        var backend = new DeviceM5Lighting(client); var scope = DeviceM5Lighting.WireId(LightingPresetKind.Group, "one"); var before = backend.Read(scope);
        var pc = DeviceWorkspaceClient.Preset(client.Snapshot!, LightingPresetKind.Group, "one", new("fixed", [new(200, 0, 0)]));
        demo.Hubs[0].DelayMs = 200;
        var pcWrite = client.ApplyLightingAsync(pc);
        var m5Write = backend.ExecuteAsync(new("queued", "ApplyLightingScene", scope, before.Stamp, before.Scenes[0].Id), CancellationToken.None);
        Assert.True(backend.Read(scope).Busy);
        Assert.Equal(Outcome.WrittenUnverified, (await pcWrite).Outcome); Assert.Equal(Outcome.Rejected, (await m5Write).Outcome);
        Assert.Equal(1, demo.Hubs[0].LightingWrites); Assert.Equal(0, demo.Hubs[1].LightingWrites); Assert.False(backend.Read(scope).Busy);
        Assert.Equal(new RgbColor(200, 0, 0), Assert.Single(client.Snapshot!.Configuration.Configuration.Lighting.Preferences!).Program.Colors[0]);
    }
    private sealed class ProjectionBackend(DeviceWorkspaceSnapshot snapshot) : IM5LightingBackend
    {
        public M5LightingView Read(string scopeId) => DeviceM5Lighting.Read(snapshot, scopeId);
        public Task<OperationResult> ExecuteAsync(M5LightingCommand request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    [Fact] public async Task ProjectedJapaneseCatalogFitsEveryFirmware13FrameAndPage()
    {
        var s = Snapshot(); s = Library(s, s.Configuration.Configuration.Lighting with {
            Groups = Enumerable.Range(0, 32).Select(i => new EndpointGroup("group/" + i, new string('照', 80), EndpointKind.Lighting, [A])).ToArray(),
            Scenes = Enumerable.Range(0, 32).Select(i => new DeviceLightingScene("scene/" + i, new string('色', 80), [new(A, Blue)], [])).ToArray() });
        await using var f = new Fixture(); await f.Initialize(); var protocol = new M5ProtocolSession(f.Core, time: f.Clock, lighting: new ProjectionBackend(s));
        var frames = new List<JsonElement>(); protocol.Outgoing += frame => { using var d = JsonDocument.Parse(frame); frames.Add(d.RootElement.Clone()); };
        protocol.Receive(Hello());
        foreach (var kind in new[] { "groups", "scenes" })
        {
            var ids = new List<string>();
            for (var page = 0; page < (kind == "groups" ? 5 : 4); page++)
            {
                protocol.Receive(JsonSerializer.Serialize(new { v = 1, type = "getLightingCatalog", sessionId = protocol.SessionId,
                    controllerEpoch = f.Core.Epoch, scopeId = "all", kind, page, requestId = "page" + page }));
                var catalog = frames.Last(frame => frame.GetProperty("type").GetString() == "lightingCatalog");
                ids.AddRange(catalog.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!));
            }
            Assert.Equal(kind == "groups" ? 33 : 32, ids.Distinct().Count());
        }
        Assert.All(frames, frame => Assert.InRange(Encoding.UTF8.GetByteCount(frame.GetRawText()) + 1, 1, 4096));
    }
    private static string Hello() => JsonSerializer.Serialize(new { v = 1, type = "hello", deviceType = "M5GO", deviceId = "panel",
        bootId = "boot", firmwareVersion = "1.3.0", capabilities = new[] { "lighting-v1" } });
    private static async Task Wait(Func<bool> ready)
    { for (var i = 0; i < 300; i++) { if (ready()) return; await Task.Delay(10); } throw new TimeoutException(); }
    [Fact] public async Task FirmwareCommandsUseOneServiceWriteAndOldFramesCannotCrossUsbWifiSessions()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var target = new EndpointReference(demo.DeviceIds[0], "led1");
        await client.SaveLightingAsync(new(LightingLibraryOperation.SaveGroup, client.Snapshot!.Configuration.Generation, "one", 0,
            Group: new("one", "片側", EndpointKind.Lighting, [target])));
        await client.SaveLightingAsync(new(LightingLibraryOperation.SaveScene, client.Snapshot!.Configuration.Generation, "one", 0,
            Scene: new("one", "青", [new(target, Blue with { EffectId = "fixed" })], [])));
        await using var f = new Fixture(); await f.Initialize();
        var backend = new DeviceM5Lighting(client); var scope = DeviceM5Lighting.WireId(LightingPresetKind.Group, "one"); var view = backend.Read(scope);
        var router = new M5TransportRouter(f.Core, new(Transport: "auto"), f.Clock, lighting: backend);
        var frames = new ConcurrentQueue<JsonElement>(); router.Outgoing += (_, frame) => { using var d = JsonDocument.Parse(frame); frames.Enqueue(d.RootElement.Clone()); };
        router.Receive("wifi", Hello()); var oldSession = router.Protocol.SessionId;
        string CommandFrame(string id, string? session) => JsonSerializer.Serialize(new { v = 1, type = "command", sessionId = session,
            controllerEpoch = f.Core.Epoch, id, commandSequence = 1, command = "ApplyLightingScene", expectedRevision = 0,
            payload = new { scopeId = scope, expectedStamp = view.Stamp, sceneId = view.Scenes[0].Id } });
        var command = CommandFrame("first", oldSession); router.Receive("wifi", command); router.Receive("wifi", command);
        await Wait(() => frames.Any(frame => frame.GetProperty("type").GetString() == "result" && frame.GetProperty("id").GetString() == "first"));
        router.Receive("wifi", command); Assert.Equal(1, demo.Hubs[0].LightingWrites);
        router.Receive("usb", Hello()); Assert.Equal("usb", router.ActiveRoute); Assert.NotEqual(oldSession, router.Protocol.SessionId);
        router.Receive("wifi", command); router.Receive("usb", command); // Old route/session cannot dispatch.
        router.Receive("usb", CommandFrame("stale", router.Protocol.SessionId));
        await Wait(() => frames.Any(frame => frame.GetProperty("type").GetString() == "result" && frame.GetProperty("id").GetString() == "stale"));
        Assert.Equal("rejected", frames.Last(frame => frame.GetProperty("type").GetString() == "result").GetProperty("outcome").GetString());
        router.Disconnect("usb"); router.Receive("wifi", Hello()); Assert.Equal("wifi", router.ActiveRoute);
        router.Receive("wifi", command); Assert.Equal(1, demo.Hubs[0].LightingWrites); Assert.Equal(0, demo.Hubs[1].LightingWrites);
        Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes)); Assert.Equal(0, f.Hub.Writes);
        Assert.All(frames, frame => Assert.InRange(Encoding.UTF8.GetByteCount(frame.GetRawText()) + 1, 1, 4096));
    }
}
