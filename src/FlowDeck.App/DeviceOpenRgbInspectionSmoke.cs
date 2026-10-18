using System.Windows;
using System.Windows.Controls;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceOpenRgbInspectionAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo,
        DeviceWorkspaceClient client, Window window, string output)
    {
        var report = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); report.Add("PASS " + message); }
        try
        {
            model.SelectedPage = 3; model.OpenRgbExpanded = true; await Settle();
            var panel = Descendants<DeviceOpenRgbSettingsView>(window).Single();
            var input = Descendants<DraftTextBox>(panel).Single();
            var button = Descendants<Button>(panel).Single(b => b.Content as string == "接続確認");
            var runtimeButton = Descendants<Button>(panel).Single(b => b.Content as string == "導入確認");
            Check(model.OpenRgbPort == "6742" && model.OpenRgbStatus == "未確認" && model.OpenRgbItems.Count == 0 && demo.OpenRgb.Calls == 0,
                "Opening settings neither connects nor fabricates a successful inspection");
            Check(model.OpenRgbRuntimeStatus == "未確認" && model.OpenRgbRuntimeVersion == "" && demo.OpenRgbRuntime.Calls == 0 &&
                runtimeButton.Command.CanExecute(null), "Opening settings does not probe installed files until explicitly requested");
            Check(button.Command.CanExecute(null), "The authenticated inspection feature enables the button");
            input.Focus(); input.Text = "7654";
            for (var i = 0; i < 3; i++) await model.PollAsync(); await Settle();
            Check(input.Text == "7654" && model.OpenRgbPort == "7654" && demo.OpenRgb.Calls == 0, "Polling preserves the edited port without initiating a connection");
            input.Text = "65536"; await Settle();
            Check(!button.Command.CanExecute(null) && model.OpenRgbStatus.Contains("1024"), "Out-of-range ports disable inspection");
            input.Text = "日本語"; await Settle();
            Check(!model.CanInspectOpenRgb && input.Text == "日本語", "Uncommitted non-numeric text remains visible and cannot be sent");
            input.Text = "6742";
            var fan = model.Cooling.Single(r => r.Draft.Target == new EndpointReference(demo.DeviceIds[0], "fan1"));
            fan.Mode = "固定"; fan.Duty = 55; await model.OperateAsync(fan);
            var writes = demo.Hubs[0].Writes; var generation = client.Snapshot!.Configuration.Generation;
            foreach (var (status,accepted,text) in new[]
            {
                (OpenRgbRuntimeInspectionStatus.NotInstalled,false,"未導入"),
                (OpenRgbRuntimeInspectionStatus.AccessDenied,false,"読み取り権限なし"),
                (OpenRgbRuntimeInspectionStatus.Invalid,false,"導入状態の確認失敗"),
                (OpenRgbRuntimeInspectionStatus.Verified,false,"導入済み・連携検証待ち"),
                (OpenRgbRuntimeInspectionStatus.Verified,true,"導入済み")
            })
            {
                demo.OpenRgbRuntime.Result = new(status,accepted,"1.0-demo");
                await model.InspectOpenRgbRuntimeAsync(); await Settle();
                Check(model.OpenRgbRuntimeStatus == text && model.OpenRgbRuntimeVersion == "対応専用版 1.0-demo" &&
                    model.OpenRgbRuntimeCheckedAt.StartsWith("前回確認"), "Runtime status is represented separately: " + status + "/" + accepted);
            }
            Check(demo.OpenRgb.Calls == 0 && demo.Hubs[0].Writes == writes && client.Snapshot!.Configuration.Generation == generation,
                "Inspecting runtime files never opens SDK, changes settings or changes active cooling");
            input.Text = "6745"; await model.PollAsync(); await Settle();
            Check(model.OpenRgbRuntimeStatus == "導入済み" && model.OpenRgbPort == "6745",
                "SDK port editing and polling preserve the independently checked installation state");
            input.Text = "6742";
            var runtimeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runtimeFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            demo.OpenRgbRuntime.Before = async ct => { runtimeEntered.SetResult(); await runtimeFinish.Task.WaitAsync(ct); };
            var runtimePending = model.InspectOpenRgbRuntimeAsync(); await runtimeEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try
            {
                await Settle();
                Check(model.OpenRgbRuntimeStatus == "確認中…" && !runtimeButton.Command.CanExecute(null) && !button.Command.CanExecute(null) && !input.IsEnabled,
                    "Checking files disables duplicate inspection and concurrent SDK edits");
            }
            finally { runtimeFinish.TrySetResult(); await runtimePending; demo.OpenRgbRuntime.Before = null; }
            demo.OpenRgbRuntime.Result = new(OpenRgbRuntimeInspectionStatus.NotInstalled,true,"1.0-demo");
            await model.InspectOpenRgbRuntimeAsync(); await Settle();
            Check(model.OpenRgbRuntimeStatus.Contains("再確認") && model.OpenRgbRuntimeVersion == "" && model.OpenRgbRuntimeCheckedAt == "" &&
                runtimeButton.Command.CanExecute(null) && client.Snapshot!.Connected, "Malformed runtime readiness clears old success and remains retryable");
            demo.OpenRgbRuntime.Result = new(OpenRgbRuntimeInspectionStatus.Verified,false,"1.0-demo");
            await model.InspectOpenRgbRuntimeAsync();
            await model.InspectOpenRgbAsync(); await Settle();
            Check(model.OpenRgbStatus == "SDK接続・設定取得済み" && model.OpenRgbItems.Count == 7 && model.OpenRgbCheckedAt.StartsWith("前回確認"),
                "Settings render the observation and its timestamp");
            Check(model.OpenRgbItems.Single(i => i.Key == "coexistence").Value == "未検証", "SDK success never implies permission to control devices");
            Check(client.Snapshot!.Configuration.Generation == generation && demo.Hubs[0].Writes == writes && fan.CurrentDuty == "55%",
                "Inspection preserves the configuration generation and active cooling output");
            foreach (var theme in model.Themes)
            {
                model.Theme = theme; panel.BringIntoView(); await Settle(); Capture(window, output, "openrgb-" + theme);
                Check(panel.IsVisible && input.ActualWidth > 40 && button.ActualWidth > 60, theme + " renders the inspection controls");
            }
            input.Text = "6743";
            Check(model.OpenRgbItems.Count == 0 && model.OpenRgbStatus == "未確認" && model.OpenRgbCheckedAt == "", "Changing the port clears the previous endpoint's result");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            demo.OpenRgb.Before = async ct => { entered.SetResult(); await finish.Task.WaitAsync(ct); };
            var pending = model.InspectOpenRgbAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try
            {
                await Settle(); Check(model.OpenRgbStatus == "確認中…" && !button.Command.CanExecute(null) && !input.IsEnabled,
                    "An in-flight request disables duplicate inspection and port editing");
                model.OpenRgbPort = "6744"; model.Render(); finish.SetResult(); await pending;
                Check(model.OpenRgbItems.Count == 0 && model.OpenRgbStatus == "未確認", "A late result cannot overwrite a changed port draft");
            }
            finally { finish.TrySetResult(); await pending; demo.OpenRgb.Before = null; }
            demo.OpenRgb.Status = OpenRgbInspectionStatus.ConnectionFailed;
            await model.InspectOpenRgbAsync(); await Settle();
            Check(model.OpenRgbStatus == "SDKへ接続できません" && model.CanInspectOpenRgb && client.Snapshot!.Connected,
                "SDK connection failure remains retryable without disconnecting the hardware service");
            demo.OpenRgb.Status = OpenRgbInspectionStatus.Observed;
            await model.InspectOpenRgbAsync();
            var display = model.DashboardSettings.First(r => r.Item.Measurement == DeviceFeature.Temperature);
            display.Name = "設定変更の確認"; await model.SaveDashboardAsync(); await model.PollAsync();
            Check(model.OpenRgbItems.Count == 0 && model.OpenRgbStatus == "未確認" && model.OpenRgbPort == "6744",
                "Adopting a new settings generation invalidates observations but retains the port draft");
            Check(model.OpenRgbRuntimeStatus == "未確認" && model.OpenRgbRuntimeVersion == "" && model.OpenRgbRuntimeCheckedAt == "",
                "New settings generations invalidate runtime readiness observations too");
            await model.InspectOpenRgbRuntimeAsync();
            await model.InspectOpenRgbAsync();
            window.Width = 960; window.Height = 640; panel.BringIntoView(); await Settle(); Capture(window, output, "openrgb-minimum");
            Check(demo.Hubs[0].Writes == writes && demo.Hubs[1].Writes == 0, "All inspection paths leave cooling and other hardware untouched");
            await demo.Runtime.DisconnectAsync(client.Snapshot!.Session!); await model.PollAsync();
            Check(model.OpenRgbItems.Count == 0 && !model.CanInspectOpenRgb, "Service disconnect removes old observation rows");
            Check(model.OpenRgbRuntimeStatus == "サービス未接続" && model.OpenRgbRuntimeVersion == "" && !model.CanInspectOpenRgbRuntime,
                "Disconnect clears runtime status and disables its inspection");
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while (!client.NeedsReconnect) await Task.Delay(25, timeout.Token);
            await model.ReconnectAsync(); await model.InspectOpenRgbAsync();
            Check(model.OpenRgbRuntimeStatus == "未確認", "Reconnected sessions cannot reuse the previous runtime readiness result");
            await model.InspectOpenRgbRuntimeAsync();
            Check(model.OpenRgbRuntimeStatus == "導入済み・連携検証待ち", "Runtime inspection can be repeated on the new session");
            Check(model.OpenRgbItems.Count == 7 && model.CanInspectOpenRgb, "A new authenticated session can perform a new inspection");
            Check(await model.CloseAsync(), "Normal application exit releases control after inspection");
            await File.WriteAllLinesAsync(Path.Combine(output, "openrgb-inspection-smoke.txt"), report); return 0;
        }
        catch (Exception e)
        {
            report.Add("FAIL " + e); report.Add("STATE " + model.OpenRgbStatus);
            await File.WriteAllLinesAsync(Path.Combine(output, "openrgb-inspection-smoke.txt"), report); return 1;
        }
    }
}
