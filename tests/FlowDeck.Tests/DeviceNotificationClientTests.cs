using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceNotificationClientTests
{
    [Fact] public async Task NotificationDraftSavesTemperatureAndRpmOnDifferentDevicesWithoutActuation()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var draft = new DeviceNotificationDraft(); draft.Update(client.Snapshot!);
        Assert.False(draft.Dirty); var targets = draft.Targets(); Assert.Equal(2, targets.Count(t => t.Measurement == DeviceFeature.Temperature));
        var first = targets.First(t => t.Target == new EndpointReference(demo.DeviceIds[0], "fan1"));
        var second = targets.First(t => t.Target == new EndpointReference(demo.DeviceIds[1], "fan1"));
        Assert.NotEqual(first.Key, second.Key); Assert.NotEqual(first.Label, second.Label);
        var rule = draft.Add(first); draft.ChangeRule(rule with { Name = "ラジエーター", Threshold = 1000, Hysteresis = 100 });
        draft.Add(second); draft.Add(targets.First(t => t.Measurement == DeviceFeature.Temperature));
        draft.Change(draft.Settings with { Desktop = false, DelaySeconds = 8 });
        for (var i = 0; i < 3; i++) { await client.RefreshAsync(); draft.Update(client.Snapshot!); }
        Assert.Equal("ラジエーター", draft.Settings.Rules![0].Name); Assert.Equal(8, draft.Settings.DelaySeconds); Assert.True(draft.CanSave);
        await client.SaveNotificationsAsync(draft.Capture()); draft.Reset(client.Snapshot!); Assert.False(draft.Dirty); Assert.Equal(3, draft.Settings.Rules!.Length);
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
        draft.Remove(rule.Id); await client.SaveNotificationsAsync(draft.Capture()); Assert.Equal(2, client.Snapshot!.Configuration.Configuration.Preferences.Notifications.Rules!.Length);
    }
    [Theory] [InlineData("epoch")] [InlineData("session")] [InlineData("generation")]
    public async Task AChangedConnectionOrGenerationPreservesEditsAndRequiresReload(string change)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var snapshot = client.Snapshot!; var draft = new DeviceNotificationDraft(); draft.Update(snapshot);
        var rule = draft.Add(draft.Targets()[0]); draft.ChangeRule(rule with { Name = "入力中の名前" });
        var changed = change switch {
            "epoch" => snapshot with { State = snapshot.State with { Inventory = snapshot.State.Inventory with { Epoch = Guid.NewGuid().ToString("N") } } },
            "session" => snapshot with { Session = Guid.NewGuid().ToString("N") },
            _ => snapshot with { Configuration = snapshot.Configuration with { Generation = Guid.NewGuid().ToString("N") } }
        };
        draft.Update(changed); Assert.True(draft.Conflict); Assert.Equal("入力中の名前", Assert.Single(draft.Settings.Rules!).Name);
        Assert.Throws<InvalidOperationException>(() => draft.Capture()); draft.Reset(changed); Assert.False(draft.Conflict); Assert.False(draft.Dirty);
    }
    [Fact] public async Task InvalidOrDuplicateRulesAreEditableButCannotBeSavedAndDisconnectedSettingsRemainVisible()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var draft = new DeviceNotificationDraft(); draft.Update(client.Snapshot!);
        var target = draft.Targets()[0]; var rule = draft.Add(target); Assert.Throws<ArgumentException>(() => draft.Add(target));
        draft.ChangeRule(rule with { Name = "", Threshold = 0 }); Assert.NotNull(draft.Problem); Assert.Throws<InvalidOperationException>(() => draft.Capture());
        draft.ChangeRule(rule); Assert.True(draft.CanSave); draft.Update(client.Snapshot! with { Connected = false });
        Assert.False(draft.CanSave); Assert.Equal(rule, Assert.Single(draft.Settings.Rules!));
        draft.Update(client.Snapshot!); Assert.True(draft.CanSave);
    }
    [Fact] public async Task EmptyMonitoringSetupHasNoInventedNotificationTargets()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, monitoringOnly: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var draft = new DeviceNotificationDraft(); draft.Update(client.Snapshot!);
        Assert.Empty(draft.Targets()); Assert.Throws<ArgumentException>(() => draft.Add(new(new(demo.DeviceIds[0], "fan1"), DeviceFeature.Rpm, "未登録", "機器")));
    }
}

