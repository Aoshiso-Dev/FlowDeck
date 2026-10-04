using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class HubDeviceDriver
{
    private readonly HashSet<string> calibrating=[];
    private string? CalibrationDevice(string endpoint,PhysicalDeviceBinding expected)
    {
        if(!DefinitionData.Cooling.CalibrationSupported) return "このHub定義では端子試験に対応していません";
        if(disposed||!connected||status is null||candidate!=expected||expected.DeviceId!=DeviceId) return "試験対象のHubの個体・接続を確認してください";
        if(!adapter.SupportsFanEndpoints||!adapter.SupportsFanDeadlines) return "このHubの端子試験に対応するWorkerが必要です";
        if(status.Channels.SingleOrDefault(c=>c.Id==endpoint) is not { Presence:Presence.Present,Duty:>=0,ControlMode:"PWM" or "DC" }) return "接続した端子の出力を取得できません";
        if(sampleTimestamp is null||clock.GetElapsedTime(sampleTimestamp.Value).TotalSeconds is <0 or >=3) return "端子の新しい値が必要です";
        return null;
    }
    public string? ValidateCalibrationStart(string endpointId,PhysicalDeviceBinding expected)
    {
        lock(gate)
        {
            if(CalibrationDevice(endpointId,expected) is { } error) return error;
            if(calibrating.Contains(endpointId)||journal.Pending.Any(p=>p.Channel==endpointId)) return "この端子の制御を終了してから試験してください";
            return status!.Channels.Single(c=>c.Id==endpointId).Rpm is not >0?"回転中のファンを確認してください":null;
        }
    }
    private CoolingCalibrationReading CalibrationReading(string endpoint)=>new(status!.Channels.Single(c=>c.Id==endpoint).Duty!.Value,
        status.Channels.Single(c=>c.Id==endpoint).Rpm??throw new IOException("回転数がありません"),sampleTimestamp!.Value);
    public async Task<CoolingCalibrationStart> BeginCalibrationAsync(CoolingCalibrationActuation actuation,CancellationToken ct)
    {
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock(gate) if(ValidateCalibrationStart(actuation.EndpointId,actuation.Binding) is { } error) return new(new(Outcome.Rejected,error));
            await ReadCoreAsync(ct).ConfigureAwait(false);
            var started=HardwareWriteDeadline.Now; var authorization=actuation.Authorize();
            if(authorization.Error is { } denied) return new(new(Outcome.Rejected,denied));
            var deadline=HardwareWriteDeadline.FromBudget(started,authorization.ValidFor??TimeSpan.FromSeconds(3));
            HubEndpointObligation obligation; CoolingCalibrationReading before;
            lock(gate)
            {
                if(ValidateCalibrationStart(actuation.EndpointId,actuation.Binding) is { } error) return new(new(Outcome.Rejected,error));
                before=CalibrationReading(actuation.EndpointId);
                if(before.Duty!=actuation.BeforeDuty) return new(new(Outcome.Rejected,"試験前に端子の出力が変わりました"));
                // The trial restores the pre-test output, never a guessed minimum or an unrelated saved exit value.
                calibrating.Add(actuation.EndpointId); attempted.Add(actuation.EndpointId);
                obligation=journal.Prepare(nativeId,candidate!.Fingerprint,ControlCoordinator.WiringSignature(status!),actuation.EndpointId,before.Duty,before.Duty,actuation.Duty);
            }
            return new(await WriteCoreAsync(obligation,ct,deadline).ConfigureAwait(false),before);
        }
        finally { io.Release(); }
    }
    public async Task<CoolingCalibrationReading> ReadCalibrationAsync(string endpointId,PhysicalDeviceBinding expected,CancellationToken ct)
    {
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock(gate) if(!calibrating.Contains(endpointId)) throw new IOException("この端子は試験中ではありません");
            await ReadCoreAsync(ct).ConfigureAwait(false);
            lock(gate) { if(CalibrationDevice(endpointId,expected) is { } error) throw new IOException(error); return CalibrationReading(endpointId); }
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
            if(result.Outcome==Outcome.Confirmed) lock(gate) { calibrating.Remove(endpointId); attempted.Remove(endpointId); }
            return result;
        }
        finally { io.Release(); }
    }
}
