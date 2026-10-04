using System.Windows;
using System.Windows.Controls;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    private static async Task RunWorkspaceUpdateChecksAsync(FlowDeckApplication app, string output, Action<bool, string> check)
    {
        await using var demo = await DemoDeviceWorkspace.CreateAsync(Path.Combine(output, "update-service"));
        var client = demo.CreateClient(); var opens = 0; var launches = 0; string? oldSession = null;
        var plan = new ServiceDeploymentPlan("update", CoolingServiceProtocol.ServiceName,
            ServiceEnvironment.InstallationDirectory, ServiceEnvironment.DataDirectory, "2.0.1", new string('a', 64), 1657, 300_000_000,
            ["S-1-5-21-111-222-333-1001"], true, [], ["現在の制御とM5GO接続を終了", "サービスを更新し、失敗時は以前の版へ戻す"], Runtime: "devices-v2");
        var reconnect = await DeviceWorkspaceInstalled.RunAsync(app, client, Path.Combine(output, "update-shell"), false,
            setup: async (owner, prepare) =>
            {
                opens++;
                var session = new ServiceSetupSession(_ => Task.FromResult(plan), _ =>
                {
                    check(client.Snapshot is { Connected: false, ExitAttempted: true }, "Elevation handoff follows confirmed service ownership release and pipe cleanup");
                    launches++; return Task.FromResult(new ServiceSetupExecution(true, null));
                }, _ => Task.FromResult(false), prepare);
                await ServiceSetupWindow.RunAsync(owner, session, async window =>
                {
                    check(!owner.IsEnabled && window.IsEnabled && (await demo.Hubs[0].ReadAsync()).Channels[2].Duty == 65,
                        "Review disables competing UI actions while existing cooling continues");
                    if (opens == 1) { window.Close(); return; }
                    if (opens == 2)
                    {
                        demo.Hubs[0].FailNextFanWrite = true;
                        await window.ApplyAsync();
                        check(session.State == ServiceSetupState.Blocked && !session.Prepared && launches == 0,
                            "A real synthetic fan restore failure keeps the owner and blocks elevation");
                        window.Close(); return;
                    }
                    await window.ApplyAsync();
                    check(session.Prepared && session.State == ServiceSetupState.Cancelled && launches == 1 &&
                        (await demo.Hubs[0].ReadAsync()).Channels[2].Duty == 70,
                        "Retry confirms the configured fan exit output before simulated UAC cancellation");
                    check(window.CloseButton.Content.ToString() == "接続へ戻る", "After release, cancelled setup offers a fresh service connection");
                    window.Close();
                });
            }, inspect: async (model, window) =>
            {
                oldSession = client.Snapshot!.Session;
                var row = model.Cooling.Single(r => r.Draft.Target == new EndpointReference(demo.DeviceIds[0], "fan3"));
                row.Mode = "固定"; row.Duty = 65; await model.OperateAsync(row);
                model.SelectedPage = 3; await Settle();
                var section = Descendants<Expander>(window).Single(e => e.Header?.ToString() == "サービス"); section.IsExpanded = true;
                foreach (var theme in new[] { "system", "light", "dark" })
                {
                    model.Theme = theme; await Settle(); Capture(window, output, "workspace-update-" + theme);
                    check(model.ServiceUpdateAvailable && model.ReviewServiceUpdate.CanExecute(null), theme + " workspace exposes service update through its settings");
                }
                await model.ReviewServiceUpdateAsync();
                check(window.IsEnabled && client.Snapshot!.Connected && launches == 0 && (await demo.Hubs[0].ReadAsync()).Channels[2].Duty == 65,
                    "Closing an unchanged review preserves the live client and cooling policy");
                await model.ReviewServiceUpdateAsync();
                check(window.IsEnabled && client.Snapshot!.ExitAttempted && model.ReviewServiceUpdate.CanExecute(null) && launches == 0,
                    "Closing a failed review preserves its release obligation and allows reopening to retry");
                await model.ReviewServiceUpdateAsync();
            });
        check(reconnect && opens == 3 && launches == 1 && client.Snapshot?.Connected == false,
            "Normal workspace relinquishes tray and client before requesting a new startup session");
        await using var fresh = demo.CreateClient(); await fresh.ConnectAsync();
        check(fresh.Snapshot!.Connected && fresh.Snapshot.Session != oldSession,
            "The released workspace reconnects with a new authenticated session rather than reusing the old client");
        await fresh.ReleaseAsync();
    }
}
