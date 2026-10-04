using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed record AsrockZoneSnapshot(byte Address, int Count, BoardLightSetting Setting);
internal sealed record AsrockRgbSnapshot(string Identity, byte Available, byte Order, byte[] Counts, AsrockZoneSnapshot[] Zones);
internal sealed record AsrockRgbRestoreResult(bool OrderRestored, BoardLightResult[] Zones, bool TopologyUnchanged, string[] Errors)
{
    public bool Restored => OrderRestored && TopologyUnchanged && Zones.Length > 0 && Zones.All(z => z.Status == "verified") && Errors.Length == 0;
}

/// <summary>All-device volatile transaction. The caller owns the HID mutex.
/// Durable backup runs before any mutation. Recovery is independent of the animation cancellation token.</summary>
internal sealed class AsrockVolatileTransaction
{
    private readonly Func<byte[], byte[]> exchange;
    public AsrockRgbSnapshot Snapshot { get; }
    private bool mutationAttempted;
    private bool restoreAttempted;

    public AsrockVolatileTransaction(string identity, Func<byte[], byte[]> exchange)
    {
        this.exchange = exchange;
        var available = Read(0x14, 1)[4]; var counts = Read(0x14, 2)[4..12]; var order = Read(0x14, 3)[4];
        var zones = new List<AsrockZoneSnapshot>();
        for (byte zone = 0; zone < 8; zone++)
            if ((available & (1 << zone)) != 0 && counts[zone] is not (0 or 0x1e))
                zones.Add(new(zone, counts[zone], AsrockRgbControl.Decode(Read(0x11, zone), order)));
        if (zones.Count == 0 || zones.Any(z => !BoardLighting.IsValid(z.Setting)))
            throw new IOException("復元可能なマザー照明設定が必要です");
        Snapshot = new(identity, available, order, counts, zones.ToArray());
    }
    internal AsrockVolatileTransaction(AsrockRgbSnapshot saved, Func<byte[], byte[]> exchange)
    { Snapshot = saved; this.exchange = exchange; mutationAttempted = true; }
    private byte[] Read(byte op, byte selector)
    {
        var response = exchange(AsrockRgbMonitor.Query(op, selector));
        return AsrockRgbMonitor.Response(response, response.Length, selector);
    }
    private bool TopologyMatches() => Read(0x14, 1)[4] == Snapshot.Available && Read(0x14, 2).AsSpan(4, 8).SequenceEqual(Snapshot.Counts);
    private bool SnapshotMatches() => TopologyMatches() && Read(0x14, 3)[4] == Snapshot.Order &&
        Snapshot.Zones.All(z => AsrockRgbControl.Decode(Read(0x11, z.Address), Snapshot.Order) == z.Setting);
    public void Begin(Action<AsrockRgbSnapshot> saveBeforeWrite, bool direct)
    {
        if (mutationAttempted || restoreAttempted) throw new InvalidOperationException("RGB試験は既に開始または終了しています");
        // Validate again after the external inventory/backup preparation, before hardware mutation.
        if (!SnapshotMatches())
            throw new IOException("マザー照明の状態が変更されました。試験を停止します");
        saveBeforeWrite(Snapshot);
        if (!SnapshotMatches()) throw new IOException("復旧記録の保存中にマザー照明が変更されました。出力は開始しません");
        mutationAttempted = true; // A failed acknowledgement can still mean that the write reached the device.
        _ = exchange(AsrockDirectProtocol.ColorOrder(AsrockDirectProtocol.StreamingOrder));
        if (Read(0x14, 3)[4] != AsrockDirectProtocol.StreamingOrder) throw new IOException("RG順序変更の読み戻しが不一致です");
        if (direct) foreach (var zone in Snapshot.Zones) _ = exchange(AsrockDirectProtocol.Enter(zone.Address));
    }
    public void CheckFrameOwnership()
    {
        if (!mutationAttempted || restoreAttempted) throw new InvalidOperationException("RGB試験が開始されていないか終了しています");
        if (!TopologyMatches() || Read(0x14, 3)[4] != AsrockDirectProtocol.StreamingOrder ||
            Snapshot.Zones.Any(z => Read(0x11, z.Address)[4] is not (15 or 0xe2 or 0xe3)))
            throw new IOException("別のRGB操作または構成変更により同期を停止しました");
    }
    public AsrockRgbRestoreResult Restore(IReadOnlyDictionary<byte, BoardLightSetting>? desired = null)
    {
        if (restoreAttempted) throw new InvalidOperationException("RGB復元は既に実施しています");
        desired ??= Snapshot.Zones.ToDictionary(z => z.Address, z => z.Setting);
        if (desired.Count != Snapshot.Zones.Length || Snapshot.Zones.Any(z => !desired.TryGetValue(z.Address, out var value) || !BoardLighting.IsValid(value)))
            throw new ArgumentException("RGB復元の対象または色が不正です");
        restoreAttempted = true;
        var errors = new List<string>(); var results = new List<BoardLightResult>();
        bool topologyMatches;
        try { topologyMatches = TopologyMatches(); }
        catch (Exception e) { return new(false, [], false, [e.Message]); }
        if (!topologyMatches) return new(false, [], false, ["RGB構成が変更されています。保存したLED配置を再送しません"]);
        if (!mutationAttempted)
        {
            // Backup/preflight failed: do not write, and do not claim that an unobserved value was restored.
            try
            {
                var order = Read(0x14, 3)[4];
                return new(order == Snapshot.Order, Snapshot.Zones.Select(z =>
                {
                    var actual = AsrockRgbControl.Decode(Read(0x11, z.Address), order);
                    return new BoardLightResult(z.Address, actual == z.Setting ? "verified" : "mismatch", actual);
                }).ToArray(), true, []);
            }
            catch (Exception e) { return new(false, [], true, [e.Message]); }
        }
        byte currentOrder;
        try { currentOrder = Read(0x14, 3)[4]; }
        catch (Exception e) { return new(false, [], true, [e.Message]); }
        if (currentOrder != AsrockDirectProtocol.StreamingOrder && currentOrder != Snapshot.Order)
            return new(false, [], true, ["色順が別の操作で変更されています。元設定への復元を停止しました"]);
        // The live path needs the same foreign-native check as startup recovery. Partial Direct entry may
        // still contain original bytes encoded with the captured order, even though the table is now zero.
        {
            try
            {
                var values = Snapshot.Zones.ToDictionary(z => z.Address, z => AsrockRgbControl.Decode(Read(0x11, z.Address), Snapshot.Order));
                if (Snapshot.Zones.Any(z => values[z.Address].Mode is not (15 or 0xe2 or 0xe3) && values[z.Address] != z.Setting && values[z.Address] != desired[z.Address]))
                    return new(false, [], true, ["照明が別の設定に変更されています。保存した状態を自動再送しません"]);
                if (currentOrder == Snapshot.Order && values.All(z => z.Value == desired[z.Key]))
                    return new(true, values.Select(z => new BoardLightResult(z.Key, "verified", z.Value)).ToArray(), true, []);
            }
            catch (Exception e) { return new(false, [], true, [e.Message]); }
        }
        var orderRestored = currentOrder == Snapshot.Order;
        if (!orderRestored)
        {
            try { _ = exchange(AsrockDirectProtocol.ColorOrder(Snapshot.Order)); }
            catch (Exception e) { errors.Add("order write: " + e.Message); }
            // Even when the acknowledgement failed, read back whether the write took effect.
            try { orderRestored = Read(0x14, 3)[4] == Snapshot.Order; }
            catch (Exception e) { errors.Add("order read: " + e.Message); }
        }
        foreach (var zone in Snapshot.Zones)
        {
            if (!orderRestored) { results.Add(new(zone.Address, "notWritten", Error: "色順の復元が未確認です")); continue; }
            string? writeError = null;
            var wanted = desired[zone.Address];
            try { _ = exchange(AsrockRgbControl.Packet(zone.Address, wanted, Snapshot.Order)); }
            catch (Exception e) { writeError = e.Message; }
            try
            {
                var actual = AsrockRgbControl.Decode(Read(0x11, zone.Address), Snapshot.Order);
                results.Add(new(zone.Address, actual == wanted ? "verified" : "mismatch", actual, writeError));
            }
            catch (Exception e) { results.Add(new(zone.Address, "unknown", Error: string.Join("; ", new[] { writeError, e.Message }.Where(s => s is not null)))); }
        }
        return new(orderRestored, results.ToArray(), true, errors.ToArray());
    }
}
