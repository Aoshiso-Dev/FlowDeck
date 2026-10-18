using System.Windows;
using System.Windows.Controls;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    private static async Task RunDeviceCatalogAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, Window window,
        string output, Action<bool, string> check)
    {
        model.SelectedPage = 3; await model.PollAsync(); await Settle();
        var hub = model.Devices.Single(d => d.Id == demo.DeviceIds[0]);
        var fan = hub.Endpoints.Single(e => e.Id == "fan1"); var rpm = fan.Capabilities.Single(c => c.Value.Feature == DeviceFeature.Rpm);
        var panel = Descendants<Expander>(window).Single(e => ReferenceEquals(e.DataContext, hub) && e.Header?.ToString() == hub.Name);
        check(!hub.Expanded && !Descendants<TextBlock>(panel).Any(t => ReferenceEquals(t.DataContext, rpm)),
            "Collapsed device cards do not create every capability detail control");
        panel.SetCurrentValue(Expander.IsExpandedProperty, true); await Settle();
        check(Descendants<TextBlock>(panel).Any(t => ReferenceEquals(t.DataContext, rpm) && t.Text == rpm.Value.Evidence),
            "Device catalog renders the service capability verification evidence");
        await model.PollAsync(); await Settle();
        check(ReferenceEquals(model.Devices.Single(d => d.Id == hub.Id), hub) && ReferenceEquals(hub.Endpoints.Single(e => e.Id == fan.Id), fan) &&
            ReferenceEquals(fan.Capabilities.Single(c => c.Value.Feature == DeviceFeature.Rpm), rpm) && panel.IsExpanded,
            "Catalog device endpoint capability rows and expanded state survive telemetry updates");
        panel.SetCurrentValue(Expander.IsExpandedProperty, false);
        var writes = demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).ToArray();
        // Presentation-only data: no driver, service registration or write capability is created for this row.
        var observation = new DeviceObservation("catalog-demo", "demo", "監視・照明の対応例", DeviceKind.Motherboard,
            IdentityConfidence.ConnectionBound, "demo:catalog", "demo-definition", ["demo:catalog"], [
                new("fan", "ケースファン", EndpointKind.Cooling, [
                    new(DeviceFeature.Rpm, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready),
                    new(DeviceFeature.FixedDuty, ImplementationSupport.Unsupported, VerificationEvidence.None, DeviceAvailability.Ready, "この端子は回転数の監視のみです")]),
                new("light", "ケース照明", EndpointKind.Lighting, [
                    new(DeviceFeature.FixedColor, ImplementationSupport.Supported, VerificationEvidence.SourceReview, DeviceAvailability.NeedsSetup, "LED数を確認してください"),
                    new(DeviceFeature.ColorFrames, ImplementationSupport.Supported, VerificationEvidence.FamilyHardware, DeviceAvailability.Conflict, "ほかの照明ソフトが使用しています")])]);
        var config = new DeviceConfigurationPackage(new(2, []), new(2, new string('a', 32), []), new(2, [], []),
            new(2, [], [], [], [], []), new(2, []), new(2, new(), new(), new(), new([], [])),
            new(2, "native-v2", new string('a', 64), [], []));
        var snapshot = new DeviceWorkspaceSnapshot(new(new("epoch", 1, [new(observation, 1, true)]), [], false, null,
            [new(new(observation.Id, "fan"), DeviceFeature.Rpm, 900, 0, false)]), new("generation", config, []), 0, true, null, Session: "session");
        var example = new DeviceCatalogRow(observation.Id); example.Update(DeviceCatalogPresentation.Build(snapshot).Single());
        model.Devices.Add(example); await Settle();
        try
        {
            var detail = Descendants<Expander>(window).Single(e => ReferenceEquals(e.DataContext, example) && e.Header?.ToString() == example.Name);
            detail.SetCurrentValue(Expander.IsExpandedProperty, true); await Settle();
            check(example.Status.Contains("回転数：利用可") && example.Status.Contains("ファン操作：未対応") && example.Status.Contains("照明：要確認"),
                "Catalog separates available RPM monitoring from unsupported control and mixed lighting readiness");
            var text = Descendants<TextBlock>(detail).Select(t => t.Text).ToArray();
            check(text.Contains("自動試験") && text.Contains("ソース照合") && text.Contains("同系統の実機") && text.Contains("未検証"),
                "Catalog renders independent implementation and evidence columns");
            check(text.Contains("LED数を確認してください") && text.Contains("ほかの照明ソフトが使用しています"),
                "Catalog shows concrete setup and conflict reasons only in expanded details");
            foreach (var theme in model.Themes)
            {
                model.Theme = theme; await Settle(); detail.BringIntoView(new Rect(0, 0, detail.ActualWidth, 400)); await Settle();
                Capture(window, output, "device-capabilities-" + theme);
                check(detail.IsVisible && Descendants<TextBlock>(detail).Any(t => t.IsVisible && t.Text == "確認根拠"), theme + " catalog details render");
            }
            check(DeviceCatalogPresentation.Build(snapshot with { Connected = false }).Length == 0,
                "Lost service connection hides cached catalog devices");
            example.Update(DeviceCatalogPresentation.Build(snapshot).Single()); window.Width = 960; window.Height = 640;
            await Settle(); detail.BringIntoView(new Rect(0, 0, detail.ActualWidth, 380)); await Settle();
            Capture(window, output, "device-capabilities-minimum");
            check(Descendants<TextBlock>(detail).Where(t => t.Text == "同系統の実機").All(t => t.ActualWidth >= 100),
                "Verification evidence remains readable at minimum window size");
            check(demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).SequenceEqual(writes), "Viewing catalog details performs no hardware output writes");
        }
        finally { model.Devices.Remove(example); window.Width = 1120; window.Height = 820; model.Render(); }
    }
}
