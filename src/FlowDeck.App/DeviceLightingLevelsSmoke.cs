using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceLightingLevelsAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, DeviceWorkspaceClient client, Window window, string output)
    {
        var report = new List<string>();
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); report.Add("PASS " + message); }
        try
        {
            model.SelectedPage = 2; await Settle();
            Descendants<Expander>(window).Single(e => AutomationProperties.GetAutomationId(e) == "LightingAdvancedSection")
                .SetCurrentValue(Expander.IsExpandedProperty, true);
            await Settle(); var panel = Descendants<DeviceLightingLevelsView>(window).Single();
            Descendants<Expander>(panel).Single().IsExpanded = true; panel.BringIntoView(); await Settle();
            Check(!model.LightLevelDraft.Dirty && !model.CanApplyLightLevel && !model.CanToggleLightLevel, "Unconfigured lighting has no fabricated applied brightness and no enabled operation");
            var fan = model.Cooling.First(); fan.Mode = "固定"; fan.Duty = 55; await model.OperateAsync(fan); var writes = demo.Hubs.Select(h => h.Writes).ToArray();
            // Inventory is sorted by randomized device IDs; match the fixture's Hub order explicitly.
            var refs = demo.DeviceIds.Select(id => model.Lights.Single(l => l.Target.DeviceId == id && l.Target.EndpointId == "led1").Target).ToArray();
            model.SelectLights(refs); model.LightEffect = "fixed"; model.LightColor = Color.FromRgb(0, 0, 200); model.LightBrightness = 100; await model.ApplyLightsAsync();
            Check(model.CanToggleLightLevel && model.LightLevelBrightness == 100 && !model.LightLevelDraft.Dirty, "Saved baseline becomes adjustable and polling does not manufacture slider edits");
            var slider = Descendants<Slider>(panel).Single(); slider.SetCurrentValue(Slider.ValueProperty, 50d);
            for (var i = 0; i < 3; i++) await model.PollAsync(); await Settle();
            Check(model.LightLevelBrightness == 50 && model.LightLevelDraft.Dirty && !model.CanSelectLightLevel && !model.CanToggleLightLevel && model.CanApplyLightLevel,
                "Slider edits survive telemetry and scope changes or off actions wait for explicit apply or reload");
            await model.ApplyLightLevelAsync(LightingAdjustmentKind.Brightness);
            Check(demo.Hubs.All(h => h.LightEffects["led1"].Colors[0] == new RgbColor(0, 0, 100)) && !model.LightLevelDraft.Dirty && model.LightLevelCurrent.Contains("50%"), "All layer applies fifty percent to both baselines and displays the applied level");
            model.LightLevelBrightness = 60; model.LightLevelBrightness = 50; await model.ApplyLightLevelAsync(LightingAdjustmentKind.Brightness);
            Check(demo.Hubs.All(h => h.LightEffects["led1"].Colors[0] == new RgbColor(0, 0, 100)), "Repeated fifty percent never cumulatively dims the baseline");
            model.SelectLights([refs[0]]); model.LightGroupName = "ケース照明"; await model.SaveLightLibraryAsync(false); var groupId = model.SelectedLightGroup!.Id;
            var scope = Descendants<ComboBox>(panel).Single(c => AutomationProperties.GetName(c) == "明るさの対象"); scope.SelectedValue = LightingAdjustmentScope.Group; await Settle();
            var group = Descendants<ComboBox>(panel).Single(c => AutomationProperties.GetName(c) == "明るさのグループ"); group.SelectedValue = groupId; await Settle();
            Check(model.LightLevelDraft.Targets.SequenceEqual(new[] { refs[0] }) && model.LightLevelBrightness == 100 && model.CanEditLightLevel, "Visible scope and group pickers select the saved endpoint membership");
            var excludedWrites = demo.Hubs[1].LightingWrites; model.LightLevelBrightness = 50; await model.ApplyLightLevelAsync(LightingAdjustmentKind.Brightness);
            Check(demo.Hubs[0].LightEffects["led1"].Colors[0] == new RgbColor(0, 0, 50) && demo.Hubs[1].LightingWrites == excludedWrites, "Group level multiplies with All and leaves excluded hardware untouched");
            model.LightLevelScope = LightingAdjustmentScope.Targets; model.SelectLights([refs[1]]); model.LightLevelBrightness = 50; await model.ApplyLightLevelAsync(LightingAdjustmentKind.Brightness);
            Check(demo.Hubs.All(h => h.LightEffects["led1"].Colors[0] == new RgbColor(0, 0, 50)), "Selection level only changes the selected endpoint and composes independently of group level");
            model.SelectLights(refs); await Settle();
            Check(model.LightLevelDraft.Mixed && model.LightLevelValue == "混在" && !model.CanApplyLightLevel && !model.LightLevelDraft.Dirty, "Mixed individual levels have an explicit mixed display and are not applied as a guessed common value");
            model.LightLevelBrightness = 80; await model.ApplyLightLevelAsync(LightingAdjustmentKind.Brightness);
            Check(demo.Hubs[0].LightEffects["led1"].Colors[0] == new RgbColor(0, 0, 40) && demo.Hubs[1].LightEffects["led1"].Colors[0] == new RgbColor(0, 0, 80), "A new common individual level retains different existing group layers");
            model.LightLevelScope = LightingAdjustmentScope.All; await model.ApplyLightLevelAsync(LightingAdjustmentKind.Off);
            Check(model.LightLevelCurrent.Contains("消灯中") && model.LightLevelBrightness == 50 && demo.Hubs.All(h => h.LightEffects["led1"].Mode == "off"), "Off retains the saved level and shows its state");
            await model.ApplyLightLevelAsync(LightingAdjustmentKind.Restore);
            Check(demo.Hubs[0].LightEffects["led1"].Colors[0] == new RgbColor(0, 0, 40) && demo.Hubs[1].LightEffects["led1"].Colors[0] == new RgbColor(0, 0, 80), "Restore recovers each endpoint's composed brightness");
            foreach (var theme in new[] { "light", "dark" }) { model.Theme = theme; panel.BringIntoView(); await Settle(); Capture(window, output, "lighting-levels-" + theme); }
            model.LightLevelBrightness = 42;
            var external = new DeviceLightingLevelDraft(); external.Update(client.Snapshot!, refs); external.SetBrightness(70); await client.ApplyLightingLevelAsync(external.Capture(LightingAdjustmentKind.Brightness));
            await model.PollAsync(); await Settle();
            Check(model.LightLevelBrightness == 42 && model.LightLevelDraft.Conflict && !model.CanApplyLightLevel && model.LightLevelCurrent.Contains("70%"), "External brightness changes preserve unsent input, show the current level and block stale apply");
            model.LightLevelDraft.Reset(); model.Render(); await Settle();
            Check(model.LightLevelBrightness == 70 && !model.LightLevelDraft.Dirty && !model.LightLevelDraft.Conflict, "Reload adopts current service levels without a phantom slider edit");
            model.LightColor = Colors.Red; Check(!model.CanEditLightLevel, "An unsent color edit cannot be accidentally discarded by intensity operations"); model.LightingDraft.Reset(); model.Render();
            model.LightEffect = "breathing"; model.LightBrightness = 100; await model.ApplyLightsAsync();
            var stream = demo.Runtime.State().Endpoints.Single(e => e.Target == refs[0]).Lighting!.GroupId;
            model.LightLevelScope = LightingAdjustmentScope.Group; model.LightLevelGroupId = groupId; model.LightLevelBrightness = 50; await model.ApplyLightLevelAsync(LightingAdjustmentKind.Brightness);
            Check(demo.Runtime.State().Endpoints.Where(e => refs.Contains(e.Target)).All(e => e.Lighting?.Running == true && e.Lighting.GroupId == stream), "Subset brightness preserves the shared running animation rather than restarting or detaching it");
            window.Width = 960; window.Height = 640; panel.BringIntoView(); await Settle(); Capture(window, output, "lighting-levels-minimum");
            Check(slider.ActualWidth > 400 && Descendants<Button>(panel).All(b => b.ActualWidth > 0), "Minimum window size keeps the intensity slider and actions usable");
            Check(writes.SequenceEqual(demo.Hubs.Select(h => h.Writes)) && fan.CurrentDuty == "55%", "All lighting adjustments leave cooling control untouched");
            Check(await model.CloseAsync(), "Normal application shutdown completes after intensity editing");
            await File.WriteAllLinesAsync(Path.Combine(output, "lighting-levels-smoke.txt"), report); return 0;
        }
        catch (Exception e) { report.Add("FAIL " + e); report.Add("STATE " + model.LightLevelStatus + " / " + model.Notice); await File.WriteAllLinesAsync(Path.Combine(output, "lighting-levels-smoke.txt"), report); return 1; }
    }
}
