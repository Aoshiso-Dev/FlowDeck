using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// Service-local register evidence, never a token accepted from IPC or serialized into telemetry.
internal sealed record MotherboardFanControlSnapshot(string Chip, int Channel, byte Mode, byte Command);

internal sealed record MotherboardFanReading : CpuFanReading
{
    internal MotherboardFanControlSnapshot Control { get; }
    internal MotherboardFanReading(CpuFanReading reading, MotherboardFanControlSnapshot control)
        : base(reading.Duty, reading.Rpm, reading.Timestamp) => Control = control;
}
