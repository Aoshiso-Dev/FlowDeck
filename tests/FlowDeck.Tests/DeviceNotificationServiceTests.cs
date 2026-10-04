using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceConfigurationAuthorityTests
{
    private static DeviceServiceRequest NotificationRequest(Rig r, DeviceOperation operation, DeviceNotificationQuery? query = null) =>
        r.Request(new(DeviceConfigurationOperation.Snapshot)) with { Operation = operation, Configuration = null, Notifications = query };
    private static async Task<DeviceNotificationFeed> Notifications(Rig r, DeviceNotificationQuery? query = null) =>
        (await r.Startup.Runtime.ExecuteAsync(NotificationRequest(r, DeviceOperation.ReadNotifications, query ?? new()))).Notifications!;
    [Fact] public async Task NotificationSettingsAreAtomicNonActuatingAndPersistAfterReload()
    {
        await using var r = await Rig.Create(); Assert.Contains("notifications-v2", r.Hello.Features);
        Assert.Equal(Outcome.Confirmed, (await r.Startup.Runtime.ExecuteAsync(r.Duty(65))).Result!.Outcome);
        var config = r.Startup.Configuration; var before = r.Startup.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "fan1");
        var count = r.Hub.Writes; var rule = new DeviceNotificationRule(Guid.NewGuid().ToString("N"), config.Cooling.Endpoints[0].Target, DeviceFeature.Rpm, "ケース", 30000);
        var settings = config.Preferences.Notifications with { Rules = [rule], DelaySeconds = 0 };
        var generation = r.Startup.Generation.Id;
        var result = await r.Send(new(DeviceConfigurationOperation.SaveNotifications, ExpectedGeneration: generation, Notifications: settings));
        Assert.True(result.Success, result.Error); Assert.Equal(["preferences.json"], result.Configuration!.ChangedComponents!);
        Assert.NotEqual(generation, r.Startup.Generation.Id); Assert.Equal(count, r.Hub.Writes);
        var after = r.Startup.Runtime.State().Endpoints.Single(e => e.Target == before.Target);
        Assert.True(after.Owned); Assert.Equal(before.PolicyRevision, after.PolicyRevision); Assert.Equal(before.ReportedDuty, after.ReportedDuty);
        var saved = DeviceConfigurationCodec.Read(r.Store.ReadActive()!.Files).Preferences.Notifications;
        Assert.Equal(rule, Assert.Single(saved.Rules!)); Assert.Single((await Notifications(r)).Events);
        Assert.False((await r.Send(new(DeviceConfigurationOperation.SaveNotifications, ExpectedGeneration: generation, Notifications: settings with { Enabled = false }))).Success);
        Assert.True(r.Startup.Configuration.Preferences.Notifications.Enabled);
    }
    [Fact] public async Task NotificationPersistenceFailureLeavesAdoptedSettingsAndOutputsUnchanged()
    {
        await using var r = await Rig.Create(); var original = r.Startup.Generation; var settings = r.Startup.Configuration.Preferences.Notifications;
        r.Store.Checkpoint = phase => { if (phase == "before-switch") throw new IOException("disk failure"); };
        var result = await r.Send(new(DeviceConfigurationOperation.SaveNotifications, ExpectedGeneration: original.Id, Notifications: settings with { Enabled = false }));
        Assert.False(result.Success); Assert.Equal(original, r.Startup.Generation); Assert.True(r.Startup.Configuration.Preferences.Notifications.Enabled);
        Assert.Equal(0, r.Hub.Writes); Assert.Equal(0, r.Hub.LightingWrites);
    }
    [Fact] public async Task NotificationSaveCannotInventRegistrationOrUseAnRgbEndpointAsTemperature()
    {
        await using var r = await Rig.Create(); var original = r.Startup.Generation.Id; var config = r.Startup.Configuration;
        foreach (var target in new[] { new EndpointReference("missing", "fan1"), config.Cooling.Endpoints[0].Target })
        {
            var rule = new DeviceNotificationRule(Guid.NewGuid().ToString("N"), target, DeviceFeature.Temperature, "誤った対象", 80);
            var reply = await r.Send(new(DeviceConfigurationOperation.SaveNotifications, ExpectedGeneration: original, Notifications: new(Rules: [rule])));
            Assert.False(reply.Success); Assert.Equal(original, r.Startup.Generation.Id);
        }
        Assert.Equal(0, r.Hub.Writes);
    }
    [Fact] public async Task RetransmittedTestAndFailedOperationProduceExactlyOneEventEach()
    {
        await using var r = await Rig.Create();
        var test = NotificationRequest(r, DeviceOperation.TestNotification);
        Assert.True((await r.Startup.Runtime.ExecuteAsync(test)).Success); Assert.True((await r.Startup.Runtime.ExecuteAsync(test)).Success);
        Assert.Single((await Notifications(r)).Events);
        var bad = r.Duty(50) with { TopologyRevision = 9999 };
        Assert.False((await r.Startup.Runtime.ExecuteAsync(bad)).Success); Assert.False((await r.Startup.Runtime.ExecuteAsync(bad)).Success);
        var events = (await Notifications(r)).Events;
        Assert.Single(events, e => e.Event.Kind == "test"); Assert.Single(events, e => e.Event.Kind == "operation-failed");
        Assert.Equal(0, r.Hub.Writes);
    }
    [Fact] public async Task PartialHardwareFailureIsRecordedEvenWhenIpcAcknowledgedTheOperation()
    {
        await using var r = await Rig.Create(); r.Driver.MapActuation = (_, _) => new(Outcome.Unknown, "読み戻し未確認");
        var request = r.Duty(60); var reply = await r.Startup.Runtime.ExecuteAsync(request);
        Assert.True(reply.Success); Assert.Equal(Outcome.Unknown, reply.Result!.Outcome);
        var feed = await Notifications(r); Assert.Contains(feed.Events, e => e.Event.Kind == "operation-failed");
        Assert.Contains(feed.Active, e => e.Key.StartsWith("system:control:", StringComparison.Ordinal));
    }
    [Fact] public async Task NonActuatingNotificationSaveInvalidatesAnOlderFullConfigurationPreview()
    {
        await using var r = await Rig.Create(); var transfer = await r.Upload(r.Changed()); var config = r.Startup.Configuration;
        Assert.True((await r.Send(new(DeviceConfigurationOperation.SaveNotifications, ExpectedGeneration: r.Startup.Generation.Id,
            Notifications: config.Preferences.Notifications with { Desktop = false }))).Success);
        Assert.False((await r.Send(r.Apply(transfer))).Success); Assert.Equal(0, r.Hub.Writes);
    }
    [Fact] public async Task ReopeningTheClientKeepsHistoryButAnotherWindowsUserCannotReadIt()
    {
        await using var r = await Rig.Create(); await r.Startup.Runtime.ExecuteAsync(NotificationRequest(r, DeviceOperation.TestNotification));
        var before = await Notifications(r); await r.Startup.Runtime.DisconnectAsync(r.Hello.Session);
        Assert.Throws<UnauthorizedAccessException>(() => r.Startup.Runtime.OpenSession("someone-else"));
        r.Hello = r.Startup.Runtime.OpenSession("user"); var after = await Notifications(r);
        Assert.Equal(before.Events[0], after.Events[0]); Assert.Equal(before.LatestSequence, after.LatestSequence);
    }
}

