using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    private static async Task CheckDeviceNativeSceneAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, DeviceWorkspaceClient client, Window window, string output, Action<bool, string> check)
    {
        var fanWrites = demo.Hubs.Select(h => h.Writes).ToArray(); var untouchedWrites = demo.Hubs[1].LightingWrites;
        var panel = Descendants<DeviceLightingLibraryView>(window).Single(); model.OpenLightLibrary(LightingPresetKind.Scene);
        var editor = model.LightLibraryEditor!; editor.Name = "パート別の内蔵照明"; var first = editor.SelectedPart!;
        window.Width = 1120; window.Height = 820; panel.BringIntoView(); await Settle();
        var effect = Descendants<ComboBox>(panel).Single(c => AutomationProperties.GetName(c) == "保存するシーンの効果");
        effect.SelectedValue = "native/nzxt-control-hub/covering-marquee"; await Settle();
        check(editor.HasPalette && editor.HasDirection && editor.HasSpeed && editor.CanPickColor && model.Notice == "", "Scene part native selection exposes its supported controls and opening a new editor clears obsolete operation messages");
        editor.Color = Color.FromRgb(200, 0, 0); editor.AddColor.Execute(null); await Settle(); editor.Color = Color.FromRgb(0, 100, 200); editor.Reverse = true; editor.Speed = 4; editor.Brightness = 60;
        editor.AddPart(); var second = editor.SelectedPart!; editor.Effect = "native/nzxt-control-hub/fading"; editor.Color = Color.FromRgb(0, 160, 0); editor.Speed = 1; editor.Brightness = 70;
        editor.SelectedPart = first; await Settle();
        check(editor.Palette.Length == 2 && editor.SelectedColor == 1 && editor.Reverse && editor.Speed == 4 && editor.Brightness == 60, "Switching scene parts restores the selected swatch and every independent native parameter");
        var speed = Descendants<Slider>(panel).Single(s => AutomationProperties.GetName(s) == "保存するシーンの速さ");
        check(speed.Maximum == 4 && speed.Value == 4 && Descendants<TextBlock>(effect).Any(t => t.Text == "流れる光 · 機器内蔵" && t.ActualWidth > 0), "Part switching updates the actual native label and slider value");
        var name = Descendants<DraftTextBox>(panel).Single(); name.Focus(); name.Text = "入力中の内蔵シーン";
        for (var i = 0; i < 3; i++) await model.PollAsync(); await Settle();
        check(ReferenceEquals(editor, model.LightLibraryEditor) && name.Text == "入力中の内蔵シーン" && editor.SelectedColor == 1 && editor.Palette.Length == 2 && editor.Reverse,
            "Polling preserves Japanese scene input, native colors, direction and current swatch");
        check(demo.Hubs[1].LightingWrites == untouchedWrites, "Editing native scene parts does not overwrite current fixed output");
        foreach (var theme in new[] { "light", "dark" }) { model.Theme = theme; effect.BringIntoView(); await Settle(); Capture(window, output, "native-scene-" + theme); }
        await model.PollAsync(); await model.SaveLightLibraryEditorAsync(); var sceneId = model.SelectedLightScene!.Id;
        var saved = client.Snapshot!.Configuration.Configuration.Lighting.Scenes.Single(s => s.Id == sceneId);
        check(saved.Requirements is { Length: 2 } && saved.Requirements.All(r => r.Feature == DeviceFeature.NativeEffect) && saved.Individual[0].Program.Reverse && saved.Individual[0].Program.Colors.Length == 2 && demo.Hubs[1].LightingWrites == untouchedWrites,
            "Saving records exact native requirements and independent unscaled programs without applying them");
        await model.RecallLightSceneAsync();
        check(demo.Hubs[0].LightEffects["led1"] is { Mode: "covering-marquee", Reverse: true, Speed: 4 } a && a.Colors.SequenceEqual(new[] { new RgbColor(120, 0, 0), new(0, 60, 120) }) &&
            demo.Hubs[1].LightEffects["led1"] is { Mode: "fading", Reverse: false, Speed: 1 } b && b.Colors.SequenceEqual(new[] { new RgbColor(0, 112, 0) }),
            "Explicit scene recall applies each native program through its compiled driver with brightness scaled once");
        var appliedWrites = demo.Hubs.Select(h => h.LightingWrites).ToArray(); model.OpenLightLibrary(LightingPresetKind.Scene, sceneId); editor = model.LightLibraryEditor!;
        editor.SelectedPart = editor.Parts[1].Id; editor.Effect = "native/nzxt-control-hub/spectrum-wave"; await Settle();
        check(!editor.CanPickColor && !editor.CanEditBrightness && editor.Brightness == 100 && editor.HasDirection && !editor.HasPalette, "The scene editor hides unsupported spectrum color and brightness operations");
        editor.SelectedPart = editor.Parts[0].Id;
        check(editor.Palette.Length == 2 && editor.Reverse && editor.Brightness == 60, "An effect change in one part leaves the other part's palette and brightness intact");
        await model.SaveLightLibraryEditorAsync(); check(appliedWrites.SequenceEqual(demo.Hubs.Select(h => h.LightingWrites)), "Updating a stored native scene never applies it implicitly");
        // Only the editor receives this memory capability fixture. No synthetic memory program is saved or applied.
        var snapshot = client.Snapshot!; var memoryState = snapshot.State with { Inventory = snapshot.State.Inventory with { Devices = snapshot.State.Inventory.Devices.Select(d =>
            d with { Device = d.Device with { Endpoints = d.Device.Endpoints.Select(e => e.Kind == EndpointKind.Lighting ? e with { NativeLightingEffects = BuiltInLightingEffects.Memory() } : e).ToArray() } }).ToArray() } };
        editor = model.LightLibraryEditor!; editor.SelectedPart = editor.Parts[1].Id; editor.Update(snapshot with { State = memoryState }); await Settle();
        check(editor.CapabilityIssue is not null && !editor.CanPickColor && editor.Effect == "native/nzxt-control-hub/spectrum-wave", "Changed capabilities keep the unsupported stored program visible without converting it by display name");
        effect.SelectedValue = "native/ene-memory/2"; await Settle(); editor.PerLed = true; editor.SelectedColor = 7; editor.Color = Color.FromRgb(150, 50, 200); editor.Speed = 201;
        await Settle(); var memoryRequirement = editor.Draft.Capture().Request.Scene!.Requirements!.Single(r => r.EffectId == "native/ene-memory/2");
        check(speed.Maximum == 255 && speed.Value == 201 && editor.Palette.Length == 8 && !editor.AddColor.CanExecute(null) && !editor.RemoveColor.CanExecute(null) && memoryRequirement.LedCount == 8 && memoryRequirement.Speed == 201,
            "UI-only memory scene editing retains eight LED colors and its 0-255 speed in captured requirements");
        var memoryPart = editor.SelectedPart!; editor.SelectedPart = editor.Parts[0].Id; editor.SelectedPart = memoryPart; await Settle();
        check(editor.SelectedColor == 7 && editor.Color == Color.FromRgb(150, 50, 200) && editor.Speed == 201, "Per-LED selection and values survive switching away from and back to a memory part");
        effect.BringIntoView(); await Settle(); Capture(window, output, "native-scene-memory-controls");
        await model.ReloadLightLibraryEditorAsync(); editor = model.LightLibraryEditor!;
        check(editor.Draft.Parts.All(p => p.Program.EffectId.StartsWith("native/nzxt-control-hub/")) && appliedWrites.SequenceEqual(demo.Hubs.Select(h => h.LightingWrites)),
            "Discarding the UI-only memory draft restores saved Hub definitions and has no hardware side effect");
        window.Width = 960; window.Height = 640; effect.BringIntoView(); await Settle(); Capture(window, output, "native-scene-minimum");
        var save = Descendants<Button>(panel).Single(b => b.Content?.ToString() == "保存"); save.BringIntoView(); await Settle();
        check(Descendants<ColorPicker>(panel).Single().ActualWidth >= 440 && save.IsVisible, "Native scene controls and save actions remain scroll-accessible at minimum window size");
        check(fanWrites.SequenceEqual(demo.Hubs.Select(h => h.Writes)), "Native scene editing, saving and recall preserve cooling control");
    }
}
