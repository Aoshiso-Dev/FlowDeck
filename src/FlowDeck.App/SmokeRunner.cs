using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Threading;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

/// <summary>Opt-in, demo-only executable UI acceptance checks and WPF render artifacts.</summary>
public static partial class SmokeRunner
{
    public static async Task RunAsync(FlowDeckApplication app, RuntimeHost host, FlowDeckViewModel vm, FlowDeckWindow window, string output)
    {
        if (!host.Demo) throw new InvalidOperationException("--smoke requires --demo");
        if (Environment.GetCommandLineArgs().Contains("--generalization-only"))
        { await RunGeneralizationAsync(app, host, vm, window, output); return; }
        var report = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); report.Add("PASS " + message); }
        try
        {
            await Settle();
            Check(vm.DeviceCatalog.Count >= 5 && vm.DeviceCatalog.Any(d => d.Details.Contains("回転数")) &&
                vm.DeviceCatalog.Any(d => d.Details.Contains("固定色")), "Common device catalog includes monitor cooling and lighting capabilities");
            var catalogRows = vm.DeviceCatalog.ToArray();
            await host.Coordinator.TickAsync(); await Settle();
            Check(catalogRows.All(row => vm.DeviceCatalog.Contains(row)), "Telemetry updates retain device row instances and expansion state");
            Check(window.Icon is not null, "New application icon is loaded by WPF");
            Check(window.Title == "FlowDeck" && typeof(FlowDeckWindow).Assembly.GetName().Name == "FlowDeck",
                "Window and executable use the FlowDeck name");
            var productInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(Environment.ProcessPath!);
            Check(productInfo.ProductName == "FlowDeck" && productInfo.FileDescription == "FlowDeck",
                "Windows executable properties use the FlowDeck product name");
            Check(Descendants<TextBlock>(window).Any(t => t.IsVisible && t.Text == "FlowDeck") &&
                !Descendants<TextBlock>(window).Any(t => t.IsVisible && t.Text == "CONTROL HUB"),
                "Sidebar displays only FlowDeck without a product subtitle");
            Check(vm.Themes.SequenceEqual(new[] { "system", "light", "dark" }), "System light and dark theme choices are retained");
            Check(!vm.HubControlsOpen && !vm.CpuFanControlsOpen && !window.DashboardHistoryExpander.IsExpanded,
                "Monitor starts with output editors and history collapsed");
            var meter = new TelemetryMeter { Maximum = 100, Value = null };
            Check(meter.Fraction == 0, "Missing telemetry leaves the indicator empty");
            meter.Value = double.NaN; Check(meter.Fraction == 0, "Non-finite telemetry never fills the indicator");
            meter.Value = 75; Check(meter.Fraction == .75, "Temperature ring reflects its labeled scale");
            Check(RpmScaleConverter.Scale(4200, false) == 5000 && RpmScaleConverter.Scale(1800, true) == 6000,
                "RPM display scale grows for fast fans and uses a separate pump scale");
            Check(Descendants<TelemetryMeter>(window).Count(m => m.IsVisible && m.Circular) == vm.DashboardTemperatures.Count,
                "Every visible temperature has a circular indicator");
            ThemeManager.Apply("system"); await Settle(); Capture(window, output, "dashboard-system");
            ThemeManager.Apply("light"); await Settle();
            Check(vm.BoardFanRows.Count == 15 && !window.BoardFanSetupExpander.IsExpanded && !window.CpuFanCurveExpander.IsExpanded,
                "All fifteen detected fan channels are listed with advanced settings collapsed");
            Check(vm.DashboardTemperatures.Count == 4 && vm.DashboardFans.Any(r => r.Item.Source == "nzxt") && vm.DashboardFans.Any(r => r.Item.Source == "motherboard"), "Dashboard unifies CPU GPU board temperatures and NZXT motherboard RPM");
            var dashboardRevision = host.Coordinator.Snapshot.ControlRevision;
            var dashboardWrites = ((FlowDeck.Infrastructure.DemoHub)host.Hub).Writes;
            vm.SelectedPage = 6;
            var pumpChoice = vm.DashboardChoices.Single(c => c.SensorId == "demo/board/fan/2");
            pumpChoice.Visible = true; pumpChoice.Name = "水冷ポンプ（デモ）"; pumpChoice.Role = "ポンプ";
            vm.DashboardSelected = pumpChoice; vm.MoveDashboard.Execute("up");
            var hidden = vm.DashboardChoices.Single(c => c.Key == "nzxt:fan5"); hidden.Visible = false;
            await host.Coordinator.TickAsync(); await Settle();
            Check(pumpChoice.Name == "水冷ポンプ（デモ）" && pumpChoice.Visible && !hidden.Visible, "Telemetry refresh preserves dashboard edits and pump assignment");
            vm.SaveDashboard.Execute(null);
            await Until(() => host.Coordinator.Settings.Dashboard.Items?.Any(i => i.Role == "pump") == true);
            await host.FlushSettingsAsync(); vm.ReloadDashboard.Execute(null); await Settle();
            Check(vm.DashboardFans.Any(r => r.Role == "ポンプ" && r.State == "0 RPM") && !vm.DashboardFans.Any(r => r.Key == "nzxt:fan5"), "Dashboard persists role name visibility and zero RPM without guessing disconnection");
            Check(host.Coordinator.Snapshot.ControlRevision == dashboardRevision && ((FlowDeck.Infrastructure.DemoHub)host.Hub).Writes == dashboardWrites && !host.CpuFan.State.Managing,
                "Dashboard customization leaves NZXT and CPU fan control unchanged");
            Check((await host.Store.LoadAsync()).Dashboard.Items!.Select(i => i.Key).SequenceEqual(vm.DashboardChoices.Select(i => i.Key)), "Dashboard order survives save and reload");
            vm.SelectedPage = 0; await Settle(); Capture(window, output, "unified-dashboard-light");
            vm.DashboardDetails.Execute(vm.DashboardFans.First(r => r.Item.Source == "nzxt"));
            Check(vm.SelectedPage == 7, "NZXT details open dedicated common control page");
            vm.DashboardDetails.Execute(vm.DashboardFans.First(r => r.Item.SensorId == "demo/board/fan/0"));
            Check(vm.SelectedPage == 8 && vm.SelectedBoardFan == MotherboardFans.Cpu, "CPU fan details open individual control without writing");
            await Settle();
            Check(window.CpuFanCard.Parent is ContentControl, "Selected motherboard editor opens inside its fan row");
            Check(window.PageScroll.VerticalOffset > 0, "Dashboard details scroll directly to the expanded motherboard row");
            var cpuRow = vm.BoardFanRows.Single(r => r.Port == MotherboardFans.Cpu);
            var rowButton = Descendants<Button>(window.BoardFanList).Single(b => ReferenceEquals(b.DataContext, cpuRow) && b.Content is Grid);
            ((IInvokeProvider)new ButtonAutomationPeer(rowButton).GetPattern(PatternInterface.Invoke)).Invoke(); await Settle();
            Check(!vm.CpuFanControlsOpen && !window.CpuFanCard.IsVisible, "Clicking the open fan row closes its editor");
            ((IInvokeProvider)new ButtonAutomationPeer(rowButton).GetPattern(PatternInterface.Invoke)).Invoke(); await Settle();
            Check(vm.CpuFanControlsOpen && window.CpuFanCard.IsVisible && !host.CpuFan.State.Managing,
                "Opening a fan row again preserves its draft without writing output");
            vm.SelectedPage = 7;
            var before = host.Coordinator.Snapshot;
            await Settle();
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(window.NzxtManualSegment).GetPattern(PatternInterface.SelectionItem)).Select();
            await Settle();
            Check(vm.DraftMode == "Manual" && window.NzxtManualSegment.IsChecked == true && window.NzxtAutoSegment.IsChecked == false,
                "Custom NZXT segments support selection automation and preserve the selected mode");
            window.NzxtFanDutySlider.SetCurrentValue(Slider.ValueProperty, 54d); await Settle();
            Slider.IncreaseSmall.Execute(null, window.NzxtFanDutySlider); await Settle();
            Check(vm.ManualText == "55", "Custom slider keeps one percent keyboard command increments");
            Slider.DecreaseSmall.Execute(null, window.NzxtFanDutySlider); await Settle();
            var sliderTrack = (Track)window.NzxtFanDutySlider.Template.FindName("PART_Track", window.NzxtFanDutySlider);
            sliderTrack.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            sliderTrack.Thumb.RaiseEvent(new DragDeltaEventArgs(30, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            sliderTrack.Thumb.RaiseEvent(new DragCompletedEventArgs(30, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            await Settle(); Check(vm.ManualDuty > 54 && host.Coordinator.Snapshot.ControlRevision == before.ControlRevision,
                "Custom round thumb drag edits the draft without changing fan output");
            window.NzxtFanDutySlider.SetCurrentValue(Slider.ValueProperty, 54d); await Settle();
            Check(vm.ManualText == "54" && !Descendants<TextBox>(window).Any(t => t.IsVisible), "NZXT manual output uses a slider without an edit box");
            Check(host.Coordinator.Snapshot.ControlRevision == before.ControlRevision && host.Coordinator.Snapshot.ConfirmedDuty == before.ConfirmedDuty, "Editing manual draft does not write");
            window.HubControlExpander.BringIntoView(new Rect(0, 0, window.HubControlExpander.ActualWidth, 420)); await Settle();
            Capture(window, output, "manual-draft");
            vm.ApplyFan.Execute(null);
            await Until(() => host.Coordinator.Snapshot.Mode == FanMode.Manual && !host.Coordinator.Snapshot.FanBusy);
            Check(host.Coordinator.Snapshot.ConfirmedDuty == 54, "All five reported duties confirmed at 54%");
            await Settle(); vm.ManualDuty = 59; vm.ApplyFan.Execute(null);
            await Until(() => host.Coordinator.Snapshot.ConfirmedDuty == 59);
            await Settle();
            vm.SelectLight.Execute("purple"); vm.ApplyLight.Execute(null);
            await Until(() => host.Coordinator.Snapshot.Lighting.Outcome == Outcome.WrittenUnverified);
            Check(host.Coordinator.Snapshot.ConfirmedDuty == 59, "Lighting is writtenUnverified and leaves fan policy unchanged");
            vm.SelectedPage = 2; await Settle();
            vm.HubLightSync = false; vm.HubLightMode = "fading"; vm.HubColorSlot = 0; vm.SelectHubLightColor.Execute("blue"); await Settle();
            var picker = window.HubColorPicker; var wheel = picker.Wheel;
            var wheelPeer = (IValueProvider)UIElementAutomationPeer.CreatePeerForElement(wheel)!.GetPattern(PatternInterface.Value);
            var untouchedLight = host.Coordinator.Snapshot.Lighting; var untouchedFans = host.Coordinator.Snapshot.ControlRevision;
            foreach (var color in new[] { Colors.Red, Colors.Lime, Colors.Blue, Colors.White, Colors.Black, Colors.Gray, Color.FromRgb(52, 137, 255), Color.FromRgb(1, 2, 3) })
            {
                var hsv = ColorMath.ToHsv(color); var roundTrip = ColorMath.FromHsv(hsv.Hue, hsv.Saturation, hsv.Value);
                if (roundTrip != color) throw new InvalidOperationException("HSV color round trip changed RGB bytes");
            }
            Check(true, "HSV conversion preserves primary colors white black gray and low brightness");
            wheelPeer.SetValue("#0000FF"); await Settle();
            Check(vm.HubSelectedColor == Colors.Blue && picker.Hex == "#0000FF", "Wheel color automation updates the draft and large preview through two-way binding");
            var area = wheel.ColorArea;
            wheel.BeginSelection(new Point(area.Left, area.Top));
            var greenAngle = 30 * Math.PI / 180;
            wheel.BeginSelection(new Point(wheel.ActualWidth / 2 + Math.Cos(greenAngle) * 238 * .425, wheel.ActualHeight / 2 + Math.Sin(greenAngle) * 238 * .425));
            Check(wheel.SelectedColor == Colors.White && Math.Abs(wheel.Hue - 120) < .1, "White retains a newly selected hue without forcing saturation");
            wheel.BeginSelection(new Point(area.Right, area.Top));
            Check(vm.HubSelectedColor == Colors.Lime, "Central color plane restores the hue selected while white");
            wheel.UpdateSelection(new Point(area.Right + 500, area.Bottom + 500));
            Check(wheel.SelectedColor == Colors.Black && wheel.Saturation == 1 && wheel.Value == 0, "Dragging outside the color plane clamps at black without wrapping");
            wheel.Adjust(Key.Up, true);
            Check(wheel.Value == .1 && vm.HubGreen > 0 && vm.HubRed == 0, "Keyboard adjustment restores brightness from black");
            picker.HexDraft = "#12ZX89"; var validBefore = picker.SelectedColor;
            Check(!picker.CommitHex() && picker.SelectedColor == validBefore && picker.HexError.Length > 0, "Invalid HEX preserves the selected color and shows a field error");
            picker.HexDraft = "3489ff"; Check(picker.CommitHex(), "HEX accepts six digits with optional hash"); await Settle();
            Check(vm.HubLightHex == "#3489FF" && wheelPeer.Value == "#3489FF", "HEX updates the wheel preview and RGB values together");
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(window.HubSecondSwatch).GetPattern(PatternInterface.SelectionItem)).Select(); await Settle();
            picker.HexDraft = "#FFB864"; picker.CommitHex();
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(window.HubFirstSwatch).GetPattern(PatternInterface.SelectionItem)).Select(); await Settle();
            Check(vm.HubLightHex == "#3489FF" && window.HubFirstSwatch.IsChecked == true, "Palette swatches switch color slots without overwriting the other color");
            var favoriteCount = vm.LightPalette.Favorites.Count;
            ((IInvokeProvider)new ButtonAutomationPeer(picker.FavoriteButton).GetPattern(PatternInterface.Invoke)).Invoke(); await Settle();
            Check(vm.LightPalette.Contains(picker.SelectedColor) && ReferenceEquals(window.BoardColorPicker.Palette, picker.Palette), "Favorites are shared by NZXT and motherboard pickers");
            ((IInvokeProvider)new ButtonAutomationPeer(picker.FavoriteButton).GetPattern(PatternInterface.Invoke)).Invoke(); await Settle();
            Check(vm.LightPalette.Favorites.Count == favoriteCount, "Clicking the saved star again removes the favorite");
            foreach (var swatch in ColorPalette.Presets.Take(3)) vm.LightPalette.ToggleFavorite(swatch.Color);
            var storedPalette = new ColorPalette(host.Store.DirectoryPath);
            Check(storedPalette.Favorites.Select(c => c.Hex).SequenceEqual(vm.LightPalette.Favorites.Select(c => c.Hex)), "Favorites persist in the isolated UI palette file");
            Check(host.Coordinator.Snapshot.Lighting == untouchedLight && host.Coordinator.Snapshot.ControlRevision == untouchedFans && vm.LightPalette.Recent.Count == 0,
                "Wheel HEX swatches and favorites edit only the draft until Apply");
            vm.HubColorSlot = 1; vm.SelectHubLightColor.Execute("purple"); vm.HubColorSlot = 0;
            Check(vm.BoardLightTargets.Count == 5, "Motherboard lighting lists all and individual hardware zones");
            await host.Coordinator.UpdateSettingsAsync(host.Coordinator.Settings with { Motherboard = host.Coordinator.Settings.Motherboard with { OpenRgbEnabled = true } }, false);
            vm.ConnectBoardLight.Execute(null); await Until(() => vm.BoardLightIdle); await Settle();
            Check(!vm.BoardLightRequiresUsb && !host.Coordinator.Settings.Motherboard.OpenRgbEnabled, "Lighting page can switch saved SDK inventory mode to direct USB");
            var lightFanRevision = host.Coordinator.Snapshot.ControlRevision;
            var hubLighting = host.Coordinator.Snapshot.Lighting;
            var ioTarget = vm.BoardLightTargets.Single(t => t.Zones.Length == 1 && t.Zones[0].HardwareZone == 5);
            vm.BoardLightTarget = ioTarget; vm.BoardLightEffect = 2; vm.SelectBoardLightColor.Execute("purple"); vm.BoardLightSpeed = 50;
            await Settle();
            Check(host.Rgb.Devices[0].Zones[0].Configuration!.Mode == 14, "Editing motherboard lighting never writes before Apply");
            window.BoardColorPicker.DetailsExpander.IsExpanded = true; window.BoardLightingCard.BringIntoView(); await Settle(); Capture(window, output, "motherboard-lighting-light");
            ThemeManager.Apply("dark"); await Settle(); Capture(window, output, "motherboard-lighting-dark"); ThemeManager.Apply("light");
            Check(Descendants<Slider>(window.BoardLightingCard).Count(s => s.IsVisible) == 4, "Motherboard RGB color and speed use sliders");
            vm.ApplyBoardLight.Execute(null); await Until(() => vm.BoardLightResult.Contains("設定一致")); await Settle();
            Check(host.Rgb.Devices[0].Zones[0].Configuration is { Mode: 2, Red: 160, Green: 121, Blue: 236 }, "Individual motherboard effect reads back and persists");
            Check(vm.LightPalette.Recent.First().Hex == "#A079EC", "A verified motherboard color enters the shared recent palette");
            Check(host.Rgb.Devices[0].Zones[1].Configuration!.Mode == 14 && host.Coordinator.Snapshot.Lighting == hubLighting &&
                host.Coordinator.Snapshot.ControlRevision == lightFanRevision, "Motherboard lighting leaves other zones NZXT lighting and cooling unchanged");
            Check(host.Coordinator.Settings.Motherboard.RgbZones!.Any(z => z.LastLighting?.Mode == 2), "Confirmed motherboard lighting is saved");
            vm.BoardLightTarget = vm.BoardLightTargets.Single(t => t.Zones.Length == 1 && t.Zones[0].HardwareZone == 3);
            Check(!vm.ApplyBoardLight.CanExecute(null), "Zero LED count zone cannot be written");
            vm.BoardLightTarget = vm.BoardLightTargets[0]; vm.BoardLightEffect = 1; vm.SelectBoardLightColor.Execute("blue");
            vm.ApplyBoardLight.Execute(null); await Until(() => vm.BoardLightResult.Contains("設定一致")); await Settle();
            Check(host.Rgb.Devices[0].Zones.Where(z => z.LedCount > 0).All(z => z.Configuration!.Mode == 1), "Apply all changes only available nonempty motherboard zones");
            vm.ReloadBoardLight.Execute(null); await Until(() => vm.BoardLightIdle); await Settle();
            Check(vm.BoardLightEffect == 1 && vm.BoardBlue == 255, "Reload reads current hardware configuration into sliders");
            window.Width = 960; window.Height = 640; await Settle(); Capture(window, output, "motherboard-lighting-minimum");
            Check(window.BoardLightingCard.ActualWidth <= window.ActualWidth - 150, "Motherboard lighting fits minimum window width");
            window.Width = 1200; window.Height = 820; window.BoardColorPicker.DetailsExpander.IsExpanded = false;
            vm.SelectedPage = 2; window.HubLightingCard.BringIntoView(); await Settle();
            vm.HubLightSync = false; vm.HubLightMode = "fading"; vm.SelectHubLightColor.Execute("blue");
            vm.HubColorSlot = 1; vm.SelectHubLightColor.Execute("purple"); vm.HubLightSpeed = 3;
            var syncFanRevision = host.Coordinator.Snapshot.ControlRevision;
            vm.ApplyHubLight.Execute(null); await Until(() => host.Coordinator.Snapshot.Lighting.Effect?.Mode == "fading");
            Check(host.Coordinator.Snapshot.Lighting.Effect!.Colors.Length == 2, "NZXT palette effects send both chosen colors");
            Check(new ColorPalette(host.Store.DirectoryPath).Recent.Take(2).Select(c => c.Hex).SequenceEqual(new[] { "#3489FF", "#A079EC" }),
                "Applying a two-color effect persists both recent colors without duplicates");
            Check(host.Coordinator.Snapshot.ControlRevision == syncFanRevision, "NZXT native effects preserve cooling control");
            window.HubColorPicker.DetailsExpander.IsExpanded = true; window.HubLightingCard.BringIntoView(); await Settle();
            Capture(window, output, "nzxt-effects-light");
            window.HubColorPicker.DetailsExpander.IsExpanded = false; window.PageScroll.ScrollToTop(); await Settle();
            Capture(window, output, "color-picker-light");
            ThemeManager.Apply("dark"); await Settle(); Capture(window, output, "color-picker-dark");
            ThemeManager.Apply("system"); await Settle(); Capture(window, output, "color-picker-system");
            window.Width = 960; window.Height = 640; await Settle(); Capture(window, output, "color-picker-minimum");
            Check(picker.ActualWidth >= 460 && picker.ActualWidth < window.ActualWidth - 150, "Color wheel and swatches fit the minimum window width");
            window.Width = 1200; window.Height = 820; ThemeManager.Apply("light"); await Settle();
            vm.HubLightSync = true; vm.HubLightMode = "breathing"; vm.HubColorSlot = 0;
            vm.SelectHubLightColor.Execute("blue"); vm.HubLightSpeed = 4;
            vm.ApplyHubLight.Execute(null); await Until(() => host.LightingSynchronized && host.LightingFrames >= 3);
            await Settle();
            Check(window.HubLightSyncEffectPicker.IsVisible && window.HubLightSyncEffectPicker.SelectedValue?.ToString() == "breathing",
                "Switching from a native palette to synchronization keeps the breathing selection visible");
            Check(!vm.BoardLightIdle && !vm.ApplyBoardLight.CanExecute(null), "Synchronization owns motherboard lighting while native editors are disabled");
            Check(!vm.HubLightEffects.Any(e => e.Id == "spectrum-wave") && vm.HubLightEffects.Any(e => e.Id == "color-cycle"),
                "Synchronization offers effects with a shared timebase rather than unrelated hardware waves");
            Check(host.Coordinator.Snapshot.ControlRevision == syncFanRevision && host.Coordinator.Snapshot.ConfirmedDuty == 59,
                "Synchronized animation leaves fan output unchanged");
            ThemeManager.Apply("dark"); await Settle(); Capture(window, output, "lighting-sync-dark");
            window.Width = 960; window.Height = 640; await Settle(); Capture(window, output, "lighting-sync-minimum");
            vm.StopLightingSync.Execute(null); await Until(() => !host.LightingSynchronized && vm.HubLightIdle); await Settle();
            Check(host.Rgb.Devices[0].Zones.Where(z => z.LedCount > 0).All(z => z.Configuration is { Mode: 1, Red: 52, Green: 137, Blue: 255 }),
                "Stopping synchronization settles every motherboard zone at the selected solid color");
            Check(((FlowDeck.Infrastructure.DemoHub)host.Hub).LightEffects["led1"].Colors[0] == new RgbColor(52,137,255),
                "Stopping synchronization settles NZXT at the same solid color");
            vm.HubLightMode = "color-cycle"; vm.ApplyHubLight.Execute(null); await Until(() => host.LightingSynchronized && host.LightingFrames >= 2);
            var pcOverride = await host.Coordinator.RequestAsync(new("sync-pc-override", "PC", host.Coordinator.Epoch,
                host.Coordinator.Snapshot.LightingRevision, "SetLighting", Lighting: "white"));
            await Until(() => !host.LightingSynchronized); await Settle();
            Check(pcOverride.Outcome == Outcome.WrittenUnverified && host.Coordinator.Snapshot.Lighting.LastSent == "white",
                "PC lighting changes stop synchronization and are not overwritten by old frames");
            await host.StopLightingSyncAsync();
            vm.RevertHubLight.Execute(null); vm.HubLightMode = "super-rainbow"; vm.HubLightReverse = true;
            vm.ApplyHubLight.Execute(null); await Until(() => host.Coordinator.Snapshot.Lighting.Effect?.Mode == "super-rainbow");
            await Settle();
            Check(window.HubLightEffectPicker.IsVisible && window.HubLightEffectPicker.SelectedValue?.ToString() == "super-rainbow",
                "Returning from synchronization keeps the native effect selection visible");
            Check(host.Coordinator.Snapshot.Lighting.Effect!.Reverse && !vm.HubLightResult.Contains("停止しました"),
                "Native lighting remains usable after a synchronization override");
            window.Width = 1200; window.Height = 900; window.HubColorPicker.DetailsExpander.IsExpanded = false;
            var sceneFanRevision = host.Coordinator.Snapshot.ControlRevision;
            var sceneLightRevision = host.Coordinator.Snapshot.LightingRevision;
            vm.LightingSceneName = "デスク"; vm.SceneIncludeHub = true; vm.SceneIncludeBoard = true;
            vm.SaveLightingScene.Execute(null); await Until(() => vm.LightingScenesIdle && vm.LightingSceneChoices.Count == 1);
            var deskScene = vm.SelectedLightingScene!;
            Check(deskScene.Hub.Single().Effect.Mode == "super-rainbow" && deskScene.Board.Length == 3 && deskScene.Sync is null,
                "Scene captures distinct native NZXT and motherboard targets without zero-LED zones");
            Check(host.Coordinator.Snapshot.LightingRevision == sceneLightRevision && host.Coordinator.Snapshot.ControlRevision == sceneFanRevision,
                "Saving a lighting scene never changes lighting or fan output");
            vm.LightingSceneName = "夜のデスク"; vm.RenameLightingScene.Execute(null);
            await Until(() => vm.LightingScenesIdle && vm.SelectedLightingScene?.Name == "夜のデスク");
            vm.ReloadLightingScenes.Execute(null); await Until(() => vm.LightingScenesIdle);
            Check(vm.SelectedLightingScene?.Name == "夜のデスク" && vm.SelectedLightingScene.Id == deskScene.Id,
                "Scene rename and reload preserve identity and saved device settings");
            deskScene = vm.SelectedLightingScene!;
            vm.RevertHubLight.Execute(null); vm.HubLightMode = "fixed"; vm.SelectHubLightColor.Execute("red"); vm.ApplyHubLight.Execute(null);
            await Until(() => vm.HubLightIdle && host.Coordinator.Snapshot.Lighting.Effect?.Mode == "fixed");
            vm.BoardLightTarget = vm.BoardLightTargets[0]; vm.BoardLightEffect = 1; vm.SelectBoardLightColor.Execute("red"); vm.ApplyBoardLight.Execute(null);
            await Until(() => vm.BoardLightIdle && vm.BoardLightResult.Contains("設定一致"));
            vm.ApplyLightingScene.Execute(null); await Until(() => vm.LightingScenesIdle && vm.LightingSceneResult.StartsWith("適用済み"));
            Check(host.Coordinator.Snapshot.Lighting.Effect?.Mode == "super-rainbow" && deskScene.Board.All(saved =>
                host.Rgb.Devices.Single().Zones.Single(z => z.HardwareZone == saved.Zone).Configuration == saved.Effect),
                "Scene recall restores per-target effects colors speeds and native direction");
            var stableLight = host.Coordinator.Snapshot.Lighting; var stableBoard = host.Rgb;
            var missingScene = deskScene with { Hub = [deskScene.Hub[0] with { HubId = "different-device" }] };
            var missing = await host.ApplyLightingSceneAsync(missingScene);
            Check(!missing.Succeeded && host.Coordinator.Snapshot.Lighting == stableLight && ReferenceEquals(host.Rgb, stableBoard),
                "Missing scene hardware is rejected before any writes");
            Task<OperationResult>? competing = null;
            void OverrideScene(OperationResult r)
            {
                if (r.Source == "照明シーン" && r.Outcome == Outcome.WrittenUnverified && competing is null)
                    competing = host.Coordinator.RequestAsync(new("scene-pc-override", "PC", host.Coordinator.Epoch,
                        host.Coordinator.Snapshot.LightingRevision, "SetLighting", Lighting: "white"));
            }
            host.Coordinator.OperationCompleted += OverrideScene;
            SceneApplyResult interrupted;
            try { interrupted = await host.ApplyLightingSceneAsync(deskScene); if (competing is not null) await competing; }
            finally { host.Coordinator.OperationCompleted -= OverrideScene; }
            Check(!interrupted.Succeeded && interrupted.Rows.Any(r => r.Result == "未完了") && host.Coordinator.Snapshot.Lighting.LastSent == "white" && ReferenceEquals(host.Rgb, stableBoard),
                "A newer PC operation interrupts scene recall and leaves pending motherboard targets untouched");
            await Until(() => vm.CanApplyHubLight);
            vm.RevertHubLight.Execute(null); vm.HubLightSync = true; vm.HubLightMode = "breathing"; vm.HubLightSpeed = 3;
            vm.SelectHubLightColor.Execute("blue"); vm.ApplyHubLight.Execute(null); await Until(() => host.LightingSynchronized && vm.HubLightIdle);
            vm.LightingSceneName = "青い呼吸"; vm.SaveLightingScene.Execute(null);
            await Until(() => vm.LightingScenesIdle && vm.LightingSceneChoices.Count == 2);
            Check(vm.SelectedLightingScene?.Sync is { Mode: "breathing", Speed: 3 } && host.LightingSynchronized,
                "Saving a synchronized scene preserves its clock effect and targets without stopping animation");
            vm.StopLightingSync.Execute(null); await Until(() => !host.LightingSynchronized && vm.HubLightIdle);
            vm.ApplyLightingScene.Execute(null); await Until(() => vm.LightingScenesIdle && host.LightingSynchronized);
            Check(vm.HubLightSync && vm.HubLightMode == "breathing" && vm.HubLightSpeed == 3 && host.ActiveLightingSync?.Zones.Length == 3,
                "Recalling a synchronized scene restarts the saved effect and updates the editor");
            vm.DeleteLightingScene.Execute(null); await Until(() => vm.LightingScenesIdle && vm.LightingSceneChoices.Count == 1);
            vm.UndoDeleteLightingScene.Execute(null); await Until(() => vm.LightingScenesIdle && vm.LightingSceneChoices.Count == 2);
            Check(host.LightingSynchronized && vm.SelectedLightingScene?.Name == "青い呼吸", "Scene deletion and undo preserve running lighting");
            Check(host.Coordinator.Snapshot.ControlRevision == sceneFanRevision && host.Coordinator.Snapshot.ConfirmedDuty == 59,
                "Scene save rename recall interruption and delete never change cooling policy");
            vm.SceneManagementOpen = true; window.PageScroll.ScrollToTop(); ThemeManager.Apply("dark"); await Settle(); Capture(window, output, "lighting-scenes-dark");
            ThemeManager.Apply("light"); await Settle(); Capture(window, output, "lighting-scenes-light");
            vm.SceneManagementOpen = false; vm.StopLightingSync.Execute(null); await Until(() => !host.LightingSynchronized && vm.HubLightIdle);
            await Until(() => vm.LightingScenesIdle);
            var originalBrightness = await host.CaptureLightingSceneAsync(Guid.NewGuid().ToString("N"), "調光前", true, true);
            var brightnessFanRevision = host.Coordinator.Snapshot.ControlRevision;
            vm.MasterBrightness = 50; vm.ApplyBrightness.Execute(null);
            await Until(() => vm.LightingScenesIdle && vm.BrightnessResult.Contains("50% · 適用済み"));
            Check(host.Coordinator.Settings.LightingPorts["led1"].Effect!.Colors[0] == originalBrightness.Hub[0].Effect.Colors[0].Scale(.5),
                "Master brightness scales applied colors while preserving the original palette");
            vm.MasterBrightness = 100; vm.ApplyBrightness.Execute(null);
            await Until(() => vm.LightingScenesIdle && vm.BrightnessResult.Contains("100% · 適用済み"));
            Check(LightingBrightness.SameOutput(originalBrightness, await host.CaptureLightingSceneAsync(Guid.NewGuid().ToString("N"), "復元", true, true)),
                "Returning brightness to 100 percent restores exact original colors without cumulative rounding");
            vm.MasterBrightness = 50; vm.ApplyBrightness.Execute(null); await Until(() => vm.LightingScenesIdle && vm.BrightnessResult.Contains("50% · 適用済み"));
            vm.TurnAllLightsOff.Execute(null); await Until(() => vm.LightingScenesIdle && vm.BrightnessResult == "消灯済み");
            var firstOff = host.LightingAdjustment;
            vm.TurnAllLightsOff.Execute(null); await Until(() => vm.LightingScenesIdle && vm.BrightnessResult == "消灯済み");
            Check(host.Coordinator.Settings.LightingPorts["led1"].Effect!.Mode == "off" && host.Rgb.Devices.Single().Zones.Where(z => z.LedCount > 0).All(z => z.Configuration?.Mode == 0) &&
                LightingBrightness.SameOutput(firstOff.BaseScene!, host.LightingAdjustment.BaseScene!), "Repeated all-off preserves the original effects and previous brightness");
            vm.RestoreLighting.Execute(null); await Until(() => vm.LightingScenesIdle && vm.BrightnessResult.Contains("50% · 適用済み"));
            Check(host.Coordinator.Settings.LightingPorts["led1"].Effect!.Colors[0] == originalBrightness.Hub[0].Effect.Colors[0].Scale(.5),
                "Restoring all-off returns to the brightness used before switching off");
            var savedDimScene = await host.CaptureLightingSceneAsync(Guid.NewGuid().ToString("N"), "別の照明", true, true);
            await host.ApplyLightingSceneAsync(savedDimScene);
            await Until(() => vm.LightingScenesIdle);
            vm.MasterBrightness = 50; vm.ApplyBrightness.Execute(null); await Until(() => vm.LightingScenesIdle && vm.BrightnessResult.Contains("50% · 適用済み"));
            Check(host.Coordinator.Settings.LightingPorts["led1"].Effect!.Colors[0] == savedDimScene.Hub[0].Effect.Colors[0].Scale(.5),
                "An explicit new scene becomes the brightness baseline even when its colors match the previous dimmed output");
            vm.SelectedLightingScene = vm.LightingSceneChoices.Single(s => s.Name == "青い呼吸");
            vm.ApplyLightingScene.Execute(null); await Until(() => vm.LightingScenesIdle && host.LightingSynchronized);
            vm.MasterBrightness = 30; vm.ApplyBrightness.Execute(null); await Until(() => vm.LightingScenesIdle && vm.BrightnessResult.Contains("30% · 適用済み"));
            Check(host.ActiveLightingSync is { Mode: "breathing", Speed: 3 } && host.ActiveLightingSync.Color == new RgbColor(52,137,255).Scale(.3),
                "Master brightness retains synchronized animation speed and scales its common color");
            vm.TurnAllLightsOff.Execute(null); await Until(() => vm.LightingScenesIdle && vm.BrightnessResult == "消灯済み" && !host.LightingSynchronized);
            vm.RestoreLighting.Execute(null); await Until(() => vm.LightingScenesIdle && host.LightingSynchronized && vm.BrightnessResult.Contains("30% · 適用済み"));
            Check(host.ActiveLightingSync?.Color == new RgbColor(52,137,255).Scale(.3), "All-off restoration restarts the saved synchronization at its previous brightness");
            vm.SelectedLightingScene = vm.LightingSceneChoices.Single(s => s.Name == "夜のデスク");
            vm.ApplyLightingScene.Execute(null); await Until(() => vm.LightingScenesIdle && vm.LightingSceneResult.StartsWith("適用済み"));
            var unsupportedBefore = host.Coordinator.Snapshot.Lighting;
            vm.MasterBrightness = 40; vm.ApplyBrightness.Execute(null); await Until(() => vm.LightingScenesIdle && vm.BrightnessResult.Contains("非対応"));
            Check(host.Coordinator.Snapshot.Lighting == unsupportedBefore && !host.LightingSynchronized, "Unsupported automatic rainbow dimming is reported before any output changes");
            vm.TurnAllLightsOff.Execute(null); await Until(() => vm.LightingScenesIdle && vm.BrightnessResult == "消灯済み");
            vm.RestoreLighting.Execute(null); await Until(() => vm.LightingScenesIdle && vm.BrightnessResult.Contains("100% · 適用済み"));
            Check(host.Coordinator.Snapshot.Lighting.Effect is { Mode: "super-rainbow", Reverse: true }, "All-off restores native rainbow settings including direction");
            var persistedAdjustment = await new LightingAdjustmentStore(host.Store.DirectoryPath).LoadAsync();
            Check(persistedAdjustment.Applied && persistedAdjustment.Brightness == 100 && persistedAdjustment.BaseScene!.Hub[0].Effect.Mode == "super-rainbow",
                "Brightness restoration data survives storage reload separately from scenes");
            var restartRoot = Path.Combine(output, "brightness-restart");
            await new LightingAdjustmentStore(Path.Combine(restartRoot, "demo")).SaveAsync(persistedAdjustment with { IsOff = true });
            await using (var restart = new RuntimeHost(restartRoot, true))
            {
                await restart.InitializeAsync(true);
                Check(restart.LightingAdjustment.IsOff && restart.Coordinator.Snapshot.Lighting.LastSent is null && !restart.LightingSynchronized,
                    "Restart loads restoration data without replaying lighting or starting animations");
                var restored = await restart.AdjustLightingAsync(restore: true);
                Check(restored.Succeeded && restart.Coordinator.Snapshot.Lighting.Effect is { Mode: "super-rainbow", Reverse: true },
                    "An explicit restore after restart uses saved original effects");
            }
            Check(host.Coordinator.Snapshot.ControlRevision == brightnessFanRevision && host.Coordinator.Snapshot.ConfirmedDuty == 59,
                "Brightness dimming all-off and restoration never change cooling policy");
            window.PageScroll.ScrollToTop(); window.Width = 1200; window.Height = 900; ThemeManager.Apply("dark"); await Settle(); Capture(window, output, "lighting-brightness-dark");
            ThemeManager.Apply("light"); await Settle(); Capture(window, output, "lighting-brightness-light");
            window.Width = 960; window.Height = 640; await Settle();
            Check(window.MasterBrightnessSlider.ActualWidth >= 100 && window.MasterBrightnessSlider.TranslatePoint(new Point(0, 0), window).Y < window.ActualHeight - 80,
                "Master brightness slider remains usable at minimum window width");
            Capture(window, output, "lighting-brightness-minimum");
            await RunLightingGroupChecksAsync(host, vm, window, output, Check);
            await RunLightingIdentificationChecksAsync(host, vm, window, output, Check);
            window.Width = 1200; window.Height = 820; window.HubColorPicker.DetailsExpander.IsExpanded = false; ThemeManager.Apply("light");
            vm.SelectedPage = 5; await Settle();
            Check(vm.BoardSensors.Any(r => r.Kind == "回転数") && vm.BoardSensors.Any(r => r.Kind == "電圧"), "Motherboard page lists RPM and voltage independently of CPU/GPU");
            var boardRevision = host.Coordinator.Snapshot.ControlRevision;
            vm.BoardSensors[0].DisplayName = "テスト温度";
            vm.SaveMotherboard.Execute(null);
            await Until(() => host.Coordinator.Settings.Motherboard.Sensors?.Any(s => s.DisplayName == "テスト温度") == true);
            Check(host.Coordinator.Snapshot.ControlRevision == boardRevision, "Motherboard display and navigation do not change fan control");
            vm.RefreshRgb.Execute(null); await Settle();
            Check(vm.RgbZones.Count == 4, "RGB inventory displays demo zones without hardware writes");
            vm.RgbZones[1].Accessory = "テストARGBファン"; vm.RgbZones[1].ConfirmedLeds = "24";
            await host.Coordinator.TickAsync(); await Settle();
            Check(vm.RgbZones[1].Accessory == "テストARGBファン", "Telemetry refresh preserves RGB annotation drafts");
            vm.SaveMotherboard.Execute(null);
            await Until(() => host.Coordinator.Settings.Motherboard.RgbZones?.Any(z => z.Accessory == "テストARGBファン" && z.ConfirmedLedCount == 24) == true);
            await host.FlushSettingsAsync(); vm.ReloadMotherboard.Execute(null); await Settle();
            Check(vm.RgbZones[1].ConfirmedLeds == "24" && host.Coordinator.Snapshot.ControlRevision == boardRevision,
                "RGB annotations persist separately from reported values without changing fan policy");
            Check(vm.BoardFanRows.Any(r => r.Name == "CPU_FAN1") && vm.BoardFanRows.Any(r => r.Name == "CHA_FAN1"), "Default sensor labels preserve verified physical header names in fan list");
            vm.SelectedPage = 8;
            await Settle();
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(window.BoardAutoSegment).GetPattern(PatternInterface.SelectionItem)).Select(); await Settle();
            Check(vm.BoardFanEditMode == "自動" && window.BoardManualSegment.IsChecked == false && !host.CpuFan.State.Managing,
                "Custom motherboard mode segments change the draft without starting control");
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(window.BoardManualSegment).GetPattern(PatternInterface.SelectionItem)).Select(); await Settle();
            var setupToggle = (ToggleButton)window.BoardFanSetupExpander.Template.FindName("HeaderSite", window.BoardFanSetupExpander);
            setupToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true); await Settle();
            Check(window.BoardFanSetupExpander.IsExpanded, "Custom disclosure header expands initial settings");
            setupToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false); await Settle();
            vm.CpuFanWiringConfirmed = true; vm.CpuFanMinimum = "30"; vm.CpuFanSave.Execute(null);
            await Until(() => host.Coordinator.Settings.CpuFan.Minimum == 30);
            Check(!host.CpuFan.State.Managing, "Saving CPU_FAN1 settings does not change output");
            await Settle(); window.BoardFanDutySlider.SetCurrentValue(Slider.ValueProperty, 47d); await Settle();
            Check(vm.CpuFanDuty == "47" && window.BoardFanDutySlider.Minimum == 30 && !host.CpuFan.State.Managing && !Descendants<TextBox>(window).Any(t => t.IsVisible),
                "Motherboard slider respects saved minimum and editing alone does not apply output");
            vm.BoardFanApply.Execute(null);
            await Until(() => host.CpuFan.State.Mode == "manual");
            Check(host.CpuFan.State.Reading?.Duty == 47 && host.Coordinator.Snapshot.ConfirmedDuty == 59 && host.Coordinator.Snapshot.ControlRevision == boardRevision,
                "CPU_FAN1 manual output is independent of NZXT");
            vm.CpuFanOverheat = "97";
            vm.CpuFanRestore.Execute(null); await Until(() => !host.CpuFan.State.Managing && vm.BoardFanCanSelect);
            Check(host.CpuFan.State.Reading?.Duty == 65, "CPU_FAN1 restore returns to the pre-control output");
            Check(vm.CpuFanDuty == "65" && window.BoardFanDutySlider.Value == 65 && vm.CpuFanOverheat == "97",
                "Returning to motherboard control aligns the slider with restored output without discarding curve drafts");
            vm.CpuFanPoints.Add(new(40, 30)); vm.CpuFanPoints.Add(new(80, 90)); vm.CpuFanOverheat = "95"; vm.CpuFanResume = "85";
            vm.CpuFanSave.Execute(null); await Until(() => host.Coordinator.Settings.CpuFan.Profile is not null);
            vm.CpuFanAuto.Execute(null); await Until(() => host.CpuFan.State.Mode == "auto");
            Check(host.CpuFan.State.Reading?.Duty is >= 30 and <= 90, "CPU_FAN1 saved temperature curve runs independently");
            vm.CpuFanRestore.Execute(null); await Until(() => !host.CpuFan.State.Managing);
            vm.CpuFanTest.Execute(null); await Until(() => host.CpuFan.State.Test is not null);
            Check(host.CpuFan.State.Test!.Restored && !host.CpuFan.State.Managing, "CPU_FAN1 bounded 100 percent test restores original control");
            vm.SelectedBoardFan = MotherboardFans.Chassis;
            Check(vm.CpuFanMinimum == "" && !vm.CpuFanWiringConfirmed, "New CHA_FAN1 editor does not inherit CPU fan calibration");
            vm.CpuFanDuty = "100"; vm.CpuFanManual.Execute(null); await Settle();
            Check(!host.BoardFans["cha-fan1"].State.Managing, "Unconfigured chassis manual output is rejected");
            vm.CpuFanTest.Execute(null); await Until(() => !vm.BoardFanCanSelect);
            vm.SelectedBoardFan = MotherboardFans.Cpu;
            Check(vm.SelectedBoardFan == MotherboardFans.Chassis, "Port selection stays fixed while its bounded test is running");
            await Until(() => host.BoardFans["cha-fan1"].State.Test is not null && vm.BoardFanCanSelect);
            Check(host.BoardFans["cha-fan1"].State.Test!.Restored && !host.BoardFans["cha-fan1"].State.Managing, "Chassis identification test restores motherboard control");
            vm.CpuFanMinimum = "35"; vm.CpuFanWiringConfirmed = true; vm.CpuFanDuty = "45";
            vm.SelectedBoardFan = MotherboardFans.Cpu; vm.SelectedBoardFan = MotherboardFans.Chassis;
            Check(vm.CpuFanMinimum == "35" && vm.CpuFanDuty == "45", "Each port keeps its unsaved editor draft when switching");
            vm.CpuFanSave.Execute(null); await Until(() => host.Coordinator.Settings.ChassisFan.Minimum == 35 && vm.BoardFanCanSelect);
            vm.CpuFanManual.Execute(null); await Until(() => host.BoardFans["cha-fan1"].State.Mode == "manual");
            vm.SelectedBoardFan = MotherboardFans.Cpu; vm.CpuFanDuty = "70"; vm.CpuFanManual.Execute(null);
            await Until(() => host.CpuFan.State.Mode == "manual");
            Check(host.CpuFan.State.Reading?.Duty == 70 && host.BoardFans["cha-fan1"].State.Reading?.Duty == 45 && host.Coordinator.Snapshot.ConfirmedDuty == 59,
                "CPU CHA and NZXT simultaneously retain independent outputs");
            vm.SelectedBoardFan = MotherboardFans.Chassis; vm.CpuFanRestore.Execute(null); await Until(() => !host.BoardFans["cha-fan1"].State.Managing);
            Check(vm.CpuFanDuty == "65" && window.BoardFanDutySlider.Value == 65, "Chassis restoration also updates its selected slider");
            Check(host.CpuFan.State.Managing && host.CpuFan.State.Reading?.Duty == 70, "Restoring chassis fan does not release CPU fan control");
            vm.CpuFanPoints.Add(new(40, 35)); vm.CpuFanPoints.Add(new(80, 80)); vm.CpuFanOverheat = "95"; vm.CpuFanResume = "85";
            vm.CpuFanGpuEnabled = true; vm.CpuFanGpuPoints.Add(new(35, 40)); vm.CpuFanGpuPoints.Add(new(80, 95)); vm.CpuFanGpuOverheat = "95"; vm.CpuFanGpuResume = "85";
            vm.CpuFanSave.Execute(null); await Until(() => host.Coordinator.Settings.ChassisFan.Profile?.Gpu is not null && vm.BoardFanCanSelect);
            vm.CpuFanAuto.Execute(null); await Until(() => host.BoardFans["cha-fan1"].State.Mode == "auto");
            Check(host.CpuFan.State.Reading?.Duty == 70 && host.BoardFans["cha-fan1"].State.Reading?.Duty is >= 35 and <= 95,
                "Saved chassis dual temperature curves run alongside manual CPU and NZXT");
            var chaChoice = vm.DashboardChoices.Single(c => c.SensorId == "demo/board/fan/1"); chaChoice.Visible = true;
            vm.SaveDashboard.Execute(null); await Until(() => vm.DashboardFans.Any(r => r.Name == "CHA_FAN1" && r.Control == "個別・自動"));
            vm.DashboardDetails.Execute(vm.DashboardFans.Single(r => r.Name == "CHA_FAN1"));
            Check(vm.SelectedPage == 8 && vm.SelectedBoardFan == MotherboardFans.Chassis, "Chassis dashboard details target the same physical port");
            await host.BoardFans.SuspendAsync(); host.BoardFans.Resume();
            Check(host.BoardFans.States.Values.All(s => !s.Managing), "Sleep restores every motherboard fan and does not auto resume");
            Check((await host.Store.LoadAsync()).ChassisFan.Profile?.Gpu is not null, "Chassis dual curves persist independently of legacy CPU settings");
            foreach (var port in MotherboardFans.Ports.Skip(2))
            {
                vm.SelectedBoardFan = port;
                Check(vm.CpuFanMinimum == "" && !vm.CpuFanWiringConfirmed, port.Name + " starts with its own unconfigured settings");
                vm.CpuFanWiringConfirmed = true; vm.CpuFanMinimum = "30"; vm.CpuFanSave.Execute(null);
                await Until(() => MotherboardFans.Settings(host.Coordinator.Settings, port.Id).Minimum == 30 && vm.BoardFanCanSelect);
                vm.BoardFanEditMode = "手動"; vm.CpuFanDuty = (50 + port.Channel).ToString(); vm.BoardFanApply.Execute(null);
                await Until(() => host.BoardFans[port.Id].State.Mode == "manual" && vm.BoardFanCanSelect);
            }
            Check(MotherboardFans.Ports.Skip(2).All(p => host.BoardFans[p.Id].State.Reading?.Duty == 50 + p.Channel),
                "Simple Apply controls every additional channel independently");
            var stableRows = vm.BoardFanRows.ToArray(); await host.Coordinator.TickAsync(); await Settle();
            Check(vm.BoardFanRows.SequenceEqual(stableRows) && Equals(window.BoardFanList.SelectedValue, vm.SelectedBoardFan),
                "Telemetry retains list row instances and selected fan");
            vm.CpuFanRestore.Execute(null); await Until(() => !host.BoardFans[vm.SelectedBoardFan.Id].State.Managing && vm.BoardFanCanSelect);
            Check(MotherboardFans.Ports.Skip(2).SkipLast(1).All(p => host.BoardFans[p.Id].State.Managing), "Restoring EC channel seven preserves all other fan owners");
            vm.CpuFanPoints.Add(new(40, 30)); vm.CpuFanPoints.Add(new(80, 90)); vm.CpuFanOverheat = "95"; vm.CpuFanResume = "85";
            vm.CpuFanSave.Execute(null); await Until(() => MotherboardFans.Settings(host.Coordinator.Settings, vm.SelectedBoardFan.Id).Profile is not null && vm.BoardFanCanSelect);
            vm.BoardFanEditMode = "自動"; vm.BoardFanApply.Execute(null);
            await Until(() => host.BoardFans[vm.SelectedBoardFan.Id].State.Mode == "auto" && vm.BoardFanCanSelect);
            Check(host.Coordinator.Snapshot.ConfirmedDuty == 59 && !host.CpuFan.State.Managing, "Additional EC automatic control leaves NZXT and CPU fan unchanged");
            await host.BoardFans.RestoreAsync();
            Check(host.BoardFans.States.Values.All(s => !s.Managing), "All fifteen fan owners restore their original control");
            await host.FlushSettingsAsync(); Check((await host.Store.LoadAsync()).BoardFans.Count == 13, "All additional fan calibrations survive settings round trip");
            vm.SelectedBoardFan = MotherboardFans.Cpu; await Settle(); window.CpuFanCard.BringIntoView(new Rect(0, 0, window.CpuFanCard.ActualWidth, 430)); await Settle();
            Capture(window, output, "cpu-fan-controls-light");
            ThemeManager.Apply("dark"); await Settle(); Capture(window, output, "motherboard-controls-dark"); ThemeManager.Apply("light"); await Settle();
            await RunCurveEditorChecksAsync(host, vm, window, output, Check);
            vm.SelectedPage = 3; await Settle();
            var originalPython = vm.PythonPath;
            await CheckDraftInputAsync(window.PythonPathBox, vm, @"C:\検証用\python.exe", () => vm.PythonPath, Check);
            vm.PythonPath = originalPython; vm.Refresh();
            await RunNotificationChecksAsync(app, host, vm, window, output, Check);
            await RunSettingsTransferChecksAsync(host, vm, window, output, Check);
            window.BoardFanSetupExpander.IsExpanded = true; await Settle(); window.BoardFanSetupExpander.BringIntoView(); await Settle();
            Capture(window, output, "board-fan-setup-light"); window.BoardFanSetupExpander.IsExpanded = false;
            window.CpuFanCurveExpander.IsExpanded = true; await Settle(); window.CpuFanCurveExpander.BringIntoView(); await Settle();
            Capture(window, output, "cpu-fan-curve-light");
            ThemeManager.Apply("dark"); window.Width = 960; window.Height = 640; await Settle(); window.CpuFanCurveExpander.BringIntoView(); await Settle();
            Capture(window, output, "cpu-fan-curve-minimum-dark");
            window.CpuFanCurveExpander.IsExpanded = false; window.CpuFanCard.BringIntoView(new Rect(0, 0, window.CpuFanCard.ActualWidth, 400)); await Settle(); Capture(window, output, "cpu-fan-minimum-dark");
            window.Width = 1100; window.Height = 800; ThemeManager.Apply("light"); await Settle();
            for (var page = 0; page < 9; page++)
            {
                vm.SelectedPage = page; await Settle(); Capture(window, output, $"page-{page}-light");
                if (page == 7) { ThemeManager.Apply("dark"); await Settle(); Capture(window, output, "nzxt-controls-dark"); ThemeManager.Apply("light"); await Settle(); }
                if (page == 3)
                {
                    var scroll = (ScrollBar)window.PageScroll.Template.FindName("PART_VerticalScrollBar", window.PageScroll);
                    var offset = window.PageScroll.VerticalOffset;
                    ScrollBar.PageDownCommand.Execute(null, scroll); await Settle();
                    Check(window.PageScroll.VerticalOffset > offset, "Custom scrollbar page command scrolls the settings form");
                }
                window.PageScroll.ScrollToBottom(); await Settle(); Capture(window, output, $"page-{page}-bottom-light");
                window.PageScroll.ScrollToTop();
            }
            vm.SelectedPage = 1; await Settle();
            Check(Descendants<TextBlock>(window).Any(t => t.IsVisible && t.Text == vm.CurveProfile?.Name), "Profile selector displays its name");
            ThemeManager.Apply("dark"); vm.SelectedPage = 0; await Settle(); Capture(window, output, "dashboard-dark");
            window.Width = 960; window.Height = 640; await Settle(); Capture(window, output, "minimum-dark");
            window.DashboardFanGrid.BringIntoView(); await Settle(); Capture(window, output, "unified-fans-minimum-dark");
            vm.SelectedPage = 6; await Settle(); Capture(window, output, "dashboard-settings-minimum-dark");
            vm.SelectedPage = 0; await Settle();
            window.DashboardHistoryExpander.IsExpanded = true; await Settle();
            Descendants<HistoryChart>(window).Single().BringIntoView(); await Settle(); Capture(window, output, "history-minimum-dark");
            window.DashboardHistoryExpander.IsExpanded = false; window.PageScroll.ScrollToTop();
            await RunMemoryLightingChecksAsync(host, vm, window, output, Check);
            vm.DemoFault.Execute("partial"); vm.ChooseMode.Execute("Manual"); vm.ManualText = "64"; vm.ApplyFan.Execute(null);
            await Until(() => host.Coordinator.Snapshot.Mode == FanMode.Fallback);
            Check(host.Coordinator.Snapshot.Faulted && host.Coordinator.Snapshot.ConfirmedDuty == 100, "Partial failure enters fallback; normal control stays suspended");
            vm.Resume.Execute(null); await Until(() => !host.Coordinator.Snapshot.Faulted);
            Check(host.Coordinator.Snapshot.ConfirmedDuty == 59, "Resume restores last confirmed policy, not failed request");
            var revision = host.Coordinator.Snapshot.ControlRevision; app.HideToTray(); await Task.Delay(1200);
            Check(host.Coordinator.Snapshot.Sequence > before.Sequence && host.Coordinator.Snapshot.ControlRevision == revision, "Tray hiding preserves control loop");
            await host.FlushSettingsAsync();
            Check((await host.Store.LoadAsync()).LastConfirmedPolicy?.ManualDuty == 59, "Confirmed policy is persisted to isolated demo settings");
            await File.WriteAllLinesAsync(Path.Combine(output, "ui-smoke.txt"), report);
            await app.ExitAsync();
        }
        catch (Exception e)
        {
            report.Add("FAIL " + e); await File.WriteAllLinesAsync(Path.Combine(output, "ui-smoke.txt"), report);
            // A failed assertion must not leave the synthetic next-write failure armed for teardown.
            if (host.Hub is DemoHub failedDemo) { failedDemo.FailNextFanWrite = false; failedDemo.Disconnected = false; }
            await app.ExitAsync(); Environment.ExitCode = 1;
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("UI acceptance condition timed out"); await Task.Delay(50); }
        await Settle();
    }
    private static async Task Settle() { await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(150); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Capture(Window window, string output, string name)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content; var dpi = VisualTreeHelper.GetDpi(content);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
    }
}