public class DeviceNotificationPipeTests
{
    private static DeviceNotificationEdit Edit(DeviceWorkspaceClient c, DeviceNotificationSettings settings)
    { var s = c.Snapshot!; return new(s.State.Inventory.Epoch, s.Session!, s.Configuration.Generation, settings); }
    [Fact] public async Task WorkspaceUsesAuthenticatedNegotiationAndRejectsStaleEditsWithoutHardwareWrites()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); Assert.True(client.SupportsNotifications);
        var snapshot = client.Snapshot!; var settings = snapshot.Configuration.Configuration.Preferences.Notifications;
        var target = snapshot.Configuration.Configuration.Dashboard.Items.First(i => i.Measurement == DeviceFeature.Temperature).Target;
        var rule = new DeviceNotificationRule(Guid.NewGuid().ToString("N"), target, DeviceFeature.Temperature, "CPU", 40, 2);
        var edit = Edit(client, settings with { Rules = [rule], DelaySeconds = 0 });
        await client.SaveNotificationsAsync(edit);
        var feed = await client.ReadNotificationsAsync(new()); Assert.Equal(rule.Id + ":limit", Assert.Single(feed.Events).Event.Key);
        await client.TestNotificationAsync(); var next = await client.ReadNotificationsAsync(new(feed.NextSequence));
        Assert.Equal("test", Assert.Single(next.Events).Event.Kind); Assert.Equal(1, next.ActiveTotal);
        Assert.Empty((await client.ReadNotificationsAsync(new(next.NextSequence))).Events);
        await Assert.ThrowsAsync<IOException>(() => client.SaveNotificationsAsync(edit));
        await Assert.ThrowsAsync<IOException>(() => client.SaveNotificationsAsync(Edit(client, settings) with { Session = new string('f', 32) }));
        await Assert.ThrowsAsync<IOException>(() => client.SaveNotificationsAsync(Edit(client, settings) with { Epoch = new string('f', 32) }));
        await client.SaveNotificationsAsync(Edit(client, settings with { Desktop = false }));
        await Assert.ThrowsAsync<IOException>(() => client.TestNotificationAsync());
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
    }
    [Fact] public async Task DedicatedFeedIsIsolatedFromStateAndMalformedOrCrossOperationPayloadsAreRejected()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var raw = DeviceServicePipeClient.ForDemo(demo.PipeName, Environment.ProcessId); await raw.ConnectAsync();
        var good = raw.Request(DeviceOperation.ReadNotifications) with { Notifications = new() };
        Assert.NotNull((await raw.SendAsync(good)).Notifications);
        Assert.Null((await raw.SendAsync(raw.Request(DeviceOperation.State))).Notifications);
        foreach (var bad in new[] { good with { Operation = DeviceOperation.State }, good with { Notifications = null },
            good with { Notifications = new(-1) }, good with { Notifications = new(Limit: 33) }, good with { Notifications = new(ActiveOffset: 1) },
            good with { Operation = DeviceOperation.TestNotification }, good with { Duty = 50 } })
            Assert.Throws<IOException>(() => DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(bad, Contract.Json)));
        Assert.False(DeviceConfigurationProtocol.Valid(new DeviceConfigurationRequest(DeviceConfigurationOperation.SaveNotifications, ExpectedGeneration: new string('a', 32))));
        Assert.False(DeviceConfigurationProtocol.Valid(new DeviceConfigurationRequest(DeviceConfigurationOperation.Snapshot, Notifications: new())));
        Assert.False((await raw.SendAsync(raw.Request(DeviceOperation.ReadNotifications) with { Notifications = new(After: 1000) })).Success);
        Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
    }
    [Fact] public void FeedValidationRejectsCorruptCountsCursorsAndUnsupportedEvents()
    {
        var m = new DeviceNotificationMonitor(); m.Evaluate(new(new(), new(2, [])), new(new(new string('a', 32), 0, []), [], false, null)); m.Test();
        var feed = m.Read(new()); Assert.True(DeviceNotificationProtocol.Valid(feed));
        foreach (var bad in new[] { feed with { NextSequence = 10 }, feed with { Events = [feed.Events[0], feed.Events[0]] },
            feed with { ActiveTotal = -1 }, feed with { ActiveOffset = 1 }, feed with { Events = [feed.Events[0] with { Event = feed.Events[0].Event with { Kind = "unexpected" } }] } })
            Assert.False(DeviceNotificationProtocol.Valid(bad));
    }
}
