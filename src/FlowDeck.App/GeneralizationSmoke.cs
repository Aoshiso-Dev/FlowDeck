using System.Windows.Controls;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    private static async Task RunGeneralizationAsync(FlowDeckApplication app, RuntimeHost host, FlowDeckViewModel vm, FlowDeckWindow window, string output)
    {
        if (!host.Demo || host.Hub is not DemoHub hub) throw new InvalidOperationException("This acceptance run requires isolated demo hardware");
        var report = new List<string>();
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); report.Add("PASS " + message); }
        try
        {
            await Until(() => vm.DeviceCatalog.Count >= 5);
            Check(host.DeviceInventoryError is null, "Common catalog accepts the existing demo inventory");
            var rows = vm.DeviceCatalog.ToArray(); await host.Coordinator.TickAsync(); await Settle();
            Check(rows.All(vm.DeviceCatalog.Contains), "Catalog retains row objects across periodic refresh");
            vm.SelectedPage = 3; await Settle();
            var catalog = Descendants<Expander>(window).Single(e => e.Header as string == "検出した機器と対応機能");
            catalog.IsExpanded = true; catalog.BringIntoView(); await Settle();
            foreach (var theme in new[] { "light", "dark" })
            { ThemeManager.Apply(theme); await Settle(); Capture(window, output, "device-catalog-" + theme); }
            Check(Descendants<TextBlock>(catalog).Any(t => t.IsVisible && t.Text.Contains("センサー")), "Common inventory renders monitoring capabilities");
            host.AddMemoryPreview();
            await host.Coordinator.StopControlAsync();
            await host.Coordinator.UpdateSettingsAsync(host.Coordinator.Settings with { Hardware = new() });
            hub.Disconnected = true; await host.Coordinator.TickAsync(); await Settle();
            Check(!host.Coordinator.Snapshot.HubReady, "Hub is absent for the following UI integration checks");
            var targets = new BoardLightAddress[] { new("demo-asrock", 2), new(MemoryLighting.Identity(0x71), 0), new(MemoryLighting.Identity(0x73), 0) };
            var writes = hub.LightEffects.Count;
            await host.StartLightingSyncAsync([], "breathing", new(30, 60, 210), 2, targets);
            await Until(() => host.LightingFrames >= 3);
            Check(host.LightingSynchronized && host.ActiveLightingSync is { HubId: "", Channels.Length: 0 }, "Shared animation starts with no configured or connected Hub");
            Check(hub.LightEffects.Count == writes, "Hub-independent animation never writes Hub lighting");
            var frame = host.Rgb;
            Check(targets.Select(t => frame.Devices.Single(d => d.ControlIdentity == t.Identity).Zones.Single(z => z.HardwareZone == t.Zone).Configuration)
                .Distinct().Count() == 1, "Motherboard and both DIMMs show the same sampled frame");
            var scene = await host.CaptureLightingSceneAsync(Guid.NewGuid().ToString("N"), "Hubなし", false, true);
            Check(scene.Sync?.Memory?.Length == 2 && LightingScenes.Validate(scene) is null, "Shared motherboard and DIMM targets survive scene capture");
            await host.StopLightingSyncAsync();
            vm.SelectedPage = 2; vm.HubLightTarget = "none"; vm.HubLightSync = true;
            await host.Coordinator.TickAsync(); await Settle();
            Check(vm.CanApplyHubLight, "Lighting Apply is available without a Hub");
            window.HubLightingCard.BringIntoView(); await Settle(); Capture(window, output, "hub-independent-lighting");
            var applied = await host.ApplyLightingSceneAsync(scene);
            Check(applied.Succeeded, "Hub-independent scene recalls successfully: " + (applied.Error ?? "OK"));
            await host.StopLightingSyncAsync();
        }
        catch (Exception e) { report.Add("FAIL " + e); Environment.ExitCode = 1; }
        finally
        {
            hub.Disconnected = false;
            await File.WriteAllLinesAsync(Path.Combine(output, "generalization-smoke.txt"), report);
            try { await app.ExitAsync(); }
            catch (Exception e)
            {
                await File.AppendAllTextAsync(Path.Combine(output, "generalization-smoke.txt"), "\nFAIL teardown " + e);
                app.Shutdown(1);
            }
        }
    }
}
