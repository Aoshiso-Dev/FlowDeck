using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    // Reachable only through the preview host's isolated built-in DEMO guard.
    public static async Task<int> RunDeviceCoolingLayoutAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo,
        DeviceWorkspaceClient client, Window window, string output)
    {
        var report = new List<string>(); var screenshots = new List<string>(); var geometry = new List<object>();
        string? error = null;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); report.Add("PASS " + message); }
        void Shot(string name) { Capture(window, output, name); screenshots.Add(name + ".png"); }
        async Task Fresh() { await client.RefreshAsync(); model.Render(); await Settle(); }
        try
        {
            Check(client.IsDemo && demo.Hubs.All(h => h.IsDemo), "Cooling layout uses authenticated DEMO and memory-backed devices only");
            await model.PrepareFirstUseAsync(); model.SelectedPage = 1; await Fresh();
            var view = Descendants<DeviceWorkspaceView>(window).Single();
            var scroll = (ScrollViewer)view.FindName("WorkspaceScroll");
            var page = LayoutElement<FrameworkElement>(view, "CoolingPage");
            var calibration = LayoutElement<Expander>(page, "CoolingCalibrationSection");
            var groups = LayoutElement<Expander>(page, "CoolingGroupsSection");
            var cooling = model.Cooling.Single(r => r.Draft.Target == new EndpointReference(demo.DeviceIds[0], "fan1"));
            var lastCooling = model.Cooling.Last();
            var card = Descendants<Border>(page).Single(e => AutomationProperties.GetAutomationId(e) == "CoolingDeviceCard" && ReferenceEquals(e.DataContext, cooling));
            var lastCard = Descendants<Border>(page).Single(e => AutomationProperties.GetAutomationId(e) == "CoolingDeviceCard" && ReferenceEquals(e.DataContext, lastCooling));
            var header = LayoutElement<FrameworkElement>(card, "CoolingDeviceHeader");
            var draft = LayoutElement<FrameworkElement>(card, "CoolingDraftControls");
            var current = LayoutElement<TextBlock>(card, "CoolingCurrentDuty");
            var slider = LayoutElement<Slider>(card, "CoolingDraftDuty");
            var details = LayoutElement<Expander>(card, "CoolingDetailsSection");
            var apply = CoolingLayoutButton(card, "DataContext.Apply");
            var lastApply = CoolingLayoutButton(lastCard, "DataContext.Apply");
            var initialWrites = demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).ToArray();
            Check(initialWrites.All(w => w == (0, 0)), "Opening Cooling sends no cooling or lighting output");

            // A saved group exposes its real readings and editor, without acquiring any output ownership.
            model.OpenCoolingGroupEditor();
            model.GroupEditor!.Name = "Layout DEMO group";
            foreach (var member in model.GroupEditor.Members)
                member.Selected = member.Target == cooling.Draft.Target || member.Target == lastCooling.Draft.Target;
            await model.SaveCoolingGroupAsync(); await Fresh();
            Check(model.CoolingGroupReadings.Count == 2 && initialWrites.SequenceEqual(demo.Hubs.Select(h => (h.Writes, h.LightingWrites))),
                "The isolated two-endpoint group fixture saves membership without output writes");
            var generation = client.Snapshot!.Configuration.Generation;
            var configuration = SettingsTransfer.Hash(client.Snapshot.Configuration.Configuration);

            foreach (var size in new[] { (Name: "normal", Width: 1120d, Height: 820d), (Name: "minimum", Width: 960d, Height: 640d) })
            foreach (var theme in new[] { "light", "dark" })
            {
                var key = size.Name + "-" + theme;
                window.Width = size.Width; window.Height = size.Height; model.Theme = theme; model.Notice = "";
                foreach (var row in model.Cooling) { row.DetailsExpanded = false; row.Reset(); }
                calibration.SetCurrentValue(Expander.IsExpandedProperty, false);
                groups.SetCurrentValue(Expander.IsExpandedProperty, false);
                await model.ReloadCoolingGroupsAsync(); await Fresh(); scroll.ScrollToTop(); await Settle();
                var calParts = CoolingLayoutParts(calibration); var groupParts = CoolingLayoutParts(groups);
                var collapsedCal = calParts.Frame.ActualHeight; var collapsedGroup = groupParts.Frame.ActualHeight;
                var calBounds = LayoutBounds(calibration, page); var groupBounds = LayoutBounds(groups, page);
                var lastCardBounds = LayoutBounds(lastCard, page);
                Check(!calParts.Content.IsVisible && !groupParts.Content.IsVisible && collapsedCal > 0 && collapsedGroup > 0 &&
                    CoolingLayoutGap(lastCardBounds, calBounds) && CoolingLayoutGap(calBounds, groupBounds) &&
                    Descendants<Border>(page).Where(e => AutomationProperties.GetAutomationId(e) == "CoolingDeviceCard")
                        .All(e => LayoutBounds(e, page).Bottom <= lastCardBounds.Bottom + 1),
                    key + " every everyday endpoint card precedes the separately framed calibration and groups with clear spacing");
                Check(LayoutInset(header, card, 12) && LayoutInset(draft, card, 12) && LayoutBounds(header, card).Bottom <= LayoutBounds(draft, card).Top &&
                    ReferenceEquals(header.DataContext, cooling) && ReferenceEquals(draft.DataContext, cooling) &&
                    ReferenceEquals(apply.CommandParameter, cooling) && !Descendants<Button>(details).Contains(apply),
                    key + " the endpoint frame owns its current reading, requested controls and Apply outside the collapsible details");
                Shot("cooling-layout-collapsed-" + key);

                calibration.SetCurrentValue(Expander.IsExpandedProperty, true); await Fresh();
                var calibrationView = Descendants<DeviceCoolingCalibrationView>(calibration).Single();
                Descendants<ComboBox>(calibrationView).Single().SetCurrentValue(Selector.SelectedItemProperty,
                    model.CalibrationChoices.Single(c => c.Target == cooling.Draft.Target));
                await Fresh();
                var calibrationEditor = LayoutElement<FrameworkElement>(calibration, "CoolingCalibrationEditorFrame");
                var start = CoolingLayoutButton(calibrationView, "StartCalibration");
                Check(calParts.Content.IsVisible && calParts.Frame.ActualHeight > collapsedCal && LayoutInset(calParts.Content, calParts.Frame, 12) &&
                    LayoutInset(calibrationEditor, calParts.Frame, 12) && ReferenceEquals(calibrationView.DataContext, model),
                    key + " calibration expansion grows its own frame and keeps the selected endpoint editor inset inside it");
                start.BringIntoView(); await Settle();
                Check(start.Command.CanExecute(start.CommandParameter) && LayoutActionReachable(start, view) && LayoutInsideViewport(start, scroll),
                    key + " the existing calibration trial action is reachable after scrolling without running it");
                geometry.Add(new { view = "calibration", key, collapsedHeight = collapsedCal, expandedHeight = calParts.Frame.ActualHeight,
                    frame = LayoutRect(LayoutBounds(calParts.Frame, view)), editor = LayoutRect(LayoutBounds(calibrationEditor, view)) });
                Shot("cooling-layout-calibration-" + key);
                calibration.SetCurrentValue(Expander.IsExpandedProperty, false); await Settle();
                Check(!start.IsVisible && groups.IsVisible && Math.Abs(calParts.Frame.ActualHeight - collapsedCal) < 1,
                    key + " collapsing calibration restores its frame and leaves the adjacent group section available");

                groups.SetCurrentValue(Expander.IsExpandedProperty, true); await Fresh();
                var groupView = Descendants<DeviceCoolingGroupsView>(groups).Single();
                model.CoolingGroupMode = "\u56fa\u5b9a";
                var groupSlider = Descendants<Slider>(groupView).Single(s => BindingOperations.GetBinding(s, RangeBase.ValueProperty)?.Path.Path == "CoolingGroupDuty");
                groupSlider.SetCurrentValue(RangeBase.ValueProperty, 64d); await Fresh();
                var groupControls = LayoutElement<FrameworkElement>(groups, "CoolingGroupControls");
                var groupReadings = LayoutElement<FrameworkElement>(groups, "CoolingGroupReadings");
                var groupApply = CoolingLayoutButton(groupView, "ApplyCoolingGroup");
                Check(groupParts.Content.IsVisible && groupParts.Frame.ActualHeight > collapsedGroup &&
                    LayoutInset(groupParts.Content, groupParts.Frame, 12) && LayoutInset(groupControls, groupParts.Frame, 12) &&
                    LayoutInset(groupReadings, groupParts.Frame, 12) && ReferenceEquals(groupReadings.DataContext, model) &&
                    model.CoolingGroupReadings.All(r => r.Duty != "64%") && model.CoolingGroupDuty == 64,
                    key + " group requested output and both actual endpoint readings stay independent inside the expanded group frame");
                CoolingLayoutButton(groupView, "EditCoolingGroup").Command.Execute(null); await Fresh();
                var groupEditor = LayoutElement<FrameworkElement>(groups, "CoolingGroupEditorFrame");
                var groupSave = CoolingLayoutButton(groupEditor, "SaveCoolingGroup");
                groupSave.BringIntoView(); await Settle();
                Check(groupEditor.IsVisible && LayoutInset(groupEditor, groupParts.Frame, 12) &&
                    groupSave.Command.CanExecute(groupSave.CommandParameter) && LayoutActionReachable(groupSave, view) && scroll.ScrollableWidth < 1,
                    key + " group membership editing stays inside its parent and its save action fits the viewport");
                geometry.Add(new { view = "groups", key, collapsedHeight = collapsedGroup, expandedHeight = groupParts.Frame.ActualHeight,
                    frame = LayoutRect(LayoutBounds(groupParts.Frame, view)), editor = LayoutRect(LayoutBounds(groupEditor, view)) });
                Shot("cooling-layout-groups-" + key);
                var groupDraft = model.GroupEditor;
                groups.SetCurrentValue(Expander.IsExpandedProperty, false); await Fresh();
                Check(!groupApply.IsVisible && !groupSave.IsVisible && ReferenceEquals(groupDraft, model.GroupEditor) && card.IsVisible && apply.IsVisible,
                    key + " collapsing the group preserves its draft and does not hide the earlier everyday endpoint actions");
                model.CloseCoolingGroupEditor.Execute(null); await Settle();

                var mode = Descendants<ComboBox>(card).Single(c => c.ItemsSource == cooling.Modes);
                mode.SetCurrentValue(Selector.SelectedItemProperty, "\u56fa\u5b9a");
                var measured = cooling.CurrentDuty;
                slider.SetCurrentValue(RangeBase.ValueProperty, 61d); await Fresh(); scroll.ScrollToTop(); await Settle();
                Check(slider.IsEnabled && cooling.Draft.Dirty && cooling.Duty == 61 && current.Text == measured && cooling.CurrentDuty == measured &&
                    cooling.Duty.ToString(CultureInfo.InvariantCulture) + "%" != measured &&
                    LayoutBounds(current, card).Bottom <= LayoutBounds(slider, card).Top && LayoutActionReachable(apply, view),
                    key + " the top endpoint Apply remains reachable while its unsent bound slider stays distinct from current output");
                var collapsedCard = card.ActualHeight;
                details.SetCurrentValue(Expander.IsExpandedProperty, true); await Fresh();
                var detailParts = CoolingLayoutParts(details);
                var edit = CoolingLayoutButton(details, "DataContext.EditCoolingSettings");
                var revert = CoolingLayoutButton(card, "DataContext.Revert");
                Check(cooling.DetailsExpanded && card.ActualHeight > collapsedCard && LayoutInset(details, card, 12) &&
                    LayoutInset(detailParts.Content, detailParts.Frame, 12) && ReferenceEquals(edit.CommandParameter, cooling) &&
                    ReferenceEquals(revert.CommandParameter, cooling) && edit.Command.CanExecute(edit.CommandParameter),
                    key + " endpoint details grow the same card and retain the correct endpoint command context");
                if (!cooling.ShowSettingsEditor) edit.Command.Execute(edit.CommandParameter);
                await Fresh();
                var editor = cooling.SettingsEditor!;
                var settingsFrame = LayoutElement<FrameworkElement>(details, "CoolingSettingsEditorFrame");
                var settingsView = Descendants<DeviceCoolingSettingsView>(details).Single();
                Check(settingsFrame.IsVisible && ReferenceEquals(settingsView.DataContext, editor) && editor.Draft.Target == cooling.Draft.Target &&
                    LayoutInset(settingsFrame, detailParts.Frame, 12) && LayoutInset(settingsFrame, card, 12),
                    key + " curve settings remain visually and logically inside the endpoint that opened them");
                header.BringIntoView(); await Settle(); Shot("cooling-layout-endpoint-" + key);

                var protection = LayoutElement<Expander>(settingsView, "CoolingProtectionSection");
                var stop = LayoutElement<Expander>(settingsView, "CoolingStopSection");
                protection.SetCurrentValue(Expander.IsExpandedProperty, false); stop.SetCurrentValue(Expander.IsExpandedProperty, false); await Settle();
                var protectionParts = CoolingLayoutParts(protection); var stopParts = CoolingLayoutParts(stop);
                var collapsedNested = detailParts.Frame.ActualHeight;
                protection.SetCurrentValue(Expander.IsExpandedProperty, true); await Settle();
                var protectionHeight = detailParts.Frame.ActualHeight;
                Check(protectionParts.Content.IsVisible && !stopParts.Content.IsVisible && protectionHeight > collapsedNested &&
                    LayoutInset(protectionParts.Content, protectionParts.Frame, 12) && LayoutInset(protection, detailParts.Frame, 12),
                    key + " manual protection expands within its own frame and the enclosing endpoint details");
                stop.SetCurrentValue(Expander.IsExpandedProperty, true); await Settle();
                var stopPermission = Descendants<CheckBox>(stop).Single();
                stopPermission.SetCurrentValue(ToggleButton.IsCheckedProperty, true); await Settle();
                Check(stop.IsVisible && stopParts.Content.IsVisible && detailParts.Frame.ActualHeight > protectionHeight &&
                    LayoutInset(stopParts.Content, stopParts.Frame, 12) && LayoutInset(stop, detailParts.Frame, 12) &&
                    LayoutBounds(protection, settingsView).Bottom <= LayoutBounds(stop, settingsView).Top &&
                    Descendants<Slider>(stop).All(s => LayoutInset(s, stopParts.Frame, 12)) && scroll.ScrollableWidth < 1,
                    key + " expanded stop and restart fields remain below protection and inside both enclosing frames without horizontal overflow");
                stop.BringIntoView(); await Settle();
                geometry.Add(new { view = "endpoint", key, collapsedHeight = collapsedCard, expandedHeight = card.ActualHeight,
                    card = LayoutRect(LayoutBounds(card, view)), details = LayoutRect(LayoutBounds(detailParts.Frame, view)),
                    protection = LayoutRect(LayoutBounds(protectionParts.Frame, view)), stop = LayoutRect(LayoutBounds(stopParts.Frame, view)),
                    current = measured, requested = cooling.Duty });
                Shot("cooling-layout-nested-" + key);
                stopPermission.SetCurrentValue(ToggleButton.IsCheckedProperty, false); await Fresh();
                var save = CoolingLayoutButton(settingsView, "DataContext.SaveCoolingSettings");
                save.BringIntoView(); await Settle();
                Check(save.Command.CanExecute(save.CommandParameter) && ReferenceEquals(save.CommandParameter, editor) &&
                    LayoutActionReachable(save, view) && LayoutInsideViewport(save, scroll),
                    key + " the expanded endpoint settings save action is reachable and still targets its own editor");
                Shot("cooling-layout-save-" + key);

                details.SetCurrentValue(Expander.IsExpandedProperty, false); await Fresh(); scroll.ScrollToTop(); await Settle();
                Check(!settingsFrame.IsVisible && revert.IsVisible && !Descendants<Button>(details).Contains(revert) &&
                    ReferenceEquals(cooling.SettingsEditor, editor) &&
                    current.IsVisible && slider.IsVisible && LayoutActionReachable(apply, view) && ReferenceEquals(apply.CommandParameter, cooling),
                    key + " collapsing endpoint details keeps current output, Apply and Revert available and preserves its settings draft");
                details.SetCurrentValue(Expander.IsExpandedProperty, true); await Settle();
                Check(settingsFrame.IsVisible && protection.IsExpanded && stop.IsExpanded && ReferenceEquals(cooling.SettingsEditor, editor),
                    key + " reopening endpoint details restores the same nested sections and settings draft");
                revert.Command.Execute(revert.CommandParameter); await Fresh();
                Check(!cooling.Draft.Dirty && current.Text == measured,
                    key + " the existing endpoint revert command clears only the unsent requested output");
                details.SetCurrentValue(Expander.IsExpandedProperty, false);
                lastCooling.Mode = "\u56fa\u5b9a"; LayoutElement<Slider>(lastCard, "CoolingDraftDuty").SetCurrentValue(RangeBase.ValueProperty, 63d);
                await Fresh(); scroll.ScrollToBottom(); await Settle();
                var bottomSectionsVisible = scroll.VerticalOffset > 0 && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1 &&
                    LayoutInsideViewport(calibration, scroll) && LayoutInsideViewport(groups, scroll) &&
                    CoolingLayoutGap(LayoutBounds(lastCard, page), LayoutBounds(calibration, page)) &&
                    CoolingLayoutGap(LayoutBounds(calibration, page), LayoutBounds(groups, page));
                lastApply.BringIntoView(); await Settle();
                Check(bottomSectionsVisible && scroll.VerticalOffset > 0 &&
                    lastApply.Command.CanExecute(lastApply.CommandParameter) && ReferenceEquals(lastApply.CommandParameter, lastCooling) &&
                    LayoutInsideViewport(lastApply, scroll) && LayoutActionReachable(lastApply, view) && scroll.ScrollableWidth < 1,
                    key + " the bottom sections retain their spacing and scrolling back reaches the last endpoint Apply with its correct target");
                Shot("cooling-layout-bottom-" + key); lastCooling.Reset();
            }
            await Fresh();
            Check(client.Snapshot!.Configuration.Generation == generation && SettingsTransfer.Hash(client.Snapshot.Configuration.Configuration) == configuration &&
                initialWrites.SequenceEqual(demo.Hubs.Select(h => (h.Writes, h.LightingWrites))),
                "All resizing, expansion, scrolling and draft editing preserve the saved DEMO configuration and every cooling and lighting output");
            Check(await model.CloseAsync(), "The Cooling layout DEMO closes normally without output ownership");
        }
        catch (Exception e) { error = e.ToString(); report.Add("FAIL " + error); }
        await File.WriteAllLinesAsync(Path.Combine(output, "cooling-layout-smoke.txt"), report);
        await File.WriteAllTextAsync(Path.Combine(output, "cooling-layout-result.json"), JsonSerializer.Serialize(new
        { passed = report.Count(l => l.StartsWith("PASS ", StringComparison.Ordinal)), failed = error is null ? 0 : 1, error,
            hardwareAccessed = false, productionIpcOpened = false, scenario = "cooling-layout", screenshots, geometry },
            new JsonSerializerOptions { WriteIndented = true }));
        return error is null ? 0 : 1;
    }

    private static Button CoolingLayoutButton(DependencyObject root, string path)
        => Descendants<Button>(root).Single(b => BindingOperations.GetBinding(b, Button.CommandProperty)?.Path.Path == path);
    private static (FrameworkElement Frame, FrameworkElement Content) CoolingLayoutParts(Expander section)
    {
        section.ApplyTemplate();
        return (section.Template.FindName("SettingsGroupFrame", section) as FrameworkElement
                ?? throw new InvalidOperationException("Cooling section frame is missing: " + AutomationProperties.GetAutomationId(section)),
            section.Template.FindName("SettingsGroupContent", section) as FrameworkElement
                ?? throw new InvalidOperationException("Cooling section content is missing: " + AutomationProperties.GetAutomationId(section)));
    }
    private static bool CoolingLayoutGap(Rect above, Rect below) => below.Top - above.Bottom is >= 16 and <= 32;
}
