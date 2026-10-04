using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    private static async Task RunLightingIdentificationChecksAsync(RuntimeHost host, FlowDeckViewModel vm, FlowDeckWindow window, string output, Action<bool,string> check)
    {
        vm.SelectedPage = 2; vm.SelectedLightingGroup = vm.LightingGroupChoices[0];
        await host.StopLightingSyncAsync(); await Settle();
        var core = host.Coordinator; var demo = (DemoHub)host.Hub;
        var fanRevision = core.Snapshot.ControlRevision; var fanWrites = demo.Writes;
        var hardware = Contract.Copy(core.Settings.Hardware);
        var hub = new LightAddress(Hub: new(DemoHub.Identity.Id, "led1")); var board = new LightAddress(Board: new("demo-asrock",2));
        var original = new HubLightSetting("fading", [new(21,42,86),new(180,25,40)], 4, true);
        await host.ApplyHubLightingAsync(new(Guid.NewGuid().ToString("N"), "識別試験", core.Epoch, core.Snapshot.LightingRevision, "SetLighting", LightingEffect: original, LightingChannels: ["led1"]));
        await Settle(); vm.RevertHubLight.Execute(null);
        vm.IdentificationTarget = vm.IdentificationTargets.Single(t => t.Address == hub);
        var brightnessBefore = Contract.Copy(host.LightingAdjustment);
        vm.HubLightTarget = "led1"; await Settle();
        var revision = core.Snapshot.LightingRevision;
        vm.LightAlias = "前面ファン"; vm.SaveLightAlias.Execute(null); await Until(() => vm.LightingScenesIdle && vm.IdentificationResult.Contains("保存しました"));
        check(vm.HubLightTargets.Single(t => t.Id == "led1").Name == "前面ファン · LED1" && vm.GroupTargetChoices.Any(t => t.Name.Contains("前面ファン")) &&
            core.Snapshot.LightingRevision == revision && Contract.Copy(core.Settings.Hardware).WiringSignature == hardware.WiringSignature,
            "Friendly names update native and group selectors without changing outputs or detected wiring");
        check(window.HubLightTargetPicker.SelectedItem is HubLightTarget { Id: "led1", Name: "前面ファン · LED1" } && vm.HubLightTarget == "led1",
            "Renaming a selected Hub port preserves its displayed selection");
        vm.IdentificationTarget = vm.IdentificationTargets.Single(t => t.Address == board); vm.LightAlias = "天面ライト"; vm.SaveLightAlias.Execute(null);
        await Until(() => vm.LightingScenesIdle && vm.IdentificationResult.Contains("保存しました"));
        check(vm.BoardLightTargets.Any(t => t.Name.StartsWith("天面ライト")) && vm.SyncBoardChoices.Any(t => t.Name.Contains("天面ライト")),
            "Motherboard names are shared by the native editor and synchronization targets");
        vm.ReloadLightAliases.Execute(null); await Until(() => vm.LightingScenesIdle && vm.IdentificationResult.Contains("再読み込み"));
        var loadedNames = await new LightingNameStore(host.Store.DirectoryPath).LoadAsync();
        check(LightingNames.Find(loadedNames, hub) == "前面ファン" && LightingNames.Find(loadedNames, board) == "天面ライト" && LightingNames.Find(loadedNames, new(Board: new("another-board",2))) is null,
            "Lighting names reload by physical identity and never attach to another motherboard");
        var zonesBefore = host.Rgb.Devices.Single().Zones.Select(z => z.Configuration).ToArray();
        vm.IdentificationTarget = vm.IdentificationTargets.Single(t => t.Address == hub);
        vm.HubLightMode = "breathing"; // An unsubmitted draft must survive identification.
        vm.IdentifyLight.Execute(null); await Until(() => host.IdentifyingLight && demo.LightEffects["led1"].Colors.SequenceEqual(new[]{new RgbColor(96,96,96)}));
        check(!vm.ApplyBrightness.CanExecute(null) && !vm.ApplyHubLight.CanExecute(null) && vm.CancelIdentification.CanExecute(null) &&
            host.Rgb.Devices.Single().Zones.Select(z => z.Configuration).SequenceEqual(zonesBefore),
            "Identification lights just the selected Hub port and disables queued GUI lighting actions while leaving other devices unchanged");
        await Until(() => !vm.IdentificationBusy && vm.IdentificationResult.Contains("元の設定"));
        check(ContractEqual(demo.LightEffects["led1"], original) && vm.HubLightMode == "breathing" && ContractEqual(host.LightingAdjustment, brightnessBefore),
            "Timed identification restores the entire Hub effect including both colors, speed and direction without losing a draft or brightness baseline");
        vm.ApplyHubLight.Execute(null); await Until(() => vm.HubLightIdle && core.Snapshot.Lighting.Effect?.Mode == "breathing");
        check(vm.HubLightResult == "送信済み", "A draft that was current before successful identification remains applicable afterwards");
        var zoneOriginal = host.Rgb.Devices.Single().Zones.Single(z => z.HardwareZone == 2).Configuration!;
        vm.IdentificationTarget = vm.IdentificationTargets.Single(t => t.Address == board); vm.IdentifyLight.Execute(null);
        await Until(() => host.IdentifyingLight && host.Rgb.Devices.Single().Zones.Single(z => z.HardwareZone == 2).Configuration == new BoardLightSetting(1,96,96,96));
        check(host.Rgb.Devices.Single().Zones.Where(z => z.HardwareZone != 2).Select(z => z.Configuration).SequenceEqual(zonesBefore.Where((_,i) => host.Rgb.Devices.Single().Zones[i].HardwareZone != 2)),
            "Motherboard identification changes only the selected hardware zone");
        vm.CancelIdentification.Execute(null); await Until(() => !vm.IdentificationBusy && vm.IdentificationResult.Contains("中断"));
        check(host.Rgb.Devices.Single().Zones.Single(z => z.HardwareZone == 2).Configuration == zoneOriginal,
            "Canceling identification restores the motherboard setting before returning");
        var identify = host.IdentifyLightAsync(hub); await Until(() => host.IdentifyingLight && core.Snapshot.Lighting.Effect?.Colors.FirstOrDefault() == new RgbColor(96,96,96));
        var newer = await core.RequestAsync(new(Guid.NewGuid().ToString("N"), "M5GO", core.Epoch, core.Snapshot.LightingRevision, "SetLighting", LightingEffect: HubLighting.Preset("purple"), LightingChannels: ["led1"]));
        var superseded = await identify;
        check(newer.Outcome == Outcome.WrittenUnverified && superseded.Superseded && !superseded.Restored && ContractEqual(demo.LightEffects["led1"], HubLighting.Preset("purple")),
            "A newer M5GO operation wins over the pending automatic Hub restoration");
        using (var cancel = new CancellationTokenSource())
        {
            var outsideChange = host.IdentifyLightAsync(board, cancel.Token); await Until(() => host.IdentifyingLight && host.Rgb.Devices.Single().Zones.Single(z => z.HardwareZone == 2).Configuration == new BoardLightSetting(1,96,96,96));
            // Simulate a different RGB program changing this zone while the demo transaction is waiting.
            var zones = host.Rgb.Devices.Single().Zones; var index = Array.FindIndex(zones,z => z.HardwareZone == 2); var external = new BoardLightSetting(2,10,20,30,80);
            zones[index] = zones[index] with { Configuration = external }; cancel.Cancel();
            var refused = await outsideChange;
            check(!refused.Restored && zones[index].Configuration == external, "A changed motherboard setting is not overwritten by identification restoration");
        }
        await host.StartLightingSyncAsync(["led1"], "breathing", new(10,20,30), 3, [new("demo-asrock",2)]); await Settle();
        var frames = host.LightingFrames; var sync = host.ActiveLightingSync;
        var syncRejected = false; try { await host.IdentifyLightAsync(board); } catch (InvalidOperationException) { syncRejected = true; }
        await Task.Delay(250);
        check(syncRejected && !vm.CanIdentifyLight && host.LightingSynchronized && host.ActiveLightingSync == sync && host.LightingFrames > frames,
            "Identification during synchronization is rejected without interrupting the shared animation");
        await host.StopLightingSyncAsync(); await Settle();
        demo.FailNextLightingWrite = true;
        var beforeFailure = Contract.Copy(core.Settings.LightingPorts["led1"].Effect!); var failed = await host.IdentifyLightAsync(hub);
        check(!failed.Succeeded && failed.Restored && ContractEqual(core.Settings.LightingPorts["led1"].Effect, beforeFailure),
            "An uncertain temporary Hub write still attempts and verifies transmission of the original effect");
        var missingRejected = false; try { await host.IdentifyLightAsync(new(Hub: new("missing-hub", "led1"))); } catch (IOException) { missingRejected = true; }
        check(missingRejected, "An absent physical device is rejected before an identification write");
        using (var cancel = new CancellationTokenSource())
        {
            var cancelHub = host.IdentifyLightAsync(hub, cancel.Token); await Until(() => host.IdentifyingLight); cancel.Cancel();
            check((await cancelHub).Restored, "Hub identification also restores when canceled early");
        }
        await Settle(); vm.IdentificationTarget = vm.IdentificationTargets.Single(t => t.Address == hub);
        vm.HubLightTarget = "all"; await Settle();
        vm.LightAlias = ""; vm.SaveLightAlias.Execute(null); await Until(() => vm.LightingScenesIdle && vm.IdentificationResult.Contains("標準の名前"));
        check(vm.HubLightTargets.Single(t => t.Id == "led1").Name == "LED1" && LightingNames.Find(host.LightNames, board) == "天面ライト", "Clearing one alias restores its standard name and preserves other aliases");
        check(window.HubLightTargetPicker.SelectedItem is HubLightTarget { Id: "all" } && vm.HubLightTarget == "all",
            "Refreshing names also preserves the all-ports selection when its label did not change");
        vm.LightAlias = "前面ファン"; vm.SaveLightAlias.Execute(null); await Until(() => vm.LightingScenesIdle && vm.IdentificationResult.Contains("保存しました"));
        window.LightingIdentificationExpander.IsExpanded = true; window.Width = 1200; window.Height = 900; window.PageScroll.ScrollToTop();
        ThemeManager.Apply("light"); await Settle(); Capture(window, output, "lighting-identification-light");
        ThemeManager.Apply("dark"); await Settle(); Capture(window, output, "lighting-identification-dark");
        window.Width = 960; window.Height = 640; await Settle();
        check(window.IdentificationTargetPicker.ActualWidth > 240 && window.LightAliasBox.ActualWidth > 150 && window.LightingIdentificationCard.ActualWidth < window.ActualWidth - 150,
            "The identification and naming panel fits the minimum window width");
        Capture(window, output, "lighting-identification-minimum");
        window.LightingIdentificationExpander.IsExpanded = false; window.Width = 1200; window.Height = 900; ThemeManager.Apply("light");
        check(core.Snapshot.ControlRevision == fanRevision && demo.Writes == fanWrites && core.Snapshot.ConfirmedDuty == 59,
            "Identification and naming never modify fan output or cooling ownership");
        await Settle(); vm.RevertHubLight.Execute(null);
        var scope = vm.LightingGroupChoices.First(g => g.Group?.Name == "ケース"); vm.SelectedLightingGroup = scope; await Settle();
        check(vm.IdentificationTargets.All(t => t.Hub is not null && scope.Group!.Hub.Contains(t.Hub) || t.Board is not null && scope.Group!.Board.Contains(t.Board)),
            "The identification selector follows the active lighting group's exact membership");
        vm.SelectedLightingGroup = vm.LightingGroupChoices[0]; await Settle();
        await using var isolated = new RuntimeHost(Path.Combine(output, "identify-failure-check"), demoOnly: true);
        await isolated.InitializeAsync(true); var isolatedCore = isolated.Coordinator; var isolatedHub = (DemoHub)isolated.Hub;
        var unconfiguredRevision = isolatedCore.Snapshot.LightingRevision; var unknownRejected = false;
        try { await isolated.IdentifyLightAsync(hub); } catch (IOException e) { unknownRejected = e.Message.Contains("元の設定が不明"); }
        check(unknownRejected && isolatedCore.Snapshot.LightingRevision == unconfiguredRevision && isolatedHub.LightEffects.IsEmpty,
            "An unknown previous Hub setting prevents identification before any hardware write");
        await isolated.ApplyHubLightingAsync(new(Guid.NewGuid().ToString("N"), "識別試験", isolatedCore.Epoch, isolatedCore.Snapshot.LightingRevision, "SetLighting", LightingEffect: original, LightingChannels: ["led1"]));
        using (var cancel = new CancellationTokenSource())
        {
            var unplugged = isolated.IdentifyLightAsync(hub, cancel.Token); await Until(() => isolated.IdentifyingLight);
            try
            {
                isolatedHub.Disconnected = true; cancel.Cancel(); var result = await unplugged;
                check(!result.Restored && !result.Succeeded && !isolated.IdentifyingLight,
                    "Disconnecting during identification reports incomplete restoration and releases the operation gate");
            }
            finally { isolatedHub.Disconnected = false; await isolatedCore.TickAsync(); }
        }
        var namesPath = new LightingNameStore(isolated.Store.DirectoryPath).PathName;
        await File.WriteAllTextAsync(namesPath, "{broken"); await isolated.LoadLightNamesAsync(); var saveRefused = false;
        try { await isolated.SaveLightNameAsync(hub, "上書きしない"); } catch (IOException) { saveRefused = true; }
        check(saveRefused && isolated.LightNamesError is not null && await File.ReadAllTextAsync(namesPath) == "{broken",
            "The runtime blocks alias saves after corruption and preserves the damaged file for recovery");
    }
    private static bool ContractEqual<T>(T left, T right) => System.Text.Json.JsonSerializer.Serialize(left, Contract.Json) == System.Text.Json.JsonSerializer.Serialize(right, Contract.Json);
}
