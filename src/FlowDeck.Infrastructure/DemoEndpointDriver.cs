using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>Deterministic in-memory device for generic service/GUI tests. It never creates a hardware adapter.</summary>
public sealed class DemoEndpointDriver(string id, string name, DeviceKind kind, bool cooling = true, bool lighting = true, int minimum = 30) : IDeviceLightingCompositionDriver
{
    private readonly object gate = new();
    private bool fanOwned, lightOwned;
    public string DeviceId => id;
    public int FanWrites { get; private set; }
    public int LightWrites { get; private set; }
    public int Duty { get; private set; } = 50;
    public LightingProgram Color { get; private set; } = new("fixed", [new(52, 137, 255)]);
    public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public DeviceLightingLimits? LightingLimits => lighting ? new(100, 500, 24, EndpointColors: true) : null;
    public DeviceObservation Observe()
    {
        var endpoints = new List<DeviceEndpoint>();
        static DeviceCapability Capability(DeviceFeature f) => new(f, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready);
        if (cooling) endpoints.Add(new("fan1", "ファン", EndpointKind.Cooling, [Capability(DeviceFeature.FixedDuty), Capability(DeviceFeature.ReleaseCooling)]));
        if (lighting) endpoints.Add(new("light1", "照明", EndpointKind.Lighting, [Capability(DeviceFeature.FixedColor), Capability(DeviceFeature.ColorFrames), Capability(DeviceFeature.RestoreLighting)]));
        return new(id, "demo", name, kind, IdentityConfidence.SerialBound, "demo:" + id, "demo-v2", ["demo:" + id], endpoints.ToArray());
    }
    public string? Validate(DeviceEndpointAction action) => action.Operation switch
    {
        DeviceOperation.SetDuty => cooling && action.EndpointId == "fan1" && action.Duty is >= 0 and <= 100 && minimum is >= 1 and <= 100 ? null : "ファン設定が不正です",
        DeviceOperation.ReleaseCooling => cooling && action.EndpointId == "fan1" ? null : "ファンがありません",
        DeviceOperation.SetLighting => lighting && action.EndpointId == "light1" && action.Lighting is { } light &&
            DeviceConfigurationRules.ValidProgram(light) && light.LedColors is null && !light.Reverse &&
            (light.EffectId == "fixed" && light.Colors.Length == 1 || light.EffectId == "off") ? null : "照明が未対応です",
        DeviceOperation.StopLighting => lighting && action.EndpointId == "light1" ? null : "照明がありません",
        _ => "模擬機器で未対応の操作です"
    };
    public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); lock (gate)
        {
            if (Validate(action) is { } error) throw new ArgumentException(error);
            if (action.Operation is DeviceOperation.SetDuty or DeviceOperation.ReleaseCooling)
            {
                fanOwned = action.Operation == DeviceOperation.SetDuty; Duty = fanOwned ? Math.Max(minimum, action.Duty!.Value) : 65;
                FanWrites++; return Task.FromResult(new DeviceDriverResult(Outcome.Confirmed, "模擬設定を確認しました", Duty));
            }
            lightOwned = action.Operation == DeviceOperation.SetLighting;
            Color = lightOwned ? new("fixed", [action.Lighting!.EffectId == "off" ? new(0, 0, 0) : action.Lighting.Colors[0].Scale(action.Lighting.Brightness / 100d)])
                : new("fixed", [new(52, 137, 255)]); LightWrites++;
            return Task.FromResult(new DeviceDriverResult(Outcome.WrittenUnverified, "模擬照明へ送信しました", ReportedLighting: Contract.Copy(Color)));
        }
    }
    public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct)
    {
        lock (gate)
        {
            var result = new List<DeviceDriverReleaseResult>();
            if (fanOwned) { fanOwned = false; Duty = 65; result.Add(new("fan1", new(Outcome.Confirmed, "模擬ファンを解除しました", Duty))); }
            if (lightOwned) { lightOwned = false; Color = new("fixed", [new(52, 137, 255)]); result.Add(new("light1", new(Outcome.WrittenUnverified, "模擬照明を復元しました", ReportedLighting: Color))); }
            Released.TrySetResult(); return Task.FromResult(result.ToArray());
        }
    }
    public string? ValidateLightingStream(string[] endpoints, LightingProgram program) =>
        lighting && endpoints.SequenceEqual(["light1"]) && DeviceLighting.ValidAnimation(program) ? null : "模擬機器の同期設定が不正です";
    public Task<IDeviceLightingStream> OpenLightingStreamAsync(string[] endpoints, LightingProgram program, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); lock (gate)
        {
            if (ValidateLightingStream(endpoints, program) is { } error) throw new ArgumentException(error);
            return Task.FromResult<IDeviceLightingStream>(new Stream(this, Contract.Copy(program), Contract.Copy(Color)));
        }
    }
    private sealed class Stream(DemoEndpointDriver owner, LightingProgram program, LightingProgram original) : IDeviceLightingStream
    {
        private RgbColor? normalStopColor;
        public Task<DeviceDriverReleaseResult[]> WriteAsync(DeviceLightingFrame frame, CancellationToken ct)
        {
            if (frame.TargetError(["light1"]) is { } invalid)
                return Task.FromResult<DeviceDriverReleaseResult[]>([new("light1", new(Outcome.Rejected, invalid))]);
            var allowed = frame.Authorize();
            if (allowed.Error is { } error) return Task.FromResult<DeviceDriverReleaseResult[]>([new("light1", new(Outcome.Rejected, error,
                Failure: allowed.Expired ? DeviceDriverFailure.LightingFrameExpired : DeviceDriverFailure.None))]);
            lock (owner.gate)
            {
                owner.lightOwned = true; owner.LightWrites++; owner.Color = new("fixed", [frame.ColorFor("light1")]);
                normalStopColor = frame.NormalStopColorFor("light1") ?? program.Colors[0].Scale(program.Brightness / 100d);
                return Task.FromResult<DeviceDriverReleaseResult[]>([new("light1", new(Outcome.WrittenUnverified, "模擬フレームを送信しました", ReportedLighting: Contract.Copy(owner.Color)))]);
            }
        }
        public Task<DeviceDriverReleaseResult[]> EndAsync(DeviceLightingStopReason reason, CancellationToken ct)
        {
            lock (owner.gate)
            {
                owner.lightOwned = false; owner.LightWrites++; owner.Color = reason == DeviceLightingStopReason.Normal
                    ? new("fixed", [normalStopColor ?? program.Colors[0].Scale(program.Brightness / 100d)]) : original;
                return Task.FromResult<DeviceDriverReleaseResult[]>([new("light1", new(Outcome.WrittenUnverified, "模擬同期を終了しました", ReportedLighting: Contract.Copy(owner.Color)))]);
            }
        }
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
