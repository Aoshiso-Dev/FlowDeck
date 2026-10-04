using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record HubLightingObligation(string DeviceId, string HubId, string Fingerprint, string WiringSignature,
    string Channel, string OwnerId, LightingExitPolicy Exit);
public sealed record HubLightingRecoveryDocument(int Version, HubLightingObligation[] Pending, string Sha256);

// The Hub cannot read its previous RGB program. Only an explicitly configured fixed/off exit is journaled.
public sealed class HubLightingRecoveryJournal
{
    private readonly object gate = new();
    private readonly string path, deviceId;
    private HubLightingObligation[] pending = [];
    public HubLightingObligation[] Pending { get { lock (gate) return Contract.Copy(pending); } }
    public HubLightingRecoveryJournal(string directory, string deviceId)
    {
        if (!DeviceConfigurationRules.Key(deviceId)) throw new ArgumentException("Hubの識別が不正です");
        this.deviceId = deviceId;
        path = Path.Combine(directory, "hub-lighting-" + SettingsTransfer.Hash(deviceId).ToLowerInvariant() + ".json");
        if (!File.Exists(path)) return;
        var saved = ServiceEnvironment.Read<HubLightingRecoveryDocument>(path);
        if (saved.Version != 2 || !ValidSet(saved.Pending) || saved.Sha256 != SettingsTransfer.Hash(saved.Pending))
            throw new IOException("Hubの照明復旧記録が不正です");
        pending = saved.Pending;
    }
    public static bool IsLed(string? channel) => channel is "led1" or "led2" or "led3" or "led4" or "led5";
    private bool Valid(HubLightingObligation? p) => p is not null && p.DeviceId == deviceId &&
        DeviceConfigurationRules.Text(p.HubId, 2048) && p.Fingerprint?.Length == 64 && p.Fingerprint.All(Uri.IsHexDigit) &&
        DeviceConfigurationRules.Text(p.WiringSignature, 4096) && IsLed(p.Channel) && DeviceServiceProtocol.Id(p.OwnerId) &&
        DeviceLightingConfiguration.ValidExit(p.Exit) && p.Exit.Action != LightingExitAction.RestoreOriginal;
    private bool ValidSet(HubLightingObligation[]? values) => values is { Length: <= 5 } && values.All(Valid) &&
        values.DistinctBy(p => p.Channel).Count() == values.Length &&
        values.Select(p => (p.HubId, p.Fingerprint, p.WiringSignature)).Distinct().Count() <= 1;
    public void Prepare(HubLightingObligation[] values)
    {
        lock (gate)
        {
            if (values.Length == 0 || !ValidSet(values)) throw new IOException("Hubの照明復旧条件が不正です");
            var channels = values.Select(p => p.Channel).ToHashSet();
            var next = pending.Where(p => !channels.Contains(p.Channel)).Concat(values).OrderBy(p => p.Channel).ToArray();
            if (!ValidSet(next) || values.Any(v => pending.SingleOrDefault(p => p.Channel == v.Channel) is { } old &&
                (old.HubId != v.HubId || old.Fingerprint != v.Fingerprint || old.WiringSignature != v.WiringSignature || old.Exit != v.Exit)))
                throw new IOException("未復旧のHubの個体・配線・照明終了設定が変更されています");
            if (next.SequenceEqual(pending)) return;
            Store(next);
        }
    }
    public void Complete(HubLightingObligation expected)
    {
        lock (gate)
        {
            if (pending.SingleOrDefault(p => p.Channel == expected.Channel) != expected)
                throw new IOException("Hubの照明復旧記録が変更されています");
            Store(pending.Where(p => p.Channel != expected.Channel).ToArray());
        }
    }
    private void Store(HubLightingObligation[] next)
    {
        ServiceEnvironment.Write(path, new HubLightingRecoveryDocument(2, next, SettingsTransfer.Hash(next)));
        pending = Contract.Copy(next);
    }
}
