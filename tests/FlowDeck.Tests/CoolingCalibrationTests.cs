using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

internal static class CalibrationTestActuation
{
    internal static CoolingCalibrationActuation Create(string endpoint,PhysicalDeviceBinding binding,int duty,int before,Func<CoolingAuthorization>? authorize=null)=>
        (CoolingCalibrationActuation)Activator.CreateInstance(typeof(CoolingCalibrationActuation),System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic,
            null,[endpoint,binding,duty,before,authorize??(()=>new())],null)!;
}
public class CoolingCalibrationTests
{
    private sealed class FastClock : TimeProvider
    {
        public override long GetTimestamp()=>TimeProvider.System.GetTimestamp()*50;
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)=>
            TimeProvider.System.CreateTimer(callback,state,dueTime==Timeout.InfiniteTimeSpan?dueTime:dueTime/50,period==Timeout.InfiniteTimeSpan?period:period/50);
    }
    [Fact] public async Task TrialSamplesForFiveSecondsAndAlwaysRestoresWithoutChoosingAMinimum()
    {
        var clock=new FastClock(); var samples=0; var restored=0;
        var result=await CoolingCalibrationRunner.RunAsync(55,ct=>Task.FromResult(new CoolingCalibrationStart(new(Outcome.Confirmed,"試験出力",55),new(45,900,clock.GetTimestamp()))),
            ct=>Task.FromResult(new CoolingCalibrationReading(55,1000+Interlocked.Increment(ref samples),clock.GetTimestamp())),
            ()=>{ restored++; return Task.FromResult(new DeviceDriverResult(Outcome.Confirmed,"復元",45)); },()=>null,default,clock);
        Assert.True(result.Completed); Assert.True(result.Restored); Assert.Equal(Outcome.Confirmed,result.Outcome); Assert.Equal(1,restored);
        Assert.True(result.Samples>1); Assert.Equal(45,result.Before!.Duty); Assert.Equal(1001,result.MinimumRpm); Assert.Equal(1000+samples,result.MaximumRpm);
    }
    [Theory] [InlineData("zero")] [InlineData("stale")] [InlineData("changed")] [InlineData("exception")] [InlineData("nan")]
    public async Task InvalidOrMissingTrialReadingsStopAndRestore(string failure)
    {
        var clock=new FastClock(); var restored=0;
        var result=await CoolingCalibrationRunner.RunAsync(55,ct=>Task.FromResult(new CoolingCalibrationStart(new(Outcome.Confirmed,""),new(45,900,clock.GetTimestamp()))),
            ct=>failure=="exception"?throw new IOException("切断"):Task.FromResult(new CoolingCalibrationReading(failure=="changed"?44:55,
                failure=="zero"?0:failure=="nan"?double.NaN:1000,failure=="stale"?clock.GetTimestamp()-clock.TimestampFrequency*4:clock.GetTimestamp())),
            ()=>{restored++;return Task.FromResult(new DeviceDriverResult(Outcome.Confirmed,"復元"));},()=>null,default,clock);
        Assert.False(result.Completed); Assert.True(result.Restored); Assert.Equal(Outcome.Failed,result.Outcome); Assert.Equal(1,restored);
    }
    [Fact] public async Task CancellationAfterBeginningDoesNotCancelRestoration()
    {
        var clock=new FastClock(); using var cancel=new CancellationTokenSource(); var restored=0;
        var result=await CoolingCalibrationRunner.RunAsync(100,ct=>{cancel.Cancel();return Task.FromResult(new CoolingCalibrationStart(new(Outcome.Confirmed,""),new(45,900,clock.GetTimestamp())));},
            ct=>throw new InvalidOperationException("Must not sample after cancellation"),()=>{restored++;return Task.FromResult(new DeviceDriverResult(Outcome.Confirmed,"復元"));},()=>null,cancel.Token,clock);
        Assert.Equal(Outcome.Failed,result.Outcome); Assert.True(result.Restored); Assert.Equal(1,restored); Assert.Equal(0,result.Samples);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LostWriteOrRestoreConfirmationCannotProduceASuccessfulTrial(bool restoreFails)
    {
        var clock=new FastClock();
        var result=await CoolingCalibrationRunner.RunAsync(100,ct=>Task.FromResult(new CoolingCalibrationStart(new(Outcome.Unknown,"応答不明"),new(45,900,clock.GetTimestamp()))),
            ct=>throw new InvalidOperationException("Must not sample an unknown write"),()=>Task.FromResult(new DeviceDriverResult(restoreFails?Outcome.Unknown:Outcome.Confirmed,"復元結果")),()=>null,default,clock);
        Assert.Equal(Outcome.Unknown,result.Outcome); Assert.False(result.Completed); Assert.Equal(!restoreFails,result.Restored);
    }
    [Fact] public async Task ARejectedPreflightNeverBeginsOrReleasesAnyEndpoint()
    {
        var result=await CoolingCalibrationRunner.RunAsync(100,ct=>throw new InvalidOperationException("no begin"),ct=>throw new InvalidOperationException("no read"),
            ()=>throw new InvalidOperationException("no release"),()=>"対象が変わりました",default);
        Assert.False(result.Completed); Assert.True(result.Restored); Assert.Equal(0,result.Samples); Assert.DoesNotContain("元の制御へ戻しました",result.Message);
    }
    [Theory] [InlineData(-4)] [InlineData(1)]
    public async Task InvalidPreTrialTimestampCannotCertifyCalibration(int seconds)
    {
        var clock=TimeProvider.System; var restored=0;
        var result=await CoolingCalibrationRunner.RunAsync(100,ct=>Task.FromResult(new CoolingCalibrationStart(new(Outcome.Confirmed,""),
                new(45,900,clock.GetTimestamp()+seconds*clock.TimestampFrequency))),
            ct=>throw new InvalidOperationException("must not sample"),()=>{restored++;return Task.FromResult(new DeviceDriverResult(Outcome.Confirmed,"復元"));},()=>null,default,clock);
        Assert.False(result.Completed); Assert.True(result.Restored); Assert.Equal(1,restored);
    }
    [Theory] [InlineData("accepted")] [InlineData("nan")] [InlineData("restore")]
    public async Task MalformedDriverResponsesRemainSerializableAndCannotProveSuccess(string failure)
    {
        var result=await CoolingCalibrationRunner.RunAsync(100,ct=>Task.FromResult(new CoolingCalibrationStart(
                new(failure=="nan"?Outcome.Confirmed:failure=="accepted"?Outcome.Accepted:Outcome.Unknown,"reply"),
                new(45,failure=="nan"?double.NaN:900,TimeProvider.System.GetTimestamp()))),
            ct=>throw new InvalidOperationException("must not sample"),()=>Task.FromResult(new DeviceDriverResult(Outcome.Confirmed,"restored",failure=="restore"?500:45)),()=>null,default);
        Assert.False(result.Completed); Assert.Equal(failure!="restore",result.Restored); Assert.True(CoolingCalibrationRules.Valid(result));
        _=System.Text.Json.JsonSerializer.Serialize(result,Contract.Json);
    }
}
