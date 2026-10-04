using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed partial class AsrockDeviceDriver
{
    public string? ValidateLightingExpansion(IDeviceLightingStream current, EndpointLightingProgram[] programs)
    {
        lock (gate)
        {
            if (active is null || current != active || journal?.Pending is not { } saved) return "追加元の照明ストリームを確認できません";
            if (!DeviceLightingComposition.Valid(programs) || programs.Length <= active.Targets.Length ||
                active.Targets.Any(z => !programs.Any(p => p.EndpointId == "zone:" + z))) return "実行中のゾーンを含めて追加してください";
            if (DeviceLightingComposition.Validate(this, programs) is { } invalid) return invalid;
            if (saved.Targets.Length != active.Targets.Length || !saved.Targets.ToHashSet().SetEquals(active.Targets)) return "照明の復旧対象が変更されています";
            return null;
        }
    }
    public IDeviceLightingStream CreateLightingExpansion(IDeviceLightingStream current, EndpointLightingProgram[] programs)
    {
        lock (gate)
        {
            if (ValidateLightingExpansion(current, programs) is { } invalid) throw new IOException(invalid);
            var saved = journal!.Pending!; var original = active!;
            var zones = programs.Select(p => Zone(p.EndpointId)).Select(z => saved.Snapshot.Zones.Single(p => p.Address == z))
                .Select(z => new RgbZone("RGB " + z.Address, 1, (uint)z.Count, null, null, "", z.Address, z.Setting)).ToArray();
            var targets = zones.Select(z => z.HardwareZone!.Value).ToArray();
            var exits = targets.ToDictionary(z => z, z => settings[z].Config.Exit!);
            var originals = (saved.Handoff?.Before.Entries ?? []).Concat(nativeJournal?.Pending?.Entries ?? []).DistinctBy(e => e.Zone)
                .Where(e => targets.Contains(e.Zone)).ToDictionary(e => e.Zone, e => e.Original);
            return new Stream(this, original.Transport, targets, new(Contract.Copy(zones), Contract.Copy(exits), Contract.Copy(originals)), original);
        }
    }
}
