namespace FlowDeck.Core;

public sealed record CoolingCalibrationReading(int Duty,double Rpm,long Timestamp);
public sealed record CoolingCalibrationStart(DeviceDriverResult Result,CoolingCalibrationReading? Before=null);
public sealed record CoolingCalibrationResult(int Duty,CoolingCalibrationReading? Before,double? MinimumRpm,double? MaximumRpm,double? LastRpm,
    int Samples,bool Completed,bool Restored,Outcome Outcome,string Message);
public sealed record CoolingCalibrationRequest(PhysicalDeviceBinding Binding,int Duty,int BeforeDuty,bool Confirmed);
public static class CoolingCalibrationRules
{
    public static bool Valid(PhysicalDeviceBinding? b)=>b is not null&&DeviceConfigurationRules.Key(b.DeviceId)&&DeviceConfigurationRules.Key(b.DefinitionId)&&
        DeviceConfigurationRules.Text(b.ProviderIdentity,2048)&&b.Fingerprint is { Length:64 }&&b.Fingerprint.All(Uri.IsHexDigit);
    public static bool Valid(CoolingCalibrationRequest? r)=>r is { Duty:>=1 and <=100,BeforeDuty:>=0 and <=100 }&&Valid(r.Binding);
    public static bool Valid(CoolingCalibrationResult? r)=>r is null||r.Duty is >=1 and <=100&&r.Samples is >=0 and <=10000&&
        r.Outcome is Outcome.Confirmed or Outcome.Rejected or Outcome.Failed or Outcome.Unknown&&r.Message is { Length:<=16384 }&&
        (r.Before is null||r.Before.Duty is >=0 and <=100&&double.IsFinite(r.Before.Rpm)&&r.Before.Rpm>0)&&
        new[]{r.MinimumRpm,r.MaximumRpm,r.LastRpm}.All(v=>v is null||double.IsFinite(v.Value)&&v>0)&&
        (r.Samples==0?r.MinimumRpm is null&&r.MaximumRpm is null&&r.LastRpm is null:r.Before is not null&&r.MinimumRpm<=r.LastRpm&&r.LastRpm<=r.MaximumRpm)&&
        (!r.Completed||r.Samples>0&&r.Before is not null)&&
        (r.Outcome!=Outcome.Confirmed||r.Completed&&r.Restored);
}
public interface IDeviceCoolingCalibrationPersistence
{
    Task PauseCoolingForCalibrationAsync(EndpointReference target,CancellationToken ct);
}

// Constructed by the authenticated service after an explicit trial confirmation; never an IPC-deserializable actuation.
public sealed class CoolingCalibrationActuation
{
    private readonly PhysicalDeviceBinding binding;
    private readonly Func<CoolingAuthorization> authorize;
    public string EndpointId { get; }
    public PhysicalDeviceBinding Binding=>Contract.Copy(binding);
    public int Duty { get; }
    public int BeforeDuty { get; }
    internal CoolingCalibrationActuation(string endpointId,PhysicalDeviceBinding binding,int duty,int beforeDuty,Func<CoolingAuthorization> authorize)
    {
        if(!DeviceConfigurationRules.Key(endpointId)||!CoolingCalibrationRules.Valid(binding)||duty is <1 or >100||beforeDuty is <0 or >100) throw new ArgumentException("端子試験の出力が不正です");
        ArgumentNullException.ThrowIfNull(authorize);
        EndpointId=endpointId; this.binding=Contract.Copy(binding); Duty=duty; BeforeDuty=beforeDuty; this.authorize=authorize;
    }
    public CoolingAuthorization Authorize()=>authorize();
}
public interface IDeviceCoolingCalibrationDriver : IPhysicalDeviceDriver
{
    string? ValidateCalibrationStart(string endpointId,PhysicalDeviceBinding expected);
    Task<CoolingCalibrationStart> BeginCalibrationAsync(CoolingCalibrationActuation actuation,CancellationToken ct);
    Task<CoolingCalibrationReading> ReadCalibrationAsync(string endpointId,PhysicalDeviceBinding expected,CancellationToken ct);
    Task<DeviceDriverResult> EndCalibrationAsync(string endpointId,CancellationToken ct);
}

