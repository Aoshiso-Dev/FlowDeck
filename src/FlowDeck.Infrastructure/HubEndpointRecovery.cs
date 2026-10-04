using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record HubEndpointObligation(string DeviceId, string HubId, string Fingerprint, string WiringSignature,
    string Channel, int ExitDuty, int OriginalDuty, int BeforeDuty, int IntendedDuty);
public sealed record HubEndpointRecoveryDocument(int Version, HubEndpointObligation[] Pending, string Sha256);

/// <summary>One service-owned journal per physical Hub, with independent obligations for each output.</summary>
public sealed class HubEndpointRecoveryJournal
{
    private readonly object gate = new();
    private readonly string path, deviceId;
    private HubEndpointObligation[] pending = [];
    public HubEndpointObligation[] Pending { get { lock (gate) return pending.ToArray(); } }
    public HubEndpointRecoveryJournal(string directory, string deviceId)
    {
        if (!DeviceConfigurationRules.Key(deviceId)) throw new ArgumentException("Hubの識別が不正です");
        this.deviceId = deviceId;
        path = Path.Combine(directory, "hub-endpoints-" + SettingsTransfer.Hash(deviceId).ToLowerInvariant() + ".json");
        if (!File.Exists(path)) return;
        var saved = ServiceEnvironment.Read<HubEndpointRecoveryDocument>(path);
        if (saved.Version != 2 || saved.Pending is not { Length: <= 5 } || saved.Pending.Any(p => !Valid(p)) ||
            saved.Pending.DistinctBy(p => p.Channel).Count() != saved.Pending.Length || saved.Sha256 != SettingsTransfer.Hash(saved.Pending) ||
            saved.Pending.Select(p => (p.HubId, p.Fingerprint, p.WiringSignature)).Distinct().Count() > 1)
            throw new IOException("Hubの端子別復旧記録が不正です");
        pending = saved.Pending;
    }
    private bool Valid(HubEndpointObligation? p) => p is not null && p.DeviceId == deviceId &&
        DeviceConfigurationRules.Text(p.HubId, 2048) && p.Fingerprint?.Length == 64 && p.Fingerprint.All(Uri.IsHexDigit) &&
        DeviceConfigurationRules.Text(p.WiringSignature, 4096) && IsFan(p.Channel) && p.ExitDuty is >= 1 and <= 100 &&
        p.OriginalDuty is >= 0 and <= 100 && p.BeforeDuty is >= 0 and <= 100 && p.IntendedDuty is >= 0 and <= 100;
    public static bool IsFan(string? channel) => channel is "fan1" or "fan2" or "fan3" or "fan4" or "fan5";
    public HubEndpointObligation Prepare(string hubId, string fingerprint, string wiring, string channel, int exitDuty, int beforeDuty, int intendedDuty)
    {
        lock (gate)
        {
            var prior = pending.SingleOrDefault(p => p.Channel == channel);
            var next = new HubEndpointObligation(deviceId, hubId, fingerprint, wiring, channel, exitDuty,
                prior?.OriginalDuty ?? beforeDuty, beforeDuty, intendedDuty);
            if (!Valid(next)) throw new IOException("Hubの端子別復旧条件が不正です");
            if (pending.Any(p => p.HubId != hubId || p.Fingerprint != fingerprint || p.WiringSignature != wiring) ||
                prior is not null && (prior.ExitDuty != exitDuty || beforeDuty != prior.BeforeDuty && beforeDuty != prior.IntendedDuty))
                throw new IOException("未復旧のHubの個体・配線・終了時出力、または報告出力が変更されています");
            Store(pending.Where(p => p.Channel != channel).Append(next).OrderBy(p => p.Channel).ToArray());
            return next;
        }
    }
    public void ConfirmReleased(HubEndpointObligation expected)
    {
        lock (gate)
        {
            if (pending.SingleOrDefault(p => p.Channel == expected.Channel) != expected)
                throw new IOException("確認中にHubの復旧記録が変更されました");
            Store(pending.Where(p => p.Channel != expected.Channel).ToArray());
        }
    }
    private void Store(HubEndpointObligation[] next)
    {
        ServiceEnvironment.Write(path, new HubEndpointRecoveryDocument(2, next, SettingsTransfer.Hash(next)));
        pending = next;
    }
}
