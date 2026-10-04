using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed class DemoCpuFan : ICpuFanDriver
{
    private int duty = 65;
    private int? original;
    public Task<CpuFanReading> ReadAsync(CancellationToken ct = default) => Task.FromResult(new CpuFanReading(duty, duty * 20, TimeProvider.System.GetTimestamp()));
    public Task<CpuFanReading> WriteAsync(int value, CancellationToken ct = default) { original ??= duty; duty = value; return ReadAsync(ct); }
    public Task RestoreAsync(CancellationToken ct = default) { if (original is { } saved) duty = saved; original = null; return Task.CompletedTask; }
}
