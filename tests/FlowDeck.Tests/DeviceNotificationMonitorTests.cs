using FlowDeck.Core;

namespace FlowDeck.Tests;

public class DeviceNotificationMonitorTests
{
    private static readonly EndpointReference Target = new("board", "fan1");
    private static readonly DeviceNotificationRule Rule = new(new string('a', 32), Target, DeviceFeature.Rpm, "ケース", 400, 50);
    private static DeviceCapability Cap(DeviceFeature f) => new(f, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready);
    private static DeviceServiceState State(double? rpm = 900, double age = 0, bool connected = true, long generation = 1, DeviceExecutionState? execution = null)
    {
        var device = new DeviceObservation("board", "demo", "マザー", DeviceKind.Motherboard, IdentityConfidence.SerialBound, "a", null, ["board"],
            [new("fan1", "FAN1", EndpointKind.Cooling, [Cap(DeviceFeature.Rpm), Cap(DeviceFeature.DutyRead)])]);
        return new(new(new string('b', 32), 1, [new(device, generation, connected)], 1),
            [execution ?? new(Target, generation, 0, Outcome.Unknown)], false, null,
            [new(Target, DeviceFeature.Rpm, rpm, age, false), new(Target, DeviceFeature.DutyRead, 0, 0, false)]);
    }
    private static DeviceNotificationConfiguration Config(DeviceNotificationSettings? settings = null) => new(settings ?? new(DelaySeconds: 0, RecoverySeconds: 0, Rules: [Rule]),
        new(2, [new("board", "マザー", "demo", DeviceKind.Motherboard, [new("fan1", "ケース", EndpointKind.Cooling)])]));
    private static DeviceNotificationFeed Feed(DeviceNotificationMonitor m) => m.Read(new());

