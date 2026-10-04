using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class NotificationTests
{
    private static NotificationRule Rule(string kind = "temperature") => new(Guid.NewGuid().ToString("N"),
        new(kind == "fan" ? "nzxt" : "sensor",kind == "fan" ? "hub" : "cpu",kind == "fan" ? "fan1" : "package",kind),"対象",kind == "fan" ? 400 : 80,kind == "fan" ? 50 : 3);
    private sealed class Rig
    {
        public TestClock Clock { get; } = new();
        public NotificationEngine Engine { get; }
        public NotificationSettings Settings;
        public NotificationRule Rule;
        public Rig(string kind = "temperature") { Rule = NotificationTests.Rule(kind); Settings = new(DelaySeconds:3,RecoverySeconds:2,Rules:[Rule]); Engine = new(Clock); }
        public NotificationEvent[] At(double? value,bool stopped = false) => Engine.Evaluate(Settings,[new(Rule.Address,Rule.Name,value,stopped)],[]);
        public NotificationEvent[] Next(double? value,bool stopped = false) { Clock.Advance(1); return At(value,stopped); }
    }
    [Fact] public void SustainedHighTemperatureRaisesOnceAndShortSpikesResetTheTimer()
    {
        var r = new Rig(); Assert.Empty(r.At(81)); Assert.Empty(r.Next(81)); Assert.Empty(r.Next(70)); Assert.Empty(r.Next(81));
        Assert.Empty(r.Next(81)); Assert.Empty(r.Next(81)); Assert.Equal("raised",Assert.Single(r.Next(81)).Kind);
        for(var i=0;i<120;i++) Assert.Empty(r.Next(90));
        Assert.Single(r.Engine.Active);
    }
    [Fact] public void TemperatureRecoveryUsesBothHysteresisAndSustainedNormalDuration()
    {
        var r = new Rig(); r.Settings = r.Settings with { DelaySeconds = 0 }; Assert.Single(r.At(85));
        Assert.Empty(r.Next(79)); Assert.Empty(r.Next(77)); Assert.Empty(r.Next(78)); Assert.Empty(r.Next(76)); Assert.Empty(r.Next(76));
        Assert.Equal("recovered",Assert.Single(r.Next(77)).Kind); Assert.Empty(r.Engine.Active);
    }
    [Fact] public void ZeroHysteresisCannotOscillateAtTheInclusiveTemperatureThreshold()
    {
        var r = new Rig(); r.Rule = r.Rule with { Hysteresis = 0 }; r.Settings = r.Settings with { DelaySeconds=0,RecoverySeconds=0,Rules=[r.Rule] };
        Assert.Single(r.At(80)); for(var i=0;i<10;i++) Assert.Empty(r.Next(80)); Assert.Equal("recovered",Assert.Single(r.Next(79)).Kind);
    }
    [Fact] public void MissingValuesNeverClaimRecoveryFromAnExistingHighTemperature()
    {
        var r = new Rig(); r.Settings = r.Settings with { DelaySeconds=0,RecoverySeconds=0 };
        Assert.Single(r.At(90)); var missing = r.Next(null); Assert.DoesNotContain(missing,e=>e.Kind == "recovered");
        Assert.Equal(2,r.Engine.Active.Length); Assert.Contains(r.Engine.Active,e=>e.Key.EndsWith(":limit") && e.Message.Contains("値未取得"));
        Assert.Equal(2,r.Next(70).Count(e=>e.Kind == "recovered")); Assert.Empty(r.Engine.Active);
    }
    [Fact] public void MissingAndNonfiniteValuesAreNotZeroRpmAndCanBeMutedPerRule()
    {
        var r = new Rig("fan"); r.Rule = r.Rule with { Missing=false }; r.Settings = r.Settings with { DelaySeconds=0,Rules=[r.Rule] };
        Assert.Empty(r.At(double.NaN)); Assert.Empty(r.Next(null)); Assert.Empty(r.Next(-1)); Assert.Single(r.Next(0));
    }
    [Fact] public void OnlyAnExplicitStopExemptsLowRpmAndRecoveryHasItsOwnRpmMargin()
    {
        var r = new Rig("fan"); r.Settings = r.Settings with { DelaySeconds=0,RecoverySeconds=0 };
        Assert.Empty(r.At(0,true)); Assert.Single(r.Next(0,false)); Assert.Empty(r.Next(430));
        Assert.Equal("recovered",Assert.Single(r.Next(450)).Kind);
        r.Rule = r.Rule with { IgnoreStopped=false }; r.Settings = r.Settings with { Rules=[r.Rule] }; Assert.Single(r.Next(0,true));
    }
    [Fact] public void RepeatedRemindersAreOptionalAndRespectTheirInterval()
    {
        var r = new Rig(); r.Settings = r.Settings with { DelaySeconds=0,RepeatSeconds=30 }; Assert.Single(r.At(90));
        for(var i=0;i<29;i++) Assert.Empty(r.Next(90)); Assert.Equal("reminder",Assert.Single(r.Next(90)).Kind);
        r.Settings = r.Settings with { Enabled=false }; Assert.Empty(r.Next(90)); Assert.Empty(r.Engine.Active);
    }
    [Fact] public void SleepOrLongPollingGapsDoNotCountAsObservedAbnormalDuration()
    {
        var r = new Rig(); Assert.Empty(r.At(90)); r.Clock.Advance(60); Assert.Empty(r.At(90));
        Assert.Empty(r.Next(90)); Assert.Empty(r.Next(90)); Assert.Single(r.Next(90));
        r.Engine.Reset(); Assert.Empty(r.At(90));
    }
    [Fact] public void ChangingThresholdOrRemovingRuleClearsOldStateWithoutFalseRecovery()
    {
        var r = new Rig(); r.Settings = r.Settings with { DelaySeconds=0,RecoverySeconds=0 }; Assert.Single(r.At(82));
        r.Rule = r.Rule with { Threshold=85 }; r.Settings = r.Settings with { Rules=[r.Rule] };
        Assert.Empty(r.Next(84)); Assert.Empty(r.Engine.Active);
        Assert.Single(r.Next(90)); r.Settings = r.Settings with { Rules=[] }; Assert.Empty(r.Next(90)); Assert.Empty(r.Engine.Active);
    }
    [Fact] public void RecoveryCanBeSilencedWithoutKeepingAnAlarmActive()
    {
        var r = new Rig(); r.Settings = r.Settings with { DelaySeconds=0,RecoverySeconds=0,Recovery=false };
        Assert.Single(r.At(90)); Assert.Empty(r.Next(70)); Assert.Empty(r.Engine.Active);
    }
    [Fact] public void SystemFaultsCanBeImmediateWhileConnectionsAreDebouncedAndDeduplicated()
    {
        var clock = new TestClock(); var engine = new NotificationEngine(clock); var options = new NotificationSettings(DelaySeconds:2,RecoverySeconds:0);
        NotificationSignal[] signals = [new("fault","CPU_FAN1",true,"出力不一致",true),new("link","Hub",true,"切断")];
        Assert.Equal("system:fault",Assert.Single(engine.Evaluate(options,[],signals)).Key);
        clock.Advance(1); Assert.Empty(engine.Evaluate(options,[],signals)); clock.Advance(1);
        Assert.Equal("system:link",Assert.Single(engine.Evaluate(options,[],signals)).Key);
        Assert.Empty(engine.Evaluate(options,[],signals)); Assert.Equal(2,engine.Evaluate(options,[],signals.Select(s=>s with { Abnormal=false }).ToArray()).Length);
    }
    [Fact] public void AddressIdentityPreventsAnotherHubFromSatisfyingAWatch()
    {
        var r = new Rig("fan"); r.Settings = r.Settings with { DelaySeconds=0 };
        var events = r.Engine.Evaluate(r.Settings,[new(r.Rule.Address with { DeviceId="replacement" },"対象",1200)],[]);
        Assert.EndsWith(":missing",Assert.Single(events).Key);
    }
    [Fact] public async Task SamplesRejectStaleSensorsAndZeroRpmAloneDoesNotMeanStopped()
    {
        await using var f = new Fixture(); await f.Initialize();
        var board = new BoardSnapshot("ASRock X870E Taichi White",[new(MotherboardFans.Cpu.SensorId,"CPU Fan","Nuvoton","fan","rpm",0,0)],f.Clock.GetTimestamp());
        var owners = new Dictionary<string,CpuFanState>();
        var values = NotificationInputs.Collect(f.Core.Settings,f.Core.Snapshot,board,owners,f.Clock);
        Assert.False(values.Single(v=>v.Address.Source=="motherboard").IntentionalStop);
        owners[MotherboardFans.Cpu.Id] = new(Reading:new(0,0,f.Clock.GetTimestamp()));
        Assert.True(NotificationInputs.Collect(f.Core.Settings,f.Core.Snapshot,board,owners,f.Clock).Single(v=>v.Address.Source=="motherboard").IntentionalStop);
        var state = f.Core.Snapshot; f.Clock.Advance(4);
        Assert.All(NotificationInputs.Collect(f.Core.Settings,state,board,owners,f.Clock),s=>Assert.Null(s.Value));
    }
    [Fact] public async Task NotificationPersistenceValidatesNamesIdentitiesThresholdsAndCorruptFiles()
    {
        var rule = Rule(); var setting = new NotificationSettings(Rules:[rule]);
        Assert.Null(Notifications.Validate(setting));
        Assert.NotNull(Notifications.Validate(setting with { Rules=[rule,rule with { Id=Guid.NewGuid().ToString("N") }] }));
        Assert.NotNull(Notifications.Validate(setting with { Rules=[rule with { Threshold=double.NaN }] }));
        Assert.NotNull(Notifications.Validate(setting with { Rules=[rule with { Hysteresis=-1 }] }));
        Assert.NotNull(Notifications.Validate(setting with { RepeatSeconds=1 }));
        Assert.NotNull(Notifications.Validate(setting with { Rules=[rule with { Address=rule.Address with { Source="unknown" } }] }));
        var directory = Path.Combine(Path.GetTempPath(),"FlowDeck-notifications-" + Guid.NewGuid().ToString("N")); var store = new NotificationStore(directory);
        await store.SaveAsync(setting); var before = await File.ReadAllTextAsync(store.PathName); await store.SaveAsync(setting with { Desktop=false });
        Assert.Equal(before,await File.ReadAllTextAsync(store.PathName + ".backup")); Assert.False((await store.LoadAsync()).Desktop);
        await File.WriteAllTextAsync(store.PathName,"{bad"); await Assert.ThrowsAsync<System.Text.Json.JsonException>(()=>store.LoadAsync());
        Assert.Equal("{bad",await File.ReadAllTextAsync(store.PathName));
    }
}
