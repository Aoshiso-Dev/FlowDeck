using System.Text;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class TestClock : TimeProvider
{
    private long timestamp = 1000;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => timestamp;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(timestamp);
    public void Advance(double seconds) => timestamp += (long)(seconds * 1000);
}
public sealed class TestLog : IEventLog { public List<object> Entries { get; } = []; public void Write(string category, object data) { lock (Entries) Entries.Add(data); } }
public sealed class Fixture : IAsyncDisposable
{
    public TestClock Clock { get; } = new();
    public DemoHub Hub { get; } = new() { DelayMs = 0 };
    public DemoTemperatures Temperatures { get; }
    public ControlCoordinator Core { get; }
    public Fixture(AppSettings? settings = null)
    { Temperatures = new(Clock); Core = new(Hub, Temperatures, settings ?? DemoHub.CreateSettings(), new TestLog(), Clock); }
    public async Task Initialize() { await Core.OpenAsync(DemoHub.Identity); await Core.TickAsync(); }
    public ControlRequest Request(string command = "SetManualDuty", int duty = 50, long? revision = null, string? id = null) => new(id ?? Guid.NewGuid().ToString(), "PC", Core.Epoch,
        revision ?? Core.Snapshot.ControlRevision, command, Duty: duty);
    public Task<OperationResult> Manual(int duty) => Core.RequestAsync(Request(duty: duty));
    public Task<OperationResult> Auto()
    { var p = Core.Settings.Profiles[1]; return Core.RequestAsync(Request("SelectAutoProfile") with { ProfileId = p.Id, ProfileRevision = p.Revision }); }
    public ValueTask DisposeAsync() => Core.DisposeAsync();
}
public class PolicyTests
{
    [Theory][InlineData(0,25)][InlineData(30,25)][InlineData(40,37.5)][InlineData(50,50)][InlineData(100,50)]
    public void InterpolationAndEndpointClamp(double temperature, double expected)
        => Assert.Equal(expected, FanPolicyEngine.Interpolate([new(30,25), new(50,50)], temperature));
    [Fact] public void SeparateCurvesMaxAndCeiling()
    {
        var p = new AutoProfile("p", "test", Cpu: new("cpu", [new(30,25), new(80,60)], 95,85), Gpu: new("gpu", [new(30,25), new(80,100)],90,80), DescentDelaySeconds:0,DescentPerSecond:1);
        Assert.Equal(55, FanPolicyEngine.Calculate(p, new(25,100,70), new Dictionary<string,double>{{"cpu",70},{"gpu",50}}));
    }
    [Theory][InlineData(0,false)][InlineData(24,false)][InlineData(25,true)][InlineData(54,true)][InlineData(100,true)][InlineData(101,false)]
    public void ManualDutyRange(int duty,bool expected) => Assert.Equal(expected,FanPolicyEngine.IsDutyAllowed(duty,new(25,100,70)));
    [Fact] public void RealDefaultsContainNoGuessedValues()
    { var s=new AppSettings(); Assert.Null(s.Limits.Minimum); Assert.Null(s.Limits.Fallback); Assert.Null(s.Limits.Exit); Assert.False(s.Startup.ResumeControl); Assert.All(s.Profiles,p=>Assert.NotNull(FanPolicyEngine.ValidateProfile(p,s.Limits))); }
    [Fact] public void DuplicateTemperaturesRejected()
    { var s=DemoHub.CreateSettings(); var p=s.Profiles[0] with { Cpu=s.Profiles[0].Cpu! with {Points=[new(40,25),new(40,50)]}}; Assert.NotNull(FanPolicyEngine.ValidateProfile(p,s.Limits)); }
    [Fact] public void DecreasingOutputRejected()
    { var s=DemoHub.CreateSettings(); var p=s.Profiles[0] with { Cpu=s.Profiles[0].Cpu! with {Points=[new(40,50),new(60,25)]}}; Assert.NotNull(FanPolicyEngine.ValidateProfile(p,s.Limits)); }
    [Fact] public void NanAndInfinityRejected()
    { var s=DemoHub.CreateSettings(); Assert.NotNull(FanPolicyEngine.ValidateProfile(s.Profiles[0] with {DescentDelaySeconds=double.NaN},s.Limits)); Assert.NotNull(FanPolicyEngine.ValidateProfile(s.Profiles[0] with {DescentPerSecond=double.PositiveInfinity},s.Limits)); }
    [Fact] public void DescentWaitAndRateAndImmediateRise()
    { var clock=new TestClock(); var limiter=new DescentLimiter(); limiter.Reset(80,clock.GetTimestamp()); Assert.Equal(80,limiter.Next(30,clock.GetTimestamp(),3,2,clock)); clock.Advance(3); Assert.Equal(80,limiter.Next(30,clock.GetTimestamp(),3,2,clock)); clock.Advance(1); Assert.Equal(78,limiter.Next(30,clock.GetTimestamp(),3,2,clock)); Assert.Equal(100,limiter.Next(100,clock.GetTimestamp(),3,2,clock)); }
    [Fact] public void FractionalDescentIsNotLost()
    { var clock=new TestClock(); var limiter=new DescentLimiter(); limiter.Reset(50,clock.GetTimestamp()); limiter.Next(25,clock.GetTimestamp(),0,.25,clock); for(var i=0;i<4;i++){clock.Advance(1); limiter.Next(25,clock.GetTimestamp(),0,.25,clock);} Assert.Equal(49,limiter.Next(25,clock.GetTimestamp(),0,.25,clock)); }
}
public class CoordinatorTests
{
    [Fact] public async Task MonitorStartupAndExitNeverWrite()
    { await using var f=new Fixture(); await f.Initialize(); Assert.Equal(0,f.Hub.Writes); await f.Core.StopControlAsync(); Assert.Equal(0,f.Hub.Writes); }
    [Fact] public async Task AllChannelsAndConfirmedPolicy()
    { await using var f=new Fixture(); await f.Initialize(); var r=await f.Manual(54); Assert.Equal(Outcome.Confirmed,r.Outcome); Assert.All(f.Core.Snapshot.Channels,c=>Assert.Equal(54,c.Duty)); Assert.Equal(54,f.Core.Snapshot.ConfirmedDuty); }
    [Fact] public async Task OldEpochRejected()
    { await using var f=new Fixture(); await f.Initialize(); var r=await f.Core.RequestAsync(f.Request() with {ControllerEpoch="old"}); Assert.Equal("STALE_SESSION",r.ErrorCode); Assert.Equal(0,f.Hub.Writes); }
    [Fact] public async Task StaleEditCannotOverwrite()
    { await using var f=new Fixture(); await f.Initialize(); var rev=f.Core.Snapshot.ControlRevision; await f.Manual(55); var r=await f.Core.RequestAsync(f.Request(duty:70,revision:rev)); Assert.Equal("CONFLICT",r.ErrorCode); Assert.Equal(55,f.Core.Snapshot.ConfirmedDuty); }
    [Fact] public async Task DuplicateRequestReturnsOriginalBeforeRevisionCheck()
    { await using var f=new Fixture(); await f.Initialize(); var req=f.Request(); var a=await f.Core.RequestAsync(req); var b=await f.Core.RequestAsync(req); Assert.Equal(a,b); Assert.Equal(1,f.Hub.Writes); }
    [Fact] public async Task ReusedIdDifferentContentRejected()
    { await using var f=new Fixture(); await f.Initialize(); var req=f.Request(); await f.Core.RequestAsync(req); var result=await f.Core.RequestAsync(req with {Duty=80}); Assert.Equal("DUPLICATE_ID",result.ErrorCode); Assert.Equal(1,f.Hub.Writes); }
    [Fact] public async Task BusyDoesNotQueueSameTarget()
    { await using var f=new Fixture(); await f.Initialize(); f.Hub.DelayMs=150; var first=f.Manual(50); var second=await f.Manual(60); Assert.Equal("BUSY",second.ErrorCode); await first; Assert.Equal(1,f.Hub.Writes); }
    [Fact] public async Task DesiredAndEffectiveSeparateWhilePending()
    { await using var f=new Fixture(); await f.Initialize(); await f.Manual(40); f.Hub.DelayMs=150; var pending=f.Manual(60); Assert.Equal(40,f.Core.Snapshot.ConfirmedDuty); Assert.Equal(60,f.Core.Snapshot.Desired!.ManualDuty); await pending; }
    [Fact] public async Task PartialFailureUsesOneFallbackAndResumeLastConfirmed()
    { await using var f=new Fixture(); await f.Initialize(); await f.Manual(40); f.Hub.FailNextFanWrite=true; var result=await f.Manual(55); Assert.Equal(Outcome.Partial,result.Outcome); Assert.True(f.Core.Snapshot.Faulted); Assert.Equal(100,f.Core.Snapshot.ConfirmedDuty); Assert.Equal(40,f.Core.Snapshot.LastConfirmed!.ManualDuty); var count=f.Hub.Writes; await f.Core.TickAsync(); Assert.Equal(count,f.Hub.Writes); var resumed=await f.Core.RequestAsync(f.Request("ResumeControl")); Assert.Equal(Outcome.Confirmed,resumed.Outcome); Assert.Equal(40,f.Core.Snapshot.ConfirmedDuty); }
    [Fact] public async Task PartialWithoutPreviousPolicyResumesMonitor()
    { await using var f=new Fixture(); await f.Initialize(); f.Hub.FailNextFanWrite=true; await f.Manual(55); var before=f.Hub.Writes; await f.Core.RequestAsync(f.Request("ResumeControl")); Assert.Equal(FanMode.Monitor,f.Core.Snapshot.Mode); Assert.Equal(before,f.Hub.Writes); }
    [Fact] public async Task FaultRejectsNormalOperations()
    { await using var f=new Fixture(); await f.Initialize(); f.Hub.FailNextFanWrite=true; await f.Manual(55); Assert.Equal(Outcome.Rejected,(await f.Manual(65)).Outcome); }
    [Fact] public async Task LightingHasSeparateRevision()
    { await using var f=new Fixture(); await f.Initialize(); var rev=f.Core.Snapshot.ControlRevision; var r=await f.Core.RequestAsync(f.Request("SetLighting",revision:f.Core.Snapshot.LightingRevision) with {Lighting="off"}); Assert.Equal(Outcome.WrittenUnverified,r.Outcome); Assert.Equal(rev,f.Core.Snapshot.ControlRevision); Assert.Equal(0,f.Hub.Writes); }
    [Fact] public async Task SavingProfileDoesNotChangeRunningCurve()
    { await using var f=new Fixture(); await f.Initialize(); await f.Auto(); var previous=f.Core.Snapshot.LastConfirmed!.Profile!; var changed=previous with {Cpu=previous.Cpu! with {Points=[new(30,100),new(80,100)]}}; var saved=f.Core.SaveProfile(changed,previous.Revision); await f.Core.TickAsync(); Assert.Equal(previous.Revision,f.Core.Snapshot.LastConfirmed!.Profile!.Revision); Assert.NotEqual(saved.Revision,previous.Revision); Assert.NotEqual(100,f.Core.Snapshot.ConfirmedDuty); }
    [Fact] public async Task ProfileVersionConflictRejected()
    { await using var f=new Fixture(); await f.Initialize(); var p=f.Core.Settings.Profiles[1]; f.Core.SaveProfile(p,p.Revision); var r=await f.Core.RequestAsync(f.Request("SelectAutoProfile") with {ProfileId=p.Id,ProfileRevision=p.Revision}); Assert.Equal("CONFLICT",r.ErrorCode); }
    [Fact] public async Task OptionalSensorsDoNotStopManual()
    { await using var f=new Fixture(); await f.Initialize(); await f.Manual(50); f.Temperatures.Missing=true; await f.Core.TickAsync(); Assert.False(f.Core.Snapshot.Faulted); }
    [Fact] public async Task RequiredMissingTemperatureStopsAutomatic()
    { await using var f=new Fixture(); await f.Initialize(); await f.Auto(); f.Temperatures.Missing=true; await f.Core.TickAsync(); Assert.True(f.Core.Snapshot.Faulted); Assert.Equal(FanMode.Fallback,f.Core.Snapshot.Mode); Assert.False(f.Core.Snapshot.CanResume); f.Temperatures.Missing=false; await f.Core.TickAsync(); Assert.True(f.Core.Snapshot.CanResume); Assert.True(f.Core.Snapshot.Faulted); }
    [Fact] public async Task OverheatRequiresBelowResumeThreshold()
    { await using var f=new Fixture(); await f.Initialize(); await f.Auto(); f.Temperatures.Cpu=98; await f.Core.TickAsync(); Assert.True(f.Core.Snapshot.Faulted); f.Temperatures.Cpu=90; await f.Core.TickAsync(); Assert.False(f.Core.Snapshot.CanResume); f.Temperatures.Cpu=70; await f.Core.TickAsync(); Assert.True(f.Core.Snapshot.CanResume); }
    [Fact] public async Task TelemetryExpiresWithoutNewMeasurements()
    { await using var f=new Fixture(); await f.Initialize(); await f.Manual(54); f.Clock.Advance(3); Assert.Null(f.Core.Snapshot.ConfirmedDuty); Assert.All(f.Core.Snapshot.Channels,c=>Assert.Null(c.Rpm)); Assert.All(f.Core.Snapshot.Sensors,c=>Assert.Null(c.Celsius)); }
    [Fact] public async Task DisconnectDoesNotClaimFallbackSuccess()
    { await using var f=new Fixture(); await f.Initialize(); await f.Manual(55); f.Hub.Disconnected=true; await f.Core.TickAsync(); Assert.False(f.Core.Snapshot.HubReady); Assert.True(f.Core.Snapshot.Faulted); Assert.Null(f.Core.Snapshot.ConfirmedDuty); Assert.Equal(1,f.Hub.Writes); }
    [Fact] public async Task ReconnectRequiresExplicitResume()
    { await using var f=new Fixture(); await f.Initialize(); await f.Manual(55); f.Hub.Disconnected=true; await f.Core.TickAsync(); f.Hub.Disconnected=false; await f.Core.TickAsync(); Assert.True(f.Core.Snapshot.HubReady); Assert.True(f.Core.Snapshot.Faulted); }
    [Fact] public async Task HardwareConditionsCannotChangeDuringControl()
    { await using var f=new Fixture(); await f.Initialize(); await f.Manual(55); Assert.Throws<InvalidOperationException>(()=>f.Core.UpdateSettings(f.Core.Settings with {Limits=new(30,100,70)})); }
    [Fact] public async Task UnknownLimitsPreventWrites()
    { await using var f=new Fixture(DemoHub.CreateSettings() with {Limits=new()}); await f.Initialize(); Assert.Equal(Outcome.Rejected,(await f.Manual(55)).Outcome); Assert.Equal(0,f.Hub.Writes); }
    [Fact] public async Task StopWritesExitBeforeMonitor()
    { await using var f=new Fixture(); await f.Initialize(); await f.Manual(55); await f.Core.StopControlAsync(); Assert.Equal(FanMode.Monitor,f.Core.Snapshot.Mode); Assert.False(f.Core.Snapshot.Managing); Assert.Equal(70,f.Core.Snapshot.ConfirmedDuty); var count=f.Hub.Writes; await f.Core.StopControlAsync(); Assert.Equal(count,f.Hub.Writes); }
    [Fact] public async Task SleepInvalidatesOldTelemetry()
    { await using var f=new Fixture(); await f.Initialize(); await f.Manual(54); f.Core.Suspend(); Assert.Null(f.Core.Snapshot.ConfirmedDuty); Assert.False(f.Core.Snapshot.HubReady); await f.Core.ResumeFromSleepAsync(); Assert.Equal(54,f.Core.Snapshot.ConfirmedDuty); Assert.False(f.Core.Snapshot.Faulted); }
}
public class FramingTests
{
    [Fact] public void FragmentedMultibyteCrLfAndMultipleFrames()
    { var parser=new JsonLineFramer(4096); var bytes=Encoding.UTF8.GetBytes("{\"名\":1}\r\n{}\n"); var results=new List<string>(); foreach(var b in bytes) results.AddRange(parser.Feed([b],1)); Assert.Equal(["{\"名\":1}","{}"],results); }
    [Fact] public void OversizeIsDiscardedInFullAndParserRecovers()
    { var parser=new JsonLineFramer(8); var bytes=Encoding.UTF8.GetBytes("12345678{}\n{}\n"); Assert.Equal(["{}"],parser.Feed(bytes,bytes.Length)); }
    [Fact] public void FrameLimitIncludesNewline()
    { var parser=new JsonLineFramer(4); Assert.Equal(["123"],parser.Feed(Encoding.UTF8.GetBytes("123\n"),4)); Assert.Empty(parser.Feed(Encoding.UTF8.GetBytes("1234\n"),5)); }
    [Fact] public void InvalidUtf8Discarded()
    { var parser=new JsonLineFramer(100); Assert.Empty(parser.Feed([0xff,0x0a],2)); }
}
