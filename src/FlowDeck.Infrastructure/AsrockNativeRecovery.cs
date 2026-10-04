using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed record AsrockNativeEntry(byte Zone, BoardLightSetting Original, BoardLightSetting Before, BoardLightSetting Intended, BoardLightSetting Exit);
internal sealed record AsrockNativeRecoveryRecord(int Version, string Boot, AsrockRecoveryScope Scope, byte Available, byte[] Counts,
    byte Order, AsrockNativeEntry[] Entries, string Hash);
internal sealed record AsrockNativeHandoff(AsrockNativeRecoveryRecord Before, byte[] Zones);

// Native commands change one zone without changing the controller's color-order table.
// The first original setting survives repeated edits; the last before/intended pair handles a lost acknowledgement.
internal sealed class AsrockNativeRecovery
{
    private readonly object gate = new();
    private readonly string path, deviceId;
    private AsrockNativeRecoveryRecord? pending;
    public AsrockNativeRecoveryRecord? Pending { get { var value = Volatile.Read(ref pending); return value is null ? null : Contract.Copy(value); } }
    public AsrockNativeRecovery(string directory, string deviceId)
    {
        if (!DeviceConfigurationRules.Key(deviceId)) throw new ArgumentException("照明の個体IDが不正です");
        this.deviceId = deviceId;
        path = Path.Combine(directory, "asrock-native-" + SettingsTransfer.Hash(deviceId).ToLowerInvariant() + ".json");
        if (!File.Exists(path)) return;
        var value = ServiceEnvironment.Read<AsrockNativeRecoveryRecord>(path);
        if (!Valid(value)) throw new IOException("マザー照明のNative復旧記録が不正です");
        pending = value;
    }
    private static bool Setting(BoardLightSetting? value) => BoardLighting.IsValid(value) && value!.MemoryPalette is null;
    private bool Valid(AsrockNativeRecoveryRecord? r) => ValidRecord(r, deviceId);
    internal static bool ValidRecord(AsrockNativeRecoveryRecord? r, string deviceId) => r is { Version: 2 } && DeviceConfigurationRules.Text(r.Boot, 128) &&
        AsrockBoundRecovery.ValidScope(r.Scope, deviceId) && r.Counts is { Length: 8 } && r.Entries is { Length: > 0 and <= 8 } &&
        r.Entries.All(e => e is not null && e.Zone < 8 && (r.Available & (1 << e.Zone)) != 0 && r.Counts[e.Zone] is not (0 or 0x1e) &&
            Setting(e.Original) && Setting(e.Before) && Setting(e.Intended) && Setting(e.Exit)) &&
        r.Entries.DistinctBy(e => e.Zone).Count() == r.Entries.Length && r.Hash == Hash(r);
    private static string Hash(AsrockNativeRecoveryRecord r) => SettingsTransfer.Hash(new
        { r.Version, r.Boot, r.Scope, r.Available, r.Counts, r.Order, Entries = r.Entries.OrderBy(e => e.Zone).ToArray() });
    public static bool LayoutMatches(AsrockNativeRecoveryRecord r, AsrockRgbSnapshot state) =>
        r.Available == state.Available && r.Order == state.Order && r.Counts.SequenceEqual(state.Counts);
    internal static string MismatchDetails(AsrockNativeRecoveryRecord expected, AsrockRgbSnapshot actual, byte zone,
        params BoardLightSetting[] accepted)
    {
        static string SettingText(BoardLightSetting? value) => value is null ? "未取得" :
            $"Mode={value.Mode} RGB=#{value.Red:X2}{value.Green:X2}{value.Blue:X2} Speed={value.Speed}";
        var current = actual.Zones.FirstOrDefault(z => z.Address == zone)?.Setting;
        var text = $"zone:{zone} 現在[{SettingText(current)}] / 期待[{string.Join(" | ", accepted.Distinct().Select(SettingText))}]";
        var layout = new List<string>();
        if (expected.Available != actual.Available) layout.Add($"Available=0x{expected.Available:X2}→0x{actual.Available:X2}");
        if (expected.Order != actual.Order) layout.Add($"Order=0x{expected.Order:X2}→0x{actual.Order:X2}");
        for (var i = 0; i < Math.Max(expected.Counts.Length, actual.Counts.Length); i++)
        {
            int? before = i < expected.Counts.Length ? expected.Counts[i] : null;
            int? after = i < actual.Counts.Length ? actual.Counts[i] : null;
            if (before != after) layout.Add($"LED数[{i}]={before?.ToString() ?? "未取得"}→{after?.ToString() ?? "未取得"}");
        }
        return layout.Count == 0 ? text : text + " / 構成(期待→現在)[" + string.Join(", ", layout) + "]";
    }
    internal static bool ValidHandoff(AsrockNativeHandoff? h, string deviceId) => h is not null && ValidRecord(h.Before, deviceId) &&
        h.Zones is { Length: > 0 and <= 8 } && h.Zones.Distinct().Count() == h.Zones.Length && h.Zones.All(z => h.Before.Entries.Any(e => e.Zone == z));
    private AsrockNativeRecoveryRecord? AfterHandoff(AsrockNativeHandoff h)
    {
        if (!ValidHandoff(h, deviceId)) throw new IOException("Native照明の引継ぎ記録が不正です");
        var next = h.Before with { Entries = h.Before.Entries.Where(e => !h.Zones.Contains(e.Zone)).ToArray() };
        return next.Entries.Length == 0 ? null : next with { Hash = Hash(next) };
    }
    private bool Matches(AsrockNativeRecoveryRecord? expected)
    {
        if (expected is null) return pending is null && !File.Exists(path);
        if (pending?.Hash != expected.Hash) return false;
        CheckCurrent(expected); return true;
    }
    // The receiving stream record must be durable before this removes any native obligation.
    public void TransferOut(AsrockNativeHandoff h)
    {
        lock (gate)
        {
            var next = AfterHandoff(h);
            if (Matches(next)) return;
            if (!Matches(h.Before) && !MatchesPartialTransfer(h)) throw new IOException("Native照明の引継ぎ元が変更されています");
            Store(next);
        }
    }
    private bool MatchesPartialTransfer(AsrockNativeHandoff h)
    {
        if (pending is not { } current) return false;
        var remaining = h.Before.Entries.Where(e => current.Entries.Any(c => c.Zone == e.Zone)).ToArray();
        if (remaining.Length != current.Entries.Length || h.Before.Entries.Any(e => !h.Zones.Contains(e.Zone) && !remaining.Contains(e))) return false;
        var expected = h.Before with { Entries = remaining };
        return Matches(expected with { Hash = Hash(expected) });
    }
    public void CheckTransferred(AsrockNativeHandoff h)
    {
        lock (gate) if (!Matches(AfterHandoff(h))) throw new IOException("引継ぎ後のNative照明所有者が変更されています");
    }
    // Only used when the transport proves that no Direct mutation has occurred.
    public void RestoreUnsentHandoff(AsrockNativeHandoff h)
    {
        lock (gate)
        {
            var next = AfterHandoff(h);
            if (Matches(h.Before)) return;
            if (!Matches(next)) throw new IOException("Native照明の引継ぎ取消先が変更されています");
            Store(h.Before);
        }
    }
    public AsrockNativeRecoveryRecord Prepare(string boot, AsrockRecoveryScope scope, AsrockRgbSnapshot state,
        byte zone, BoardLightSetting desired, BoardLightSetting exit)
    {
        lock (gate)
        {
            var before = state.Zones.SingleOrDefault(z => z.Address == zone)?.Setting ?? throw new IOException("照明ゾーンがありません");
            var prior = pending?.Entries.SingleOrDefault(e => e.Zone == zone);
            if (pending is { } old)
            {
                CheckCurrent(old);
                if (old.Boot != boot || old.Scope != scope || !LayoutMatches(old, state) || prior is not null && (prior.Intended != before || prior.Exit != exit))
                    throw new IOException("Native照明の所有・構成・設定が変更されています");
            }
            else if (File.Exists(path)) throw new IOException("別のNative照明復旧記録があります");
            var entry = new AsrockNativeEntry(zone, prior?.Original ?? before, before, desired, exit);
            var next = new AsrockNativeRecoveryRecord(2, boot, scope, state.Available, state.Counts.ToArray(), state.Order,
                [.. (pending?.Entries ?? []).Where(e => e.Zone != zone), entry], "");
            next = next with { Entries = next.Entries.OrderBy(e => e.Zone).ToArray(), Hash = Hash(next) };
            if (!Valid(next)) throw new IOException("Native照明の復旧条件が不正です");
            Store(next); return Contract.Copy(next);
        }
    }
    public void CheckCurrent(AsrockNativeRecoveryRecord expected)
    {
        lock (gate)
        {
            if (pending?.Hash != expected.Hash) throw new IOException("Native照明の所有記録が変更されています");
            var disk = ServiceEnvironment.Read<AsrockNativeRecoveryRecord>(path);
            if (!Valid(disk) || disk.Hash != expected.Hash) throw new IOException("保存済みNative照明の所有記録が変更されています");
        }
    }
    // Explicit offline operator choice, separate from automatic recovery. The original obligation is
    // retained until the new Off intent is durable; no ordinary command may adopt a foreign setting.
    internal AsrockNativeRecoveryRecord PrepareOffRecovery(AsrockNativeRecoveryRecord expected, string boot,
        PhysicalDeviceBinding binding, string connection, IdentityConfidence confidence, AsrockRgbSnapshot state,
        byte zone, BoardLightSetting approvedBefore)
    {
        lock (gate)
        {
            CheckCurrent(expected);
            if (expected.Scope.Confidence != IdentityConfidence.SerialBound || confidence != IdentityConfidence.SerialBound ||
                AsrockBoundRecovery.Check(expected.Boot, expected.Scope, boot, binding, connection, confidence) is not null ||
                !LayoutMatches(expected, state) || approvedBefore != new BoardLightSetting(1, 0, 0, 0) ||
                state.Zones.SingleOrDefault(z => z.Address == zone)?.Setting != approvedBefore)
                throw new IOException("明示した消灯復旧の個体・構成・現在値が一致しません");
            var prior = expected.Entries.SingleOrDefault(e => e.Zone == zone) ?? throw new IOException("消灯復旧の対象記録がありません");
            var off = new BoardLightSetting(0, 0, 0, 0);
            var entry = prior with { Before = approvedBefore, Intended = off, Exit = off };
            var next = expected with { Entries = expected.Entries.Select(e => e.Zone == zone ? entry : e).ToArray() };
            next = next with { Hash = Hash(next) };
            if (!Valid(next)) throw new IOException("消灯復旧の保存条件が不正です");
            Store(next); return Contract.Copy(next);
        }
    }
    public void Complete(AsrockNativeRecoveryRecord expected, byte zone)
    {
        lock (gate)
        {
            CheckCurrent(expected);
            if (!expected.Entries.Any(e => e.Zone == zone)) throw new IOException("Native照明の復旧対象がありません");
            var next = expected with { Entries = expected.Entries.Where(e => e.Zone != zone).ToArray() };
            if (next.Entries.Length == 0) Store(null); else Store(next with { Hash = Hash(next) });
        }
    }
    // Called only when the live transport proves that no mutating command was issued.
    public void CancelUnsent(AsrockNativeRecoveryRecord expected, AsrockNativeRecoveryRecord? previous)
    {
        lock (gate)
        {
            CheckCurrent(expected);
            if (previous is not null && (!Valid(previous) || previous.Scope != expected.Scope)) throw new IOException("元のNative所有記録が不正です");
            Store(previous);
        }
    }
    private void Store(AsrockNativeRecoveryRecord? next)
    {
        if (next is null) File.Delete(path); else ServiceEnvironment.Write(path, next);
        Volatile.Write(ref pending, next is null ? null : Contract.Copy(next));
    }
}
