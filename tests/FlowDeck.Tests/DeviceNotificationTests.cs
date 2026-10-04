using FlowDeck.Core;

namespace FlowDeck.Tests;

public class DeviceNotificationTests
{
    [Fact]
    public void IdenticalEndpointNamesOnDifferentDevicesStayIndependent()
    {
        var a = new DeviceNotificationRule(Guid.NewGuid().ToString("N"), new("hub-a", "fan1"), DeviceFeature.Rpm, "前面", 400, 50);
        var b = a with { Id = Guid.NewGuid().ToString("N"), Target = new("hub-b", "fan1") };
        var engine = new DeviceNotificationEngine(); var settings = new DeviceNotificationSettings(DelaySeconds: 0, Rules: [a, b]);
        var events = engine.Evaluate(settings, [new(a.Target, a.Measurement, a.Name, 300), new(b.Target, b.Measurement, b.Name, 800)], []);
        Assert.Equal(a.Id + ":limit", Assert.Single(events).Key);
    }
    [Fact]
    public void MissingIsNotZeroAndOnlyConfirmedStopSuppressesLowRpm()
    {
        var r = new DeviceNotificationRule(Guid.NewGuid().ToString("N"), new("controller", "pump"), DeviceFeature.Rpm, "ポンプ", 600);
        var settings = new DeviceNotificationSettings(DelaySeconds: 0, RecoverySeconds: 0, Rules: [r]); var engine = new DeviceNotificationEngine();
        var events = engine.Evaluate(settings, [new(r.Target, r.Measurement, r.Name, null)], []);
        Assert.Equal(r.Id + ":missing", Assert.Single(events).Key);
        engine.Reset(); Assert.Empty(engine.Evaluate(settings, [new(r.Target, r.Measurement, r.Name, 0, true)], []));
        Assert.Equal(r.Id + ":limit", Assert.Single(engine.Evaluate(settings, [new(r.Target, r.Measurement, r.Name, 0)], [])).Key);
    }
    [Fact]
    public void ArbitraryTemperatureKeepsHysteresisAndCannotRecoverWhileMissing()
    {
        var r = new DeviceNotificationRule(Guid.NewGuid().ToString("N"), new("cooler", "water"), DeviceFeature.Temperature, "水温", 45, 3);
        var settings = new DeviceNotificationSettings(DelaySeconds: 0, RecoverySeconds: 0, Rules: [r]); var engine = new DeviceNotificationEngine();
        Assert.Single(engine.Evaluate(settings, [new(r.Target, r.Measurement, r.Name, 46)], []));
        Assert.Empty(engine.Evaluate(settings, [new(r.Target, r.Measurement, r.Name, 44)], []));
        Assert.DoesNotContain(engine.Evaluate(settings, [], []), e => e.Kind == "recovered");
        Assert.Equal(2, engine.Evaluate(settings, [new(r.Target, r.Measurement, r.Name, 42)], []).Count(e => e.Kind == "recovered"));
    }
    [Fact]
    public void InvalidAndDuplicateTargetsAreRejectedBeforeEvaluation()
    {
        var r = new DeviceNotificationRule(Guid.NewGuid().ToString("N"), new("a", "water"), DeviceFeature.Temperature, "水温", 45);
        Assert.NotNull(DeviceNotifications.Validate(new(Rules: [r with { Measurement = DeviceFeature.FixedColor }])));
        Assert.NotNull(DeviceNotifications.Validate(new(Rules: [r, r with { Id = Guid.NewGuid().ToString("N") }])));
        var engine = new DeviceNotificationEngine(); var sample = new DeviceNotificationSample(r.Target, r.Measurement, r.Name, 40);
        Assert.Throws<ArgumentException>(() => engine.Evaluate(new(Rules: [r]), [sample, sample], []));
    }
}
