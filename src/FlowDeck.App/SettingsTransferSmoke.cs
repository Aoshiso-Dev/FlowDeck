using System.Text.Json;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    private static async Task RunSettingsTransferChecksAsync(RuntimeHost host, FlowDeckViewModel vm, FlowDeckWindow window, string output, Action<bool, string> check)
    {
        var core = host.Coordinator; var snapshot = core.Snapshot; var writes = ((DemoHub)host.Hub).Writes; var rgb = SettingsTransfer.Hash(host.Rgb);
        vm.SelectedPage = 10; window.PageScroll.ScrollToTop(); window.Width = 1200; window.Height = 1000; await Settle();
        check(window.SettingsTransferPanel.IsVisible && vm.PageTitle == "設定の書き出し・復元", "Settings transfer has a dedicated page under Settings");
        var export = Path.Combine(output, "export.flowdeck.json"); await vm.ExportSettingsFileAsync(export);
        check(File.Exists(export) && vm.SettingsTransferResult.Contains("書き出し"), "Export captures the complete persisted settings without a hardware operation");
        var archive = SettingsTransfer.Read(await File.ReadAllBytesAsync(export));
        check(archive.Content.Settings.LastConfirmedPolicy is null && !archive.Content.Settings.Hardware.WiringConfirmed && archive.Content.Settings.PythonPath is null,
            "Portable data excludes runtime confirmation, fan wiring acknowledgement and executable paths");
        check(archive.Content.Scenes.Scenes!.Length == host.SceneLibrary.Scenes!.Length && archive.Content.Groups.Groups!.Length == host.GroupLibrary.Groups!.Length &&
            archive.Content.Names.Names!.Length == host.LightNames.Names!.Length, "Scenes, groups and physical lighting names are included in export");
        var edited = SettingsTransfer.Create(archive.Content with { Settings = archive.Content.Settings with { Ui = archive.Content.Settings.Ui with { Theme = "dark" } },
            Notifications = archive.Content.Notifications with { DelaySeconds = 17 }, Palette = new(["#55BBFF", "#AA77FF"], ["#00CCAA"]) });
        var source = Path.Combine(output, "restore.flowdeck.json"); await File.WriteAllBytesAsync(source, JsonSerializer.SerializeToUtf8Bytes(edited, SettingsTransfer.Indented));
        await vm.PreviewSettingsFileAsync(source); await Settle();
        check(vm.SettingsHasPreview && vm.SettingsDifferences.Any(d => d.After == "17") && window.SettingsTransferPanel.DifferenceGrid.Items.Count > 1,
            "Before/after differences include notification and palette changes");
        check(vm.SettingsDeviceChecks.Any(s => s.Contains("機種名は一致")) && vm.SettingsDeviceChecks.Any(s => s.Contains("配線確認を解除")),
            "Review distinguishes matching model names from physical identity and shows fan revalidation");
        check(host.NotificationOptions.DelaySeconds != 17 && core.Settings.Ui.Theme != "dark", "Preview has not applied imported preferences");
        ThemeManager.Apply("light"); await Settle(); Capture(window, output, "settings-transfer-light");
        ThemeManager.Apply("dark"); await Settle(); Capture(window, output, "settings-transfer-dark");
        window.Width = 960; window.Height = 640; await Settle();
        check(window.SettingsTransferPanel.PreviewCard.ActualWidth < window.ActualWidth - 150 && window.SettingsTransferPanel.DifferenceGrid.ActualWidth > 600,
            "The review grid fits the minimum window width");
        Capture(window, output, "settings-transfer-minimum");
        var button = new ButtonAutomationPeer(window.SettingsTransferPanel.RestoreButton);
        ((IInvokeProvider)button.GetPattern(PatternInterface.Invoke)).Invoke(); await Until(() => vm.SettingsTransferIdle && vm.SettingsHasPending);
        check(!vm.SettingsHasPreview && !vm.ScheduleSettingsRestore.CanExecute(null), "The restore button queues exactly one reviewed snapshot");
        check(core.Snapshot.ControlRevision == snapshot.ControlRevision && core.Snapshot.LightingRevision == snapshot.LightingRevision && ((DemoHub)host.Hub).Writes == writes &&
            SettingsTransfer.Hash(host.Rgb) == rgb, "Export, review and queue leave all current fan and RGB outputs unchanged");
        vm.CancelSettingsRestore.Execute(null); await Until(() => vm.SettingsTransferIdle && !vm.SettingsHasPending);
        check(vm.SettingsTransferResult.Contains("取り消し"), "A queued restore can be cancelled without restarting");
        await File.WriteAllTextAsync(source, "{invalid"); await vm.PreviewSettingsFileAsync(source);
        check(!vm.SettingsHasPreview && !vm.ScheduleSettingsRestore.CanExecute(null), "An invalid input clears the previous preview and cannot be scheduled");
        // Isolated real RuntimeHost lifecycle, using demo adapters only.
        var isolatedRoot = Path.Combine(output, "restore-lifecycle");
        await using (var first = new RuntimeHost(isolatedRoot, demoOnly: true))
        {
            await first.InitializeAsync(true); await first.FlushSettingsAsync();
            var path = Path.Combine(output, "restart.flowdeck.json"); var saved = await first.ExportSettingsAsync(path);
            var replacement = SettingsTransfer.Create(saved.Content with { Settings = saved.Content.Settings with { Ui = new("dark") }, Notifications = new(DelaySeconds: 17, Rules: []) });
            await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(replacement, SettingsTransfer.Indented));
            await first.ScheduleSettingsAsync(await first.PreviewSettingsAsync(path));
            check(first.Coordinator.Snapshot.Managing && first.SettingsRestorePending, "Queued restoration permits the existing cooling loop to continue until normal exit");
        }
        await using (var second = new RuntimeHost(isolatedRoot, demoOnly: true))
        {
            await second.InitializeAsync(true); await Task.Delay(200);
            check(!second.Coordinator.Snapshot.Managing && second.Coordinator.Snapshot.Mode == FanMode.Monitor && ((DemoHub)second.Hub).Writes == 0,
                "The next startup restores before runtime creation and starts monitoring with zero fan writes");
            check(second.Coordinator.Settings.Ui.Theme == "dark" && second.NotificationOptions.DelaySeconds == 17 && !second.SettingsRestorePending,
                "Restored preferences and notifications are loaded together on the next startup");
            check(!second.Coordinator.Settings.Hardware.WiringConfirmed && second.BoardFans.States.Values.All(s => !s.Managing) && second.SettingsRestoreMessage.Contains("復元"),
                "Restored fan setup requires explicit revalidation and leaves every motherboard fan under firmware control");
            check(Directory.EnumerateFiles(Path.Combine(second.Store.DirectoryPath, "settings-transfer"), "before.flowdeck.json", SearchOption.AllDirectories).Any(),
                "Startup leaves a usable pre-restore settings backup");
        }
        window.Width = 1200; window.Height = 1000; ThemeManager.Apply("light"); vm.SelectedPage = 7; vm.RevertFan.Execute(null); await Settle();
    }
}
