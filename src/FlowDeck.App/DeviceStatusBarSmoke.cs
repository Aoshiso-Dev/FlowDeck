using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    // Runs only through --devices-preview --demo --smoke --status-bar. All state is isolated DEMO data.
    public static async Task<int> RunDeviceStatusBarAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo,
        DeviceWorkspaceClient client, Window window, string output)
    {
        var report = new List<string>(); string? error = null;
        void Check(bool condition, string label)
        { if (!condition) throw new InvalidOperationException(label); report.Add("PASS " + label); }
        try
        {
            Check(client.IsDemo && demo.Hubs.All(h => h.IsDemo), "The status scenario is confined to the built-in authenticated DEMO");
            await model.PrepareFirstUseAsync(); await Settle();
            Check(model.StatusMessage.Contains("端子を自動認識済み") && model.StatusDetails.Contains("端子を自動認識済み") &&
                string.IsNullOrWhiteSpace(model.WarningMessage), "Initial lighting recognition is ordinary UI guidance with full status details");
            Check(model.Lights.Count(l => l.Ready) > 0 && demo.Hubs.All(h => h.Writes == 0 && h.LightingWrites == 0),
                "Recognized lighting is ready without initial output writes");
            Capture(window, output, "status-bar-recognized");
            await model.ReloadSetupAsync(); await Settle();
            Check(!string.IsNullOrWhiteSpace(model.StatusMessage) && string.IsNullOrWhiteSpace(model.WarningMessage),
                "Successful device recognition reports ordinary status without an upper warning");
            var dashboard = model.DashboardSettings.First(); dashboard.Name = "ステータス試験の表示名";
            await model.SaveDashboardAsync(); await Settle();
            Check(!model.DashboardDraft.Dirty && !string.IsNullOrWhiteSpace(model.StatusMessage) && string.IsNullOrWhiteSpace(model.WarningMessage),
                "A successful display-settings save uses ordinary status");
            await model.ReconnectAsync(); await Settle();
            Check(client.Snapshot is { Connected: true } && !string.IsNullOrWhiteSpace(model.StatusMessage) &&
                string.IsNullOrWhiteSpace(model.WarningMessage), "A healthy reconnect uses ordinary status and leaves no obsolete warning");

            var generation = client.Snapshot!.Configuration.Generation;
            var configurationHash = SettingsTransfer.Hash(client.Snapshot.Configuration.Configuration);
            var writes = demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).ToArray();
            RunStatusFeedbackCases(new DeviceWorkspaceViewModel(client), Check);
            await RunStatusBarViewCases(model, window, output, Check);
            RunPersistentStatusWarningCases(model, client.Snapshot!, Check);
            await RunStatusInlineCases(model, client, window, output, Check);
            await client.RefreshAsync(true); model.Render(); await Settle();
            Check(demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).SequenceEqual(writes),
                "Hints, status details, warning presentation, navigation and restore preview cause no DEMO output writes");
            Check(client.Snapshot!.Configuration.Generation == generation &&
                SettingsTransfer.Hash(client.Snapshot.Configuration.Configuration) == configurationHash,
                "Status and warning UI cannot advance or rewrite the saved configuration generation");
            Check(await model.CloseAsync(), "The status scenario closes the unowned DEMO normally");
        }
        catch (Exception e)
        {
            error = e.ToString(); report.Add("FAIL " + error);
            report.Add("STATE " + model.StatusMessage + " / " + model.WarningMessage);
        }
        await File.WriteAllLinesAsync(Path.Combine(output, "device-status-bar-smoke.txt"), report);
        await File.WriteAllTextAsync(Path.Combine(output, "status-bar-result.json"), JsonSerializer.Serialize(new
        {
            passed = report.Count(l => l.StartsWith("PASS ", StringComparison.Ordinal)), failed = error is null ? 0 : 1,
            error, hardwareAccessed = false, scenario = "status-bar", screenshots = new[]
            { "status-bar-light.png", "status-bar-dark.png", "status-bar-minimum-light.png", "status-bar-minimum-dark.png",
                "status-bar-warning.png", "status-bar-details.png", "status-bar-recognized.png" }
        }, new JsonSerializerOptions { WriteIndented = true }));
        return error is null ? 0 : 1;
    }

    private static void RunStatusFeedbackCases(DeviceWorkspaceViewModel model, Action<bool, string> check)
    {
        model.ReportStatus("一般案内", hint: true);
        check(model.StatusMessage == "一般案内" && string.IsNullOrEmpty(model.WarningMessage), "A hint is visible when no operation or result exists");
        model.ReportResult("保存完了", Outcome.Confirmed); model.ReportStatus("後から来た一般案内", hint: true);
        check(model.StatusMessage == "保存完了", "A late hint cannot replace the latest operation result");
        var older = model.BeginStatus("処理Aを実行中");
        model.ReportResult("先に届いた結果", Outcome.Confirmed);
        check(model.StatusMessage == "処理Aを実行中", "An active operation outranks an incoming result");
        var newer = model.BeginStatus("処理Bを実行中"); older.Dispose(); older.Dispose();
        check(model.StatusMessage == "処理Bを実行中", "Disposing an older concurrent operation cannot erase the newer progress");
        model.ReportResult("最新の結果", Outcome.Confirmed); model.ReportStatus("別のヒント", hint: true);
        check(model.StatusMessage == "処理Bを実行中", "Hints and results remain behind current progress");
        newer.Dispose();
        check(model.StatusMessage == "最新の結果", "The newest result appears after the final operation completes");
        using (model.BeginStatus("短い再取得")) { }
        check(model.StatusMessage == "最新の結果", "Finishing an operation without a new result preserves the previous result");
        model.ReportResult("操作の結果が不明です", Outcome.Unknown);
        check(model.WarningMessage.Contains("操作の結果が不明です"), "An uncertain operation result remains a warning");
        model.ReportResult("次の操作は完了", Outcome.Confirmed);
        check(string.IsNullOrEmpty(model.WarningMessage), "A confirmed result clears only the transient operation warning");
        foreach (var saveState in new[] { DevicePolicySaveState.Failed, DevicePolicySaveState.Superseded })
        {
            var persistenceMessage = "保存の状態: " + saveState;
            model.ReportResult(new DeviceOperationResult("status-smoke", "smoke", Outcome.Confirmed, DateTimeOffset.UtcNow,
                [new(new("demo-hub-a", "fan1"), 1, 1, Outcome.Confirmed, "出力は確認済み",
                    Persistence: new(saveState, persistenceMessage))], "操作は完了"));
            check(model.WarningMessage.Contains(persistenceMessage), saveState + " persistence remains a warning even when output was confirmed");
        }
        model.ReportResult(new DeviceOperationResult("status-smoke", "smoke", Outcome.Confirmed, DateTimeOffset.UtcNow,
            [new(new("demo-hub-a", "fan1"), 1, 1, Outcome.Confirmed, "確認済み", Persistence: new(DevicePolicySaveState.Saved, "保存済み"))], "保存も完了"));
        check(string.IsNullOrWhiteSpace(model.WarningMessage), "A fully saved confirmed result clears the transient persistence warning");
        const string sessionOnly = "今回の接続にだけ適用され、設定は保存されません";
        model.ReportResult(new DeviceOperationResult("status-smoke", "smoke", Outcome.Confirmed, DateTimeOffset.UtcNow,
            [new(new("demo-hub-a", "led1"), 1, 1, Outcome.Confirmed, "適用済み", Persistence: new(DevicePolicySaveState.SessionOnly, sessionOnly))], "接続限定の操作は完了"));
        check(string.IsNullOrWhiteSpace(model.WarningMessage) && model.StatusMessage.Contains(sessionOnly) && model.StatusDetails.Contains(sessionOnly),
            "An intentional session-only success retains its explanation in ordinary status and full details");
        model.ReportConnectionFailure("接続を確認できません"); model.ReportConnectionRestored();
        check(string.IsNullOrWhiteSpace(model.WarningMessage), "A successful reconnect clears its own connection warning");
        model.ReportConnectionFailure("接続を確認できません"); model.Notice = "保存に関する別の警告";
        model.ReportConnectionRestored();
        check(model.WarningMessage.Contains("保存に関する別の警告"), "Reconnect cannot acknowledge another operation's warning");
        model.ReportConnectionFailure("同じ文章の警告"); model.Notice = "同じ文章の警告";
        model.ReportConnectionRestored();
        check(model.WarningMessage.Contains("同じ文章の警告"), "An unrelated warning with identical text does not inherit the connection warning's ownership");
        model.ReportResult("保持する直近結果", Outcome.Confirmed); model.ReportConnectionFailure("一時的な切断");
        model.ReportConnectionRestored(automatic: true);
        check(model.StatusMessage == "保持する直近結果" && string.IsNullOrWhiteSpace(model.WarningMessage),
            "Automatic reconnect clears its own failure without replacing the user's latest result with a hint");
        var longMessage = "全文確認: " + string.Concat(Enumerable.Repeat("長い処理結果を省略せず確認します。", 80)) + "末尾の確認文字";
        model.ReportStatus(longMessage);
        check(model.HasStatusDetails && model.StatusDetails.Contains(longMessage), "Status details retain a long result including its final characters");
        for (var i = 0; i < 25; i++) model.ReportStatus("履歴上限/" + i.ToString("D2"));
        check(!model.StatusDetails.Contains("履歴上限/04") &&
            Enumerable.Range(5, 20).All(i => model.StatusDetails.Contains("履歴上限/" + i.ToString("D2"))) &&
            model.StatusDetails.Split(Environment.NewLine + Environment.NewLine).Length == 20,
            "The bounded history keeps the newest twenty complete messages without growing indefinitely");
    }

    private static async Task RunStatusBarViewCases(DeviceWorkspaceViewModel model, Window window, string output, Action<bool, string> check)
    {
        model.ReportStatus("接続した機器を認識しました"); await Settle();
        var changes = 0; var beforeDetails = model.StatusDetails;
        void FeedbackChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        { if (e.PropertyName is nameof(model.StatusMessage) or nameof(model.StatusDetails) or nameof(model.WarningMessage)) changes++; }
        model.PropertyChanged += FeedbackChanged;
        try { for (var i = 0; i < 3; i++) { model.ReportStatus("接続した機器を認識しました"); model.Render(); } }
        finally { model.PropertyChanged -= FeedbackChanged; }
        check(changes == 0 && model.StatusDetails == beforeDetails,
            "Repeated identical status and renders produce no duplicate feedback notifications or history entries");
        var bar = StatusElement<FrameworkElement>(window, "WorkspaceStatusBar");
        var status = StatusElement<TextBlock>(window, "WorkspaceStatusMessage");
        var warning = StatusElement<TextBlock>(window, "WorkspaceWarningMessage");
        check(bar.IsVisible && status.Text == model.StatusMessage && !warning.IsVisible,
            "Ordinary status occupies the bottom bar while the upper warning area is absent");
        check(AutomationProperties.GetLiveSetting(status) == AutomationLiveSetting.Polite &&
            AutomationProperties.GetLiveSetting(warning) == AutomationLiveSetting.Assertive,
            "Status and important warnings expose polite and assertive accessibility live regions");
        model.ReportResult("操作結果の確認が必要です", Outcome.Unknown); await Settle();
        check(warning.IsVisible && warning.Text.Contains("操作結果の確認が必要です"),
            "An important unresolved result is rendered in the upper warning region");
        Capture(window, output, "status-bar-warning");
        model.ReportResult("接続した機器を認識しました", Outcome.Confirmed); await Settle();
        check(!warning.IsVisible, "A resolved transient warning releases its upper space");
        check(status.TextTrimming == TextTrimming.CharacterEllipsis && status.TextWrapping == TextWrapping.NoWrap,
            "The bottom message uses one trimmed line instead of expanding page content");
        var position = bar.TranslatePoint(new Point(), (UIElement)window.Content); var height = bar.ActualHeight;
        foreach (var page in new[] { 1, 2, 3, 0 })
        {
            model.SelectedPage = page; await Settle();
            var current = bar.TranslatePoint(new Point(), (UIElement)window.Content);
            check(Math.Abs(current.Y - position.Y) < 1 && Math.Abs(bar.ActualHeight - height) < 1,
                "The status bar remains fixed while navigating page " + page);
        }
        var longMessage = "詳細の全文: " + string.Concat(Enumerable.Repeat("スクロールしてすべての説明を読むことができます。", 90)) + "末尾まで保持";
        model.ReportStatus(longMessage); await Settle();
        var detailsButton = StatusElement<ToggleButton>(window, "StatusDetailsButton");
        check(detailsButton.IsVisible && detailsButton.IsEnabled && detailsButton.Focusable,
            "The details control is visible, enabled and keyboard focusable");
        window.Activate(); Keyboard.Focus(detailsButton);
        detailsButton.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        detailsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Settle();
        var details = StatusElement<TextBox>(window, "StatusDetailsText");
        check(details.IsVisible && details.IsReadOnly && details.Text.Contains(longMessage),
            "The opened details show the complete read-only message");
        // WPF Popups have a separate presentation root, outside Window.Content.
        var panel = StatusElement<FrameworkElement>(window, "StatusDetailsPopup");
        var dpi = VisualTreeHelper.GetDpi(panel);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(panel.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(panel);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(output, "status-bar-details.png"))) encoder.Save(stream);
        Keyboard.Focus(details); details.SelectAll();
        check(details.IsKeyboardFocusWithin && details.SelectedText.Contains("末尾まで保持"),
            "Keyboard focus and text selection reach the full long result");
        check(Math.Abs(bar.ActualHeight - height) < 1 &&
            Math.Abs(bar.TranslatePoint(new Point(), (UIElement)window.Content).Y - position.Y) < 1,
            "Opening long details leaves the status row and page geometry unchanged");
        details.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(details)!, 0, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent }); await Settle();
        check(detailsButton.IsChecked != true && detailsButton.IsKeyboardFocused,
            "Escape closes status details and returns keyboard focus to their control");
        foreach (var theme in new[] { "light", "dark" })
        {
            window.Width = 1120; window.Height = 820; model.Theme = theme; model.SelectedPage = 0; await Settle();
            Capture(window, output, "status-bar-" + theme);
            check(bar.IsVisible && status.ActualWidth > 200, theme + " theme keeps ordinary status readable");
            window.Width = window.MinWidth; window.Height = window.MinHeight; await Settle();
            Capture(window, output, "status-bar-minimum-" + theme);
            check(bar.IsVisible && status.ActualWidth > 120 && detailsButton.ActualWidth > 20 &&
                bar.TranslatePoint(new Point(0, bar.ActualHeight), (UIElement)window.Content).Y <= ((FrameworkElement)window.Content).ActualHeight + 1,
                theme + " minimum-size layout retains the entire status row and details control");
        }
        window.Width = 1120; window.Height = 820;
    }

    private static void RunPersistentStatusWarningCases(DeviceWorkspaceViewModel model, DeviceWorkspaceSnapshot original, Action<bool, string> check)
    {
        // Exercise the production state reducer without replacing the client's state or creating hardware obligations.
        var render = typeof(DeviceWorkspaceViewModel).GetMethod("RenderPersistentWarnings", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("Persistent-warning reducer is missing");
        void Observe(DeviceWorkspaceSnapshot snapshot) => render.Invoke(model, [snapshot]);
        var healthy = original with { Connected = true, Error = null, ElapsedSeconds = 0 };
        var light = healthy.State.Endpoints.First(e => healthy.State.Inventory.Devices.Any(d => d.Device.Id == e.Target.DeviceId &&
            d.Device.Endpoints.Any(p => p.Id == e.Target.EndpointId && p.Kind == EndpointKind.Lighting)));
        DeviceWorkspaceSnapshot WithExecution(DeviceExecutionState execution) => healthy with
        { State = healthy.State with { Endpoints = healthy.State.Endpoints.Select(e => e.Target == execution.Target ? execution : e).ToArray() } };
        const string fault = "照明端子の終了処理を完了できませんでした";
        var failed = WithExecution(light with { Lighting = new("status-smoke", false, 0, 0, null, 0, fault) });
        model.SelectLights([]); Observe(failed); model.ReportResult("別の保存は完了しました", Outcome.Confirmed);
        check(model.PersistentWarning.Contains(fault) && model.WarningMessage.Contains(fault),
            "An unselected lighting fault survives another operation's success");
        model.ReportConnectionFailure("接続が切れました"); model.ReportConnectionRestored();
        check(model.WarningMessage.Contains(fault), "A successful service reconnect cannot clear an endpoint's unresolved recovery warning");
        model.SelectedPage = 1;
        check(model.WarningMessage.Contains(fault), "Page navigation cannot dismiss an unresolved device fault");
        Observe(healthy with { Connected = false });
        check(model.WarningMessage.Contains(fault) && model.WarningMessage.Contains("最終確認"),
            "A disconnected snapshot preserves the last unresolved lighting fault");
        Observe(healthy with { ElapsedSeconds = 3 });
        check(model.WarningMessage.Contains(fault), "A stale healthy-looking snapshot cannot clear a fault");
        Observe(healthy with { State = healthy.State with { Endpoints = healthy.State.Endpoints.Where(e => e.Target != light.Target).ToArray() } });
        check(model.WarningMessage.Contains(fault), "A missing endpoint does not establish that its fault recovered");
        Observe(WithExecution(light with { Generation = light.Generation + 1 }));
        check(model.WarningMessage.Contains(fault), "An obsolete execution generation cannot clear a current endpoint fault");
        var replacement = WithExecution(light with { Generation = light.Generation + 1, Owned = true,
            Outcome = Outcome.Unknown, Lighting = null, Cooling = null, Calibration = null });
        replacement = replacement with { State = replacement.State with { Inventory = replacement.State.Inventory with
        { Devices = replacement.State.Inventory.Devices.Select(d => d.Device.Id == light.Target.DeviceId ?
            d with { Generation = d.Generation + 1 } : d).ToArray() } } };
        foreach (var pending in new[] { Outcome.Unknown, Outcome.Pending })
        {
            Observe(replacement with { State = replacement.State with { Endpoints = replacement.State.Endpoints.Select(e =>
                e.Target == light.Target ? e with { Outcome = pending } : e).ToArray() } });
            check(model.WarningMessage.Contains(fault), "A new connection generation with owned " + pending + " execution cannot erase its previous recovery obligation");
        }
        Observe(replacement with { State = replacement.State with { Endpoints = replacement.State.Endpoints.Select(e =>
            e.Target == light.Target ? e with { Outcome = Outcome.Confirmed, Owned = false } : e).ToArray() } });
        check(string.IsNullOrWhiteSpace(model.PersistentWarning), "A fresh unowned confirmed execution can clear the obligation on the new connection generation");
        Observe(healthy);
        check(string.IsNullOrWhiteSpace(model.PersistentWarning) && string.IsNullOrWhiteSpace(model.WarningMessage),
            "A fresh healthy execution for the current endpoint clears the resolved warning");
        DeviceWorkspaceSnapshot WithAvailability(DeviceAvailability availability) => healthy with
        { State = healthy.State with { Inventory = healthy.State.Inventory with { Devices = healthy.State.Inventory.Devices.Select(d =>
            d.Device.Id != light.Target.DeviceId ? d : d with { Device = d.Device with { Endpoints = d.Device.Endpoints.Select(e =>
                e.Id != light.Target.EndpointId ? e : e with { Capabilities = e.Capabilities.Select(c => c.Feature != DeviceFeature.FixedColor ? c :
                    c with { Availability = availability, Reason = "終了処理待ちの能力状態" }).ToArray() }).ToArray() } }).ToArray() } } };
        foreach (var availability in new[] { DeviceAvailability.NeedsSetup, DeviceAvailability.TemporarilyUnavailable })
        { Observe(WithAvailability(availability)); check(string.IsNullOrWhiteSpace(model.PersistentWarning), availability + " alone is not misreported as a recovery obligation"); }
        Observe(WithAvailability(DeviceAvailability.Failed)); model.ReportStatus("通常の案内", hint: true);
        check(model.PersistentWarning.Contains("終了処理待ちの能力状態"), "A supported lighting recovery obligation survives informational hints");
        Observe(WithAvailability(DeviceAvailability.TemporarilyUnavailable));
        check(model.PersistentWarning.Contains("終了処理待ちの能力状態"), "Unreadable device state does not clear a prior recovery obligation");
        Observe(healthy);
        var fan = healthy.State.Endpoints.First(e => healthy.State.Inventory.Devices.Any(d => d.Device.Id == e.Target.DeviceId &&
            d.Device.Endpoints.Any(p => p.Id == e.Target.EndpointId && p.Kind == EndpointKind.Cooling)));
        Observe(WithExecution(fan with { Cooling = new(FanMode.Fallback, 100, true, "冷却入力の異常が未解決です", false, false) }));
        model.ReportResult("別の操作は成功", Outcome.Confirmed);
        check(model.WarningMessage.Contains("冷却入力の異常"), "A cooling fault cannot be dismissed by an unrelated confirmed result");
        Observe(healthy);
        var trial = new CoolingCalibrationResult(40, null, null, null, null, 0, false, false, Outcome.Rejected, "試験前の拒否");
        Observe(WithExecution(fan with { Owned = true, Calibration = trial, Outcome = Outcome.Rejected }));
        check(string.IsNullOrWhiteSpace(model.PersistentWarning), "A rejected calibration with no original reading invents no recovery warning");
        trial = trial with { Before = new(45, 1000, 1), Message = "元の回転数へ復帰できません" };
        Observe(WithExecution(fan with { Owned = true, Calibration = trial, Outcome = Outcome.Pending }));
        check(string.IsNullOrWhiteSpace(model.PersistentWarning), "An active pending calibration is ordinary progress rather than failed recovery");
        Observe(WithExecution(fan with { Owned = true, Calibration = trial, Outcome = Outcome.Unknown }));
        model.ReportResult("表示設定を保存しました", Outcome.Confirmed);
        check(model.WarningMessage.Contains(trial.Message), "A calibration recovery obligation survives an unrelated successful save");
        Observe(healthy);
        Observe(healthy with { State = healthy.State with { LifecycleError = "復旧ジャーナルを確認してください" } });
        model.ReportResult("完了", Outcome.Confirmed);
        check(model.WarningMessage.Contains("復旧ジャーナル"), "A lifecycle recovery failure remains above a successful result");
        Observe(healthy with { Connected = false });
        check(model.WarningMessage.Contains("復旧ジャーナル"), "Disconnect retains an unresolved lifecycle warning");
        Observe(healthy);
        check(string.IsNullOrWhiteSpace(model.WarningMessage), "Only a fresh healthy state clears the resolved lifecycle warning");
    }

    private static async Task RunStatusInlineCases(DeviceWorkspaceViewModel model, DeviceWorkspaceClient client,
        Window window, string output, Action<bool, string> check)
    {
        var snapshot = client.Snapshot!; var targets = model.Lights.Take(2).Select(l => l.Target).ToArray();
        check(targets.Length == 2, "Two independent DEMO lights are available for mixed-selection presentation");
        model.SelectedPage = 2; model.SelectLights(targets);
        var mixed = snapshot.Configuration.Configuration.Lighting with
        { Preferences = [new(targets[0], new("fixed", [new(200, 20, 30)])), new(targets[1], new("fixed", [new(20, 30, 200)]))] };
        model.LightingDraft.Update(snapshot.State, snapshot.Configuration.Generation, mixed, 0, true);
        typeof(DeviceWorkspaceViewModel).GetMethod("PublishLightingEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, null);
        model.ReportStatus("照明を選択しました", hint: true); await Settle();
        check(model.LightingDraft.Mixed && string.IsNullOrWhiteSpace(model.WarningMessage) &&
            Descendants<TextBlock>(window).Any(t => t.IsVisible && t.Text == model.LightEditStatus),
            "Mixed lighting remains inline guidance rather than an upper warning");
        var row = model.Lights.Single(l => l.Target == targets[0]);
        row.Update(row.Name, "終了処理待ち", ready: false, needsRecovery: true);
        typeof(DeviceWorkspaceViewModel).GetMethod("PublishLightingEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, null);
        await Settle();
        check(model.LightRecoveryRequired && Descendants<TextBlock>(window).Any(t => t.IsVisible && t.Text == model.LightRecoveryMessage),
            "Lighting recovery instructions remain beside the affected controls");
        model.Render(); model.SelectedPage = 3; model.SetupExpanded = true; await Settle();
        check(!string.IsNullOrWhiteSpace(model.SetupUsage) && Descendants<TextBlock>(window).Any(t => t.Text == model.SetupUsage),
            "Endpoint input and capacity guidance remains in the settings form");
        var backup = Path.Combine(output, "status-preview.flowdeck.json");
        await model.ExportSettingsToAsync(backup); await model.LoadImportAsync(backup); await Settle();
        check(model.HasImport && !model.ImportConfirmed && !model.CanApplyImport && !string.IsNullOrWhiteSpace(model.ImportStatus),
            "Loading a restore preview retains its inline confirmation requirement");
        var inline = model.ImportStatus; model.ReportResult("表示の確認は完了しました", Outcome.Confirmed);
        check(model.ImportStatus == inline && !model.ImportConfirmed && !model.CanApplyImport,
            "A status success cannot dismiss restoration warnings or authorize restoration");
        model.ClearImport.Execute(null); model.Render();
    }

    private static T StatusElement<T>(DependencyObject root, string id) where T : FrameworkElement =>
        StatusElements(root).OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);
    private static IEnumerable<DependencyObject> StatusElements(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>(); var pending = new Stack<DependencyObject>(); pending.Push(root);
        while (pending.TryPop(out var item))
        {
            if (!seen.Add(item)) continue; yield return item;
            if (item is Visual)
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++) pending.Push(VisualTreeHelper.GetChild(item, i));
            foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>()) pending.Push(child);
            if (item is Popup { Child: { } popupChild }) pending.Push(popupChild);
        }
    }
}
