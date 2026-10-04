using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed class DemoHub(HubIdentity? selectedIdentity = null) : IHubLightingEndpointAdapter
{
    private int lightingPortCount = 1;
    public int LightingPortCount { get => lightingPortCount; init => lightingPortCount = value is >= 1 and <= 3 ? value : throw new ArgumentOutOfRangeException(nameof(value)); }
    private readonly HubIdentity instanceIdentity = selectedIdentity ?? Identity;
    private int[] duties = [45, 45, 45, 45, 45];
    public bool IsDemo => true;
    public bool SupportsFanEndpoints => true;
    public bool SupportsFanDeadlines => true;
    public bool SupportsLightingEndpoints => true;
    public bool SupportsLightingDeadlines => true;
    public string Version => "DEMO / no hardware";
    public bool Disconnected { get; set; }
    public bool FailNextFanWrite { get; set; }
    public int Writes { get; private set; }
    public int LightingWrites { get; private set; }
    public int LightingNonFrameWrites { get; private set; }
    public int DelayMs { get; set; } = 350;
    public Func<CancellationToken,Task>? BeforeFanWrite { get; set; }
    public static readonly HubIdentity Identity = new("demo", "DEMO Control Hub", "DEMO", "virtual");
    public static AppSettings CreateSettings()
    {
        var profiles = new[] { ("quiet", "静音", 30), ("balanced", "標準", 45), ("cool", "冷却優先", 70) }
            .Select(p => new AutoProfile(p.Item1, p.Item2, 1,
                new("demo/cpu", [new(30, 25), new(60, p.Item3), new(75, 85), new(90, 100)], 95, 85),
                new("demo/gpu", [new(30, 25), new(65, p.Item3), new(78, 90), new(88, 100)], 90, 80), 3, 2)).ToArray();
        var data = CreateStatus([45, 45, 45, 45, 45]);
        return new() { Hardware = new(Identity, data.Channels.Select(c => c.Id).ToArray(), data.Lighting.Select(c => c.Id).ToArray(),
            ControlCoordinator.WiringSignature(data), true, data.Lighting), Limits = new(25, 100, 70),
            CpuSensorId = "demo/cpu", GpuSensorId = "demo/gpu", Profiles = profiles };
    }
    private static HubStatus CreateStatus(int[] duty, int lightingPorts = 1) => new(Enumerable.Range(1, 5).Select(i =>
        new FanChannel($"fan{i}", Presence.Present, duty[i - 1] == 0 ? 0 : 280 + duty[i - 1] * 17 + i * 11,
        duty[i - 1], "PWM")).ToArray(), Enumerable.Range(1, lightingPorts).Select(i => new LedChannel("led" + i, "DEMO RGB", 24)).ToArray(), "DEMO-1");
    private void Check() { if (Disconnected) throw new IOException("DEMO: Hub切断"); }
    public Task<HubIdentity[]> ProbeAsync(CancellationToken ct = default) => Task.FromResult(Disconnected ? Array.Empty<HubIdentity>() : [instanceIdentity]);
    public Task<HubStatus> OpenAsync(HubIdentity identity, long generation, CancellationToken ct = default) { Check(); return ReadAsync(ct); }
    public Task<HubStatus> ReadAsync(CancellationToken ct = default) { Check(); return Task.FromResult(CreateStatus(duties, LightingPortCount)); }
    public async Task<HubResult> SetFanGroupAsync(string[] channels, int duty, CancellationToken ct = default)
    {
        Check(); Writes++; await Task.Delay(DelayMs, ct); Check();
        var partial = FailNextFanWrite; FailNextFanWrite = false;
        foreach (var id in channels) { var i = int.Parse(id.AsSpan(3)) - 1; if (!partial || i != 2) duties[i] = duty; }
        var state = CreateStatus(duties, LightingPortCount);
        return new(partial ? Outcome.Partial : Outcome.Confirmed, state, channels.Select(id =>
            new ChannelResult(id, "completed", state.Channels.Single(c => c.Id == id).Duty,
                state.Channels.Single(c => c.Id == id).Duty == duty ? "confirmed" : "mismatch")).ToArray(), partial ? "DUTY_MISMATCH" : null);
    }
    public async Task<HubResult> SetFanEndpointAsync(string channel, int duty, HubEndpointExpectation expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var current = await ReadAsync(ct);
        if (expected.HubId != instanceIdentity.Id || expected.WiringSignature != ControlCoordinator.WiringSignature(current) ||
            current.Channels.SingleOrDefault(c => c.Id == channel)?.Duty != expected.CurrentDuty || duty is < 0 or > 100)
            return new(Outcome.Rejected, Error: "DEMO: 端子の条件が変更されました");
        if(BeforeFanWrite is { } before) await before(ct);
        await Task.Delay(DelayMs, ct); Check();
        if (HardwareWriteDeadline.Expired(expected.ValidUntilTick64))
            return new(Outcome.Rejected, current, Error: HardwareWriteDeadline.ExpiredError);
        Writes++; var partial = FailNextFanWrite; FailNextFanWrite = false;
        if (!partial || channel != "fan3") duties[int.Parse(channel.AsSpan(3)) - 1] = duty;
        var state = CreateStatus(duties, LightingPortCount); var reported = state.Channels.Single(c => c.Id == channel).Duty;
        return new(partial && channel == "fan3" ? Outcome.Partial : Outcome.Confirmed, state,
            [new(channel, "completed", reported, reported == duty ? "confirmed" : "mismatch")], reported == duty ? null : "DUTY_MISMATCH");
    }
    public System.Collections.Concurrent.ConcurrentDictionary<string, HubLightSetting> LightEffects { get; } = new();
    public bool FailNextLightingWrite { get; set; }
    public Task<HubResult> SetLightingEffectAsync(string[] channels, HubLightSetting effect, CancellationToken ct = default)
    {
        Check(); ct.ThrowIfCancellationRequested();
        if (!HubLighting.Valid(effect)) return Task.FromResult(new HubResult(Outcome.Rejected));
        foreach (var channel in channels) LightEffects[channel] = effect;
        if (FailNextLightingWrite) { FailNextLightingWrite = false; return Task.FromResult(new HubResult(Outcome.Unknown, Error: "DEMO: 照明応答不明")); }
        return Task.FromResult(new HubResult(Outcome.WrittenUnverified));
    }
    public async Task<HubResult> SetLightingEndpointsAsync(string[] channels, HubLightSetting effect, HubLightingExpectation expected, CancellationToken ct = default)
    {
        Check(); ct.ThrowIfCancellationRequested(); var state = await ReadAsync(ct);
        if (!HubLighting.Valid(effect) || channels.Length == 0 || channels.Distinct().Count() != channels.Length ||
            channels.Any(c => !state.Lighting.Any(l => l.Id == c)) || expected.HubId != instanceIdentity.Id ||
            expected.WiringSignature != ControlCoordinator.WiringSignature(state)) return new(Outcome.Rejected, Error: "DEMO: 照明の条件が変更されました");
        await Task.Delay(DelayMs, ct); Check();
        if (HardwareWriteDeadline.Expired(expected.ValidUntilTick64)) return new(Outcome.Rejected, Error: HardwareWriteDeadline.LightingExpiredError);
        LightingWrites += channels.Length;
        if (expected.ValidUntilTick64 is null) LightingNonFrameWrites += channels.Length;
        foreach (var channel in channels) LightEffects[channel] = Contract.Copy(effect);
        if (FailNextLightingWrite) { FailNextLightingWrite = false; return new(Outcome.Unknown, Error: "DEMO: 照明応答不明"); }
        return new(Outcome.WrittenUnverified, Channels: channels.Select(c => new ChannelResult(c, "completed", null, "writtenUnverified")).ToArray());
    }
    public async Task<HubResult> SetLightingAsync(string[] channels, string preset, CancellationToken ct = default)
    { Check(); await Task.Delay(DelayMs, ct); Check(); return new(Outcome.WrittenUnverified, Channels: channels.Select(c => new ChannelResult(c, "completed", null, "writtenUnverified")).ToArray()); }
    public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
public sealed class DemoTemperatures(TimeProvider? clock = null) : ITemperatureSource
{
    public bool Missing { get; set; }
    public double Cpu { get; set; } = 58;
    public double Gpu { get; set; } = 64;
    public Task<SensorReading[]> ReadAsync(CancellationToken ct = default) => Task.FromResult<SensorReading[]>([
        new("demo/cpu", "CPU Package (DEMO)", "cpu", Missing ? null : Cpu, (clock ?? TimeProvider.System).GetTimestamp()),
        new("demo/gpu", "GPU Core (DEMO)", "gpu", Missing ? null : Gpu, (clock ?? TimeProvider.System).GetTimestamp())]);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
