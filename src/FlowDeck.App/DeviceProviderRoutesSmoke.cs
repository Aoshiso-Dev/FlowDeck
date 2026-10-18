using System.Windows;
using System.Windows.Controls;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceProviderRoutesAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo,
        DeviceWorkspaceClient client, Window window, string output)
    {
        var report = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); report.Add("PASS " + message); }
        try
        {
            model.SelectedPage = 3; model.OpenRgbExpanded = true; await Settle();
            var panel = Descendants<DeviceOpenRgbSettingsView>(window).Single();
            var selector = Descendants<ComboBox>(panel).Single();
            var input = Descendants<DraftTextBox>(panel).Single();
            var previewButton = Descendants<Button>(panel).Single(b => b.Content as string == "切替確認");
            var applyButton = Descendants<Button>(panel).Single(b => b.Content as string == "適用");
            var routing = demo.Routing!;
            Check(model.ProviderRouteAvailable && model.CurrentProviderRoute == "FlowDeck" && !model.CanPreviewProviderRoute && !model.CanApplyProviderRoute,
                "Opening the route editor keeps the adopted route and requires an actual change");
            var fan = model.Cooling.Single(r => r.Draft.Target == new EndpointReference(demo.DeviceIds[0], "fan1"));
            fan.Mode = "固定"; fan.Duty = 55; await model.OperateAsync(fan);
            var writes = demo.Hubs[0].Writes; var generation = client.Snapshot!.Configuration.Generation;
            selector.SelectedValue = true; await Settle();
            Check(model.UseOpenRgb && model.CanPreviewProviderRoute && !model.CanApplyProviderRoute, "Selecting OpenRGB requires a service-produced review before apply");
            for (var i = 0; i < 3; i++) await model.PollAsync();
            Check(model.UseOpenRgb && routing.Created.Count == 1 && demo.OpenRgb.Calls == 0, "Polling retains the selection without SDK access or a route switch");
            await model.PreviewProviderRouteAsync(); await Settle();
            Check(model.HasProviderRoutePreview && model.CanApplyProviderRoute && model.ProviderRouteChanges.Contains("切替デモ照明") &&
                model.ProviderRouteChanges.Contains("制御を継続：") && model.ProviderRouteChanges.Contains("ケースファン") &&
                !model.ProviderRouteChanges.Contains("すべての冷却・照明制御を終了"),
                "Review distinguishes the switching light from the independent cooling that continues");
            Check(demo.Hubs[0].Writes == writes && fan.CurrentDuty == "55%" && client.Snapshot!.Configuration.Generation == generation,
                "Review leaves active cooling and the adopted configuration untouched");
            foreach (var theme in model.Themes)
            {
                model.Theme = theme; applyButton.BringIntoView(); await Settle(); Capture(window, output, "provider-routes-" + theme);
                Check(selector.IsVisible && previewButton.ActualWidth > 60 && applyButton.IsVisible, theme + " renders the route review and apply action");
            }
            input.Text = "6743"; await Settle();
            Check(!model.HasProviderRoutePreview && !model.CanApplyProviderRoute, "Changing the SDK port invalidates the reviewed plan");
            input.Text = "65536"; await Settle(); Check(!model.CanPreviewProviderRoute, "An invalid port cannot produce a route review");
            input.Text = "6742"; await model.PreviewProviderRouteAsync();
            selector.SelectedValue = false; await Settle();
            Check(!model.HasProviderRoutePreview && !model.CanApplyProviderRoute && !model.CanPreviewProviderRoute, "Changing the route choice invalidates its review");
            selector.SelectedValue = true; await model.PreviewProviderRouteAsync();
            var display = model.DashboardSettings.First(); display.Name = "切替前の表示変更";
            await model.SaveDashboardAsync(); await model.PollAsync();
            Check(!model.HasProviderRoutePreview && model.UseOpenRgb && !model.CanApplyProviderRoute, "A new settings generation clears review without discarding the selection");
            await model.PreviewProviderRouteAsync(); routing.Reject = true;
            try { await model.ApplyProviderRouteAsync(); throw new InvalidOperationException("A changed policy was accepted"); }
            catch (IOException) { }
            finally { routing.Reject = false; }
            Check(!routing.Created[0].Disposed && routing.Created.Count == 1 && demo.Hubs[0].Writes == writes && !model.CanApplyProviderRoute,
                "A changed compiled policy rejects apply before control release and requires a new review");
            await model.PreviewProviderRouteAsync(); await model.ApplyProviderRouteAsync(); await model.PollAsync();
            Check(model.CurrentProviderRoute == "OpenRGB" && !model.HasProviderRoutePreview && !model.CanPreviewProviderRoute &&
                routing.Created.Count == 2 && routing.Created[0].Disposed, "Apply adopts the reviewed route and closes the previous provider");
            Check(client.Snapshot!.State.Endpoints.Single(e => e.Target == fan.Draft.Target).Owned &&
                demo.Hubs[0].Writes == writes && fan.CurrentDuty == "55%",
                "Route adoption retains the independent cooling policy and does not issue an exit output");
            Check(model.ProviderDeviceSelectionAvailable && model.ProviderDeviceSelectionIdle && model.ProviderDeviceChoices.Count == 2 &&
                model.ProviderDeviceChoices.All(r => !r.Selected), "SDK connection exposes two candidates without selecting either for control");
            var chosen = model.ProviderDeviceChoices[0]; var other = model.ProviderDeviceChoices[1];
            var checkbox = Descendants<CheckBox>(panel).Single(c => c.DataContext == chosen);
            checkbox.IsChecked = true; await Settle();
            for (var i = 0; i < 3; i++) await model.PollAsync();
            Check(chosen.Selected && !other.Selected && model.CanPreviewProviderRoute &&
                client.Snapshot!.Configuration.Configuration.Preferences.Providers!.OpenRgbDeviceIds is null,
                "A checkbox edit survives polling and does not save or switch the running provider");
            await model.PreviewProviderRouteAsync();
            Check(model.ProviderRouteChanges.Contains("OpenRGBの制御対象：" + chosen.Name) && model.CanApplyProviderRoute,
                "The service review names the selected physical device");
            other.Selected = true;
            Check(!model.HasProviderRoutePreview && !model.CanApplyProviderRoute, "Changing a device checkbox invalidates the previous review");
            other.Selected = false; await model.PreviewProviderRouteAsync();
            foreach (var theme in model.Themes)
            {
                model.Theme = theme; checkbox.BringIntoView(); await Settle(); Capture(window, output, "provider-selection-" + theme);
                Check(checkbox.IsVisible && chosen.Selected && model.CanApplyProviderRoute, theme + " renders and retains the device selection");
            }
            window.Width = 960; window.Height = 640; checkbox.BringIntoView(); await Settle(); Capture(window, output, "provider-selection-minimum");
            var sdkBeforeSelection = routing.Created.Last(); await model.ApplyProviderRouteAsync(); await model.PollAsync();
            Check(sdkBeforeSelection.Disposed && chosen.Selected && !other.Selected && !model.CanPreviewProviderRoute &&
                client.Snapshot!.Configuration.Configuration.Preferences.Providers!.OpenRgbDeviceIds!.SequenceEqual([chosen.Id]),
                "Selection-only apply replaces the provider and preserves the adopted checkbox without a false dirty state");
            chosen.Selected = false; await model.PreviewProviderRouteAsync();
            Check(model.ProviderRouteChanges.Contains("OpenRGBの制御対象：なし"), "An empty selection is shown explicitly in the review");
            await model.ApplyProviderRouteAsync();
            Check(!model.CanPreviewProviderRoute && client.Snapshot!.Configuration.Configuration.Preferences.Providers!.OpenRgbDeviceIds is null,
                "Clearing every selection persists monitoring-only control scope");
            model.UseOpenRgb = false; await model.PreviewProviderRouteAsync();
            Check(model.ProviderRouteChanges.Contains("demo-sdk") && model.CanApplyProviderRoute, "The reverse route has a separate review");
            window.Width = 960; window.Height = 640; applyButton.BringIntoView(); await Settle(); Capture(window, output, "provider-routes-minimum");
            var lastSdk = routing.Created.Last();
            await model.ApplyProviderRouteAsync(); Check(model.CurrentProviderRoute == "FlowDeck" && lastSdk.Disposed,
                "Returning to the direct route closes the SDK fixture before adoption");
            model.UseOpenRgb = true; await model.PreviewProviderRouteAsync();
            var disconnectedSession = client.Snapshot!.Session!;
            await demo.Runtime.DisconnectAsync(disconnectedSession); await model.PollAsync();
            Check(!model.HasProviderRoutePreview && !model.CanApplyProviderRoute, "Disconnect invalidates the pending review");
            Check(demo.OpenRgb.Calls == 0, "The dedicated route fixture performs no SDK connection even through the UI");
            // The normal poll may already have reconnected. Observe a different live session,
            // rather than waiting to catch the transient NeedsReconnect state after it ended.
            bool Reconnected() => client.Connected && client.Snapshot is { Connected: true, Session: { } current } && current != disconnectedSession;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while (!Reconnected())
                {
                    if (client.NeedsReconnect) await model.ReconnectAsync();
                    else await model.PollAsync();
                    if (Reconnected()) break;
                    await Task.Delay(25, timeout.Token);
                }
            model.Render();
            Check(Reconnected() && model.CanPreviewProviderRoute && !model.CanApplyProviderRoute && !model.HasProviderRoutePreview,
                "A new session keeps the draft but cannot reuse the old review");
            Check(await model.CloseAsync(), "Normal exit confirms service release after reconnection");
            await File.WriteAllLinesAsync(Path.Combine(output, "provider-routes-smoke.txt"), report); return 0;
        }
        catch (Exception e)
        {
            report.Add("FAIL " + e); report.Add("NOTICE " + model.Notice);
            await File.WriteAllLinesAsync(Path.Combine(output, "provider-routes-smoke.txt"), report); return 1;
        }
    }
}
