using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceLightingPersonalizationAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, DeviceWorkspaceClient client, Window window, string output)
    {
        var report = new List<string>();
        void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); report.Add("PASS " + text); }
        try
        {
            model.SelectedPage = 2; model.SelectLights(model.Lights.Select(l => l.Target)); await Settle();
            Check(model.LightPersonalizationSupported && model.LightAliasTargets.Count == 2 && model.LightSavedColors!.Favorites.Count == 0, "Service advertises registered alias targets and a fresh v2 palette without loading legacy colors");
            var fan = model.Cooling.First(); fan.Mode = "固定"; fan.Duty = 55; await model.OperateAsync(fan); var fanWrites = demo.Hubs.Select(h => h.Writes).ToArray();
            model.LightColor = Color.FromRgb(12, 34, 56); model.LightBrightness = 63;
            var picker = Descendants<ColorPicker>(window).Single(p => ReferenceEquals(p.DataContext, model));
            Check(ReferenceEquals(picker.Palette, model.LightSavedColors), "The visible color wheel shares the service-owned palette");
            await model.LightSavedColors!.ToggleFavoriteAsync(model.LightColor); await Settle();
            Check(model.LightSavedColors.Warning == "" && model.LightSavedColors.Favorites.Single().Hex == "#0C2238" && picker.FavoriteLabel == "★ 登録済み", "Saving a favorite updates the visible swatch and registration label");
            Check(model.CanApplyLighting && model.LightingDraft.Dirty && model.LightColor == Color.FromRgb(12,34,56) && model.LightBrightness == 63 && demo.Hubs.All(h => h.LightingWrites == 0), "Favorite persistence retains the unsent color and brightness without output or a false configuration conflict");
            model.OpenLightLibrary(LightingPresetKind.Scene); var editor = model.LightLibraryEditor!; editor.Name = "入力中のシーン";
            editor.Color = Color.FromRgb(77,88,99); await model.LightSavedColors.ToggleFavoriteAsync(editor.Color); await Settle();
            Check(ReferenceEquals(editor.SavedColors, model.LightSavedColors) && editor.Name == "入力中のシーン" && editor.Color == Color.FromRgb(77,88,99) && model.CanSaveLightLibraryEditor, "Scene parts share favorites while their unsaved name and program remain editable");
            model.CloseLightLibraryEditor.Execute(null); await Settle();
            var expander = Descendants<Expander>(window).Single(e => Equals(e.Header, "照明の名前")); expander.IsExpanded = true;
            model.LightAliasTarget = model.LightAliasTargets[0]; var target = model.LightAliasTarget.Target;
            await Settle();
            var name = Descendants<DraftTextBox>(window).Single(t => AutomationProperties.GetName(t) == "照明の名前");
            name.BringIntoView(); name.Focus(); name.Text = "前面ファン・変換中";
            for (var i = 0; i < 3; i++) await model.PollAsync(); await Settle();
            Check(name.Text == "前面ファン・変換中" && model.LightAlias == name.Text && model.CanSaveLightAlias, "Japanese alias input survives telemetry polling without losing text");
            await model.LightSavedColors.ToggleFavoriteAsync(Color.FromRgb(130,40,210));
            Check(name.Text == "前面ファン・変換中" && model.CanSaveLightAlias && model.CanApplyLighting, "Saving another favorite preserves simultaneous alias and output drafts");
            await model.SaveLightAliasAsync(); await Settle();
            Check(model.LightAliasTarget.Name.EndsWith("前面ファン・変換中") && model.Lights.Single(l => l.Target == target).Name.EndsWith("前面ファン・変換中") && !model.CanSaveLightAlias, "Alias save updates the stable target row and selector by endpoint identity");
            Check(model.CanApplyLighting && demo.Hubs.All(h => h.LightingWrites == 0), "Alias persistence preserves unsent output and does not actuate lighting");
            foreach (var theme in new[] { "light", "dark" }) { model.Theme = theme; picker.BringIntoView(); await Settle(); Capture(window, output, "lighting-colors-" + theme); }
            name.BringIntoView(); await Settle(); Capture(window, output, "lighting-alias-dark");
            await model.ApplyLightsAsync(); await Settle();
            Check(model.LightSavedColors.Recent.First().Hex == "#0C2238" && client.Snapshot!.Configuration.Configuration.Preferences.Palette.Recent.First() == "#0C2238", "Explicit apply records the unscaled base color in service-owned recent colors");
            var writes = demo.Hubs.Select(h => h.LightingWrites).ToArray(); picker.Choose(Color.FromRgb(130,40,210)); await Settle();
            Check(model.LightColor == Color.FromRgb(130,40,210) && model.LightingDraft.Dirty && writes.SequenceEqual(demo.Hubs.Select(h => h.LightingWrites)), "Choosing a stored color edits only the draft until explicit apply");
            model.LightAlias = "残す入力";
            await client.SaveLightingEditAsync(DeviceLightingPersonalization.Capture(client.Snapshot!, LightingLibraryOperation.SaveAlias, new(target, "別の編集")));
            await model.PollAsync();
            Check(model.LightAlias == "残す入力" && !model.CanSaveLightAlias && model.LightAliasDraft!.Conflict && model.LightingDraft.Conflict, "An external settings edit preserves local text and blocks stale alias and output writes");
            model.ReloadLightAlias(); model.ReloadLights.Execute(null); await Settle();
            Check(model.LightAlias == "別の編集" && !model.LightAliasDraft!.Conflict && model.CanApplyLighting, "Explicit reload adopts the new alias and current output");
            model.LightAlias = ""; await model.SaveLightAliasAsync();
            Check(!client.Snapshot!.Configuration.Configuration.Lighting.Names.Any(n => n.Target == target) && model.LightAliasTarget.Name.EndsWith("LED1"), "Saving an empty alias restores the registered default name");
            var palette = model.LightSavedColors; await palette.ToggleFavoriteAsync(Color.FromRgb(12,34,56));
            Check(!palette.Contains(Color.FromRgb(12,34,56)) && writes.SequenceEqual(demo.Hubs.Select(h => h.LightingWrites)), "Removing a favorite does not alter the applied lighting");
            var another = new DeviceWorkspaceViewModel(client); await another.InitializeAsync();
            Check(another.LightSavedColors!.Favorites.Select(c => c.Hex).SequenceEqual(palette.Favorites.Select(c => c.Hex)) && another.LightSavedColors.Recent[0].Hex == "#0C2238", "A new GUI model reads favorites and recent colors from the service configuration");
            // UI-only removal fixture: no service settings or real registrations are changed.
            var snapshot = client.Snapshot!; var config = snapshot.Configuration.Configuration;
            var removed = snapshot with { Configuration = snapshot.Configuration with { Generation = new('f',32), Configuration = config with {
                Devices = config.Devices with { Devices = config.Devices.Devices.Select(d => d with { Endpoints = d.Endpoints.Where(e => new EndpointReference(d.Id,e.Id) != target).ToArray() }).ToArray() } } } };
            model.RenderLightPersonalization(removed); await Settle();
            Check(model.LightAliasTarget?.Target == target && model.LightAliasDraft!.Conflict && !model.CanSaveLightAlias, "Removing an unedited registered target retains its identity without reconstructing an invalid alias editor");
            model.ReloadLightAlias(); name.Text = "削除後も残す入力"; model.RenderLightPersonalization(removed); await Settle();
            Check(model.LightAliasTarget?.Target == target && name.Text == "削除後も残す入力" && !model.CanSaveLightAlias, "Removing a target during Japanese input keeps the selected row and text while blocking stale save");
            model.ReloadLightAlias(); model.Render(); ThemeManager.Apply(model.Theme);
            var selector = Descendants<ComboBox>(expander).Single(); await Settle();
            Check(ReferenceEquals(selector.SelectedItem, model.LightAliasTarget) && selector.SelectedItem is DeviceLightRow,
                "The visible alias selector retains its selected endpoint through removal and restoration");
            var saveAlias = Descendants<Button>(expander).Single(b => Equals(b.Content, "保存"));
            window.Width = 960; window.Height = 640; saveAlias.BringIntoView(); await Settle(); Capture(window, output, "lighting-alias-minimum");
            var saveBounds = saveAlias.TransformToAncestor(window).TransformBounds(new Rect(saveAlias.RenderSize));
            Check(name.ActualWidth > 300 && saveBounds.Top >= 0 && saveBounds.Bottom <= window.ActualHeight, "Alias input and actions remain scroll-accessible at the minimum window size");
            Check(fanWrites.SequenceEqual(demo.Hubs.Select(h => h.Writes)) && fan.CurrentDuty == "55%", "Personalization and color selection leave cooling control unchanged");
            Check(await model.CloseAsync(), "Normal shutdown restores configured output after personalization");
            await File.WriteAllLinesAsync(Path.Combine(output, "lighting-personalization-smoke.txt"), report); return 0;
        }
        catch (Exception e) { report.Add("FAIL " + e); report.Add("STATE " + model.Notice + " / " + model.LightEditStatus + " / " + model.LightSavedColors?.Warning); await File.WriteAllLinesAsync(Path.Combine(output, "lighting-personalization-smoke.txt"), report); return 1; }
    }
}