public static class CoolingCalibrationRunner
{
    public static readonly TimeSpan Duration=TimeSpan.FromSeconds(5),SampleInterval=TimeSpan.FromMilliseconds(250);
    // Each callback schedules one bounded device operation. The five-second wait never holds a hardware resource lease.
    public static async Task<CoolingCalibrationResult> RunAsync(int duty,Func<CancellationToken,Task<CoolingCalibrationStart>> begin,
        Func<CancellationToken,Task<CoolingCalibrationReading>> sample,Func<Task<DeviceDriverResult>> finish,Func<string?> guard,
        CancellationToken ct,TimeProvider? time=null)
    {
        if(duty is <1 or >100) throw new ArgumentOutOfRangeException(nameof(duty));
        var clock=time??TimeProvider.System; CoolingCalibrationReading? before=null; double? minimum=null,maximum=null,last=null;
        var count=0; var attempted=false; var completed=false; var message=""; var outcome=Outcome.Rejected; DeviceDriverResult? restored=null;
        static bool ValidResult(DeviceDriverResult? value)=>value is not null&&value.Outcome is Outcome.Confirmed or Outcome.Rejected or Outcome.Failed or Outcome.Unknown&&
            value.ReportedDuty is null or >=0 and <=100&&value.Message is { Length:<=16384 };
        void Check() { ct.ThrowIfCancellationRequested(); if(guard() is { } error) throw new IOException(error); }
        try
        {
            Check(); attempted=true; var started=await begin(ct).ConfigureAwait(false); before=started?.Before;
            if(before is not null&&(before.Duty is <0 or >100||!double.IsFinite(before.Rpm)||before.Rpm<=0)) before=null;
            if(!ValidResult(started?.Result)) { outcome=Outcome.Unknown; message="試験出力の応答が不正です"; }
            else if(started!.Result.Outcome!=Outcome.Confirmed) { outcome=started.Result.Outcome; message=started.Result.Message; }
            else
            {
                if(before is null||before.Duty is <0 or >100||!double.IsFinite(before.Rpm)||before.Rpm<=0||clock.GetElapsedTime(before.Timestamp).TotalSeconds is <0 or >=3)
                    throw new IOException("試験前の回転を確認できません");
                var start=clock.GetTimestamp();
                do
                {
                    await Task.Delay(SampleInterval,clock,ct).ConfigureAwait(false); Check();
                    var current=await sample(ct).ConfigureAwait(false); Check();
                    if(current.Duty!=duty||!double.IsFinite(current.Rpm)||current.Rpm<=0||clock.GetElapsedTime(current.Timestamp).TotalSeconds is <0 or >=3)
                        throw new IOException("試験出力または新しい回転数を確認できません");
                    minimum=Math.Min(minimum??current.Rpm,current.Rpm); maximum=Math.Max(maximum??current.Rpm,current.Rpm); last=current.Rpm; count++;
                } while(clock.GetElapsedTime(start)<Duration);
                completed=true; outcome=Outcome.Confirmed; message="端子試験を完了しました";
            }
        }
        catch(OperationCanceledException) { outcome=attempted?Outcome.Failed:Outcome.Rejected; message="端子試験を中止しました"; }
        catch(Exception e) { outcome=attempted?Outcome.Failed:Outcome.Rejected; message=e.Message; }
        finally
        {
            if(attempted)
            {
                try { restored=await finish().ConfigureAwait(false); }
                catch(Exception e) { restored=new(Outcome.Unknown,e.Message); }
            }
        }
        var released=!attempted||ValidResult(restored)&&restored?.Outcome==Outcome.Confirmed;
        if(!released) { outcome=Outcome.Unknown; message+=" / 元の制御の復元を確認できません: "+restored?.Message; }
        else if(attempted) message+=" / 元の制御へ戻しました";
        return new(duty,before,minimum,maximum,last,count,completed,released,outcome,message.Length>16384?message[..16384]:message);
    }
}
