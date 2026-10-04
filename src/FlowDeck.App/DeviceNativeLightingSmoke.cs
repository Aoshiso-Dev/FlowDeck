using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceNativeLightingAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, DeviceWorkspaceClient client, Window window, string output)
    {
        var report = new List<string>();
        void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); report.Add("PASS " + text); }
        try
        {
            model.SelectedPage = 2; await Settle(); var targets = model.Lights.Select(l => l.Target).ToArray(); model.SelectLights(targets);
            Check(model.LightEffects.Count(e => e.Id.StartsWith("native/")) == 7, "Both registered Hub endpoints advertise their common native modes through authenticated service inventory");
            var fan = model.Cooling.First(); fan.Mode = "固定"; fan.Duty = 55; await model.OperateAsync(fan); var fanWrites = demo.Hubs.Select(h => h.Writes).ToArray();
            var effect = Descendants<ComboBox>(window).Single(c => AutomationProperties.GetName(c) == "照明効果");
            effect.SelectedValue = "native/nzxt-control-hub/covering-marquee"; await Settle();
            Check(model.LightHasPalette && model.LightHasDirection && model.LightHasSpeed && model.CanPickLightColor && model.CanEditLightBrightness, "Choosing the visible native effect exposes its palette, direction and supported scalar controls");
            model.LightColor = Color.FromRgb(200, 0, 0); model.AddLightColor.Execute(null); await Settle(); model.LightColor = Color.FromRgb(0, 160, 0);
            model.AddLightColor.Execute(null); await Settle(); model.LightColor = Color.FromRgb(0, 0, 240); model.LightReverse = true; model.LightSpeed = 4; model.LightBrightness = 50;
            for (var i = 0; i < 3; i++) await model.PollAsync(); await Settle();
            Check(model.LightPalette.Length == 3 && model.SelectedLightColor == 2 && model.LightReverse && model.LightSpeed == 4 && model.LightBrightness == 50 && model.CanApplyLighting && demo.Hubs.All(h => h.LightingWrites == 0), "Unsent palette, selected swatch, direction and slider values survive polling without output");
            foreach (var theme in new[] { "light", "dark" }) { model.Theme = theme; effect.BringIntoView(); await Settle(); Capture(window, output, "native-lighting-" + theme); }
            await model.PollAsync(); await model.ApplyLightsAsync();
            var wanted = new[] { new RgbColor(100, 0, 0), new(0, 80, 0), new(0, 0, 120) };
            Check(demo.Hubs.All(h => h.LightEffects["led1"] is { Mode: "covering-marquee", Speed: 4, Reverse: true } p && p.Colors.SequenceEqual(wanted)), "Explicit native apply reaches both compiled Hub drivers with the complete ordered palette and one brightness scale");
            await model.PollAsync(); Check(!model.LightingDraft.Dirty && !model.LightingDraft.Mixed && model.LightPalette.Length == 3 && model.LightReverse, "Applied native preferences return to the editor without becoming mixed or downgrading to a common effect");
            model.LightSceneName = "内蔵パレット"; await model.SaveLightLibraryAsync(true); var saved = client.Snapshot!.Configuration.Configuration.Lighting.Scenes.Single();
            Check(saved.Individual.All(i => i.Program.Reverse && i.Program.Speed == 4 && i.Program.Brightness == 50 && i.Program.Colors.Length == 3), "Saving a scene retains native effect IDs and unscaled palette, direction and brightness");
            model.SelectedLightColor = 0; model.RemoveLightColor.Execute(null); await Settle();
            Check(model.LightPalette.Length == 2 && model.LightingDraft.Program.Colors[0] == new RgbColor(0, 160, 0), "Removing a selected palette color keeps the order of remaining colors");
            model.LightEffect = "native/nzxt-control-hub/fading"; while (model.AddLightColor.CanExecute(null)) model.AddLightColor.Execute(null);
            Check(model.LightPalette.Length == 8 && !model.AddLightColor.CanExecute(null) && !model.LightHasDirection && !model.LightReverse, "Palette bounds and unavailable direction are enforced when switching effects");
            model.LightEffect = "native/nzxt-control-hub/spectrum-wave"; await Settle();
            Check(!model.CanPickLightColor && !model.CanEditLightBrightness && model.LightBrightness == 100 && model.LightPalette.Length == 0 && model.LightHasDirection && model.LightPreviewLabel == "機器内蔵効果", "A hardware-generated spectrum exposes no unsupported color or brightness editor and no fabricated animation preview");
            await model.ApplyLightsAsync(); Check(demo.Hubs.All(h => h.LightEffects["led1"].Mode == "spectrum-wave"), "The native spectrum is sent as a device effect rather than a software frame stream");
            await model.RecallLightSceneAsync(); Check(demo.Hubs.All(h => h.LightEffects["led1"].Colors.SequenceEqual(wanted) && h.LightEffects["led1"].Reverse), "Scene recall restores the native palette and direction");
            model.LightEffect = "breathing"; await Settle();
            Check(!model.LightHasPalette && !model.LightHasDirection && !model.LightReverse && model.LightSpeedMaximum == 4 && model.LightingDraft.Program.Colors.Length == 1, "Switching explicitly to a common effect normalizes the native-only parameters");
            await model.ApplyLightsAsync(); model.LightEffect = "native/nzxt-control-hub/breathing"; await model.ApplyLightsAsync();
            Check(demo.Runtime.State().Endpoints.Where(e => targets.Contains(e.Target)).All(e => e.Lighting?.Running != true) && demo.Hubs.All(h => h.LightEffects["led1"].Mode == "breathing"), "Switching from software breathing stops the old stream before starting native breathing");
            // UI-only capability fixture for the memory controls. It never sends the synthetic selection to the Hub service.
            var snapshot = client.Snapshot!; var state = snapshot.State with { Inventory = snapshot.State.Inventory with { Devices = snapshot.State.Inventory.Devices.Select(d =>
                d with { Device = d.Device with { Endpoints = d.Device.Endpoints.Select(e => e.Kind == EndpointKind.Lighting ? e with { NativeLightingEffects = BuiltInLightingEffects.Memory() } : e).ToArray() } }).ToArray() } };
            model.LightingDraft.Update(state, snapshot.Configuration.Generation, snapshot.Configuration.Configuration.Lighting, 0, true);
            foreach (var choice in BuiltInLightingEffects.Memory()) model.LightEffects.Add(new(choice.Id, choice.Name + " · メモリ内蔵"));
            var lightWrites = demo.Hubs.Select(h => h.LightingWrites).ToArray(); effect.SelectedValue = "native/ene-memory/2"; await Settle();
            var speed = Descendants<Slider>(window).Single(s => AutomationProperties.GetName(s) == "照明効果の速さ");
            Check(speed.Maximum == 255 && speed.Value == 224 && model.LightSpeed == 224 && model.LightAllowsPerLed, "Memory capability UI uses the advertised 0-255 speed and default without WPF coercing it to the common 0-4 range");
            model.LightPerLed = true; model.SelectedLightColor = 7; model.LightColor = Color.FromRgb(150, 40, 210); model.LightSpeed = 201; await Settle();
            Check(model.LightPalette.Length == 8 && !model.AddLightColor.CanExecute(null) && !model.RemoveLightColor.CanExecute(null) && model.LightingDraft.Program.LedColors![7] == new RgbColor(150, 40, 210) && model.LightSpeed == 201, "UI-only memory fixture edits exactly eight LED colors without changing the palette length");
            effect.BringIntoView(); await Settle(); Capture(window, output, "native-memory-controls");
            model.LightPerLed = false; Check(model.LightingDraft.Program.LedColors is null && lightWrites.SequenceEqual(demo.Hubs.Select(h => h.LightingWrites)), "Leaving per-LED editing changes only the draft and the memory UI fixture never actuates Hub hardware");
            model.LightingDraft.Reset(); model.Render(); model.LightEffect = "native/nzxt-control-hub/covering-marquee";
            window.Width = 960; window.Height = 640; effect.BringIntoView(); await Settle(); Capture(window, output, "native-lighting-minimum");
            Check(Descendants<ColorPicker>(window).Single(p => ReferenceEquals(p.DataContext, model)).ActualWidth >= 440 && model.CanApplyLighting, "Native controls remain usable at the minimum window width");
            Check(fanWrites.SequenceEqual(demo.Hubs.Select(h => h.Writes)) && fan.CurrentDuty == "55%", "Native lighting and UI-only memory edits leave cooling control unchanged");
            Check(await model.CloseAsync(), "Normal shutdown releases native lighting ownership and restores cooling");
            await File.WriteAllLinesAsync(Path.Combine(output, "native-lighting-smoke.txt"), report); return 0;
        }
        catch (Exception e) { report.Add("FAIL " + e); report.Add("STATE " + model.LightEditStatus + " / " + model.Notice); await File.WriteAllLinesAsync(Path.Combine(output, "native-lighting-smoke.txt"), report); return 1; }
    }
}
