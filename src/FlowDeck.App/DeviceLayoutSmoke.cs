using System.Globalization;
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
    // The preview router permits this scenario only with the isolated built-in DEMO.
    public static async Task<int> RunDeviceLayoutReviewAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo,
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
            Check(client.IsDemo && demo.Hubs.All(h => h.IsDemo), "Layout checks use only authenticated DEMO and memory-backed devices");
            await model.PrepareFirstUseAsync(); await Fresh();
            var snapshot = client.Snapshot!;
            var generation = snapshot.Configuration.Generation;
            var configuration = SettingsTransfer.Hash(snapshot.Configuration.Configuration);
            var initialWrites = demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).ToArray();
            Check(initialWrites.All(w => w == (0, 0)), "Opening the layout scenario sends no cooling or lighting output");
            var view = Descendants<DeviceWorkspaceView>(window).Single();
            var scroll = (ScrollViewer)view.FindName("WorkspaceScroll");
            var footer = LayoutElement<FrameworkElement>(window, "LightingApplyFooter");
            var apply = LayoutElement<Button>(window, "LightingApplyButton");
            var statusBar = LayoutElement<FrameworkElement>(window, "WorkspaceStatusBar");
            var light = model.Lights.Single(l => l.Target == new EndpointReference(demo.DeviceIds[0], "led1"));
            var cooling = model.Cooling.Single(r => r.Draft.Target == new EndpointReference(demo.DeviceIds[0], "fan1"));

            foreach (var size in new[] { (Name: "normal", Width: 1120d, Height: 820d), (Name: "minimum", Width: 960d, Height: 640d) })
            foreach (var theme in new[] { "light", "dark" })
            {
                var key = size.Name + "-" + theme;
                window.Width = size.Width; window.Height = size.Height; model.Theme = theme;
                model.Notice = ""; model.SelectedPage = 0; await Fresh(); scroll.ScrollToTop(); await Settle();
                var name = Descendants<TextBlock>(window).First(t => AutomationProperties.GetAutomationId(t) == "MonitorFanName");
                var rpm = Descendants<ContentControl>(window).Single(t => AutomationProperties.GetAutomationId(t) == "MonitorFanRpm" && ReferenceEquals(t.DataContext, name.DataContext));
                name.BringIntoView(); await Settle();
                var nameBounds = LayoutBounds(name, view); var rpmBounds = LayoutBounds(rpm, view);
                var inkWidth = Math.Min(name.ActualWidth, new FormattedText(name.Text, CultureInfo.CurrentUICulture,
                    name.FlowDirection, new Typeface(name.FontFamily, name.FontStyle, name.FontWeight, name.FontStretch),
                    name.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(name).PixelsPerDip).WidthIncludingTrailingWhitespace);
                var gap = rpmBounds.Left - nameBounds.Left - inkWidth;
                Check(name.DataContext is DashboardReading && name.IsVisible && rpm.IsVisible && gap is >= -1 and <= 180 &&
                    Math.Abs(nameBounds.Top + nameBounds.Height / 2 - rpmBounds.Top - rpmBounds.Height / 2) <= 32,
                    key + " monitor pairs the actual fan-name text and RPM closely in the same row");
                Check(!footer.IsVisible && LayoutInsideViewport(name, scroll) && LayoutInsideViewport(rpm, scroll),
                    key + " monitor row remains in the viewport without a lighting footer");
                geometry.Add(new { view = "monitor", key, name = LayoutRect(nameBounds), rpm = LayoutRect(rpmBounds), nameInkWidth = inkWidth, gap });
                Shot("layout-monitor-" + key);

                model.SelectedPage = 1; await Fresh(); scroll.ScrollToTop(); await Settle();
                var current = Descendants<TextBlock>(window).Single(t => AutomationProperties.GetAutomationId(t) == "CoolingCurrentDuty" && ReferenceEquals(t.DataContext, cooling));
                var slider = Descendants<Slider>(window).Single(t => AutomationProperties.GetAutomationId(t) == "CoolingDraftDuty" && ReferenceEquals(t.DataContext, cooling));
                current.BringIntoView(); await Settle();
                var measured = cooling.CurrentDuty; var writesBeforeDraft = demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).ToArray();
                var mode = Descendants<ComboBox>(window).Single(c => ReferenceEquals(c.DataContext, cooling) && c.ItemsSource == cooling.Modes);
                mode.SetCurrentValue(Selector.SelectedItemProperty, "固定"); await Settle();
                var draftDuty = cooling.Duty == 61 ? 62d : 61d;
                slider.SetCurrentValue(RangeBase.ValueProperty, draftDuty); await Fresh();
                var currentBounds = LayoutBounds(current, view); var draftBounds = LayoutBounds(slider, view);
                Check(slider.IsEnabled && cooling.Draft.Dirty && cooling.Duty == draftDuty && current.Text.Contains(cooling.CurrentDuty) &&
                    cooling.CurrentDuty == measured && cooling.Duty.ToString(CultureInfo.InvariantCulture) + "%" != measured,
                    key + " editing the real bound slider keeps current duty distinct from the unsent requested value");
                Check(current.IsVisible && LayoutInsideViewport(current, scroll) && currentBounds.Bottom <= draftBounds.Top + 1 &&
                    writesBeforeDraft.SequenceEqual(demo.Hubs.Select(h => (h.Writes, h.LightingWrites))),
                    key + " current output stays above the editor and draft input performs no output write");
                geometry.Add(new { view = "cooling", key, current = LayoutRect(currentBounds), draft = LayoutRect(draftBounds), measured, draftDuty });
                Shot("layout-cooling-" + key);
                cooling.Reset(); await Fresh();

                model.SelectedPage = 3; await Fresh();
                var parent = Descendants<Expander>(window).Single(e => Equals(e.Header, "モニターの表示"));
                parent.SetCurrentValue(Expander.IsExpandedProperty, false); parent.BringIntoView(); await Settle(); parent.ApplyTemplate();
                var frame = parent.Template.FindName("SettingsGroupFrame", parent) as FrameworkElement
                    ?? throw new InvalidOperationException("The settings parent frame is missing");
                var content = parent.Template.FindName("SettingsGroupContent", parent) as FrameworkElement
                    ?? throw new InvalidOperationException("The settings parent content is missing");
                var collapsedHeight = frame.ActualHeight;
                Check(!content.IsVisible && collapsedHeight > 0, key + " closed settings parent hides its content inside the common frame");
                parent.SetCurrentValue(Expander.IsExpandedProperty, true); await Settle();
                var child = Descendants<Expander>(parent).First(e => Equals(e.Header, "表示形式・目盛"));
                child.SetCurrentValue(Expander.IsExpandedProperty, false); await Settle();
                var parentHeight = frame.ActualHeight;
                Check(content.IsVisible && parentHeight > collapsedHeight && LayoutInset(content, frame, 12),
                    key + " opening a settings parent reveals indented content contained by that same frame");
                child.SetCurrentValue(Expander.IsExpandedProperty, true); child.BringIntoView(); await Settle();
                Check(frame.ActualHeight > parentHeight && LayoutInset(child, frame, 12) && LayoutInset(content, frame, 12) && scroll.ScrollableWidth < 1,
                    key + " opening a nested settings editor grows its parent frame without horizontal overflow");
                geometry.Add(new { view = "settings", key, collapsedHeight, parentHeight, expandedHeight = frame.ActualHeight,
                    frame = LayoutRect(LayoutBounds(frame, view)), content = LayoutRect(LayoutBounds(content, view)), child = LayoutRect(LayoutBounds(child, view)) });
                Shot("layout-settings-" + key);
                child.SetCurrentValue(Expander.IsExpandedProperty, false); parent.SetCurrentValue(Expander.IsExpandedProperty, false);

                model.SelectedPage = 2; await Fresh(); model.SelectLights([light.Target]); model.LightEffect = "fixed"; model.LightColor = Colors.MediumPurple;
                var advanced = LayoutElement<Expander>(view, "LightingAdvancedSection");
                advanced.SetCurrentValue(Expander.IsExpandedProperty, true); await Fresh();
                var library = Descendants<Expander>(window).Single(e => Equals(e.Header, "シーン・グループ"));
                library.SetCurrentValue(Expander.IsExpandedProperty, true); await Fresh();
                model.ReportResult("画面試験：未確認の結果を表示しています。内容は詳細で確認できます。", Outcome.Unknown);
                scroll.ScrollToTop(); await Settle();
                var top = LayoutBounds(footer, view);
                Check(advanced.IsExpanded && library.IsVisible && library.IsExpanded &&
                    model.CanApplyLighting && apply.IsEnabled && apply.Command.CanExecute(apply.CommandParameter) &&
                    LayoutActionReachable(apply, view) && top.Bottom <= LayoutBounds(statusBar, view).Top + 1 && scroll.ScrollableHeight > 0,
                    key + " lighting Apply stays reachable above the status bar while the editor can scroll");
                Check(LayoutElement<TextBlock>(window, "WorkspaceWarningMessage").IsVisible && model.WarningMessage.Contains("画面試験"),
                    key + " the fixed lighting actions do not hide a current warning");
                Shot("layout-lighting-top-" + key);
                scroll.ScrollToBottom(); await Fresh();
                var bottom = LayoutBounds(footer, view);
                Check(scroll.VerticalOffset > 0 && LayoutSameRect(top, bottom) && apply.IsEnabled && LayoutActionReachable(apply, view),
                    key + " scrolling lighting to the bottom preserves the footer position and button hit target");
                geometry.Add(new { view = "lighting", key, top = LayoutRect(top), bottom = LayoutRect(bottom),
                    scrollOffset = scroll.VerticalOffset, scrollHeight = scroll.ScrollableHeight, apply = LayoutRect(LayoutBounds(apply, view)) });
                Shot("layout-lighting-bottom-" + key);
                library.SetCurrentValue(Expander.IsExpandedProperty, false);
                advanced.SetCurrentValue(Expander.IsExpandedProperty, false); model.Notice = "";
            }
            await Fresh();
            Check(client.Snapshot!.Configuration.Generation == generation && SettingsTransfer.Hash(client.Snapshot.Configuration.Configuration) == configuration &&
                initialWrites.SequenceEqual(demo.Hubs.Select(h => (h.Writes, h.LightingWrites))),
                "Resizing, scrolling, expanding settings, selecting colors and editing unsent duty preserve saved settings and all DEMO outputs");

            // One real synthetic driver failure verifies that moving the action bar cannot conceal recovery.
            window.Width = 960; window.Height = 640; model.SelectedPage = 2; await Fresh();
            model.SelectLights([light.Target]); model.LightEffect = "fixed"; model.LightColor = Colors.Lime;
            demo.Hubs[0].FailNextLightingWrite = true;
            apply.Command.Execute(apply.CommandParameter);
            await LayoutUntil(() => model.LightingIdle && model.LightRecoveryRequired); await Settle();
            var stop = Descendants<Button>(window).Single(b => AutomationProperties.GetName(b) == "選択した照明の制御を終了");
            Check(light.NeedsRecovery && !model.CanApplyLighting && !apply.IsEnabled && stop.IsEnabled && model.StopLights.CanExecute(null),
                "A genuine DEMO write failure still blocks Apply and enables only the existing recovery command");
            foreach (var theme in new[] { "light", "dark" })
            {
                model.Theme = theme; scroll.ScrollToBottom(); await Fresh();
                var recoveryText = LayoutElement<TextBlock>(window, "LightingRecoveryMessage");
                var recoveryScroll = LayoutParent<ScrollViewer>(recoveryText);
                recoveryScroll.ScrollToTop(); await Settle();
                Check(!LayoutElement<Expander>(view, "LightingAdvancedSection").IsExpanded &&
                    LayoutActionReachable(stop, view) && LayoutBounds(footer, view).Bottom <= LayoutBounds(statusBar, view).Top + 1 &&
                    recoveryText.IsVisible && recoveryText.Text == model.LightRecoveryMessage && !string.IsNullOrWhiteSpace(recoveryText.Text) &&
                    LayoutBounds(recoveryScroll, footer).Bottom <= footer.ActualHeight &&
                    LayoutElement<TextBlock>(window, "WorkspaceWarningMessage").IsVisible,
                    theme + " minimum-size lighting keeps the full recovery text accessible and Stop visible with its warning");
                geometry.Add(new { view = "lighting-recovery", theme, footer = LayoutRect(LayoutBounds(footer, view)), stop = LayoutRect(LayoutBounds(stop, view)) });
                Shot("layout-lighting-recovery-" + theme);
            }
            var fanWrites = demo.Hubs.Select(h => h.Writes).ToArray();
            stop.Command.Execute(stop.CommandParameter);
            await LayoutUntil(() => model.LightingIdle && !model.LightRecoveryRequired); await Fresh();
            Check(light.Ready && demo.Hubs[0].LightEffects["led1"].Mode == "off" && fanWrites.SequenceEqual(demo.Hubs.Select(h => h.Writes)),
                "The existing footer recovery command clears only the selected DEMO light and never changes cooling");
            Check(await model.CloseAsync(), "The layout DEMO exits normally after its explicit recovery");
        }
        catch (Exception e) { error = e.ToString(); report.Add("FAIL " + error); }
        await File.WriteAllLinesAsync(Path.Combine(output, "device-layout-review-smoke.txt"), report);
        await File.WriteAllTextAsync(Path.Combine(output, "layout-review-result.json"), JsonSerializer.Serialize(new
        { passed = report.Count(l => l.StartsWith("PASS ", StringComparison.Ordinal)), failed = error is null ? 0 : 1, error,
            hardwareAccessed = false, productionIpcOpened = false, scenario = "layout-review", screenshots, geometry },
            new JsonSerializerOptions { WriteIndented = true }));
        return error is null ? 0 : 1;
    }

    private static T LayoutElement<T>(DependencyObject root, string id) where T : FrameworkElement
        => Descendants<T>(root).Single(e => AutomationProperties.GetAutomationId(e) == id);
    private static T LayoutParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T found) return found;
        throw new InvalidOperationException("The expected layout ancestor is missing: " + typeof(T).Name);
    }
    private static Rect LayoutBounds(FrameworkElement element, Visual relative)
        => element.TransformToAncestor(relative).TransformBounds(new Rect(new Point(), element.RenderSize));
    private static double[] LayoutRect(Rect value) => [value.Left, value.Top, value.Width, value.Height];
    private static bool LayoutSameRect(Rect a, Rect b) => Math.Abs(a.Left - b.Left) < 1 && Math.Abs(a.Top - b.Top) < 1 &&
        Math.Abs(a.Width - b.Width) < 1 && Math.Abs(a.Height - b.Height) < 1;
    private static bool LayoutInset(FrameworkElement child, FrameworkElement parent, double inset)
    {
        var b = LayoutBounds(child, parent);
        return b.Left >= inset - 1 && b.Right <= parent.ActualWidth - inset + 1 && b.Top >= 0 && b.Bottom <= parent.ActualHeight + 1;
    }
    private static bool LayoutInsideViewport(FrameworkElement element, ScrollViewer scroll)
    {
        var b = LayoutBounds(element, scroll);
        return element.IsVisible && b.Left >= -1 && b.Right <= scroll.ActualWidth + 1 && b.Top >= -1 && b.Bottom <= scroll.ActualHeight + 1;
    }
    private static bool LayoutActionReachable(Button button, FrameworkElement view)
    {
        var b = LayoutBounds(button, view);
        if (!button.IsVisible || !button.IsEnabled || b.Width <= 0 || b.Height <= 0 || b.Left < 0 || b.Top < 0 ||
            b.Right > view.ActualWidth + 1 || b.Bottom > view.ActualHeight + 1) return false;
        var hit = view.InputHitTest(new Point(b.Left + b.Width / 2, b.Top + b.Height / 2)) as DependencyObject;
        while (hit is not null)
        {
            if (ReferenceEquals(hit, button)) return true;
            hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
        }
        return false;
    }
    private static async Task LayoutUntil(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 8000;
        while (!condition()) { if (Environment.TickCount64 >= deadline) throw new TimeoutException("Layout DEMO transition timed out"); await Task.Delay(20); }
    }
}
