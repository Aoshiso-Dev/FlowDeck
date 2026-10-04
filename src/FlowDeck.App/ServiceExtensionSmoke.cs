using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    private static async Task<RemoteControlCoordinator> RunServiceExtensionChecksAsync(RuntimeHost host, FlowDeckViewModel vm,
        FlowDeckWindow window, string output, Action<bool, string> check)
    {
        var remote = (RemoteControlCoordinator)host.Coordinator;
        var fanRevision = remote.Snapshot.ControlRevision;
        var hubId = remote.Settings.Hardware.Hub!.Id;
        var board = host.Rgb.Devices.Single(d => d.ControlIdentity is not null);
        var boardId = board.ControlIdentity!;
        var color = new RgbColor(40, 100, 200);
        var effect = new HubLightSetting("fixed", [color]);
        var baseline = new LightingScene(Guid.NewGuid().ToString("N"), "サービス試験の基準",
            remote.Settings.Hardware.Leds!.Select(c => new SceneHubLight(hubId, c, effect)).ToArray(),
            board.Zones.Where(z => z.HardwareZone is not null && z.LedCount > 0)
                .Select(z => new SceneBoardLight(boardId, z.HardwareZone!.Value, new(1, color.R, color.G, color.B))).ToArray());
        var applied = await host.ApplyLightingSceneAsync(baseline);
        if (!applied.Succeeded) throw new IOException("Service scene baseline: " + applied.Error);
        await Settle(); vm.SelectedPage = 2; window.PageScroll.ScrollToTop();
        vm.NewLightingGroup.Execute(null); vm.GroupManagementOpen = true; await Settle();
        await CheckDraftInputAsync(window.LightingGroupNameBox, vm, "サービスの照明", () => vm.LightingGroupName, check);
        foreach (var target in vm.GroupTargetChoices) target.Selected = target.Hub?.Channel == "led1" || target.Board?.Zone == 2;
        vm.SaveLightingGroup.Execute(null); await Until(() => vm.LightingScenesIdle && vm.CurrentLightingGroup?.Name == "サービスの照明");
        var group = vm.CurrentLightingGroup!;
        check(group.Hub.Length == 1 && group.Board.Length == 1, "Service lighting editors save a mixed physical group");
        vm.SceneIncludeHub = vm.SceneIncludeBoard = true; vm.SceneManagementOpen = true; await Settle();
        await CheckDraftInputAsync(window.SceneNameBox, vm, "サービスの青", () => vm.LightingSceneName, check);
        vm.SaveLightingScene.Execute(null); await Until(() => vm.LightingScenesIdle && vm.SelectedLightingScene?.Name == "サービスの青");
        var scene = vm.SelectedLightingScene!;
        check(scene.Hub.Single().HubId == hubId && scene.Board.Single().Identity == boardId && scene.Board[0].Zone == 2,
            "Service scene capture retains exact Hub and motherboard identities");
        vm.ReloadLightingScenes.Execute(null); await Until(() => vm.LightingScenesIdle);
        vm.SelectedLightingScene = vm.LightingSceneChoices.Single(s => s.Id == scene.Id);
        check((await new LightingSceneStore(host.Store.DirectoryPath).LoadAsync()).Scenes!.Any(s => s.Id == scene.Id), "Scenes reload from user storage with a service backend");
        var otherHub = SettingsTransfer.Hash(remote.Settings.LightingPorts.Where(p => p.Key != "led1").ToDictionary());
        var otherBoard = host.Rgb.Devices.Single().Zones.Where(z => z.HardwareZone != 2).Select(z => z.Configuration).ToArray();
        vm.MasterBrightness = 50; vm.ApplyBrightness.Execute(null);
        await Until(() => vm.LightingScenesIdle && host.AdjustmentFor(group) is { Applied: true, Brightness: 50 });
        check(remote.Settings.LightingPorts["led1"].Effect!.Colors[0] == color.Scale(.5) &&
            host.Rgb.Devices.Single().Zones.Single(z => z.HardwareZone == 2).Configuration == new BoardLightSetting(1, 20, 50, 100),
            "The brightness slider dims the selected service Hub port and motherboard zone together");
        vm.TurnAllLightsOff.Execute(null); await Until(() => vm.LightingScenesIdle && host.AdjustmentFor(group).IsOff);
        vm.RestoreLighting.Execute(null); await Until(() => vm.LightingScenesIdle && host.AdjustmentFor(group) is { Applied: true, IsOff: false });
        check(remote.Settings.LightingPorts["led1"].Effect!.Colors[0] == color.Scale(.5) &&
            SettingsTransfer.Hash(remote.Settings.LightingPorts.Where(p => p.Key != "led1").ToDictionary()) == otherHub &&
            host.Rgb.Devices.Single().Zones.Where(z => z.HardwareZone != 2).Select(z => z.Configuration).SequenceEqual(otherBoard),
            "Service group off and restore preserve other Hub ports and motherboard zones");
        vm.ApplyLightingScene.Execute(null); await Until(() => vm.LightingScenesIdle && vm.LightingSceneResult.StartsWith("適用済み"));
        check(remote.Settings.LightingPorts["led1"].Effect!.Colors[0] == color, "Recalling a saved scene applies its original color through IPC");
        vm.IdentificationTarget = vm.IdentificationTargets.Single(t => t.Address == new LightAddress(Hub: new(hubId, "led1")));
        window.LightingIdentificationExpander.IsExpanded = true; await Settle();
        await CheckDraftInputAsync(window.LightAliasBox, vm, "サービス前面", () => vm.LightAlias, check);
        vm.SaveLightAlias.Execute(null); await Until(() => vm.LightingScenesIdle && vm.IdentificationResult.Contains("保存しました"));
        vm.IdentifyLight.Execute(null);
        await Until(() => host.IdentifyingLight && remote.Settings.LightingPorts["led1"].Effect!.Colors[0] == new RgbColor(96, 96, 96));
        check(!vm.ApplyBrightness.CanExecute(null) && vm.CancelIdentification.CanExecute(null), "Service identification blocks conflicting edits while the temporary color is active");
        vm.CancelIdentification.Execute(null); await Until(() => !vm.IdentificationBusy);
        check(remote.Settings.LightingPorts["led1"].Effect!.Colors[0] == color &&
            LightingNames.Find((await new LightingNameStore(host.Store.DirectoryPath).LoadAsync()), new(Hub: new(hubId, "led1"))) == "サービス前面",
            "Cancelled identification restores the service port and preserves its saved name");
        var peer = new LightingPeer(host); peer.Start(); var stamp = host.Read(group.Id).Stamp;
        var result = await peer.Apply("SetLightingBrightness", group.Id, brightness: 25);
        await Until(() => vm.MasterBrightness == 25);
        check(result.GetProperty("outcome").GetString() == "writtenUnverified" && remote.Settings.LightingPorts["led1"].Effect!.Colors[0] == color.Scale(.25),
            "M5 Wi-Fi lighting commands reach the service and update the PC brightness slider");
        result = await peer.Apply("LightingOff", group.Id, stamp: stamp);
        check(result.GetProperty("error").GetString() == "STALE_LIGHTING" && !host.AdjustmentFor(group).IsOff,
            "Stale M5 lighting edits are rejected with the service backend");
        peer.Hello("usb"); await peer.Apply("LightingOff", group.Id); await peer.Apply("RestoreLighting", group.Id);
        check(peer.Router.ActiveRoute == "usb" && host.AdjustmentFor(group) is { Brightness: 25, IsOff: false } &&
            remote.Settings.LightingPorts["led1"].Effect!.Colors[0] == color.Scale(.25), "M5 USB restores the same service lighting group after route switching");
        check(remote.Snapshot.ControlRevision == fanRevision && remote.Snapshot.ConfirmedDuty == 59,
            "Service scenes brightness groups identification and M5 lighting do not change fan control");
        Capture(window, output, "service-lighting-extensions");

        var original = Contract.Copy(remote.Settings.Profiles.Single(p => p.Id == "balanced"));
        vm.SelectedPage = 1; vm.CurveProfile = vm.Profiles.Single(p => p.Id == original.Id); vm.ReloadCurve.Execute(null);
        vm.UseCpu = true; vm.UseGpu = false; vm.CpuPoints.Clear();
        vm.CpuPoints.Add(new(30, 30)); vm.CpuPoints.Add(new(50, 50)); vm.CpuPoints.Add(new(80, 90));
        vm.CpuOverheat = "95"; vm.CpuResume = "85"; vm.DescentDelay = "0"; vm.DescentRate = "5"; vm.Refresh(); await Settle();
        var chart = window.HubCpuCurve;
        check(chart.BeginEdit(1), "The graphical fan curve remains editable with a service backend");
        chart.DragTo(chart.Position(60, 65)); chart.CommitEdit();
        var points = vm.CpuPoints.Select(p => new CurvePoint(p.Temperature, p.Duty)).ToArray();
        vm.SaveCurve.Execute(null); await Until(() => remote.Settings.Profiles.Single(p => p.Id == original.Id).Revision > original.Revision);
        check(remote.Settings.Profiles.Single(p => p.Id == original.Id).Cpu!.Points.SequenceEqual(points) && remote.Snapshot.ConfirmedDuty == 59,
            "Saving the dragged curve updates service settings while manual fan output remains unchanged");
        vm.SaveCurve.Execute("apply"); await Until(() => remote.Snapshot.Mode == FanMode.Auto && !remote.Snapshot.FanBusy);
        check(remote.Snapshot.LastConfirmed!.Profile!.Cpu!.Points.SequenceEqual(points), "Explicit curve application runs the saved profile in the service");
        await remote.RequestAsync(new(Guid.NewGuid().ToString("N"), "service-smoke", remote.Epoch, remote.Snapshot.ControlRevision, "SetManualDuty", Duty: 59));
        await remote.SaveProfileAsync(original, remote.Settings.Profiles.Single(p => p.Id == original.Id).Revision);
        vm.ReloadCurve.Execute(null); await Settle();

        vm.SelectedPage = 9; vm.NotificationsDesktop = false; vm.NotificationDelay = vm.NotificationRecoveryDelay = vm.NotificationRepeat = 0;
        vm.SaveNotificationSettings.Execute(null); await Until(() => vm.NotificationIdle && !host.NotificationOptions.Desktop);
        vm.NewNotificationRule.Execute(null);
        vm.NotificationTarget = vm.NotificationTargets.Single(t => t.Address.Source == "sensor" && t.Address.SensorId == remote.Settings.CpuSensorId);
        await Settle();
        await CheckDraftInputAsync(window.NotificationsPanel.NotificationNameBox, vm, "サービス CPU", () => vm.NotificationName, check);
        vm.NotificationThreshold = 55; vm.NotificationHysteresis = 3;
        vm.SaveNotificationRule.Execute(null); await Until(() => vm.NotificationIdle && host.NotificationOptions.Rules!.Length == 1);
        var rule = host.NotificationOptions.Rules!.Single(); host.EvaluateNotifications();
        await Until(() => host.ActiveNotifications.Any(e => e.Key == rule.Id + ":limit") &&
            vm.NotificationHistory.Any(e => e.Event.Key == rule.Id + ":limit" && e.Kind == "警告"));
        check(host.ActiveNotifications.Any(e => e.Key == rule.Id + ":limit") && vm.NotificationHistory.Any(e => e.Kind == "警告"),
            "Service temperature readings drive user notification rules and history");
        var count = host.NotificationHistory.Count(e => e.Key == rule.Id + ":limit" && e.Kind == "raised");
        for (var i = 0; i < 3; i++) host.EvaluateNotifications();
        check(host.NotificationHistory.Count(e => e.Key == rule.Id + ":limit" && e.Kind == "raised") == count && remote.Snapshot.ConfirmedDuty == 59,
            "Repeated service readings do not duplicate notifications or change cooling");

        vm.SelectedPage = 10; var export = Path.Combine(output, "service-export.flowdeck.json");
        await vm.ExportSettingsFileAsync(export); var archive = SettingsTransfer.Read(await File.ReadAllBytesAsync(export));
        check(archive.Content.Scenes.Scenes!.Any(s => s.Id == scene.Id) && archive.Content.Groups.Groups!.Any(g => g.Id == group.Id) &&
            archive.Content.Notifications.Rules!.Length == 1 && !archive.Content.Settings.ServiceConfigurationPending,
            "Service mode exports cooling settings together with local lighting and notification libraries");
        var edited = SettingsTransfer.Create(archive.Content with { Settings = archive.Content.Settings with
            { Limits = archive.Content.Settings.Limits with { Exit = 78 }, Ui = new("dark") }, Notifications = archive.Content.Notifications with { DelaySeconds = 17 } });
        var restore = Path.Combine(output, "service-restore.flowdeck.json");
        await File.WriteAllBytesAsync(restore, JsonSerializer.SerializeToUtf8Bytes(edited, SettingsTransfer.Indented));
        await vm.PreviewSettingsFileAsync(restore); var before = remote.Snapshot.ControlRevision;
        check(vm.SettingsHasPreview && vm.SettingsDifferences.Count > 0 && remote.Settings.Limits.Exit == 70,
            "Service restore preview shows changes without replacing the active service settings");
        vm.ScheduleSettingsRestore.Execute(null); await Until(() => vm.SettingsTransferIdle && vm.SettingsHasPending);
        check(remote.Snapshot.ControlRevision == before && remote.Snapshot.ConfirmedDuty == 59, "Queuing a service restore leaves current control running");
        vm.CancelSettingsRestore.Execute(null); await Until(() => vm.SettingsTransferIdle && !vm.SettingsHasPending);
        await vm.PreviewSettingsFileAsync(restore); vm.ScheduleSettingsRestore.Execute(null); await Until(() => vm.SettingsHasPending && vm.SettingsTransferIdle);
        check(await host.SwitchAsync(true), "A service RuntimeHost can shut down normally and reinitialize for a queued restore");
        remote = (RemoteControlCoordinator)host.Coordinator; await Until(() => remote.Connected && !remote.ConfigurationPending); await host.FlushSettingsAsync();
        check(remote.Settings.Limits.Exit == 78 && remote.Settings.Ui.Theme == "dark" && host.NotificationOptions.DelaySeconds == 17 && !host.SettingsRestorePending,
            "Next startup applies the reviewed service configuration and local preferences together");
        check(!remote.Snapshot.Managing && remote.Snapshot.Mode == FanMode.Monitor && !remote.Settings.Hardware.WiringConfirmed &&
            remote.BoardFans.States.Values.All(s => !s.Managing) && remote.LastStatus.Channels.All(c => c.Duty == 70),
            "Restored service settings start monitoring and do not apply the newly imported exit output");
        vm.SelectedPage = 10; await Settle(); Capture(window, output, "service-settings-restored");
        return remote;
    }
}
