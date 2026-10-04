using FlowDeck.Core;
using System.Windows;
using System.Windows.Media;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    private static async Task RunMemoryLightingChecksAsync(RuntimeHost host, FlowDeckViewModel vm, FlowDeckWindow window, string output, Action<bool,string> check)
    {
        vm.SelectedLightingGroup = vm.LightingGroupChoices[0]; host.AddMemoryPreview(); vm.SelectedPage = 2; vm.Refresh(); await Until(() => vm.MemoryLightTargets.Count == 3);
        check(vm.MemoryLightTargets.Count == 3 && vm.BoardLightTargets.All(t => !MemoryLighting.IsIdentity(t.Identity)), "Memory card separates two DIMMs from motherboard targets");
        var fanRevision = host.Coordinator.Snapshot.ControlRevision;
        vm.MemoryLightTarget = vm.MemoryLightTargets.Single(t => t.Id == MemoryLighting.Identity(0x71));
        vm.MemoryLightEffect = 1; vm.MemorySelectedColor = Color.FromRgb(40, 80, 200); vm.MemoryLightBrightness = 50;
        await Settle(); check(vm.CanApplyMemoryLight, "Memory application requires an identified current setting");
        check(host.Rgb.Devices.Single(d => d.ControlIdentity == MemoryLighting.Identity(0x71)).Zones[0].Configuration!.Mode == 14, "Memory editing stays a draft until Apply");
        vm.ApplyMemoryLight.Execute(null); await Until(() => vm.MemoryLightResult.Contains("設定一致"));
        check(host.Rgb.Devices.Single(d => d.ControlIdentity == MemoryLighting.Identity(0x71)).Zones[0].Configuration == new BoardLightSetting(1,20,40,100), "DIMM color picker and brightness apply the scaled RGB");
        check(host.Rgb.Devices.Single(d => d.ControlIdentity == MemoryLighting.Identity(0x73)).Zones[0].Configuration!.Mode == 14, "Individual memory apply preserves the other DIMM");
        check(vm.SyncBoardChoices.Count(t => MemoryLighting.IsIdentity(t.Board?.Identity)) == 2, "Both DIMMs can be selected in shared lighting synchronization");
        vm.MemoryLightTarget = vm.MemoryLightTargets[0]; vm.MemoryLightEffect = 2; vm.MemoryLightSpeed = 75; vm.MemoryLightBrightness = 100;
        vm.ApplyMemoryLight.Execute(null); await Until(() => !host.SceneApplying && vm.BoardLightIdle && host.Rgb.Devices.Where(d => d.IsMemory).All(d => d.Zones[0].Configuration?.Mode == 2));
        check(host.Rgb.Devices.Where(d => d.IsMemory).All(d => d.Zones[0].Configuration!.Speed == 64), "All-memory native breathing uses five speed steps");
        var group = new LightingGroup(Guid.NewGuid().ToString("N"), "メモリ", [], [new(MemoryLighting.Identity(0x71),0),new(MemoryLighting.Identity(0x73),0)]);
        await host.SaveLightingGroupAsync(group);
        var scene = await host.CaptureLightingSceneAsync(Guid.NewGuid().ToString("N"), "メモリブルー", false, true, group);
        check(scene.Hub.Length == 0 && scene.Board.Length == 2 && scene.Board.All(b => MemoryLighting.IsIdentity(b.Identity)), "Memory scenes retain both DIMM identities without motherboard or Hub targets");
        var half = LightingBrightness.Build(scene,50); var applied = await host.ApplyLightingSceneAsync(half,group);
        check(applied.Succeeded, "Memory group brightness routes through the existing scene coordinator");
        await host.StartLightingSyncAsync(["led1"],"breathing",new(52,137,255),2);
        await Until(() => host.LightingFrames >= 3);
        check(host.ActiveLightingSync?.Memory?.Length == 2 && host.Rgb.Devices.Where(d => d.IsMemory).Select(d => d.Zones[0].Configuration).Distinct().Count() == 1,
            "Motherboard and both DIMMs receive the same sampled breathing frame");
        var synchronized = await host.CaptureLightingSceneAsync(Guid.NewGuid().ToString("N"),"全体同期",true,true);
        check(synchronized.Sync?.Memory?.Length == 2 && synchronized.Board.All(b => !MemoryLighting.IsIdentity(b.Identity)), "Synchronized memory targets survive scene capture without duplicate native targets");
        await host.StopLightingSyncAsync();
        check(host.Rgb.Devices.Where(d => d.IsMemory).All(d => d.Zones[0].Configuration == new BoardLightSetting(1,52,137,255)), "Stopping synchronized memory settles both modules at the selected fixed color");
        check(host.Coordinator.Snapshot.ControlRevision == fanRevision, "Memory lighting never changes fan policy");
        var hubLightingBefore = host.Coordinator.Snapshot.LightingRevision;
        await host.StartLightingSyncAsync([], "breathing", new(30, 60, 210), 2,
            [new("demo-asrock", 2), new(MemoryLighting.Identity(0x71), 0), new(MemoryLighting.Identity(0x73), 0)]);
        await Until(() => host.LightingFrames >= 3);
        check(host.ActiveLightingSync is { HubId: "", Channels.Length: 0, Memory.Length: 2 } &&
            host.Coordinator.Snapshot.LightingRevision == hubLightingBefore, "Board and memory synchronization does not acquire or write the Hub");
        var noHubScene = await host.CaptureLightingSceneAsync(Guid.NewGuid().ToString("N"), "Hubなし同期", false, true);
        check(noHubScene.Sync?.Channels.Length == 0 && LightingScenes.Validate(noHubScene) is null,
            "Hub-independent synchronized scene can be captured without including Hub settings");
        await host.StopLightingSyncAsync();
        var noHubApplied = await host.ApplyLightingSceneAsync(noHubScene);
        check(noHubApplied.Succeeded && host.LightingSynchronized, "Hub-independent synchronized scene can be recalled");
        await host.StopLightingSyncAsync();
        vm.Refresh(); window.Width = 1100; window.Height = 860; ThemeManager.Apply("light"); await Settle();
        window.MemoryLightingCard.BringIntoView(new Rect(0,0,window.MemoryLightingCard.ActualWidth,window.MemoryLightingCard.ActualHeight)); await Settle(); Capture(window, output, "memory-lighting-light");
        ThemeManager.Apply("dark"); window.Width = 960; window.Height = 640; await Settle(); window.MemoryLightingCard.BringIntoView(); await Settle(); Capture(window, output, "memory-lighting-minimum-dark");
        ThemeManager.Apply("light");
    }
}
