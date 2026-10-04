using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public record HubFanObligation(string HubId, string WiringSignature, int ExitDuty, int IntendedDuty, Dictionary<string, int> Before);
public record HubFanRecoveryDocument(int Version, HubFanObligation? Pending, string Sha256);

public sealed class HubFanRecoveryJournal
{
    private readonly string path;
    public HubFanObligation? Pending { get; private set; }
    public HubFanRecoveryJournal(string directory)
    {
        path = Path.Combine(directory, "hub-fan-recovery.json");
        if (!File.Exists(path)) return;
        var saved = ServiceEnvironment.Read<HubFanRecoveryDocument>(path);
        if (saved.Version != 1 || saved.Sha256 != SettingsTransfer.Hash(saved.Pending) || saved.Pending is { } value && !Valid(value))
            throw new IOException("Hubのファン復旧記録が不正です");
        Pending = saved.Pending;
    }
    private static bool Valid(HubFanObligation value) => !string.IsNullOrWhiteSpace(value.HubId) && value.HubId.Length <= 2048 &&
        !string.IsNullOrWhiteSpace(value.WiringSignature) && value.WiringSignature.Length <= 4096 && value.ExitDuty is >= 1 and <= 100 &&
        value.IntendedDuty is >= 0 and <= 100 && value.Before is { Count: >= 1 and <= 5 } &&
        value.Before.All(p => p.Key is "fan1" or "fan2" or "fan3" or "fan4" or "fan5" && p.Value is >= 0 and <= 100);
    public void Save(HubFanObligation next)
    {
        if (!Valid(next)) throw new IOException("Hubの復旧対象または終了時出力が不正です");
        if (Pending is { } prior && (prior.HubId != next.HubId || prior.WiringSignature != next.WiringSignature || prior.ExitDuty != next.ExitDuty ||
            !prior.Before.Keys.Order().SequenceEqual(next.Before.Keys.Order()))) throw new IOException("未復旧のHubの対象・配線・終了時出力を変更できません");
        Store(next);
    }
    public void Clear() => Store(null);
    private void Store(HubFanObligation? next)
    {
        ServiceEnvironment.Write(path, new HubFanRecoveryDocument(1, next, SettingsTransfer.Hash(next)));
        Pending = next;
    }
}

/// <summary>Persists an exit obligation before a Hub fan write; lighting remains unaffected.</summary>
public sealed class RecoverableHubAdapter(IHubAdapter inner, HubFanRecoveryJournal journal, Func<AppSettings> settings) : IHubAdapter
{
    private readonly SemaphoreSlim gate = new(1);
    private HubIdentity? opened;
    private long innerGeneration;
    private long NextGeneration(long requested) => innerGeneration = Math.Max(checked(innerGeneration + 1), requested);
    public bool IsDemo => inner.IsDemo;
    public string Version => inner.Version;
    public Task<HubIdentity[]> ProbeAsync(CancellationToken ct = default) => inner.ProbeAsync(ct);
    private async Task<HubIdentity> ResolveAsync(string id, CancellationToken ct)
    {
        var matches = (await inner.ProbeAsync(ct)).Where(h => h.Id == id).ToArray();
        return matches.Length == 1 ? matches[0] : throw new IOException("接続対象のHubを一意に検出できません");
    }
    public async Task<HubStatus> OpenAsync(HubIdentity identity, long generation, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (journal.Pending is { } saved && saved.HubId != identity.Id) throw new IOException("未復旧のHubと別の機器へ接続できません");
            // A persisted or client-supplied path is never an executable device address in the service.
            var detected = await ResolveAsync(identity.Id, ct);
            opened = null;
            var status = await inner.OpenAsync(detected, NextGeneration(generation), ct); opened = detected; return status;
        }
        finally { gate.Release(); }
    }
    public Task<HubStatus> ReadAsync(CancellationToken ct = default) => inner.ReadAsync(ct);
    private static Dictionary<string, int> Duties(HubStatus status, IEnumerable<string> channels) => channels.ToDictionary(id => id, id =>
    {
        var channel = status.Channels.SingleOrDefault(c => c.Id == id);
        return channel is { Presence: Presence.Present, Duty: >= 0 and <= 100 } ? channel.Duty.Value
            : throw new IOException("Hubの復旧対象の報告出力を取得できません: " + id);
    });
    private static bool Confirmed(HubResult result, HubFanObligation saved) => result.Outcome == Outcome.Confirmed &&
        result.Status is { } status && saved.Before.Keys.All(id => status.Channels.Any(c => c.Id == id && c.Presence == Presence.Present && c.Duty == saved.ExitDuty));
    public async Task<HubResult> SetFanGroupAsync(string[] channels, int duty, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var current = settings(); var status = await inner.ReadAsync(ct);
            if (opened is null || opened.Id != current.Hardware.Hub?.Id || !current.Hardware.WiringConfirmed ||
                current.Hardware.WiringSignature != ControlCoordinator.WiringSignature(status) || current.Limits.Exit is not (>= 1 and <= 100) ||
                current.Hardware.Fans is null || !current.Hardware.Fans.Order().SequenceEqual(channels.Order()))
                throw new IOException("Hubの個体・配線・終了時出力を確認できません");
            var saved = new HubFanObligation(opened.Id, current.Hardware.WiringSignature, current.Limits.Exit.Value, duty, Duties(status, channels));
            journal.Save(saved);
            var result = await inner.SetFanGroupAsync(channels, duty, ct);
            if (duty == saved.ExitDuty && Confirmed(result, saved)) journal.Clear();
            return result;
        }
        finally { gate.Release(); }
    }
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (journal.Pending is not { } saved) return;
            var identity = await ResolveAsync(saved.HubId, ct);
            var status = await inner.OpenAsync(identity, NextGeneration(1), ct); opened = identity;
            if (ControlCoordinator.WiringSignature(status) != saved.WiringSignature) throw new IOException("復旧対象のHubの配線が変わっています");
            var duties = Duties(status, saved.Before.Keys);
            if (duties.All(p => p.Value == saved.ExitDuty)) { journal.Clear(); return; }
            if (duties.Any(p => p.Value != saved.Before[p.Key] && p.Value != saved.IntendedDuty))
                throw new IOException("Hubの出力が別の制御で変わっています。以前の設定を自動適用できません");
            var restore = saved with { Before = duties, IntendedDuty = saved.ExitDuty };
            journal.Save(restore);
            var result = await inner.SetFanGroupAsync(saved.Before.Keys.ToArray(), saved.ExitDuty, ct);
            if (!Confirmed(result, saved)) throw new IOException("Hubの終了時出力を確認できません");
            journal.Clear();
        }
        finally { gate.Release(); }
    }
    public Task<HubResult> SetLightingAsync(string[] channels, string preset, CancellationToken ct = default) => inner.SetLightingAsync(channels, preset, ct);
    public Task<HubResult> SetLightingEffectAsync(string[] channels, HubLightSetting effect, CancellationToken ct = default) => inner.SetLightingEffectAsync(channels, effect, ct);
    public Task CloseAsync(CancellationToken ct = default) => inner.CloseAsync(ct);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
