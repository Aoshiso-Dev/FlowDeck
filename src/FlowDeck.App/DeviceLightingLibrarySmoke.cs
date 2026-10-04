using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceLightingLibraryAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, DeviceWorkspaceClient client, Window window, string output)
    {
        var report = new List<string>();
        void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); report.Add("PASS " + text); }
        try
        {
            model.SelectedPage = 2; await Settle(); var panel = Descendants<DeviceLightingLibraryView>(window).Single();
            Check(!model.HasLightLibraryEditor && model.LightGroups.Count == 0 && model.LightScenes.Count == 0, "Library editors begin closed without fabricated groups or scenes");
            var fan = model.Cooling.Single(r => r.Draft.Target == new EndpointReference(demo.DeviceIds[0], "fan1")); fan.Mode = "固定"; fan.Duty = 55; await model.OperateAsync(fan); var fanWrites = demo.Hubs[0].Writes;
            model.OpenLightLibrary(LightingPresetKind.Group); panel.BringIntoView(); await Settle();
            var editor = model.LightLibraryEditor!; var name = Descendants<DraftTextBox>(panel).Single(); name.Focus(); name.Text = "前面と背面の照明";
            foreach (var member in editor.Members) member.Selected = true;
            for (var i = 0; i < 3; i++) await model.PollAsync(); await Settle();
            Check(ReferenceEquals(editor, model.LightLibraryEditor) && name.Text == "前面と背面の照明" && editor.Targets.Length == 2 && model.CanSaveLightLibraryEditor,
                "Japanese group text, member choices and editor identity survive polling");
            name.Text = ""; Check(!model.CanSaveLightLibraryEditor, "An empty name disables save without discarding selected members"); name.Text = "前面と背面の照明";
            await model.SaveLightLibraryEditorAsync(); var groupId = model.SelectedLightGroup!.Id;
            Check(model.LightGroups.Single().Name == "前面と背面の照明" && model.LightGroups.Single().Revision == 1 && demo.Hubs.All(h => h.LightingWrites == 0),
                "Creating a group stores endpoint identities without sending output");
            model.LightLibraryEditor!.Name = "前面のみ"; model.LightLibraryEditor.Members[1].Selected = false; await model.SaveLightLibraryEditorAsync();
            model.ChooseLightGroup.Execute(null); await Settle();
            Check(model.LightGroups.Single().Revision == 2 && model.LightingDraft.Targets.SequenceEqual(new[] { model.LightLibraryEditor!.Members[0].Target }),
                "Editing membership advances its revision and the target picker uses the saved members");
            model.OpenLightLibrary(LightingPresetKind.Scene); editor = model.LightLibraryEditor!; editor.Name = "青と赤のシーン";
            await Settle();
            var m5Visibility = Descendants<CheckBox>(panel).Single(c => AutomationProperties.GetName(c) == "M5GOに表示するシーン");
            Check(m5Visibility.IsVisible && m5Visibility.IsChecked == true, "A new scene offers the bound M5GO visibility choice");
            m5Visibility.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            for (var i = 0; i < 3; i++) await model.PollAsync();
            Check(!editor.ShowOnM5 && m5Visibility.IsChecked == false && editor.M5SceneCount == "0 / 32",
                "The M5GO choice and displayed slot count survive periodic refresh");
            editor.Effect = "breathing"; editor.Color = Color.FromRgb(200, 10, 20); editor.Brightness = 65; editor.Speed = 4;
            var firstPart = editor.SelectedPart!; editor.AddPart(); editor.Color = Color.FromRgb(10, 30, 200); editor.Brightness = 80; var secondPart = editor.SelectedPart!;
            Check(editor.Parts.Length == 2 && !editor.CanAdd && !editor.Members[0].Available && editor.Members[1].Available, "Scene parts have disjoint endpoint membership and enforce duplicate prevention");
            editor.SelectedPart = firstPart; await Settle();
            var effect = Descendants<ComboBox>(panel).Single(c => AutomationProperties.GetName(c) == "保存するシーンの効果");
            Check(editor.Effect == "breathing" && editor.Brightness == 65 && editor.Speed == 4 && Descendants<TextBlock>(effect).Any(t => t.Text == "呼吸" && t.ActualWidth > 0),
                "Switching parts restores both values and the actual visible effect label");
            var brightness = Descendants<Slider>(panel).Single(s => AutomationProperties.GetName(s) == "保存するシーンの明るさ");
            brightness.SetCurrentValue(Slider.ValueProperty, 60d); await model.PollAsync(); await Settle();
            Check(ReferenceEquals(editor, model.LightLibraryEditor) && editor.Brightness == 60 && editor.SelectedPart == firstPart && demo.Hubs.All(h => h.LightingWrites == 0),
                "Unsent scene colors, brightness and selected part survive a refresh without actuation");
            foreach (var theme in new[] { "light", "dark" }) { model.Theme = theme; panel.BringIntoView(); await Settle(); Capture(window, output, "lighting-library-" + theme); }
            await model.PollAsync(); await model.SaveLightLibraryEditorAsync(); var sceneId = model.SelectedLightScene!.Id;
            Check(client.Snapshot!.Configuration.Configuration.Lighting.Scenes.Single().Individual.Length == 2 && demo.Hubs.All(h => h.LightingWrites == 0),
                "Saving a multipart scene preserves separate programs and does not apply it");
            Check(!client.Snapshot!.Configuration.Configuration.Lighting.Scenes.Single().ShowOnM5 && DeviceM5Lighting.Read(client.Snapshot, "all").Scenes.Length == 0,
                "A PC-only scene is saved without entering the firmware catalog");
            await Settle(); m5Visibility.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            await model.SaveLightLibraryEditorAsync();
            Check(DeviceM5Lighting.Read(client.Snapshot, "all").Scenes.Single().Id == DeviceM5Lighting.WireId(LightingPresetKind.Scene, sceneId) && demo.Hubs.All(h => h.LightingWrites == 0),
                "Explicit M5GO publication keeps the scene identity and does not send lighting output");
            await model.RecallLightSceneAsync();
            var running = demo.Runtime.State().Endpoints.Single(e => e.Target == new EndpointReference(demo.DeviceIds[0], "led1"));
            Check(running.Lighting?.Running == true && demo.Hubs[1].LightEffects["led1"].Colors.SequenceEqual(new[] { new RgbColor(8, 24, 160) }),
                "Explicit scene recall starts the first effect and applies the second fixed color and brightness");
            var runningId = running.Lighting!.GroupId; var secondWrites = demo.Hubs[1].LightingWrites;
            model.OpenLightLibrary(LightingPresetKind.Scene, sceneId); editor = model.LightLibraryEditor!; editor.Name = "改名したシーン";
            await model.SaveLightLibraryEditorAsync();
            Check(demo.Runtime.State().Endpoints.Single(e => e.Target == running.Target).Lighting?.GroupId == runningId && demo.Hubs[1].LightingWrites == secondWrites,
                "Renaming an active scene neither restarts the running effect nor rewrites a fixed target");
            editor = model.LightLibraryEditor!; name.Focus(); name.Text = "保存前の日本語";
            var external = new DeviceLightingLibraryDraft(client.Snapshot!, LightingPresetKind.Scene, sceneId) { Name = "別操作のシーン" };
            await client.SaveLightingEditAsync(external.Capture());
            await model.PollAsync(); await Settle();
            Check(ReferenceEquals(editor, model.LightLibraryEditor) && editor.Name == "保存前の日本語" && editor.Draft.Conflict && !model.CanSaveLightLibraryEditor,
                "A concurrent settings change retains the typed draft and rejects a stale save");
            Check(!model.ApplyLightScene.CanExecute(null), "A selected older scene revision cannot be recalled after external editing");
            await model.ReloadLightLibraryEditorAsync(); editor = model.LightLibraryEditor!;
            Check(!editor.Draft.Conflict && editor.Name == "別操作のシーン", "Explicit reload adopts the saved definition after a conflict");
            editor.SelectedPart = editor.Parts[1].Id; editor.Color = Color.FromRgb(0, 180, 0); await model.SaveLightLibraryEditorAsync();
            Check(demo.Hubs[1].LightingWrites == secondWrites && demo.Runtime.State().Endpoints.Single(e => e.Target == running.Target).Lighting?.GroupId == runningId,
                "Editing a stored part leaves the applied scene unchanged until recall");
            model.OpenLightLibrary(LightingPresetKind.Scene, sceneId); await model.PollAsync();
            window.Width = 960; window.Height = 640; panel.BringIntoView(); await Settle(); Capture(window, output, "lighting-library-minimum");
            var save = Descendants<Button>(panel).Single(b => b.Content?.ToString() == "保存"); save.BringIntoView(); await Settle();
            Check(save.IsVisible && Descendants<ColorPicker>(panel).Single().ActualWidth >= 440, "At minimum size the color editor has sufficient width and actions remain scroll-accessible");
            model.OpenLightLibrary(LightingPresetKind.Group, groupId); await model.SaveLightLibraryEditorAsync(true);
            Check(model.LightGroups.Count == 0 && !model.HasLightLibraryEditor && demo.Runtime.State().Endpoints.Single(e => e.Target == running.Target).Lighting?.GroupId == runningId,
                "Deleting a saved group leaves current lighting ownership unchanged");
            model.OpenLightLibrary(LightingPresetKind.Scene, sceneId);
            external = new(client.Snapshot!, LightingPresetKind.Scene, sceneId); await client.SaveLightingEditAsync(external.Capture(true));
            await model.PollAsync(); Check(model.LightLibraryEditor!.Draft.Conflict && !model.CanDeleteLightLibraryEditor, "External deletion leaves the old draft visible and blocks repeated deletion");
            await model.ReloadLightLibraryEditorAsync(); Check(!model.HasLightLibraryEditor && model.Notice.Contains("削除されています"), "Reloading a deleted item closes only the obsolete editor with a clear status");
            Check(model.LightScenes.Count == 0 && demo.Hubs[1].LightingWrites == secondWrites && demo.Hubs[0].Writes == fanWrites && fan.CurrentDuty == "55%",
                "Deleting a saved scene preserves applied lighting and PC fan control");
            await CheckDeviceNativeSceneAsync(model, demo, client, window, output, Check);
            Check(await model.CloseAsync() && (await demo.Hubs[0].ReadAsync()).Channels[0].Duty == 70, "Normal exit still restores the configured cooling output");
            await File.WriteAllLinesAsync(Path.Combine(output, "lighting-library-smoke.txt"), report); return 0;
        }
        catch (Exception e) { report.Add("FAIL " + e); report.Add("STATE " + model.LightLibraryEditor?.Status + " / " + model.Notice); await File.WriteAllLinesAsync(Path.Combine(output, "lighting-library-smoke.txt"), report); return 1; }
    }
}
