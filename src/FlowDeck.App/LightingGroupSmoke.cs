using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    private static async Task RunLightingGroupChecksAsync(RuntimeHost host, FlowDeckViewModel vm, FlowDeckWindow window, string output, Action<bool, string> check)
    {
        var fanRevision = host.Coordinator.Snapshot.ControlRevision;
        var lightRevision = host.Coordinator.Snapshot.LightingRevision;
        vm.NewLightingGroup.Execute(null); vm.LightingGroupName = "ケース";
        foreach (var choice in vm.GroupTargetChoices) choice.Selected = choice.Hub?.Channel == "led1" || choice.Board?.Zone == 2;
        vm.SaveLightingGroup.Execute(null); await Until(() => vm.LightingScenesIdle && vm.CurrentLightingGroup?.Name == "ケース");
        var caseGroup = vm.CurrentLightingGroup!;
        check(caseGroup.Hub.Length == 1 && caseGroup.Board.Single().Zone == 2 && vm.BoardLightTargets.All(t => t.Zones.All(z => z.HardwareZone == 2)) && vm.SyncBoardChoices.Count == 1,
            "Mixed group creation limits both native editors and synchronization choices to its members");
        check(host.Coordinator.Snapshot.LightingRevision == lightRevision && host.Coordinator.Snapshot.ControlRevision == fanRevision,
            "Creating a lighting group changes neither lighting output nor cooling policy");
        vm.ToggleGroupManagement.Execute(null); await Settle();
        check(window.LightingGroupManagement.IsVisible && window.LightingGroupPicker.IsVisible,
            "The compact group management button opens its editor without hiding the scope selector");
        vm.ToggleGroupManagement.Execute(null); await Settle();
        vm.NewLightingGroup.Execute(null); vm.LightingGroupName = "マザー";
        foreach (var choice in vm.GroupTargetChoices) choice.Selected = choice.Board?.Zone is 5 or 6;
        vm.SaveLightingGroup.Execute(null); await Until(() => vm.LightingScenesIdle && vm.CurrentLightingGroup?.Name == "マザー");
        var boardGroup = vm.CurrentLightingGroup!;
        vm.ReloadLightingGroups.Execute(null); await Until(() => vm.LightingScenesIdle);
        var loaded = await new LightingGroupStore(host.Store.DirectoryPath).LoadAsync();
        check(loaded.Groups is { Length: 2 } && loaded.Groups[0].Hub.SequenceEqual(caseGroup.Hub) && loaded.Groups[1].Board.SequenceEqual(boardGroup.Board),
            "Lighting group identities and memberships survive disk reload");
        vm.SelectedLightingGroup = vm.LightingGroupChoices.Single(g => g.Group?.Id == caseGroup.Id);
        vm.RevertHubLight.Execute(null); vm.HubLightSync = false; vm.HubLightMode = "fixed"; vm.SelectHubLightColor.Execute("blue");
        vm.ApplyHubLight.Execute(null); await Until(() => vm.HubLightIdle && host.Coordinator.Snapshot.Lighting.Effect?.Mode == "fixed");
        vm.BoardLightEffect = 1; vm.SelectBoardLightColor.Execute("purple"); vm.ApplyBoardLight.Execute(null);
        await Until(() => vm.BoardLightIdle && vm.BoardLightResult.Contains("設定一致"));
        var otherZones = host.Rgb.Devices.Single().Zones.Where(z => z.HardwareZone is 5 or 6).ToDictionary(z => z.HardwareZone!.Value, z => z.Configuration);
        var caseScene = await host.CaptureLightingSceneAsync(Guid.NewGuid().ToString("N"), "ケース", true, true, caseGroup);
        vm.MasterBrightness = 50; vm.ApplyBrightness.Execute(null); await Until(() => vm.LightingScenesIdle && vm.BrightnessResult.Contains("50% · 適用済み"));
        check(host.Coordinator.Settings.LightingPorts["led1"].Effect!.Colors[0] == new RgbColor(52,137,255).Scale(.5) &&
            host.Rgb.Devices.Single().Zones.Where(z => z.HardwareZone is 5 or 6).All(z => z.Configuration == otherZones[z.HardwareZone!.Value]),
            "Mixed group dimming changes its Hub port and motherboard zone while leaving other zones unchanged");
        vm.SelectedLightingGroup = vm.LightingGroupChoices.Single(g => g.Group?.Id == boardGroup.Id);
        var hubBeforeBoard = host.Coordinator.Snapshot.LightingRevision;
        vm.MasterBrightness = 25; vm.ApplyBrightness.Execute(null); await Until(() => vm.LightingScenesIdle && vm.BrightnessResult.Contains("25% · 適用済み"));
        check(host.Coordinator.Snapshot.LightingRevision == hubBeforeBoard && !vm.CanApplyHubLight && host.AdjustmentFor(caseGroup).Applied,
            "A motherboard-only group never sends to NZXT and preserves another group's brightness baseline");
        var dimmedBoard = host.Rgb.Devices.Single().Zones.Where(z => z.HardwareZone is 5 or 6).ToDictionary(z => z.HardwareZone!.Value, z => z.Configuration);
        vm.SelectedLightingGroup = vm.LightingGroupChoices.Single(g => g.Group?.Id == caseGroup.Id);
        check(vm.MasterBrightness == 50, "Changing groups loads that group's brightness without sending any settings");
        vm.TurnAllLightsOff.Execute(null); await Until(() => vm.LightingScenesIdle && vm.BrightnessResult == "消灯済み");
        vm.RestoreLighting.Execute(null); await Until(() => vm.LightingScenesIdle && vm.BrightnessResult.Contains("50% · 適用済み"));
        check(host.Rgb.Devices.Single().Zones.Where(z => z.HardwareZone is 5 or 6).All(z => z.Configuration == dimmedBoard[z.HardwareZone!.Value]),
            "Group all-off and restoration leave a separately dimmed group unchanged");
        foreach (var choice in vm.GroupTargetChoices) if (choice.Board is not null) choice.Selected = choice.Board.Zone == 5;
        vm.SaveLightingGroup.Execute(null); await Until(() => vm.LightingScenesIdle && vm.CurrentLightingGroup!.Board.Single().Zone == 5);
        check(!vm.RestoreLighting.CanExecute(null), "Changing group membership prevents restoration into the previous members");
        foreach (var choice in vm.GroupTargetChoices) if (choice.Board is not null) choice.Selected = choice.Board.Zone == 2;
        vm.SaveLightingGroup.Execute(null); await Until(() => vm.LightingScenesIdle && vm.CurrentLightingGroup!.Board.Single().Zone == 2);
        caseGroup = vm.CurrentLightingGroup!;
        vm.LightingSceneName = "ケースのみ"; vm.SaveLightingScene.Execute(null);
        await Until(() => vm.LightingScenesIdle && vm.SelectedLightingScene?.Name == "ケースのみ");
        check(vm.SelectedLightingScene!.Hub.Length == 1 && vm.SelectedLightingScene.Board.Length == 1 && vm.SelectedLightingScene.Board[0].Zone == 2,
            "Scene capture in a group saves only that group's physical targets");
        vm.SelectedLightingScene = vm.LightingSceneChoices.Single(s => s.Name == "夜のデスク");
        vm.ApplyLightingScene.Execute(null); await Until(() => vm.LightingScenesIdle && vm.LightingSceneResult.StartsWith("適用済み"));
        check(host.Coordinator.Snapshot.Lighting.Effect?.Mode == "super-rainbow" && host.Rgb.Devices.Single().Zones.Where(z => z.HardwareZone is 5 or 6).All(z => z.Configuration == dimmedBoard[z.HardwareZone!.Value]),
            "Recalling a larger native scene through a group applies only the intersection of targets");
        vm.RevertHubLight.Execute(null); vm.HubLightSync = true; vm.HubLightMode = "breathing"; vm.HubLightSpeed = 3; vm.SelectHubLightColor.Execute("blue");
        vm.ApplyHubLight.Execute(null); await Until(() => host.LightingSynchronized && vm.HubLightIdle);
        check(host.ActiveLightingSync!.Zones.SequenceEqual(new byte[]{2}) && host.Rgb.Devices.Single().Zones.Where(z => z.HardwareZone is 5 or 6).All(z => z.Configuration == dimmedBoard[z.HardwareZone!.Value]),
            "Group synchronization streams only to explicitly selected motherboard zones");
        check((await new LightingGroupStore(host.Store.DirectoryPath).LoadAsync()).SyncTargets!.SequenceEqual(new[]{new BoardLightAddress("demo-asrock",2)}),
            "Explicit motherboard synchronization selection persists across restart");
        vm.LightingSceneName = "ケースの呼吸"; vm.SaveLightingScene.Execute(null);
        await Until(() => vm.LightingScenesIdle && vm.SelectedLightingScene?.Name == "ケースの呼吸");
        check(vm.SelectedLightingScene!.Sync!.Zones.SequenceEqual(new byte[]{2}), "A group scene preserves the synchronization subset");
        var active = host.ActiveLightingSync; var frames = host.LightingFrames;
        vm.SelectedLightingGroup = vm.LightingGroupChoices.Single(g => g.Group?.Id == boardGroup.Id);
        vm.MasterBrightness = 30; vm.ApplyBrightness.Execute(null);
        await Until(() => vm.LightingScenesIdle && vm.BrightnessResult.Contains("グループ外")); await Task.Delay(250);
        check(host.LightingSynchronized && host.ActiveLightingSync == active && host.LightingFrames > frames &&
            host.Rgb.Devices.Single().Zones.Where(z => z.HardwareZone is 5 or 6).All(z => z.Configuration == dimmedBoard[z.HardwareZone!.Value]),
            "An incompatible group action is rejected before stopping animation or modifying unselected lights");
        vm.StopLightingSync.Execute(null); await Until(() => !host.LightingSynchronized && vm.HubLightIdle);
        check(host.AdjustmentFor(boardGroup).Applied && vm.MasterBrightness == 25, "Stopping another group's synchronization preserves independent dimming");
        vm.SelectedLightingGroup = vm.LightingGroupChoices.Single(g => g.Group?.Id == caseGroup.Id);
        vm.SelectedLightingScene = vm.LightingSceneChoices.Single(s => s.Name == "ケースの呼吸"); vm.ApplyLightingScene.Execute(null);
        await Until(() => vm.LightingScenesIdle && host.LightingSynchronized);
        check(host.ActiveLightingSync!.Zones.SequenceEqual(new byte[]{2}), "Recalling a synchronized group scene retains its zone selection");
        window.Width = 1200; window.Height = 1000; vm.GroupManagementOpen = true; window.SyncTargetsExpander.IsExpanded = true;
        window.PageScroll.ScrollToTop(); ThemeManager.Apply("dark"); await Settle(); Capture(window, output, "lighting-groups-dark");
        ThemeManager.Apply("light"); await Settle(); Capture(window, output, "lighting-groups-light");
        vm.GroupManagementOpen = false; window.SyncTargetsExpander.IsExpanded = false; window.Height = 900;
        ThemeManager.Apply("dark"); await Settle(); Capture(window, output, "lighting-groups-color");
        ThemeManager.Apply("light"); window.Width = 960; window.Height = 640;
        await Settle(); check(window.LightingGroupsCard.ActualWidth < window.ActualWidth - 150 && window.MasterBrightnessSlider.ActualWidth > 100,
            "Group selection and scoped brightness fit the minimum window width"); Capture(window, output, "lighting-groups-minimum");
        vm.StopLightingSync.Execute(null); await Until(() => !host.LightingSynchronized && vm.HubLightIdle);
        var missingGroup = new LightingGroup(Guid.NewGuid().ToString("N"), "未接続グループ", [new("missing-hub","led2")], [new("demo-asrock",2)]);
        await host.SaveLightingGroupAsync(missingGroup); vm.ReloadLightingGroups.Execute(null); await Until(() => vm.LightingScenesIdle && vm.LightingGroupChoices.Count == 4);
        vm.SelectedLightingGroup = vm.LightingGroupChoices.Single(g => g.Group?.Id == missingGroup.Id);
        var unchangedRevision = host.Coordinator.Snapshot.LightingRevision; var unchangedRgb = host.Rgb;
        vm.TurnAllLightsOff.Execute(null); await Until(() => vm.LightingScenesIdle && vm.BrightnessResult.Contains("NZXT"));
        check(vm.LightingGroupSummary.Contains("未検出 1") && host.Coordinator.Snapshot.LightingRevision == unchangedRevision && ReferenceEquals(host.Rgb, unchangedRgb),
            "Missing group hardware remains visible and rejects writes without falling back to all devices");
        vm.DeleteLightingGroup.Execute(null); await Until(() => vm.LightingScenesIdle && vm.LightingGroupChoices.Count == 3);
        vm.SelectedLightingGroup = vm.LightingGroupChoices.Single(g => g.Group?.Id == caseGroup.Id); vm.DeleteLightingGroup.Execute(null);
        await Until(() => vm.LightingScenesIdle && vm.LightingGroupChoices.Count == 2); vm.UndoDeleteLightingGroup.Execute(null);
        await Until(() => vm.LightingScenesIdle && vm.LightingGroupChoices.Count == 3);
        check(host.Coordinator.Snapshot.LightingRevision == unchangedRevision && vm.CurrentLightingGroup?.Id == caseGroup.Id,
            "Group deletion and undo preserve device state and stable group identity");
        var groupPath = new LightingGroupStore(host.Store.DirectoryPath).PathName; var validGroups = await File.ReadAllTextAsync(groupPath);
        await File.WriteAllTextAsync(groupPath, "{corrupt"); vm.ReloadLightingGroups.Execute(null);
        await Until(() => vm.LightingScenesIdle && host.GroupLibraryError is not null);
        var refused = false; try { await host.AdjustLightingAsync(off: true); } catch (IOException) { refused = true; }
        check(refused && !vm.TurnAllLightsOff.CanExecute(null) && !vm.CanApplyHubLight && host.Coordinator.Snapshot.LightingRevision == unchangedRevision,
            "Corrupt group storage cannot silently turn a scoped action into an all-device write");
        await File.WriteAllTextAsync(groupPath, validGroups); vm.ReloadLightingGroups.Execute(null);
        await Until(() => vm.LightingScenesIdle && host.GroupLibraryError is null);
        vm.SelectedLightingGroup = vm.LightingGroupChoices.Single(g => g.Group?.Id == boardGroup.Id);
        var caseBeforeReload = host.Rgb.Devices.Single().Zones.Single(z => z.HardwareZone == 2).Configuration;
        vm.TurnAllLightsOff.Execute(null); await Until(() => vm.LightingScenesIdle && vm.BrightnessResult == "消灯済み");
        vm.ReloadLightingGroups.Execute(null); await Until(() => vm.LightingScenesIdle && host.AdjustmentFor(boardGroup).IsOff);
        vm.RestoreLighting.Execute(null); await Until(() => vm.LightingScenesIdle && vm.BrightnessResult.Contains("25% · 適用済み"));
        check(host.Rgb.Devices.Single().Zones.Where(z => z.HardwareZone is 5 or 6).All(z => z.Configuration == dimmedBoard[z.HardwareZone!.Value]) &&
            host.Rgb.Devices.Single().Zones.Single(z => z.HardwareZone == 2).Configuration == caseBeforeReload,
            "Reloading group restoration data restores only that group's original brightness and members");
        check(host.Coordinator.Snapshot.ControlRevision == fanRevision && host.Coordinator.Snapshot.ConfirmedDuty == 59,
            "All group edits dimming scene recall and synchronization leave cooling policy unchanged");
        vm.SelectedLightingGroup = vm.LightingGroupChoices[0]; window.SyncTargetsExpander.IsExpanded = false;
    }
}
