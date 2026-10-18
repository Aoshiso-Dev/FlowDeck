using System.Text.Json;
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
    // The preview route rejects external pipes and other scenarios before constructing this fixture.
    public static async Task<int> RunDeviceSimpleUxAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo,
        DeviceWorkspaceClient client, Window window, string output)
    {
        var report = new List<string>(); var screenshots = new List<string>(); var geometry = new List<object>();
        string? error = null;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); report.Add("PASS " + message); }
        void Shot(string name) { Capture(window, output, name); screenshots.Add(name + ".png"); }
        async Task Fresh() { await client.RefreshAsync(); model.Render(); await Settle(); }
        (int Fan, int Light)[] Writes() => demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).ToArray();
        try
        {
            Check(client.IsDemo && DeviceWorkspaceOptions.DemoPipe(demo.PipeName) && demo.Hubs.All(h => h.IsDemo),
                "The entire scenario uses an authenticated isolated DEMO pipe and memory-backed devices");
            await model.PrepareFirstUseAsync(); await Fresh();
            var initialWrites = Writes(); var generation = client.Snapshot!.Configuration.Generation;
            var configuration = SettingsTransfer.Hash(client.Snapshot.Configuration.Configuration);
            Check(initialWrites.All(w => w == (0, 0)), "Opening the simple editor sends no output");
            var first = new EndpointReference(demo.DeviceIds[0], "led1");
            var second = new EndpointReference(demo.DeviceIds[1], "led1");

            // Recreate the view model against the same idle DEMO owner: UI restoration must not acquire another owner.
            model.SelectedPage = 2; model.SelectLights([second]); await model.FlushUiAsync();
            var store = new DeviceUiPreferencesStore(Path.Combine(output, "ui.json"));
            store.Load();
            Check(store.Workspace.SelectedPage == 2 && store.Workspace.LightingTargets!.SequenceEqual([second]),
                "The local UI file stores the last page and selected endpoint identity");
            model = new DeviceWorkspaceViewModel(client, new DeviceUiPreferencesStore(store.PathName));
            await model.InitializeAsync(); window.DataContext = model; await Fresh();
            Check(model.SelectedPage == 2 && model.LightingDraft.Targets.SequenceEqual([second]) &&
                !model.LightHasPendingChanges && !model.CanApplyLighting,
                "Recreating the editor restores only its page and selection, without an unsent program");
            model.SelectedPage = 1; model.SelectLights([first]); await Fresh(); await model.PollAsync();
            Check(model.SelectedPage == 1 && model.LightingDraft.Targets.SequenceEqual([first]),
                "Later polls do not overwrite a newly chosen page or target with startup preferences");

            var filteredPath = Path.Combine(output, "ui-filtered.json");
            await File.WriteAllTextAsync(filteredPath, JsonSerializer.Serialize(new DeviceUiPreferences(1, new(),
                new(99, [second, second, new(demo.DeviceIds[0], "fan1"), new("missing-device", "led1")])), Contract.Json));
            var filteredBytes = await File.ReadAllBytesAsync(filteredPath);
            var filtered = new DeviceWorkspaceViewModel(client, new DeviceUiPreferencesStore(filteredPath));
            await filtered.InitializeAsync(); await filtered.FlushUiAsync();
            var filteredAfterBytes = await File.ReadAllBytesAsync(filteredPath);
            Check(filtered.SelectedPage == 0 && filtered.LightingDraft.Targets.SequenceEqual([second]) &&
                filteredBytes.SequenceEqual(filteredAfterBytes),
                "Invalid page, duplicate and absent or non-lighting targets are filtered without rewriting the source UI file");
            var malformedPath = Path.Combine(output, "ui-malformed.json");
            await File.WriteAllTextAsync(malformedPath, "{ invalid local UI preferences");
            var malformed = new DeviceWorkspaceViewModel(client, new DeviceUiPreferencesStore(malformedPath));
            await malformed.InitializeAsync(); await malformed.FlushUiAsync();
            Check(malformed.SelectedPage == 0 && malformed.LightingDraft.Targets.Length == 0 &&
                !string.IsNullOrWhiteSpace(malformed.WarningMessage) &&
                await File.ReadAllTextAsync(malformedPath) == "{ invalid local UI preferences",
                "Malformed local preferences fall back visibly and preserve the original bytes");
            Check(Writes().SequenceEqual(initialWrites) && client.Snapshot!.Configuration.Generation == generation &&
                SettingsTransfer.Hash(client.Snapshot.Configuration.Configuration) == configuration,
                "Restoring UI preferences changes no device output or authoritative configuration");

            var view = Descendants<DeviceWorkspaceView>(window).Single();
            var scroll = (ScrollViewer)view.FindName("WorkspaceScroll");
            var cooling = model.Cooling.Single(r => r.Draft.Target == new EndpointReference(demo.DeviceIds[0], "fan1"));
            var sibling = model.Cooling.Single(r => r.Draft.Target == new EndpointReference(demo.DeviceIds[0], "fan2"));
            var failure = model.Cooling.Single(r => r.Draft.Target == new EndpointReference(demo.DeviceIds[0], "fan3"));
            Border Card(DeviceCoolingRow row) => Descendants<Border>(view).Single(e =>
                AutomationProperties.GetAutomationId(e) == "CoolingDeviceCard" && ReferenceEquals(e.DataContext, row));
            var card = Card(cooling); var slider = LayoutElement<Slider>(card, "CoolingDraftDuty");
            var currentDuty = LayoutElement<TextBlock>(card, "CoolingCurrentDuty");
            var mode = Descendants<ComboBox>(card).Single(c => c.ItemsSource == cooling.Modes);
            var coolingApply = LayoutElement<Button>(card, "CoolingApplyButton");
            var coolingRevert = LayoutElement<Button>(card, "CoolingRevertButton");
            var originalMode = cooling.Mode; var originalDuty = cooling.Duty;
            Check(!cooling.HasPendingChanges && !coolingApply.IsEnabled && !model.Revert.CanExecute(cooling),
                "An unchanged cooling row has no pending state and no enabled Apply or Revert");
            Check(cooling.Draft.DeviceManaged && currentDuty.IsVisible && currentDuty.Text == cooling.CurrentDuty &&
                !slider.IsVisible && Writes().SequenceEqual(initialWrites),
                "Device-managed cooling shows its current reading without a disabled or misleading output slider");
            mode.SetCurrentValue(Selector.SelectedItemProperty, "固定");
            await Settle();
            Check(slider.IsVisible && slider.IsEnabled && currentDuty.IsVisible &&
                currentDuty.Text == cooling.CurrentDuty && Writes().SequenceEqual(initialWrites),
                "Choosing fixed control reveals the editable slider while preserving the read-only current output");
            slider.SetCurrentValue(RangeBase.ValueProperty, 61d); await Fresh();
            Check(cooling.HasPendingChanges && cooling.ModeChanged && cooling.DutyChanged && !cooling.ProfileChanged &&
                coolingApply.IsEnabled && coolingRevert.IsVisible && model.Revert.CanExecute(cooling) &&
                LayoutElement<TextBlock>(card, "CoolingDutyPending").IsVisible && Writes().SequenceEqual(initialWrites),
                "Editing the real cooling inputs marks only changed values without sending output");
            slider.SetCurrentValue(RangeBase.ValueProperty, (double)originalDuty);
            mode.SetCurrentValue(Selector.SelectedItemProperty, originalMode); await Fresh();
            Check(!cooling.HasPendingChanges && !coolingApply.IsEnabled && !cooling.ModeChanged && !cooling.DutyChanged,
                "Returning every cooling input to its original value cancels the pending change");
            mode.SetCurrentValue(Selector.SelectedItemProperty, "固定");
            slider.SetCurrentValue(RangeBase.ValueProperty, 61d); sibling.Duty = 62;
            model.SelectedPage = 2; await Fresh(); model.SelectedPage = 1; await Fresh();
            Check(cooling.Duty == 61 && sibling.Duty == 62 && cooling.HasPendingChanges && sibling.HasPendingChanges,
                "Navigation and polling retain separate unsent drafts for two cooling endpoints");
            var siblingRevert = LayoutElement<Button>(Card(sibling), "CoolingRevertButton");
            siblingRevert.Command.Execute(siblingRevert.CommandParameter); await Fresh();
            Check(!sibling.HasPendingChanges && cooling.HasPendingChanges && Writes().SequenceEqual(initialWrites),
                "Reverting one endpoint leaves another endpoint's draft intact and writes no output");
            var beforeFanApply = demo.Hubs[0].Writes;
            coolingApply.Command.Execute(coolingApply.CommandParameter);
            await LayoutUntil(() => !cooling.Busy && demo.Hubs[0].Writes > beforeFanApply); await Fresh();
            Check(!cooling.HasPendingChanges && !coolingApply.IsEnabled && cooling.CurrentDuty == "61%" &&
                (await demo.Hubs[0].ReadAsync()).Channels.Single(c => c.Id == "fan1").Duty == 61 && demo.Hubs[1].Writes == 0,
                "The actual bound Apply changes only its DEMO fan and clears the confirmed pending state");
            cooling.Duty = 65; cooling.Duty = 61; await Fresh();
            Check(!cooling.HasPendingChanges && !model.Apply.CanExecute(cooling),
                "After success, the confirmed duty becomes the baseline for a local undo");

            model.SelectedPage = 2; model.SelectLights([first]); await Fresh();
            var lightApply = LayoutElement<Button>(view, "LightingApplyButton");
            var lightFooter = LayoutElement<FrameworkElement>(view, "LightingApplyFooter");
            var advanced = LayoutElement<Expander>(view, "LightingAdvancedSection");
            var originalColor = model.LightColor; var beforeLightEdit = Writes();
            Check(!model.LightHasPendingChanges && !lightApply.IsEnabled,
                "Selecting ready lighting alone does not produce a pending program or enable Apply");
            model.LightColor = Colors.MediumPurple; await Fresh();
            Check(model.LightHasPendingChanges && model.LightColorPending && !model.LightEffectPending && lightApply.IsEnabled &&
                LayoutElement<TextBlock>(view, "LightingColorPending").IsVisible && Writes().SequenceEqual(beforeLightEdit),
                "A color edit exposes its pending marker but sends no output");
            model.LightColor = originalColor; await Fresh();
            Check(!model.LightHasPendingChanges && !lightApply.IsEnabled,
                "Returning lighting to its original program cancels pending state");
            model.LightColor = Colors.MediumPurple; model.SelectLights([second]); await Fresh();
            Check(!model.LightHasPendingChanges && !lightApply.IsEnabled && model.LightColor == originalColor &&
                Writes().SequenceEqual(beforeLightEdit), "Switching the target discards its previous unsent color without applying it elsewhere");
            model.SelectLights([first, second]); model.LightColor = Colors.MediumPurple;
            model.SelectLights([second, first]); await Fresh();
            Check(model.LightHasPendingChanges && model.LightColor == Colors.MediumPurple,
                "Reordering an identical lighting target set does not discard its draft");
            model.SelectLights([first]); model.LightColor = Colors.MediumPurple;
            var beforeLightApply = demo.Hubs[0].LightingWrites;
            lightApply.Command.Execute(lightApply.CommandParameter);
            await LayoutUntil(() => model.LightingIdle && demo.Hubs[0].LightingWrites > beforeLightApply); await Fresh();
            Check(!model.LightHasPendingChanges && !lightApply.IsEnabled && demo.Hubs[1].LightingWrites == 0 &&
                client.Snapshot!.Configuration.Configuration.Lighting.Preferences!.Single(p => p.Target == first).Program.Colors[0] == new RgbColor(147, 112, 219),
                "A successful DEMO write and preference save clear only the submitted lighting draft");
            model.LightSceneName = "Simple UX DEMO scene"; await model.SaveLightLibraryAsync(true); await Fresh();
            model.LightColor = Colors.Lime; await model.RecallLightSceneAsync(); await Fresh();
            Check(model.LightHasPendingChanges && model.LightColor == Colors.Lime,
                "Recalling a saved scene cannot silently clear a different unsent editor color");
            var beforeReload = Writes(); model.ReloadLights.Execute(null); await Fresh();
            Check(!model.LightHasPendingChanges && model.LightColor == Colors.MediumPurple && Writes().SequenceEqual(beforeReload),
                "Explicit lighting Revert reloads current preferences without sending output");
            model.SelectLights([second]); model.LightColor = Colors.Cyan; await model.ApplyLightsAsync(); await Fresh();
            model.SelectLights([first, second]); await Fresh();
            Check(model.LightingDraft.Mixed && !model.LightHasPendingChanges && !model.CanApplyLighting,
                "Different saved programs show a mixed selection without pretending an unapplied edit exists");
            model.LightColor = Colors.MediumPurple; await Fresh();
            Check(!model.LightingDraft.Mixed && model.LightHasPendingChanges && model.CanApplyLighting,
                "An explicit common value turns a mixed selection into an applicable pending program");

            model.SelectLights([first]); model.LightColor = Colors.Tomato; cooling.Duty = 66; await Fresh();
            var beforeLayout = Writes(); var layoutGeneration = client.Snapshot!.Configuration.Generation;
            foreach (var size in new[] { (Name: "normal", Width: 1120d, Height: 820d), (Name: "minimum", Width: 960d, Height: 640d) })
            foreach (var theme in new[] { "light", "dark" })
            {
                var key = size.Name + "-" + theme;
                window.Width = size.Width; window.Height = size.Height; model.Theme = theme;
                model.SelectedPage = 1; cooling.DetailsExpanded = false; await Fresh(); scroll.ScrollToTop(); await Settle();
                var details = LayoutElement<Expander>(card, "CoolingDetailsSection");
                var everyday = LayoutElement<FrameworkElement>(card, "CoolingDraftControls");
                coolingApply.BringIntoView(); await Settle();
                Check(!details.IsExpanded && everyday.IsVisible && coolingRevert.IsVisible &&
                    !Descendants<Button>(details).Contains(coolingRevert) && !Descendants<Button>(details).Contains(coolingApply) &&
                    LayoutActionReachable(coolingApply, view) && LayoutActionReachable(coolingRevert, view) && scroll.ScrollableWidth < 1,
                    key + " cooling exposes current output, pending edit, Apply and Revert without opening details");
                Shot("simple-cooling-everyday-" + key);
                details.SetCurrentValue(Expander.IsExpandedProperty, true); await Fresh(); coolingApply.BringIntoView(); await Settle();
                Check(details.IsExpanded && cooling.HasPendingChanges && cooling.Duty == 66 &&
                    LayoutActionReachable(coolingApply, view) && scroll.ScrollableWidth < 1,
                    key + " opening cooling details preserves its draft and ordinary actions");
                Shot("simple-cooling-details-" + key);
                details.SetCurrentValue(Expander.IsExpandedProperty, false);

                model.SelectedPage = 2; advanced.SetCurrentValue(Expander.IsExpandedProperty, false); await Fresh();
                model.ReportResult("DEMO layout warning: a previous operation needs review", Outcome.Unknown);
                scroll.ScrollToTop(); await Settle();
                var top = LayoutBounds(lightFooter, view);
                Check(!advanced.IsExpanded && LayoutElement<FrameworkElement>(view, "LightingEverydayControls").IsVisible &&
                    model.LightHasPendingChanges && LayoutActionReachable(lightApply, view) &&
                    LayoutElement<TextBlock>(view, "WorkspaceWarningMessage").IsVisible && scroll.ScrollableWidth < 1,
                    key + " lighting exposes its basic inputs and pending Apply while keeping warnings outside collapsed details");
                Shot("simple-lighting-everyday-" + key);
                advanced.SetCurrentValue(Expander.IsExpandedProperty, true); await Fresh(); scroll.ScrollToBottom(); await Settle();
                Check(advanced.IsExpanded && model.LightColor == Colors.Tomato && model.LightHasPendingChanges &&
                    LayoutSameRect(top, LayoutBounds(lightFooter, view)) && LayoutActionReachable(lightApply, view) && scroll.ScrollableWidth < 1,
                    key + " lighting details and scrolling leave the draft and fixed Apply footer intact");
                geometry.Add(new { key, coolingApply = LayoutRect(LayoutBounds(coolingApply, card)),
                    lightFooter = LayoutRect(top), lightApply = LayoutRect(LayoutBounds(lightApply, view)),
                    width = view.ActualWidth, height = view.ActualHeight });
                Shot("simple-lighting-details-" + key); model.Notice = "";
            }
            Check(Writes().SequenceEqual(beforeLayout) && client.Snapshot!.Configuration.Generation == layoutGeneration,
                "Themes, window sizes, everyday/details navigation and pending markers do not submit output or settings");
            coolingRevert.Command.Execute(coolingRevert.CommandParameter); model.ReloadLights.Execute(null); await Fresh();

            // Real failures in the memory-backed adapter exercise outcome and recovery handling, not fabricated UI flags.
            window.Width = 960; window.Height = 640; model.SelectedPage = 1;
            failure.Duty = 64; demo.Hubs[0].FailNextFanWrite = true;
            await model.OperateAsync(failure); await Fresh();
            Check(failure.HasPendingChanges && failure.Duty == 64 && !failure.CanApply &&
                !string.IsNullOrWhiteSpace(failure.Message) && !string.IsNullOrWhiteSpace(model.WarningMessage),
                "A genuine DEMO fan mismatch retains the requested draft and exposes the failure instead of claiming success");
            var failedCard = Card(failure); failure.DetailsExpanded = false; failedCard.BringIntoView(); await Settle();
            Check(LayoutElement<FrameworkElement>(failedCard, "CoolingDraftControls").IsVisible &&
                LayoutElement<TextBlock>(view, "WorkspaceWarningMessage").IsVisible,
                "A cooling failure warning remains visible when endpoint details are collapsed");
            Shot("simple-cooling-failure-minimum");
            await model.OperateAsync(failure, DeviceOperation.ReleaseCooling); await Fresh();
            Check(!failure.HasPendingChanges && !client.Snapshot!.State.Endpoints.Single(e => e.Target == failure.Draft.Target).Owned,
                "The existing DEMO cooling release recovers the failed endpoint without treating the failed draft as applied");

            model.SelectedPage = 2; model.SelectLights([first]); model.LightColor = Colors.Lime;
            demo.Hubs[0].FailNextLightingWrite = true; await model.ApplyLightsAsync(); await Fresh();
            Check(model.LightHasPendingChanges && model.LightColor == Colors.Lime && model.LightRecoveryRequired &&
                !model.CanApplyLighting && model.StopLights.CanExecute(null),
                "A genuine DEMO lighting failure retains the draft and requires the existing recovery action");
            advanced.SetCurrentValue(Expander.IsExpandedProperty, false);
            foreach (var theme in new[] { "light", "dark" })
            {
                model.Theme = theme; await Fresh(); scroll.ScrollToBottom(); await Settle();
                var recovery = LayoutElement<TextBlock>(view, "LightingRecoveryMessage");
                var stop = Descendants<Button>(lightFooter).Single(b =>
                    System.Windows.Data.BindingOperations.GetBinding(b, Button.CommandProperty)?.Path.Path == "StopLights");
                Check(recovery.IsVisible && !string.IsNullOrWhiteSpace(recovery.Text) && LayoutActionReachable(stop, view) &&
                    LayoutElement<TextBlock>(view, "WorkspaceWarningMessage").IsVisible,
                    theme + " minimum-size everyday lighting keeps recovery and Stop accessible outside details");
                Shot("simple-lighting-recovery-minimum-" + theme);
            }
            model.StopLights.Execute(null); await LayoutUntil(() => model.LightingIdle && !model.LightRecoveryRequired); await Fresh();
            Check(model.LightHasPendingChanges && model.LightColor == Colors.Lime,
                "Finishing lighting recovery does not silently apply or discard the pending color");
            var beforeCancel = Writes(); model.ReloadLights.Execute(null); await Fresh();
            Check(!model.LightHasPendingChanges && !model.CanApplyLighting && Writes().SequenceEqual(beforeCancel),
                "Revert after recovery clears only the draft and performs no output");
            await model.FlushUiAsync();
            Check(await model.CloseAsync(), "The isolated simple-UX scenario completes its normal owner release");
        }
        catch (Exception e) { error = e.ToString(); report.Add("FAIL " + error); }
        await File.WriteAllLinesAsync(Path.Combine(output, "device-simple-ux-smoke.txt"), report);
        await File.WriteAllTextAsync(Path.Combine(output, "simple-ux-result.json"), JsonSerializer.Serialize(new
        { passed = report.Count(l => l.StartsWith("PASS ", StringComparison.Ordinal)), failed = error is null ? 0 : 1, error,
            hardwareAccessed = false, productionIpcOpened = false, scenario = "simple-ux", screenshots, geometry,
            captureMethod = "RenderTargetBitmap renders the DEMO WPF visual tree; no desktop capture or physical input is used" },
            new JsonSerializerOptions { WriteIndented = true }));
        return error is null ? 0 : 1;
    }
}
