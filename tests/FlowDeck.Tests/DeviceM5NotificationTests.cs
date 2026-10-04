using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceM5NotificationTests
{
    private const string Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Other = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static DeviceServiceState EmptyState => new(new(Id, 0, []), [], false, null);
    private sealed class Rig
    {
        public readonly TestClock Clock = new();
        public readonly DeviceNotificationMonitor Monitor;
        public DeviceNotificationSettings Settings = new(DelaySeconds: 2, RecoverySeconds: 2);
        public Rig() => Monitor = new(Clock);
        public DeviceNotificationFeed Feed => Monitor.Read(new());
        public void Evaluate() => Monitor.Evaluate(new(Settings, new(2, [])), EmptyState);
        public void Report(bool connected, string id = Id) { Monitor.ReportM5(new(id, connected)); Evaluate(); }
        public void Tick(bool connected, int seconds = 1) { for (var i = 0; i < seconds; i++) { Clock.Advance(1); Report(connected); } }
    }
    [Fact] public void NeverConnectedIsQuietAndBriefTransportGapsDoNotNotify()
    {
        var r = new Rig(); r.Report(false); r.Tick(false, 8); Assert.Empty(r.Feed.Events);
        r.Report(true); r.Report(false); r.Tick(false); r.Tick(true); Assert.Empty(r.Feed.Events);
        r.Report(false); r.Tick(false, 2); Assert.Equal("raised", Assert.Single(r.Feed.Events).Event.Kind);
        Assert.Equal("system:m5-offline", Assert.Single(r.Feed.Active).Key);
        r.Report(true); r.Tick(true); Assert.Single(r.Feed.Events); r.Tick(true);
        Assert.Equal("recovered", r.Feed.Events[^1].Event.Kind); Assert.Equal(0, r.Feed.ActiveTotal);
    }
    [Fact] public void RepeatedReportsDeduplicateAndRemindersUseTheSavedInterval()
    {
        var r = new Rig { Settings = new(DelaySeconds: 0, RepeatSeconds: 30) }; r.Report(true); r.Report(false);
        r.Tick(false, 29); Assert.Single(r.Feed.Events); r.Tick(false);
        Assert.Equal("reminder", r.Feed.Events[^1].Event.Kind); Assert.Equal(2, r.Feed.Events.Length);
        Assert.True(r.Feed.Events.All(e => e.Desktop)); Assert.True(DeviceNotificationProtocol.Valid(r.Feed));
    }
    [Theory][InlineData("disconnect")][InlineData("replace")][InlineData("release")][InlineData("configuration")]
    public void IntentionalTransitionsClearWarningsWithoutInventingRecovery(string transition)
    {
        var r = new Rig { Settings = new(DelaySeconds: 0, RecoverySeconds: 0) }; r.Report(true); r.Report(false);
        var revision = r.Feed.ActiveRevision;
        switch (transition)
        {
            case "disconnect": r.Monitor.ReportM5(new()); r.Evaluate(); break;
            case "replace": r.Report(false, Other); break;
            case "release": r.Monitor.ClearM5(); r.Evaluate(); break;
            default: r.Monitor.Evaluate(new(r.Settings, new(2, [])), EmptyState with { ConfigurationChanging = true }); break;
        }
        Assert.Single(r.Feed.Events); Assert.Empty(r.Feed.Active); Assert.True(r.Feed.ActiveRevision > revision);
    }
    [Theory][InlineData(3)][InlineData(60)][InlineData(-1)]
    public void StaleOrBackwardsReportClockClearsAndRequiresNewConnectedEvidence(int elapsed)
    {
        var r = new Rig { Settings = new(DelaySeconds: 0, RecoverySeconds: 0) }; r.Report(true); r.Report(false);
        r.Clock.Advance(elapsed); r.Evaluate(); Assert.Empty(r.Feed.Active); Assert.Single(r.Feed.Events);
        r.Report(false); r.Tick(false, 4); Assert.Single(r.Feed.Events);
        r.Report(true); r.Report(false); Assert.Equal(2, r.Feed.Events.Length);
    }
    [Fact] public void NewReportAfterAReportingGapCannotCompleteAnOlderOfflineTimer()
    {
        var r = new Rig(); r.Report(true); r.Report(false); r.Clock.Advance(3); r.Report(false);
        r.Tick(false, 4); Assert.Empty(r.Feed.Events);
        r.Report(true); r.Report(false); r.Tick(false, 2); Assert.Single(r.Feed.Events);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void DisabledOrDisconnectOptOutRequiresNewHealthyEvidenceOnReenable(bool all)
    {
        var r = new Rig { Settings = new(DelaySeconds: 0, RecoverySeconds: 0) }; r.Report(true); r.Report(false);
        r.Settings = all ? r.Settings with { Enabled = false } : r.Settings with { Disconnects = false }; r.Evaluate();
        Assert.Empty(r.Feed.Active); Assert.Single(r.Feed.Events);
        r.Settings = r.Settings with { Enabled = true, Disconnects = true }; r.Report(false); Assert.Single(r.Feed.Events);
        r.Report(true); r.Report(false); Assert.Equal(2, r.Feed.Events.Length);
    }
    [Fact] public void DesktopAndRecoveryPreferencesAlsoApplyToM5()
    {
        var r = new Rig { Settings = new(Desktop: false, Recovery: false, DelaySeconds: 0, RecoverySeconds: 0) };
        r.Report(true); r.Report(false); r.Report(true); Assert.Empty(r.Feed.Active);
        Assert.False(Assert.Single(r.Feed.Events).Desktop); Assert.Equal("raised", r.Feed.Events[0].Event.Kind);
    }
    [Theory][InlineData("missing")][InlineData("bad-id")][InlineData("disarmed-connected")][InlineData("wrong-operation")]
    [InlineData("other-payload")][InlineData("targets")][InlineData("duty")]
    public void StatusContractRejectsMalformedAndCrossOperationReports(string variant)
    {
        var good = new DeviceServiceRequest(2, Id, Id, Id, 1, "PC", DeviceOperation.ReportM5Connection, M5Connection: new(Id, true));
        Assert.Equal(good, DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(good, Contract.Json)));
        var bad = variant switch {
            "missing" => good with { M5Connection = null }, "bad-id" => good with { M5Connection = new("M5GO", true) },
            "disarmed-connected" => good with { M5Connection = new(null, true) }, "wrong-operation" => good with { Operation = DeviceOperation.State },
            "other-payload" => good with { Notifications = new() }, "targets" => good with { Targets = [new(new("board", "fan1"), 1, 0)] },
            _ => good with { Duty = 50 }
        };
        Assert.Throws<IOException>(() => DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(bad, Contract.Json)));
    }
}

