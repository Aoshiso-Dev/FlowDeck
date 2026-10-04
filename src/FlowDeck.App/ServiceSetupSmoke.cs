using System.Windows;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    private static async Task RunServiceSetupChecksAsync(FlowDeckApplication app, string output, Action<bool, string> check)
    {
        var plan = new ServiceDeploymentPlan("install", CoolingServiceProtocol.ServiceName,
            ServiceEnvironment.InstallationDirectory, ServiceEnvironment.DataDirectory, "2.0.0", new string('a', 64), 1657, 300_000_000,
            ["S-1-5-21-111-222-333-1001"], true, [], ["配布ファイルを保護された仮配置へコピーして再検証", "監視用の新しい設定でサービスを開始", "失敗時は以前の配置と設定へ復元"], Runtime: "devices-v2");
        var current = plan; var launches = 0; var confirmed = false; ServiceSetupExecution outcome = new(true, null);
        TaskCompletionSource<ServiceSetupExecution>? running = null;
        var model = new ServiceSetupSession(_ => Task.FromResult(current), _ => { launches++; return running?.Task ?? Task.FromResult(outcome); },
            _ => Task.FromResult(confirmed));
        var window = new ServiceSetupWindow(model); app.MainWindow = window; window.Show();
        try
        {
            await window.ReviewAsync(); await Settle();
            check(model.State == ServiceSetupState.Ready && window.ApplyButton.IsEnabled && launches == 0,
                "Setup review shows a verified plan without creating an elevated process");
            check(window.InstallPathText.Text == plan.InstallationDirectory && window.DetailsText.Text.Contains(plan.ManifestHash!) &&
                window.DetailsText.Text.Contains(plan.AllowedUsers[0]), "Review exposes the exact destination, package hash and permitted user");
            foreach (var theme in new[] { "system", "light", "dark" })
            {
                ThemeManager.Apply(theme); await Settle(); Capture(window, output, "setup-" + theme);
                check(window.StepsList.Items.Count == 3 && window.ApplyButton.ActualWidth > 100, theme + " theme keeps setup changes and explicit administrator action visible");
            }
            window.Width = 580; window.Height = 480; await Settle(); Capture(window, output, "setup-minimum");
            var edge = window.ApplyButton.TranslatePoint(new(window.ApplyButton.ActualWidth, window.ApplyButton.ActualHeight), window);
            check(edge.X < window.ActualWidth && edge.Y < window.ActualHeight, "Minimum setup window scrolls the plan while retaining the administrator action");
            current = plan with { Blockers = ["前回の更新記録が残っています。先に復旧してください"] };
            await window.ReviewAsync(); await window.ApplyAsync();
            check(!window.ApplyButton.IsEnabled && launches == 0 && window.BlockersText.Text.Contains("復旧"), "Preflight blockers are visible and prevent elevation");
            current = plan; await window.ReviewAsync(); current = plan with { ManifestHash = new('b', 64) }; await window.ApplyAsync();
            check(launches == 0 && model.State == ServiceSetupState.Blocked, "A changed package between review and apply cannot launch");
            current = plan; await window.ReviewAsync(); await window.ApplyAsync();
            check(model.State == ServiceSetupState.Cancelled && window.ReviewButton.IsEnabled && !window.ApplyButton.IsEnabled,
                "Cancelled administrator confirmation requires a new review and leaves the UI usable");
            running = new(TaskCreationOptions.RunContinuationsAsynchronously); await window.ReviewAsync(); var apply = window.ApplyAsync();
            await window.ApplyAsync(); window.Close();
            check(window.IsVisible && !window.ApplyButton.IsEnabled && !window.ReviewButton.IsEnabled && launches == 2,
                "Deployment cannot be duplicated or interrupted by closing its review window");
            running.SetResult(new(false, 0)); await apply;
            check(model.State == ServiceSetupState.Unconfirmed && window.CloseButton.Content.ToString() == "閉じる",
                "Zero process exit without verified installation does not offer successful reconnect");
            running = null; outcome = new(false, 0); confirmed = true; current = plan with { Action = "update" };
            await window.ReviewAsync(); check(window.ApplyButton.Content.ToString() == "管理者として更新", "Installed-service review clearly names the update action");
            await window.ApplyAsync(); window.Close();
            check(await window.Completion && launches == 3, "Confirmed installation returns an explicit reconnect result");
        }
        finally { if (window.IsVisible) window.Close(); }

        var cancelled = false;
        var slow = new ServiceSetupSession(async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return plan;
        }, _ => throw new InvalidOperationException("no execution in review"), _ => Task.FromResult(false));
        window = new(slow); app.MainWindow = window; window.Show(); var review = window.ReviewAsync(); window.Close(); await review;
        check(cancelled && !await window.Completion && !window.IsVisible, "Closing a slow review cancels and waits before releasing the window");

        var setupCalls = 0; var connects = 0; var setupResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new DeviceStartupWindow(new(Path.Combine(output, "setup-entry", "ui-v2.json")), ct =>
        { connects++; return DeviceStartupConnection.ConnectAsync(() => DeviceServiceRegistration.Missing,
            () => throw new InvalidOperationException("no service client expected"), ct); }, _ => { setupCalls++; return setupResult.Task; });
        app.MainWindow = entry; entry.Show(); await entry.ConnectAsync();
        check(entry.SetupButton.IsEnabled, "Missing-service startup offers the actual setup entry");
        var setup = entry.OpenSetupAsync(); await entry.OpenSetupAsync(); await entry.ConnectAsync(); entry.Close();
        check(entry.IsVisible && setupCalls == 1 && connects == 1 && !entry.RetryButton.IsEnabled,
            "Startup cannot connect, duplicate setup, or close while its setup owns the operation");
        setupResult.SetResult(true); await setup;
        check(connects == 2 && entry.RetryButton.IsEnabled, "Successful setup reconnects through the normal authenticated startup path");
        entry.Close(); await entry.FinishAsync();
    }
}
