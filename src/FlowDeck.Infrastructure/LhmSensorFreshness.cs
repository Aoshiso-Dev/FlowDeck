using System.Reflection;
using System.Runtime.CompilerServices;
using LibreHardwareMonitor.Hardware;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed record SensorAcquisition(long Timestamp, string? Problem);

/// <summary>Checks that the pinned provider assigned a reading during this update; unchanged numeric readings still count.</summary>
internal sealed class LhmSensorFreshness(Func<IHardware, IDisposable>? acquireSuperIo = null)
{
    internal static bool PatchedLibrary { get; } = typeof(Computer).Assembly.GetName().Version == new Version(0, 9, 6, 0) &&
        typeof(Computer).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(a => a.Key == "FlowDeck.LhmPatch" && a.Value == "8");
    private sealed class LastObservation(long timestamp)
    {
        public long Timestamp = timestamp;
        public ulong? SourceSample;
        public long SourceTimestamp;
        public float? SourceValue;
    }
    private readonly ConditionalWeakTable<ISensor, LastObservation> last = new();
    // An explicit assignment counter avoids dependence on optional history, averaging, and wall-clock timestamps.
    private static ulong? ReadStamp(ISensor sensor) => PatchedLibrary && sensor is ISensorUpdateEvidence evidence ? evidence.UpdateSequence : null;
    private static bool Relevant(ISensor sensor) => sensor.SensorType is SensorType.Temperature or SensorType.Fan or SensorType.Control;

    public IReadOnlyDictionary<ISensor, SensorAcquisition> Update(IHardware hardware, long timestamp, out string? error)
    {
        var before = new Dictionary<ISensor, ulong?>(ReferenceEqualityComparer.Instance);
        foreach (var sensor in hardware.Sensors.Where(Relevant)) before[sensor] = ReadStamp(sensor);
        error = null; string? acquisitionError = null;
        IDisposable? lease = null;
        try
        {
            if (hardware.HardwareType == HardwareType.SuperIO)
            {
                try { lease = (acquireSuperIo ?? LhmSuperIoReadLease.Acquire)(hardware); }
                catch (NotSupportedException e) { acquisitionError = e.Message; }
            }
            hardware.Update();
        }
        catch (Exception e) { error = "読み取り失敗: " + MotherboardDisplay.ShortText(e.Message, 240); }
        finally { lease?.Dispose(); }
        var observations = new Dictionary<ISensor, SensorAcquisition>(ReferenceEqualityComparer.Instance);
        foreach (var sensor in hardware.Sensors.Where(Relevant))
        {
            var saved = last.GetValue(sensor, _ => new(timestamp)); var after = ReadStamp(sensor);
            var updated = before.TryGetValue(sensor, out var previous) && previous is not null && after is not null && previous != after;
            // Unknown SuperIO implementations can reassign stale chip arrays. Only the verified lease supplies read evidence.
            var problem = error ?? acquisitionError ?? (after is null ? "このセンサーの更新時刻を確認できません" :
                updated ? null : "今回のセンサー値の更新を確認できません");
            var sample = PatchedLibrary && sensor is ISensorUpdateEvidence evidence ? evidence.SourceSample : null;
            if (sample is not null)
            {
                // Remember first observation even after a partial/failed update. A later retry cannot rejuvenate it.
                if (sample != saved.SourceSample)
                {
                    saved.SourceSample = sample; saved.SourceTimestamp = timestamp; saved.SourceValue = sensor.Value;
                }
                else if (sensor.Value != saved.SourceValue)
                    problem ??= "同じ測定サンプルの値が変化しました";
            }
            if (problem is null) saved.Timestamp = sample is null ? timestamp : saved.SourceTimestamp;
            observations.Add(sensor, new(saved.Timestamp, problem));
        }
        return observations;
    }
}
