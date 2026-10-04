using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceMonitoringSetupAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, Window window, string output)
    {
        var report = new List<string>();
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); report.Add("PASS " + label); }
        try
        {
            Check(model.SelectedPage == 0 && model.AutomaticMonitoring && model.DashboardTemperatures.Count == 2 && model.DashboardFans.Count == 10,
                "First launch immediately displays temperature and RPM without any port confirmation");
            await model.PrepareFirstUseAsync(); await Settle();
            Check(model.SelectedPage == 0 && model.SetupEditor is null && model.Lights.Count(l => l.Ready) == 2 && model.Lights.All(l => !l.NeedsSetup),
                "Known lighting becomes usable during normal launch without a setup button, checkbox or wizard");
            Check(demo.Hubs.All(h => h.Writes == 0 && h.LightingWrites == 0) && model.Cooling.All(r => !r.CanApply),
                "Automatic first use preserves hardware cooling and emits no lighting output");
            Capture(window, output, "first-use-without-confirmation");
            model.SelectedPage = 3; await Settle();
            Check(model.DashboardTemperatures.Count == 2 && model.DashboardFans.Count == 10 && model.MonitoringCandidates.Count == 12,
                "Automatic monitoring remains an unsaved view; readings can still be selected for a custom dashboard");
            Check(!model.CanRegisterMonitoring && model.Cooling.All(r => !r.CanApply), "Initial proposals grant no cooling output permission");
            var temp = model.MonitoringCandidates.First(r => r.Candidate.Selection.Item.Measurement == DeviceFeature.Temperature);
            var fan = model.MonitoringCandidates.Single(r => r.Candidate.Selection.Item.Target == new EndpointReference(demo.DeviceIds[0], "fan1"));
            foreach (var row in new[] { temp, fan })
                Descendants<CheckBox>(window).Single(c => ReferenceEquals(c.DataContext, row)).SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            await model.PollAsync(); await Settle();
            Check(temp.Selected && fan.Selected && model.MonitoringCandidates.Contains(fan) && model.CanRegisterMonitoring, "Checkbox selections and rows survive telemetry polling");
            foreach (var theme in new[] { "light", "dark" }) { model.Theme = theme; await Settle(); Capture(window, output, "monitoring-candidates-" + theme); }
            await model.RegisterMonitoringAsync();
            Check(model.DashboardTemperatures.Count == 1 && model.DashboardFans.Count == 1 && model.MonitoringCandidates.Count == 10, "Only selected readings appear in the monitor");
            Check(demo.Hubs.All(h => h.Writes == 0 && h.LightingWrites == 0) && model.Cooling.All(r => !r.CanApply), "Monitoring registration performs no output and grants no control");
            var disappearing = model.MonitoringCandidates.First(r => r.Candidate.Selection.Item.Target.DeviceId == demo.DeviceIds[1]); disappearing.Selected = true;
            demo.Hubs[1].Disconnected = true; await demo.Runtime.PollAsync(); await model.PollAsync();
            Check(disappearing.Selected && model.MonitoringDraft.Conflict && !model.CanRegisterMonitoring, "Disconnect retains the choice but prevents applying an old inventory");
            demo.Hubs[1].Disconnected = false; await demo.Runtime.PollAsync(); await demo.Runtime.PollAsync(); await model.ReloadMonitoringAsync();
            Check(!model.MonitoringDraft.Dirty && !model.MonitoringDraft.Conflict, "Explicit reload clears old selections after reconnection");
            foreach (var row in model.MonitoringCandidates.ToArray()) row.Selected = true;
            await model.RegisterMonitoringAsync(); model.SelectedPage = 0; await Settle();
            Check(model.DashboardTemperatures.Count == 2 && model.DashboardFans.Count == 10 && !model.HasMonitoringCandidates, "Additional selection completes monitoring for both detected Hubs");
            Capture(window, output, "monitoring-added");
            Check(demo.Hubs.All(h => h.Writes == 0 && h.LightingWrites == 0), "All monitoring setup actions leave hardware output unchanged");
            demo.AdditionalSensorsPerDevice = 160; await demo.Runtime.PollAsync(); await model.ReloadMonitoringAsync();
            model.SelectedPage = 3; await Settle();
            Check(model.MonitoringDraft.CandidateCount == 320 && model.HasMonitoringPages && model.MonitoringCandidates.Count == DeviceMonitoringDraft.PageSize,
                "More than 256 discovered readings remain available through bounded pages");
            var firstTarget = model.MonitoringCandidates[0].Candidate.Selection.Item.Target;
            model.MonitoringCandidates[0].Selected = true;
            var visited = new HashSet<string>();
            for (var page = 0; page < model.MonitoringDraft.PageCount; page++)
            {
                foreach (var row in model.MonitoringCandidates) visited.Add(row.Key);
                if (page + 1 < model.MonitoringDraft.PageCount)
                {
                    var next = Descendants<Button>(window).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "監視候補の次ページ");
                    Check(next.Command.CanExecute(null), "Next page remains available before the last page");
                    next.Command.Execute(null); await model.PollAsync(); await Settle();
                }
            }
            Check(visited.Count == 320 && !model.NextMonitoringPage.CanExecute(null), "Every same-named reading is reachable and the final page cannot advance");
            await model.PollAsync();
            var last = model.MonitoringCandidates.Last(); last.Selected = true; var lastTarget = last.Candidate.Selection.Item.Target;
            await model.PollAsync(); await Settle();
            Check(model.MonitoringDraft.SelectedCount == 2 && model.CanRegisterMonitoring && last.Selected,
                $"Selections on different pages survive telemetry refresh without switching physical targets (selected={model.MonitoringDraft.SelectedCount}, available={model.CanRegisterMonitoring}, last={last.Selected}, conflict={model.MonitoringDraft.Conflict}, notice={model.Notice})");
            foreach (var theme in new[] { "system", "light", "dark" })
            {
                model.Theme = theme; await model.PollAsync(); await Settle();
                Descendants<Expander>(window).Single(e => Equals(e.Header, "検出した温度・回転数")).BringIntoView();
                await Settle(); Capture(window, output, "monitoring-pages-" + theme);
                Check(model.HasMonitoringPages && model.MonitoringPageLabel.Contains("320") && model.CanRegisterMonitoring,
                    theme + " theme retains paging, total count and selected registrations");
            }
            window.Width = window.MinWidth; window.Height = window.MinHeight; await Settle(); Capture(window, output, "monitoring-pages-minimum");
            Check(Descendants<Button>(window).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "監視候補の前ページ").ActualWidth > 0,
                "Minimum window retains the previous-page control");
            await model.PollAsync(); await model.RegisterMonitoringAsync();
            Check(model.DashboardSettings.Any(r => r.Item.Target == firstTarget) && model.DashboardSettings.Any(r => r.Item.Target == lastTarget) &&
                model.MonitoringDraft.RegisteredCount == 14, "The service adopts only the two selected identities from different pages");
            var remove = model.DashboardSettings.Single(r => r.Item.Target == firstTarget);
            await Settle();
            var details = Descendants<Expander>(window).Single(e => ReferenceEquals(e.DataContext, remove) && Equals(e.Header, "表示形式・目盛"));
            details.IsExpanded = true; await Settle();
            var removeButton = Descendants<Button>(window).Single(b => ReferenceEquals(b.DataContext, remove) &&
                System.Windows.Automation.AutomationProperties.GetName(b) == "モニターの登録を削除");
            details.BringIntoView(); await Settle(); Capture(window, output, "monitoring-remove");
            removeButton.Command.Execute(removeButton.CommandParameter); await model.PollAsync();
            Check(model.DashboardSettings.All(r => r.Item.Target != firstTarget) && model.CanSaveDashboard,
                "Removing a display registration remains an unsaved edit across polling");
            await model.SaveDashboardAsync();
            Check(model.MonitoringDraft.RegisteredCount == 13 && model.MonitoringDraft.Candidates.Any(c => c.Selection.Item.Target == firstTarget) &&
                model.DashboardSettings.All(r => r.Item.Target != firstTarget), "Saved removal frees a slot and exposes the exact reading for explicit registration again");
            Check(demo.Hubs.All(h => h.Writes == 0 && h.LightingWrites == 0), "Paging, registration and removal never change fan or lighting output");
            Check(await model.CloseAsync(), "Monitoring setup exits with confirmed session release");
            await File.WriteAllLinesAsync(Path.Combine(output, "device-monitoring-smoke.txt"), report); return 0;
        }
        catch (Exception e) { report.Add("FAIL " + e); await File.WriteAllLinesAsync(Path.Combine(output, "device-monitoring-smoke.txt"), report); return 1; }
    }
}
