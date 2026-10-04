using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class MotherboardDeviceDriver
{
    private readonly HashSet<string> calibrating=[];
    private string? CalibrationDevice(string endpoint,PhysicalDeviceBinding expected)
    {
        if(disposed||journal is null||recoveryError is not null) return recoveryError??"マザーの試験・復旧を利用できません";
        if(owner.Cached(DeviceId) is not { } current||identity!=MotherboardCoolingIdentity.From(current)||expected!=BindingCandidate||expected.DeviceId!=DeviceId)
            return "試験対象のマザーの個体・接続を確認してください";
        if(Definition?.Ports.Any(p=>p.Id==endpoint)!=true) return "未対応のマザー端子です";
        return null;
    }
    public string? ValidateCalibrationStart(string endpointId,PhysicalDeviceBinding expected)
    {
        lock(gate)
        {
            if(CalibrationDevice(endpointId,expected) is { } error) return error;
            var port=Definition!.Ports.Single(p=>p.Id==endpointId);
            if(calibrating.Contains(endpointId)||journal!.Pending.Any(p=>p.Chip==port.Chip&&p.Channel==port.Channel)) return "この端子の制御を終了してから試験してください";
            var samples=ReadCachedSamples();
            var rpm=samples.SingleOrDefault(s=>s.EndpointId==endpointId&&s.Feature==DeviceFeature.Rpm);
            var duty=samples.SingleOrDefault(s=>s.EndpointId==endpointId&&s.Feature==DeviceFeature.DutyRead);
            return rpm?.Value is not >0||duty?.Value is not (>=0 and <=100)||new[]{rpm,duty}.Any(s=>s!.Problem is not null||clock.GetElapsedTime(s.Timestamp).TotalSeconds is <0 or >=3)
                ?"回転中のファンと新しい出力値を確認してください":null;
        }
    }
    private static CoolingCalibrationReading CalibrationReading(CpuFanReading reading)=>new((int)Math.Round(reading.Duty),reading.Rpm,reading.Timestamp);
    public async Task<CoolingCalibrationStart> BeginCalibrationAsync(CoolingCalibrationActuation actuation,CancellationToken ct)
    {
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock(gate) if(ValidateCalibrationStart(actuation.EndpointId,actuation.Binding) is { } error) return new(new(Outcome.Rejected,error));
            var before=await ExecuteAsync(Command(actuation.EndpointId,MotherboardFanOperation.Read),ct).ConfigureAwait(false)??throw new IOException("端子の実測がありません");
            if(before.Rpm<=0||(int)Math.Round(before.Duty)!=actuation.BeforeDuty) return new(new(Outcome.Rejected,"試験前に端子の出力・回転が変わりました"));
            var started=HardwareWriteDeadline.Now; var authorization=actuation.Authorize();
            if(authorization.Error is { } denied) return new(new(Outcome.Rejected,denied));
            var deadline=HardwareWriteDeadline.FromBudget(started,authorization.ValidFor??TimeSpan.FromSeconds(3));
            lock(gate)
            {
                if(ValidateCalibrationStart(actuation.EndpointId,actuation.Binding) is { } error) return new(new(Outcome.Rejected,error));
                calibrating.Add(actuation.EndpointId); attempted.Add(actuation.EndpointId);
            }
            try
            {
                var reading=await ExecuteAsync(Command(actuation.EndpointId,MotherboardFanOperation.Write,actuation.Duty,actuation.BeforeDuty,deadline),ct).ConfigureAwait(false);
                return new(reading is not null&&(int)Math.Round(reading.Duty)==actuation.Duty?new(Outcome.Confirmed,"試験出力を確認しました",actuation.Duty):
                    new(Outcome.Unknown,"試験出力を確認できません"),CalibrationReading(before));
            }
            catch(Exception e) { return new(new(Outcome.Unknown,e.Message),CalibrationReading(before)); }
        }
        finally { io.Release(); }
    }
    public async Task<CoolingCalibrationReading> ReadCalibrationAsync(string endpointId,PhysicalDeviceBinding expected,CancellationToken ct)
    {
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock(gate)
            {
                if(!calibrating.Contains(endpointId)) throw new IOException("この端子は試験中ではありません");
                if(CalibrationDevice(endpointId,expected) is { } error) throw new IOException(error);
            }
            var value=await ExecuteAsync(Command(endpointId,MotherboardFanOperation.Read),ct).ConfigureAwait(false)??throw new IOException("回転数を取得できません");
            return CalibrationReading(value);
        }
        finally { io.Release(); }
    }
    public async Task<DeviceDriverResult> EndCalibrationAsync(string endpointId,CancellationToken ct)
    {
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock(gate) if(!calibrating.Contains(endpointId)) return new(Outcome.Confirmed,"この端子では試験を開始していません");
            var result=await ReleaseOneAsync(endpointId,ct).ConfigureAwait(false);
            if(result.Outcome==Outcome.Confirmed) lock(gate) calibrating.Remove(endpointId);
            return result;
        }
        finally { io.Release(); }
    }
}