    [Theory]
    [InlineData(null, 0, true)]
    [InlineData(0d, 3, true)]
    [InlineData(0d, 0, false)]
    [InlineData(double.NaN, 0, true)]
    public void UnavailableReadingsAreMissingAndNeverLowRpm(double? rpm, double age, bool connected)
    {
        var m = new DeviceNotificationMonitor(); m.Evaluate(Config(), State(rpm, age, connected));
        var e = Assert.Single(Feed(m).Events); Assert.Equal(Rule.Id + ":missing", e.Event.Key);
    }
    [Fact] public void RealZeroIsLowAndOnlyFreshConfirmedOwnedStopSuppressesIt()
    {
        var stopped = new DeviceExecutionState(Target, 1, 1, Outcome.Confirmed, ReportedDuty: 0, Owned: true,
            Cooling: new(FanMode.Auto, 0, false, null, false, false));
        foreach (var variant in new[] { stopped with { Owned = false }, stopped with { Outcome = Outcome.Pending },
            stopped with { Generation = 2 }, stopped with { ReportedDuty = 1 }, stopped with { Cooling = stopped.Cooling! with { Restarting = true } } })
        {
            var m = new DeviceNotificationMonitor(); m.Evaluate(Config(), State(0, execution: variant));
            Assert.Contains(Feed(m).Events, e => e.Event.Key == Rule.Id + ":limit");
        }
        var monitor = new DeviceNotificationMonitor(); monitor.Evaluate(Config(), State(0, execution: stopped)); Assert.Empty(Feed(monitor).Events);
        var staleDuty = State(0, execution: stopped) with { Telemetry = [new(Target, DeviceFeature.Rpm, 0, 0, false), new(Target, DeviceFeature.DutyRead, 0, 3, true)] };
        monitor.Evaluate(Config(), staleDuty); Assert.Equal(Rule.Id + ":limit", Assert.Single(Feed(monitor).Events).Event.Key);
    }
    [Fact] public void DuplicateReadingsCannotChooseAnArbitraryMeasurement()
    {
        var m = new DeviceNotificationMonitor(); var s = State(0);
        m.Evaluate(Config(), s with { Telemetry = [.. s.Telemetry!, s.Telemetry![0] with { Value = 1000 }] });
        Assert.Equal(Rule.Id + ":missing", Assert.Single(Feed(m).Events).Event.Key);
    }
    [Fact] public void ReconnectionRestartsTheThresholdDelayAndNoGuiIsNeeded()
    {
        var clock = new TestClock(); var m = new DeviceNotificationMonitor(clock); var c = Config(new(DelaySeconds: 2, Rules: [Rule]));
        m.Evaluate(c, State(0)); clock.Advance(1); m.Evaluate(c, State(0)); clock.Advance(1); m.Evaluate(c, State(0, generation: 2));
        Assert.Empty(Feed(m).Events); clock.Advance(1); m.Evaluate(c, State(0, generation: 2)); Assert.Empty(Feed(m).Events);
        clock.Advance(1); m.Evaluate(c, State(0, generation: 2)); Assert.Single(Feed(m).Events);
    }
    [Fact] public void MissingAfterAnAlarmCannotAnnounceRecovery()
    {
        var m = new DeviceNotificationMonitor(); var config = Config(new(DelaySeconds: 0, RecoverySeconds: 0, Disconnects: false, Rules: [Rule]));
        m.Evaluate(config, State(0)); m.Evaluate(config, State(null));
        Assert.Equal(2, Feed(m).ActiveTotal); Assert.DoesNotContain(Feed(m).Events, e => e.Event.Kind == "recovered");
        m.Evaluate(config, State(450)); Assert.Equal(0, Feed(m).ActiveTotal);
        Assert.Equal(2, Feed(m).Events.Count(e => e.Event.Kind == "recovered"));
    }
    [Fact] public void DisconnectRequiresARegisteredPreviouslyHealthyDeviceIncludingRgbOnlyDevices()
    {
        var m = new DeviceNotificationMonitor(); var c = Config(new(DelaySeconds: 0, RecoverySeconds: 0));
        m.Evaluate(c, State(connected: false)); Assert.Empty(Feed(m).Events);
        m.Evaluate(c, State()); m.Evaluate(c, State(connected: false)); Assert.Equal("system:device:board", Assert.Single(Feed(m).Events).Event.Key);
        m.Evaluate(c, State()); Assert.Equal("recovered", Feed(m).Events[^1].Event.Kind);
        var rgb = State() with { Telemetry = [] }; rgb = rgb with { Inventory = rgb.Inventory with { Devices = [rgb.Inventory.Devices[0] with {
            Device = rgb.Inventory.Devices[0].Device with { Endpoints = [new("rgb", "RGB", EndpointKind.Lighting, [Cap(DeviceFeature.FixedColor)])] } }] } };
        var other = new DeviceNotificationMonitor(); other.Evaluate(c, rgb); other.Evaluate(c, rgb with { Inventory = rgb.Inventory with { Devices = [] } });
        Assert.Single(Feed(other).Events);
        var unregistered = new DeviceNotificationMonitor(); unregistered.Evaluate(c with { Devices = new(2, []) }, State());
        unregistered.Evaluate(c with { Devices = new(2, []) }, State(connected: false)); Assert.Empty(Feed(unregistered).Events);
    }
    [Fact] public void InitialUnknownIsQuietButOwnedUnknownAndReleaseFaultAreImmediate()
    {
        var m = new DeviceNotificationMonitor(); var c = Config(new(DelaySeconds: 300)); m.Evaluate(c, State()); Assert.Empty(Feed(m).Events);
        var s = State(execution: new(Target, 1, 1, Outcome.Unknown, Owned: true, Message: "復元未確認")) with { LifecycleError = "復元待ち" };
        m.Evaluate(c, s); Assert.Equal(2, Feed(m).ActiveTotal); Assert.All(Feed(m).Events, e => Assert.Equal("raised", e.Event.Kind));
    }
    [Fact] public void DisabledNotificationsClearActiveButRetainHistoryAndDesktopOptOutIsCaptured()
    {
        var m = new DeviceNotificationMonitor(); var c = Config(); m.Evaluate(c with { Settings = c.Settings with { Desktop = false } }, State(0));
        Assert.False(Assert.Single(Feed(m).Events).Desktop); m.Evaluate(c with { Settings = c.Settings with { Enabled = false } }, State(0));
        Assert.Equal(0, Feed(m).ActiveTotal); Assert.Single(Feed(m).Events); Assert.Throws<InvalidOperationException>(() => m.Test());
        m.Evaluate(c, State(900)); m.Test(); Assert.True(Feed(m).Events[^1].Desktop); Assert.Equal("test", Feed(m).Events[^1].Event.Kind);
    }
    [Fact] public void HistoryHasBoundedPagesAndExplicitLossAndCannotReadAFutureCursor()
    {
        var m = new DeviceNotificationMonitor(); m.Evaluate(Config(), State());
        for (var i = 0; i < 300; i++) m.Test();
        var first = Feed(m); Assert.True(DeviceNotificationProtocol.Valid(first)); Assert.True(first.HistoryGap);
        Assert.Equal(45, first.OldestSequence); Assert.Equal(300, first.LatestSequence); Assert.Equal(32, first.Events.Length);
        var events = first.Events.ToList(); var page = first;
        while (page.NextSequence < page.LatestSequence) { page = m.Read(new(page.NextSequence)); events.AddRange(page.Events); }
        Assert.Equal(256, events.Count); Assert.Equal(256, events.DistinctBy(e => e.Sequence).Count()); Assert.Equal(300, page.NextSequence);
        Assert.Throws<IOException>(() => m.Read(new(301)));
    }
    [Fact] public void ActivePagesAreCompleteAndCannotMixDifferentRevisions()
    {
        var m = new DeviceNotificationMonitor(); var rules = Enumerable.Range(0, 70).Select(i => Rule with { Id = Guid.NewGuid().ToString("N"), Target = new("board", "missing" + i) }).ToArray();
        m.Evaluate(Config(new(DelaySeconds: 0, Rules: rules)), State()); var first = Feed(m);
        Assert.Equal(70, first.ActiveTotal); Assert.Equal(32, first.Active.Length);
        var second = m.Read(new(ActiveOffset: 32, ActiveRevision: first.ActiveRevision));
        var third = m.Read(new(ActiveOffset: 64, ActiveRevision: first.ActiveRevision));
        Assert.Equal(70, first.Active.Concat(second.Active).Concat(third.Active).DistinctBy(e => e.Key).Count());
        m.Evaluate(Config(new(Enabled: false)), State()); Assert.Throws<IOException>(() => m.Read(new(ActiveOffset: 32, ActiveRevision: first.ActiveRevision)));
    }
    [Fact] public void FaultMessagesAreBoundedAndEvaluationErrorsDoNotEraseExistingWarnings()
    {
        var m = new DeviceNotificationMonitor(); m.Evaluate(Config(), State(0) with { LifecycleError = new string('x', 20000) });
        m.EvaluationFailed(new IOException("bad provider")); var feed = Feed(m);
        Assert.True(DeviceNotificationProtocol.Valid(feed)); Assert.Equal(2, feed.ActiveTotal); Assert.Contains("bad provider", feed.Error);
        Assert.All(feed.Events, e => Assert.True(e.Event.Message.Length <= 512));
    }
}