public partial class DeviceConfigurationAuthorityTests
{
    private static DeviceServiceRequest M5Report(Rig r, DeviceM5NotificationReport report) =>
        NotificationRequest(r, DeviceOperation.ReportM5Connection) with { M5Connection = report };
    private static async Task EnableImmediateM5Notifications(Rig r) => Assert.True((await r.Send(new(DeviceConfigurationOperation.SaveNotifications,
        ExpectedGeneration: r.Startup.Generation.Id, Notifications: new(DelaySeconds: 0, RecoverySeconds: 0)))).Success);
    [Fact] public async Task M5ReportUsesUnifiedHistoryAndReplayCacheWithoutHardwareOrStorageWrites()
    {
        await using var r = await Rig.Create(); await EnableImmediateM5Notifications(r);
        Assert.Contains(DeviceM5NotificationReport.Feature, r.Hello.Features);
        Assert.True((await r.Startup.Runtime.ExecuteAsync(r.Duty(65))).Success);
        var writes = r.Hub.Writes; var generation = r.Startup.Generation.Id; var state = r.Startup.Runtime.State().Endpoints;
        var id = Guid.NewGuid().ToString("N"); await r.Startup.Runtime.ExecuteAsync(M5Report(r, new(id, true)));
        var offline = M5Report(r, new(id, false)); await r.Startup.Runtime.ExecuteAsync(offline); await r.Startup.Runtime.ExecuteAsync(offline);
        Assert.Equal("system:m5-offline", Assert.Single((await Notifications(r)).Events).Event.Key);
        await r.Startup.Runtime.ExecuteAsync(M5Report(r, new())); var feed = await Notifications(r);
        Assert.Single(feed.Events); Assert.Empty(feed.Active);
        Assert.Equal(writes, r.Hub.Writes); Assert.Equal(0, r.Hub.LightingWrites); Assert.Equal(generation, r.Startup.Generation.Id);
        Assert.Equal(state, r.Startup.Runtime.State().Endpoints);
    }
    [Fact] public async Task M5MonitorIsClearedWithTheAuthenticatedOwnerAndOldReportsCannotReachTheNewOwner()
    {
        await using var r = await Rig.Create(); await EnableImmediateM5Notifications(r);
        var id = Guid.NewGuid().ToString("N"); await r.Startup.Runtime.ExecuteAsync(M5Report(r, new(id, true)));
        await r.Startup.Runtime.ExecuteAsync(M5Report(r, new(id, false))); var stale = M5Report(r, new(id, true));
        await r.Startup.Runtime.DisconnectAsync(r.Hello.Session); r.Hello = r.Startup.Runtime.OpenSession("user");
        Assert.False((await r.Startup.Runtime.ExecuteAsync(stale)).Success);
        await r.Startup.Runtime.ExecuteAsync(M5Report(r, new(id, false)));
        var feed = await Notifications(r); Assert.Empty(feed.Active); Assert.Single(feed.Events);
    }
}
