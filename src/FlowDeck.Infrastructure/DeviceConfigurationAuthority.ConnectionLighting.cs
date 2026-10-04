using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceConfigurationAuthority : IDeviceConnectionLightingHandler
{
    // Never overlay transient settings into Configuration: snapshots, exports and all save paths remain durable-only.
    private DeviceConfigurationReply PrepareConnectionLighting(string owner, DeviceControlRuntime runtime)
    {
        var basis = configuration; var before = generation; var revision = Volatile.Read(ref connectionPreparationRevision);
        bool Current() => !applyingConfiguration && failure is null && ReferenceEquals(configuration, basis) && generation == before &&
            Volatile.Read(ref connectionPreparationRevision) == revision;
        // Any saved registration is intentional: imported, cleared, removed setup and old bindings are never reapproved.
        var excluded = basis.Devices.Devices.Select(d => d.Id).Concat(basis.Bindings.Devices.Select(b => b.DeviceId)).ToHashSet();
        var count = runtime.PrepareConnectionLighting(owner, excluded,
            DeviceControlCapacity.Count(basis.Devices, basis.Cooling, basis.Lighting), Current);
        return Reply("現在の接続で使えるメモリ照明を確認しました") with { PreparedLighting = count };
    }
    private long connectionPreparationRevision;
}
