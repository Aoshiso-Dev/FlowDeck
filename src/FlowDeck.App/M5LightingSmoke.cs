using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    private sealed class LightingPeer(RuntimeHost host)
    {
        public M5TransportRouter Router { get; } = new(host.Coordinator, new(Transport: "auto"), lighting: host);
        public ConcurrentQueue<(string Route, JsonElement Frame)> Messages { get; } = new();
        private int sequence;
        public void Start()
        {
            Router.Outgoing += (route, text) => { using var doc = JsonDocument.Parse(text); Messages.Enqueue((route, doc.RootElement.Clone())); };
            Hello("wifi");
        }
        public void Hello(string route) => Router.Receive(route, JsonSerializer.Serialize(new
        { v = 1, type = "hello", deviceType = "M5GO", deviceId = "lighting-smoke", bootId = "boot", firmwareVersion = "1.3.0", capabilities = new[] { "lighting-v1" } }));
        public void Send(string route, string type, Dictionary<string, object?> data)
        {
            data["v"] = 1; data["type"] = type; data["sessionId"] = Router.Protocol.SessionId; data["controllerEpoch"] = host.Coordinator.Epoch;
            Router.Receive(route, JsonSerializer.Serialize(data));
        }
        public async Task<JsonElement> Apply(string name, string scope, string? scene = null, int? brightness = null, string? stamp = null)
        {
            await Until(() => !host.Read(scope).Busy);
            var id = "light-" + ++sequence;
            var payload = new Dictionary<string, object?> { ["scopeId"] = scope, ["expectedStamp"] = stamp ?? host.Read(scope).Stamp };
            if (scene is not null) payload["sceneId"] = scene;
            if (brightness is not null) payload["brightness"] = brightness;
            Send(Router.ActiveRoute!, "command", new() { ["id"] = id, ["commandSequence"] = sequence, ["command"] = name, ["expectedRevision"] = 0, ["payload"] = payload });
            await Until(() => Messages.Any(m => m.Frame.GetProperty("type").GetString() == "result" && m.Frame.GetProperty("id").GetString() == id));
            return Messages.Last(m => m.Frame.GetProperty("type").GetString() == "result" && m.Frame.GetProperty("id").GetString() == id).Frame;
        }
    }
    private static async Task RunM5LightingChecksAsync(RuntimeHost host, FlowDeckViewModel vm, FlowDeckWindow window, string output, Action<bool, string> check)
    {
        await host.StopLightingSyncAsync();
        var fanRevision = host.Coordinator.Snapshot.ControlRevision; var fanWrites = ((DemoHub)host.Hub).Writes;
        var baseline = await host.CaptureLightingSceneAsync(Guid.NewGuid().ToString("N"), "端末試験前", true, true);
        var board = host.Rgb.Devices.Single();
        var zone = board.Zones.Single(z => z.HardwareZone == 2);
        var group = new LightingGroup(Guid.NewGuid().ToString("N"), "M5 マザー", [], [new(board.ControlIdentity!, 2)]);
        await host.SaveLightingGroupAsync(group);
        var scene = new LightingScene(Guid.NewGuid().ToString("N"), "M5 青い照明", [], [new(board.ControlIdentity!, 2, new(1, 40, 80, 200))]);
        await host.SaveSceneAsync(scene);
        var peer = new LightingPeer(host); peer.Start();
        var lightRevision = host.Coordinator.Snapshot.LightingRevision;
        var result = await peer.Apply("ApplyLightingScene", group.Id, scene.Id);
        check(result.GetProperty("outcome").GetString() == "writtenUnverified" && host.Rgb.Devices.Single().Zones.Single(z => z.HardwareZone == 2).Configuration == scene.Board[0].Effect,
            "M5 Wi-Fi scene command reaches the host and changes only the selected motherboard group");
        check(host.Coordinator.Snapshot.LightingRevision == lightRevision && board.Zones.Where(z => z.HardwareZone != 2).All(z => host.Rgb.Devices.Single().Zones.Single(n => n.HardwareZone == z.HardwareZone).Configuration == z.Configuration),
            "Board-only M5 scene preserves NZXT and all unselected motherboard zones");
        var oldStamp = host.Read(group.Id).Stamp;
        await peer.Apply("SetLightingBrightness", group.Id, brightness: 50);
        check(host.AdjustmentFor(group) is { Applied: true, Brightness: 50 } && host.Rgb.Devices.Single().Zones.Single(z => z.HardwareZone == 2).Configuration == new BoardLightSetting(1, 20, 40, 100),
            "M5 brightness scales the original group color and persists its restoration baseline");
        result = await peer.Apply("LightingOff", group.Id, stamp: oldStamp);
        check(result.GetProperty("error").GetString() == "STALE_LIGHTING" && !host.AdjustmentFor(group).IsOff,
            "M5 stale edit detects a motherboard-only change even when the NZXT revision is unchanged");
        await peer.Apply("LightingOff", group.Id);
        check(host.AdjustmentFor(group).IsOff, "M5 all-off is scoped to the selected group");
        var previousSession = peer.Router.Protocol.SessionId; peer.Hello("usb");
        check(peer.Router.ActiveRoute == "usb" && peer.Router.Protocol.SessionId != previousSession, "USB preempts Wi-Fi with a fresh M5 lighting session");
        var beforeIgnored = host.Read(group.Id).Stamp;
        peer.Send("wifi", "command", new() { ["id"] = "inactive", ["commandSequence"] = 20, ["expectedRevision"] = 0, ["command"] = "RestoreLighting", ["payload"] = new { scopeId = group.Id, expectedStamp = beforeIgnored } });
        check(host.AdjustmentFor(group).IsOff && host.Read(group.Id).Stamp == beforeIgnored, "Inactive Wi-Fi cannot write lighting while USB owns the panel");
        result = await peer.Apply("RestoreLighting", group.Id);
        check(result.GetProperty("outcome").GetString() == "writtenUnverified" && host.AdjustmentFor(group) is { Brightness: 50, IsOff: false, Applied: true },
            "M5 USB restores the previous effect and brightness after transport switching");
        oldStamp = host.Read(group.Id).Stamp; await host.SaveSceneAsync(scene with { Name = "M5 保存名を変更" });
        result = await peer.Apply("LightingOff", group.Id, stamp: oldStamp);
        check(result.GetProperty("error").GetString() == "STALE_LIGHTING", "Scene library edits invalidate open M5 lighting drafts");
        var missingId = Guid.NewGuid().ToString("N"); result = await peer.Apply("LightingOff", missingId);
        check(result.GetProperty("outcome").GetString() == "rejected" && !host.Read(missingId).Ready && !host.AdjustmentFor(group).IsOff,
            "A missing M5 group is rejected without falling back to all lighting");
        var syncScene = new LightingScene(Guid.NewGuid().ToString("N"), "M5 同期", [], [],
            new(host.Coordinator.Settings.Hardware.Hub!.Id, ["led1"], board.ControlIdentity!, [2], "breathing", new(100, 40, 200), 2));
        await host.SaveSceneAsync(syncScene); result = await peer.Apply("ApplyLightingScene", "all", syncScene.Id);
        await Until(() => host.LightingFrames >= 2);
        check(result.GetProperty("outcome").GetString() == "writtenUnverified" && host.LightingSynchronized, "M5 scene recall starts a shared Hub and motherboard animation");
        oldStamp = host.Read("all").Stamp; var frames = host.LightingFrames; await Until(() => host.LightingFrames > frames + 1);
        check(host.Read("all").Stamp == oldStamp, "Animation frames do not continually invalidate the M5 editing stamp");
        result = await peer.Apply("StopLightingSync", "all", stamp: oldStamp);
        check(result.GetProperty("outcome").GetString() == "writtenUnverified" && !host.LightingSynchronized, "M5 can explicitly stop synchronized lighting");
        check(peer.Messages.All(m => Encoding.UTF8.GetByteCount(m.Frame.GetRawText()) + 1 <= 4096), "Actual host lighting results and state fit the firmware frame limit");
        vm.ReloadLightingGroups.Execute(null); await Until(() => vm.LightingScenesIdle && vm.LightingGroupChoices.Any(g => g.Group?.Id == group.Id));
        vm.SelectedLightingGroup = vm.LightingGroupChoices.Single(g => g.Group?.Id == group.Id);
        await peer.Apply("SetLightingBrightness", group.Id, brightness: 60); await Until(() => vm.MasterBrightness == 60);
        check(vm.BrightnessResult.Contains("60%"), "PC brightness slider follows a completed M5 change for its selected group");
        vm.MasterBrightness = 73; await peer.Apply("SetLightingBrightness", group.Id, brightness: 50);
        await Until(() => vm.BrightnessResult == "M5GOで変更あり・未適用");
        check(vm.MasterBrightness == 73, "A remote M5 change preserves the PC brightness draft and marks the conflict");
        vm.SelectedLightingGroup = vm.LightingGroupChoices[0];
        await host.ApplyLightingSceneAsync(baseline);
        await Settle();
        vm.SelectedPage = 3; vm.PreviewOpen = true; vm.VirtualButton.Execute("C"); await Settle();
        check(vm.VirtualDisplay.Contains("対象グループ"), "Virtual M5 home C opens the negotiated lighting menu");
        vm.VirtualButton.Execute("B"); await Settle();
        check(vm.VirtualDisplay.Contains("M5 マザー"), "Virtual M5 group catalog exposes the saved board group");
        vm.VirtualButton.Execute("long"); vm.VirtualButton.Execute("C"); vm.VirtualButton.Execute("C"); vm.VirtualButton.Execute("C"); vm.VirtualButton.Execute("B");
        check(vm.VirtualDisplay.Contains("設定値"), "Virtual M5 brightness editor opens without applying a value");
        var beforeCancel = host.Read("all").Stamp; vm.VirtualButton.Execute("A"); vm.VirtualButton.Execute("long");
        check(host.Read("all").Stamp == beforeCancel && vm.VirtualDisplay.Contains("FlowDeck"), "M5 brightness adjustment followed by long B discards the draft without writes");
        vm.VirtualButton.Execute("C"); await Settle(); window.PageScroll.ScrollToTop(); ThemeManager.Apply("dark"); await Settle(); Capture(window, output, "m5-lighting-menu-dark");
        ThemeManager.Apply("light"); await Settle(); Capture(window, output, "m5-lighting-menu-light");
        vm.VirtualButton.Execute("long"); vm.PreviewOpen = false;
        check(host.Coordinator.Snapshot.ControlRevision == fanRevision && ((DemoHub)host.Hub).Writes == fanWrites && host.Coordinator.Snapshot.ConfirmedDuty == 59,
            "M5 scenes brightness restoration synchronization and navigation never write fans or change their policy");
        await host.DeleteSceneAsync(scene.Id); await host.DeleteSceneAsync(syncScene.Id); await host.DeleteLightingGroupAsync(group.Id);
        await using (var boardOnly = new RuntimeHost(Path.Combine(output, "m5-board-only"), true))
        {
            await boardOnly.InitializeAsync(true); await boardOnly.Coordinator.StopControlAsync();
            var demoHub = (DemoHub)boardOnly.Hub; demoHub.Disconnected = true; await boardOnly.Coordinator.TickAsync();
            try
            {
                var onlyBoard = boardOnly.Rgb.Devices.Single();
                var onlyGroup = new LightingGroup(Guid.NewGuid().ToString("N"), "マザーのみ", [], [new(onlyBoard.ControlIdentity!, 2)]);
                await boardOnly.SaveLightingGroupAsync(onlyGroup);
                var onlyScene = new LightingScene(Guid.NewGuid().ToString("N"), "単独照明", [], [new(onlyBoard.ControlIdentity!, 2, new(1, 25, 50, 75))]);
                await boardOnly.SaveSceneAsync(onlyScene);
                var boardPeer = new LightingPeer(boardOnly); boardPeer.Start();
                var changed = await boardPeer.Apply("ApplyLightingScene", onlyGroup.Id, onlyScene.Id);
                check(!boardOnly.Coordinator.Snapshot.HubReady && changed.GetProperty("outcome").GetString() == "writtenUnverified" &&
                    boardOnly.Rgb.Devices.Single().Zones.Single(z => z.HardwareZone == 2).Configuration == onlyScene.Board[0].Effect,
                    "A disconnected NZXT Hub does not block an M5 motherboard-only scene");
            }
            finally { demoHub.Disconnected = false; await boardOnly.Coordinator.OpenAsync(DemoHub.Identity); }
        }
    }
}
