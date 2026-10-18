using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public record LightIdentificationResult(bool Succeeded, bool Restored, bool Superseded, string Message, long? RestoredRevision = null);

public sealed partial class RuntimeHost
{
    public LightingNameCollection LightNames { get; private set; } = new(Names: []);
    public string? LightNamesError { get; private set; }
    public bool IdentifyingLight { get; private set; }
    private LightingNameStore LightNameStore => new(Store.DirectoryPath);
    public async Task LoadLightNamesAsync()
    {
        await lightingSceneGate.WaitAsync();
        try
        {
            try { LightNames = await LightNameStore.LoadAsync(); LightNamesError = null; }
            catch (Exception e) { LightNames = new(Names: []); LightNamesError = "照明名を読み込めません: " + e.Message; }
        }
        finally { lightingSceneGate.Release(); }
    }
    public async Task SaveLightNameAsync(LightAddress address, string name)
    {
        await lightingSceneGate.WaitAsync();
        try
        {
            if (LightNamesError is not null) throw new IOException(LightNamesError);
            var next = LightingNames.Set(LightNames, address, name);
            await LightNameStore.SaveAsync(next); LightNames = next;
        }
        finally { lightingSceneGate.Release(); }
    }
    public string LightName(LightAddress address, string fallback) => LightingNames.Find(LightNames, address) is { } name ? name + " · " + fallback : fallback;

    public async Task<LightIdentificationResult> IdentifyLightAsync(LightAddress address, CancellationToken cancellationToken = default)
    {
        if (!address.Valid) throw new ArgumentException("照明を1ポート選択してください");
        await lightingSceneGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (LightingSynchronized) throw new InvalidOperationException("同期停止後に識別点灯してください");
            await lightingCleanup;
            IdentifyingLight = true;
            var result = address.Hub is { } hub ? await IdentifyHubLightAsync(hub, cancellationToken) : await IdentifyBoardLightAsync(address.Board!, cancellationToken);
            Log.Write("lightingIdentification", new { address, result });
            if (!result.Restored && !result.Superseded)
            {
                var affected = new LightingScene(Guid.NewGuid().ToString("N"), "識別点灯",
                    address.Hub is { } h ? [new(h.HubId, h.Channel, new("fixed", [new(96,96,96)]))] : [],
                    address.Board is { } b ? [new(b.Identity, b.Zone, new(1,96,96,96))] : []);
                await InvalidateAdjustmentBaselineAsync(affected);
            }
            return result;
        }
        finally { IdentifyingLight = false; lightingSceneGate.Release(); }
    }
    private static async Task<bool> HoldIdentificationAsync(CancellationToken token)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(3), token); return true; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
    }
    private async Task<LightIdentificationResult> IdentifyHubLightAsync(HubLightAddress target, CancellationToken token)
    {
        var owner = Coordinator; var start = owner.Snapshot;
        if (owner.LightingTargetError(target.HubId, [target.Channel]) is { } error) throw new IOException(error);
        if (!owner.Settings.LightingPorts.TryGetValue(target.Channel, out var previous) || previous.HubId != target.HubId ||
            previous.Outcome != Outcome.WrittenUnverified || !HubLighting.Valid(previous.Effect))
            throw new IOException("元の設定が不明です。一度照明を適用してから識別してください");
        var original = Contract.Copy(previous.Effect!);
        Log.Write("lightingIdentificationStart", new { target, original });
        token.ThrowIfCancellationRequested();
        var sent = await owner.RequestAsync(new(Guid.NewGuid().ToString("N"), "識別点灯", owner.Epoch, start.LightingRevision, "SetLighting",
            LightingEffect: new("fixed", [new(96,96,96)]), LightingChannels: [target.Channel]));
        if (sent.Outcome == Outcome.Rejected) throw new IOException(sent.Message);
        var elapsed = sent.Outcome == Outcome.WrittenUnverified && await HoldIdentificationAsync(token);
        var current = owner.Snapshot;
        if (!ReferenceEquals(owner, Coordinator) || current.DeviceGeneration != start.DeviceGeneration || current.LightingRevision != sent.LightingRevision)
            return new(false, false, true, "接続または照明が変更されたため、自動復帰を取り消しました");
        // Expected revision is checked atomically by the coordinator, including another request between this check and dispatch.
        var restored = await owner.RequestAsync(new(Guid.NewGuid().ToString("N"), "識別点灯の復帰", owner.Epoch, sent.LightingRevision, "SetLighting",
            LightingEffect: original, LightingChannels: [target.Channel]));
        if (restored.Outcome != Outcome.WrittenUnverified)
            return new(false, false, restored.ErrorCode is "CONFLICT" or "STALE_SESSION", "元の設定への復帰を確認できません: " + restored.Message);
        return new(elapsed, true, false, elapsed ? "元の設定を送信しました" : "識別点灯を中断し、元の設定を送信しました", restored.LightingRevision);
    }
    private async Task<LightIdentificationResult> IdentifyBoardLightAsync(BoardLightAddress target, CancellationToken token)
    {
        await rgbGate.WaitAsync(token);
        try
        {
            if (!Demo && !MemoryLighting.IsIdentity(target.Identity) && Coordinator.Settings.Motherboard.OpenRgbEnabled) throw new IOException("マザー照明をUSB接続に切り替えてください");
            if (!Demo) Rgb = await ReadNativeRgbAsync();
            var zone = Rgb.Devices.SingleOrDefault(d => d.ControlIdentity == target.Identity)?.Zones.SingleOrDefault(z => z.HardwareZone == target.Zone && z.LedCount > 0);
            if (Rgb.Error is not null || zone is null || !BoardLighting.IsValid(zone.Configuration)) throw new IOException("対象または元の照明設定を取得できません");
            var original = zone.Configuration!; var identifying = new BoardLightSetting(1,96,96,96);
            var attempted = false; var elapsed = false; string? writeError = null;
            Log.Write("lightingIdentificationStart", new { target, original });
            try
            {
                token.ThrowIfCancellationRequested(); attempted = true;
                var sent = await ApplyBoardLightingCoreAsync(target.Identity, [new(target.Zone, original, identifying)]);
                if (sent.Verified) elapsed = await HoldIdentificationAsync(token);
                else writeError = "識別点灯の設定を確認できません";
            }
            catch (Exception e) when (attempted) { writeError = e.Message; }
            // The driver reads the exact USB identity and compares this temporary setting before restoring; external changes win.
            try
            {
                var restored = await ApplyBoardLightingCoreAsync(target.Identity, [new(target.Zone, identifying, original)]);
                if (!restored.Verified) return new(false, false, false, "元の設定への復帰を確認できません。再取得してください");
            }
            catch (Exception e) { return new(false, false, false, "元の設定への復帰を確認できません: " + e.Message); }
            return new(elapsed && writeError is null, true, false, writeError is not null ? writeError + " · 元の設定は復帰済み" : elapsed ? "元の設定へ復帰しました" : "識別点灯を中断し、元の設定へ復帰しました");
        }
        finally { rgbGate.Release(); }
    }
}
