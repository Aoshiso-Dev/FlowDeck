using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceDiagnosticsAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo,
        DeviceWorkspaceClient client, Window window, string output)
    {
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks.Add("PASS " + message); }
        try
        {
            model.SelectedPage = 3; model.DiagnosticsExpanded = true; await Settle();
            var panel = Descendants<DeviceDiagnosticsView>(window).Single();
            Check(model.CanCreateDiagnostics && !model.CanSaveDiagnostics && model.DiagnosticsStatus == "未作成", "Opening diagnostics does not capture or write a report");
            Check(Descendants<CheckBox>(panel).Count() == 3, "The user can inspect and choose three optional sections");
            model.DiagnosticMeasurements = false; model.DiagnosticOperations = false; model.DiagnosticConfiguration = false;
            for (var i = 0; i < 3; i++) await model.PollAsync();
            Check(!model.DiagnosticMeasurements && !model.DiagnosticOperations && !model.DiagnosticConfiguration, "Polling preserves the scope selection");
            await model.CreateDiagnosticsAsync();
            Check(model.CanSaveDiagnostics && model.DiagnosticsStatus == "作成済み" && model.DiagnosticsSummary.Contains("時点"), "Capture enables saving and displays the snapshot time");
            var path = Path.Combine(output, "summary.json"); await model.SaveDiagnosticsToAsync(path);
            var report = JsonSerializer.Deserialize<DeviceDiagnosticExport>(await File.ReadAllTextAsync(path), Contract.Json)!;
            Check(report.AppVersion == typeof(FlowDeckApplication).Assembly.GetName().Version!.ToString() &&
                report.AppBuildId == typeof(FlowDeckApplication).Assembly.ManifestModule.ModuleVersionId.ToString("N"), "Export identifies the actual GUI version and build");
            Check(model.DiagnosticsVersions.Contains("サービス デモ") && model.DiagnosticsVersions.Contains("PawnIO：未取得") &&
                report.Service.Environment.KernelDrivers is { Length: 0 }, "Demo does not probe installed drivers or claim a real service version");
            Check(Descendants<TextBlock>(panel).Any(t => t.Text == model.DiagnosticsVersions), "Version summary is bound to the captured report");
            Check(report.Service.Scope == DeviceDiagnosticScope.Summary && report.Service.Events is null && report.Service.Configuration is null &&
                report.Service.Devices.All(d => d.Endpoints.All(e => e.Measurements is null)), "Summary export actually excludes the unselected sections");
            Check(report.Service.FormatVersion == 2 && report.Service.Devices.All(d => d.Hardware is not null) &&
                report.Service.Devices.Count(d => d.Hardware!.Items.Any(i => i.Kind == DeviceDiagnosticHardwareKind.UsbProduct && i.VendorId == 0x1E71 && i.ProductId == 0x2022)) == demo.Hubs.Length,
                "Hardware summary contains matched Hub products without probing devices");
            Check(model.DiagnosticsSummary.Contains("機種・版情報：") && Descendants<TextBlock>(panel).Any(t => t.Text == model.DiagnosticsSummary),
                "Hardware coverage is displayed beside the snapshot counts");
            model.DiagnosticMeasurements = true;
            Check(!model.CanSaveDiagnostics && model.DiagnosticsSummary == "" && model.DiagnosticsVersions == "", "A changed scope invalidates the prior preview");
            model.DiagnosticOperations = true; model.DiagnosticConfiguration = true;
            var fan = model.Cooling.Single(r => r.Draft.Target == new EndpointReference(demo.DeviceIds[0], "fan1"));
            fan.Mode = "固定"; fan.Duty = 55; await model.OperateAsync(fan);
            var before = demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).ToArray(); var generation = client.Snapshot!.Configuration.Generation;
            await model.CreateDiagnosticsAsync(); await model.SaveDiagnosticsToAsync(Path.Combine(output, "all.json"));
            Check(model.DiagnosticsSummary.Contains("取得済み") && model.DiagnosticsSummary.Contains("履歴"), "Full preview includes configuration and history counts");
            Check(before.SequenceEqual(demo.Hubs.Select(h => (h.Writes, h.LightingWrites))) && client.Snapshot!.Configuration.Generation == generation && fan.CurrentDuty == "55%",
                "Capturing and saving do not change outputs or the adopted configuration");
            var all = await File.ReadAllTextAsync(Path.Combine(output, "all.json"));
            Check(demo.DeviceIds.All(id => !all.Contains(id)) && !all.Contains(client.Snapshot.Session!), "Export omits physical IDs and the authenticated session");
            try { await model.SaveDiagnosticsToAsync(output); throw new InvalidOperationException("Expected file failure"); } catch (IOException) { }
            Check(model.CanSaveDiagnostics && model.DiagnosticsIdle, "A failed save retains the captured report and permits retry");
            await model.SaveDiagnosticsToAsync(Path.Combine(output, "retry.json"));
            Check(File.Exists(Path.Combine(output, "retry.json")), "Retry saves the same report");
            foreach (var theme in model.Themes)
            {
                model.Theme = theme; panel.BringIntoView(); await Settle(); Capture(window, output, "diagnostics-" + theme);
                Check(panel.IsVisible && Descendants<Button>(panel).All(b => b.ActualWidth > 60), theme + " renders both actions without clipping");
            }
            window.Width = 960; window.Height = 640; panel.BringIntoView(); await Settle(); Capture(window, output, "diagnostics-minimum");
            Check(panel.ActualWidth > 600 && Descendants<CheckBox>(panel).All(c => c.ActualWidth > 50), "Minimum window size preserves the scope choices");
            await demo.Runtime.DisconnectAsync(client.Snapshot!.Session!);
            try { await client.RefreshAsync(); } catch (IOException) { }
            model.Render();
            Check(!model.CanCreateDiagnostics && model.CanSaveDiagnostics, "A disconnected service prevents recapture but preserves the dated report");
            await model.SaveDiagnosticsToAsync(Path.Combine(output, "offline.json"));
            Check(File.Exists(Path.Combine(output, "offline.json")), "An already captured report can be saved offline");
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while (!client.NeedsReconnect) await Task.Delay(25, timeout.Token);
            await model.ReconnectAsync(); Check(model.CanCreateDiagnostics, "Reconnection restores capture");
            Check(await model.CloseAsync(), "Normal exit completes after diagnostics");
            await File.WriteAllLinesAsync(Path.Combine(output, "diagnostics-smoke.txt"), checks); return 0;
        }
        catch (Exception e) { checks.Add("FAIL " + e); await File.WriteAllLinesAsync(Path.Combine(output, "diagnostics-smoke.txt"), checks); return 1; }
    }
}