public class DeviceNotificationInboxTests
{
    private const string Epoch = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static DeviceNotificationEntry Entry(long sequence, DateTimeOffset? time = null, bool desktop = true) =>
        new(sequence, new("test", "テスト", "test", "通知", time ?? DateTimeOffset.UtcNow), desktop);
    private static DeviceNotificationFeed Feed(long latest, long next, DeviceNotificationEntry[] entries, bool desktop = true, long oldest = 1, long revision = 0) =>
        new(latest, oldest, next, oldest > 1, entries, [], 0, 0, revision, true, desktop);
    [Fact] public void InitialHistoryPagesAreSilentButAnEventArrivingDuringLoadingIsDeliveredOnce()
    {
        var inbox = new DeviceNotificationInbox(); Assert.Empty(inbox.Accept(Epoch, Feed(3, 1, [Entry(1)])));
        var next = Feed(4, 4, [Entry(2), Entry(3), Entry(4)]);
        Assert.Equal(4, Assert.Single(inbox.Accept(Epoch, next)).Sequence); Assert.Empty(inbox.Accept(Epoch, next));
        Assert.Equal(4, inbox.History.Length); Assert.Equal(4, inbox.Cursor);
    }
    [Fact] public void ReconnectKeepsCursorButNewServiceEpochLoadsSilentlyAndResetsHistory()
    {
        var inbox = new DeviceNotificationInbox(); inbox.Accept(Epoch, Feed(1, 1, [Entry(1)]));
        Assert.Equal(2, Assert.Single(inbox.Accept(Epoch, Feed(2, 2, [Entry(2)]))).Sequence);
        Assert.Empty(inbox.Accept(Epoch, Feed(2, 2, [Entry(1), Entry(2)])));
        var other = new string('b', 32); Assert.Empty(inbox.Accept(other, Feed(1, 1, [Entry(1)])));
        Assert.Single(inbox.History); Assert.Equal(1, inbox.Cursor); Assert.True(inbox.SameEpoch(other));
    }
    [Fact] public void MutedAndOldEventsAreRetainedWithoutDesktopReplay()
    {
        var inbox = new DeviceNotificationInbox(); inbox.Accept(Epoch, Feed(0, 0, []));
        Assert.Empty(inbox.Accept(Epoch, Feed(1, 1, [Entry(1, desktop: false)])));
        Assert.Empty(inbox.Accept(Epoch, Feed(2, 2, [Entry(2)], desktop: false)));
        Assert.Empty(inbox.Accept(Epoch, Feed(3, 3, [Entry(3, DateTimeOffset.UtcNow.AddMinutes(-1))])));
        Assert.Empty(inbox.Accept(Epoch, Feed(4, 4, [Entry(4, DateTimeOffset.UtcNow.AddMinutes(1))])));
        Assert.Equal(4, inbox.History.Length);
    }
    [Fact] public void BoundedHistoryReportsLossAndRejectsOlderPagesWithoutRollingBackTheCursor()
    {
        var inbox = new DeviceNotificationInbox(); inbox.Accept(Epoch, Feed(0, 0, []));
        for (var i = 1; i <= 300; i++) inbox.Accept(Epoch, Feed(i, i, [Entry(i)], oldest: Math.Max(1, i - 255)));
        Assert.Equal(256, inbox.History.Length); Assert.True(inbox.HistoryGap); Assert.Equal(300, inbox.Cursor);
        Assert.Empty(inbox.Accept(Epoch, Feed(200, 200, [Entry(200)]))); Assert.Equal(300, inbox.Cursor); Assert.Equal(300, inbox.History[0].Sequence);
    }
    [Fact] public void OlderActivePagesCannotReplaceMoreRecentWarnings()
    {
        var inbox = new DeviceNotificationInbox(); inbox.Accept(Epoch, Feed(0, 0, [], revision: 3));
        inbox.Accept(Epoch, Feed(0, 0, [], revision: 2)); Assert.Equal(3, inbox.Feed!.ActiveRevision);
        Assert.Throws<IOException>(() => inbox.Accept(Epoch, Feed(0, 20, [])));
    }
    [Fact] public void NotificationGuiSmokeRequiresAnIsolatedEmbeddedDemo()
    {
        string[] args = ["--devices-preview", "--demo", "--smoke", "--data-dir", Path.GetTempPath(), "--notifications"];
        Assert.True(DeviceWorkspaceOptions.Parse(args).Notifications);
        Assert.Throws<ArgumentException>(() => DeviceWorkspaceOptions.Parse(args.Where(a => a != "--smoke").ToArray()));
        Assert.Throws<ArgumentException>(() => DeviceWorkspaceOptions.Parse([.. args, "--cooling-calibration"]));
        Assert.Throws<ArgumentException>(() => DeviceWorkspaceOptions.Parse([.. args, "--devices-pipe", "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N"), "--server-pid", "1"]));
    }
}
