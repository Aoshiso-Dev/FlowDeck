namespace FlowDeck.Core;

public sealed record DeviceControlUsage(int Cooling, int Lighting, int Leds, int UnknownLedCounts);
public sealed class DeviceControlCapacityException(string message) : ArgumentException(message);

/// <summary>Saved control reservations, including disconnected and unconfirmed targets. Discovery and monitoring are separate.</summary>
public static class DeviceControlCapacity
{
    public const int MaximumCooling = 64;
    public const int MaximumLighting = 128;
    public const int MaximumLeds = 4096;

    public static DeviceControlUsage Count(DeviceRegistrations devices, CoolingConfiguration cooling, LightingConfiguration lighting)
    {
        var counts = devices.Devices.SelectMany(d => d.Endpoints.Select(e => (Target: new EndpointReference(d.Id, e.Id), e.Setup?.LedCount)))
            .ToDictionary(e => e.Target, e => e.LedCount);
        var targets = lighting.Endpoints ?? [];
        return new(cooling.Endpoints.Length, targets.Length, targets.Sum(e => counts.GetValueOrDefault(e.Target) ?? 0),
            targets.Count(e => counts.GetValueOrDefault(e.Target) is null));
    }

    public static string? Problem(DeviceControlUsage usage) => usage.Cooling > MaximumCooling ?
        $"冷却の制御設定は{MaximumCooling}端子までです（{usage.Cooling}端子）。不要な端子の制御設定を解除してください" :
        usage.Lighting > MaximumLighting ? $"照明の制御設定は{MaximumLighting}ゾーンまでです（{usage.Lighting}ゾーン）。不要な端子の制御設定を解除してください" :
        usage.Leds > MaximumLeds ? $"制御設定のLED合計は{MaximumLeds}個までです（{usage.Leds}個）。LED数を確認するか、不要な端子の制御設定を解除してください" : null;

    public static string? ReplacementProblem(DeviceRegistrations devices, CoolingConfiguration cooling, LightingConfiguration lighting,
        EndpointReference target, bool isCooling, int? leds)
    {
        var usage = Count(devices, cooling, lighting);
        if (isCooling) return Problem(usage with { Cooling = usage.Cooling + (cooling.Endpoints.Any(e => e.Target == target) ? 0 : 1) });
        var exists = (lighting.Endpoints ?? []).Any(e => e.Target == target);
        var previous = exists ? devices.Devices.SingleOrDefault(d => d.Id == target.DeviceId)?.Endpoints.SingleOrDefault(e => e.Id == target.EndpointId)?.Setup?.LedCount ?? 0 : 0;
        return Problem(usage with { Lighting = usage.Lighting + (exists ? 0 : 1), Leds = usage.Leds - previous + (leds ?? 0) });
    }
}
