using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    private static async Task RunDeviceNameSettingsAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, Window window,
        string output, Action<bool, string> check)
    {
        model.SelectedPage = 3; await Settle();
        var panel = Descendants<Expander>(window).Single(e => e.Header?.ToString() == "機器の管理");
        panel.SetCurrentValue(Expander.IsExpandedProperty, true); await Settle(); panel.BringIntoView(); await Settle();
        var choices = Descendants<ComboBox>(panel).Single();
        var input = Descendants<DraftTextBox>(panel).Single(t => AutomationProperties.GetName(t) == "機器の名前");
        var first = model.DeviceNameChoices.Single(c => c.Id == demo.DeviceIds[0]);
        var second = model.DeviceNameChoices.Single(c => c.Id == demo.DeviceIds[1]);
        var row = model.Cooling.Single(c => c.Draft.Target == new EndpointReference(first.Id, "fan1"));
        row.Mode = "固定"; row.Duty = 65; await model.OperateAsync(row);
        var before = demo.Runtime.State(); var writes = demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).ToArray();
        choices.SetCurrentValue(ComboBox.SelectedItemProperty, first);
        input.SetCurrentValue(TextBox.TextProperty, "前面のファンハブ");
        await model.PollAsync(); await Settle();
        check(input.Text == "前面のファンハブ" && model.EditedDeviceName == input.Text && model.CanSaveDeviceName,
            "Device name WPF Japanese input survives service polling");
        choices.SetCurrentValue(ComboBox.SelectedItemProperty, second); input.SetCurrentValue(TextBox.TextProperty, "ラジエーター用ハブ");
        choices.SetCurrentValue(ComboBox.SelectedItemProperty, first); await Settle();
        check(input.Text == "前面のファンハブ", "Switching device selection retains each unsaved name independently");
        await model.SaveDeviceNameAsync(); await Settle();
        check(first.Name == "前面のファンハブ" && model.DeviceNameChoices.Contains(first) && model.Devices.Single(d => d.Id == first.Id).Name == first.Name,
            "Saved device name updates stable selection and device catalog");
        check(row.Connection == first.Name && model.DashboardSettings.Where(d => d.Item.Target.DeviceId == first.Id).All(d => d.Source.StartsWith(first.Name + " / ")),
            "Cooling and dashboard source labels follow registered device name");
        check(demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).SequenceEqual(writes), "Naming a device sends no fan or lighting writes");
        var after = demo.Runtime.State();
        check(before.Endpoints.Where(e => e.Owned).All(e => after.Endpoints.Any(a => a.Target == e.Target && a.Owned && a.PolicyRevision == e.PolicyRevision)),
            "Device name save preserves active cooling and lighting ownership and revisions");
        choices.SetCurrentValue(ComboBox.SelectedItemProperty, second); await Settle();
        check(input.Text == "ラジエーター用ハブ" && model.SelectedDeviceNameDraft?.Conflict == true && !model.CanSaveDeviceName,
            "Another saved device conflicts with an older draft without erasing its text");
        await model.ReloadDeviceNameAsync();
        check(input.Text == second.Name && model.SelectedDeviceNameDraft?.Conflict == false, "Explicit reload returns the current saved name");
        input.SetCurrentValue(TextBox.TextProperty, "   "); check(!model.CanSaveDeviceName, "Empty device names cannot be saved");
        input.SetCurrentValue(TextBox.TextProperty, "ラジエーター用ハブ"); await model.SaveDeviceNameAsync();
        check(second.Name == "ラジエーター用ハブ" && first.Name == "前面のファンハブ", "Same-model devices keep independent names after both saves");
        input.SetCurrentValue(TextBox.TextProperty, first.Name); await model.SaveDeviceNameAsync(); await Settle();
        check(first.Name == second.Name && first.Label != second.Label && choices.DisplayMemberPath == nameof(DeviceNameChoice.Label) &&
            ReferenceEquals(choices.SelectedItem, second), "Duplicate device names have distinct visible choices without changing the selected identity");
        input.SetCurrentValue(TextBox.TextProperty, "ラジエーター用ハブ"); await model.SaveDeviceNameAsync();
        check(first.Label == first.Name && second.Label == second.Name && demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).SequenceEqual(writes),
            "Unique names remove display suffixes while repeated name edits leave outputs untouched");
        foreach (var theme in model.Themes)
        {
            model.Theme = theme; await Settle(); panel.BringIntoView(); await Settle(); Capture(window, output, "device-names-" + theme);
            check(input.IsVisible && input.IsEnabled && input.Text == second.Name, theme + " device name editor is visible and retains input");
        }
        window.Width = 960; window.Height = 640; await Settle(); panel.BringIntoView(); await Settle();
        Capture(window, output, "device-names-minimum");
        check(input.ActualWidth > 200 && input.ActualHeight > 20, "Device name editor remains usable at minimum window size");
        window.Width = 1120; window.Height = 820;
        await model.OperateAsync(row, DeviceOperation.ReleaseCooling);
    }
}
