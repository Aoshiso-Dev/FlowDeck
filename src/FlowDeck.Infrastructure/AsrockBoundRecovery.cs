using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed record AsrockRecoveryScope(PhysicalDeviceBinding Binding, string ConnectionId, IdentityConfidence Confidence, string OwnerId);
internal sealed record AsrockBoundRecoveryRecord(int Version, string Boot, AsrockRecoveryScope Scope, byte[] Targets,
    AsrockRgbSnapshot Snapshot, Dictionary<byte, BoardLightSetting> Desired, string Hash,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] AsrockNativeHandoff? Handoff = null);

// Per-device, service-private record. A USB path locates an interface; it is never evidence of a manufactured serial.
internal sealed class AsrockBoundRecovery
{
    private readonly object gate = new();
    private readonly string path, deviceId;
    private AsrockBoundRecoveryRecord? pending;
    public AsrockBoundRecoveryRecord? Pending { get { var value = Volatile.Read(ref pending); return value is null ? null : Contract.Copy(value); } }
    public AsrockBoundRecovery(string directory, string deviceId)
    {
        if (!DeviceConfigurationRules.Key(deviceId)) throw new ArgumentException("マザー照明の個体IDが不正です");
        this.deviceId = deviceId;
        path = Path.Combine(directory, "asrock-lighting-" + SettingsTransfer.Hash(deviceId).ToLowerInvariant() + ".json");
        if (!File.Exists(path)) return;
        var value = ServiceEnvironment.Read<AsrockBoundRecoveryRecord>(path);
        if (!Valid(value)) throw new IOException("マザー照明の個体付き復旧記録が不正です");
        pending = value;
    }
    private static bool Hex(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    internal static bool ValidScope(AsrockRecoveryScope? s, string deviceId) => s?.Binding is { } b && b.DeviceId == deviceId &&
        DeviceConfigurationRules.Text(b.ProviderIdentity, 1024) && b.DefinitionId == "asrock-usb-26ce-01a2-v1" && Hex(b.Fingerprint) &&
        DeviceServiceProtocol.Id(s.ConnectionId) && DeviceServiceProtocol.Id(s.OwnerId) && Enum.IsDefined(s.Confidence) &&
        (s.Confidence != IdentityConfidence.SerialBound || b.ProviderIdentity.StartsWith("serial:", StringComparison.Ordinal));
    private bool Valid(AsrockBoundRecoveryRecord? v)
    {
        var s = v?.Snapshot;
        return v is { Version: 2 } && DeviceConfigurationRules.Text(v.Boot, 128) && ValidScope(v.Scope, deviceId) && s is not null &&
            DeviceConfigurationRules.Text(s.Identity, 2048) && s.Counts is { Length: 8 } && s.Zones is { Length: > 0 and <= 8 } &&
            s.Zones.All(z => z is not null && z.Address < 8 && z.Count is > 0 and <= 255 && s.Counts[z.Address] == z.Count &&
                (s.Available & (1 << z.Address)) != 0 && z.Count != 0x1e && BoardLighting.IsValid(z.Setting) && z.Setting.MemoryPalette is null) &&
            s.Zones.DistinctBy(z => z.Address).Count() == s.Zones.Length &&
            Enumerable.Range(0, 8).Count(i => (s.Available & (1 << i)) != 0 && s.Counts[i] is not (0 or 0x1e)) == s.Zones.Length &&
            v.Targets is { Length: > 0 and <= 8 } && v.Targets.Distinct().Count() == v.Targets.Length && v.Targets.All(z => s.Zones.Any(s => s.Address == z)) &&
            v.Desired is not null && v.Desired.Count == s.Zones.Length && s.Zones.All(z => v.Desired.TryGetValue(z.Address, out var desired) &&
                BoardLighting.IsValid(desired) && desired.MemoryPalette is null) &&
            (v.Handoff is null || AsrockNativeRecovery.ValidHandoff(v.Handoff, deviceId) && v.Handoff.Before.Boot == v.Boot &&
                v.Handoff.Before.Scope.Binding == v.Scope.Binding && v.Handoff.Before.Scope.ConnectionId == v.Scope.ConnectionId &&
                v.Handoff.Before.Scope.Confidence == v.Scope.Confidence && AsrockNativeRecovery.LayoutMatches(v.Handoff.Before, s) &&
                v.Handoff.Zones.Order().SequenceEqual(v.Handoff.Before.Entries.Select(e => e.Zone).Intersect(v.Targets).Order()) &&
                v.Handoff.Before.Entries.All(e => s.Zones.SingleOrDefault(z => z.Address == e.Zone)?.Setting == e.Intended)) && v.Hash == Hash(v);
    }
    private static string Hash(AsrockBoundRecoveryRecord v) => v.Handoff is null ? SettingsTransfer.Hash(new
        { v.Version, v.Boot, v.Scope, v.Targets, v.Snapshot, Desired = v.Desired.OrderBy(p => p.Key).ToArray() }) : SettingsTransfer.Hash(new
        { v.Version, v.Boot, v.Scope, v.Targets, v.Snapshot, Desired = v.Desired.OrderBy(p => p.Key).ToArray(), v.Handoff });
    public IAsrockStreamRecovery ForStream(AsrockRecoveryScope scope, byte[] targets, AsrockNativeRecovery? native = null, AsrockNativeHandoff? handoff = null)
    {
        if (!ValidScope(scope, deviceId) || handoff is not null && (native is null || !AsrockNativeRecovery.ValidHandoff(handoff, deviceId)))
            throw new ArgumentException("マザー照明の復旧条件が不正です");
        return new StreamRecord(this, Contract.Copy(scope), targets.ToArray(), native, handoff is null ? null : Contract.Copy(handoff));
    }
    private sealed class StreamRecord(AsrockBoundRecovery store, AsrockRecoveryScope scope, byte[] targets,
        AsrockNativeRecovery? native, AsrockNativeHandoff? handoff) : IAsrockStreamRecovery
    {
        public bool Exists => store.Pending is not null;
        public bool SupportsReduction => true;
        public void CheckOwnership()
        {
            var current = store.Pending ?? throw new IOException("照明復旧記録がありません");
            if (current.Scope != scope) throw new IOException("照明復旧記録の所有者が変更されています");
            store.CheckCurrent(current);
            if (handoff is not null) native!.CheckTransferred(handoff);
        }
        public void Save(string boot, AsrockRgbSnapshot snapshot, Dictionary<byte, BoardLightSetting>? desired = null)
        {
            lock (store.gate)
            {
                var next = new AsrockBoundRecoveryRecord(2, boot, scope, targets, snapshot,
                    desired ?? snapshot.Zones.ToDictionary(z => z.Address, z => z.Setting), "", handoff);
                next = next with { Hash = Hash(next) };
                if (!store.Valid(next)) throw new IOException("照明復旧記録の設定・構成が不正です");
                if (store.pending is { } old && (old.Scope != scope || old.Boot != boot ||
                    SettingsTransfer.Hash(old.Snapshot) != SettingsTransfer.Hash(snapshot) || !old.Targets.SequenceEqual(targets) ||
                    SettingsTransfer.Hash(old.Handoff) != SettingsTransfer.Hash(handoff)))
                    throw new IOException("以前の所有者の照明復旧記録を置き換えられません");
                if (store.pending is { } expected)
                {
                    var disk = ServiceEnvironment.Read<AsrockBoundRecoveryRecord>(store.path);
                    if (!store.Valid(disk) || disk.Hash != expected.Hash) throw new IOException("保存済みの照明所有者が変更されています");
                    if (handoff is not null) native!.CheckTransferred(handoff);
                }
                else if (File.Exists(store.path)) throw new IOException("別の照明復旧記録が作成されています");
                else if (handoff is not null) native!.CheckCurrent(handoff.Before);
                ServiceEnvironment.Write(store.path, next); Volatile.Write(ref store.pending, Contract.Copy(next));
                if (handoff is not null) native!.TransferOut(handoff);
            }
        }
        public void Clear()
        {
            lock (store.gate)
            {
                if (store.pending is not { } old) return;
                if (old.Scope != scope) throw new IOException("照明復旧記録の所有者が変更されています");
                CheckOwnership(); store.Complete(old);
            }
        }
        public void Expand(byte[] expanded, Dictionary<byte, BoardLightSetting> desired)
        {
            lock (store.gate)
            {
                CheckOwnership();
                var old = store.pending!;
                if (!targets.All(expanded.Contains) || expanded.Length <= targets.Length) throw new IOException("照明の追加対象が不正です");
                var before = handoff?.Before ?? native?.Pending;
                var moving = before?.Entries.Select(e => e.Zone).Intersect(expanded).Order().ToArray() ?? [];
                var nextHandoff = moving.Length == 0 ? null : new AsrockNativeHandoff(before!, moving);
                var next = old with { Targets = expanded.ToArray(), Desired = Contract.Copy(desired), Handoff = nextHandoff };
                next = next with { Hash = Hash(next) };
                if (!store.Valid(next)) throw new IOException("追加する照明の復旧記録が不正です");
                ServiceEnvironment.Write(store.path, next);
                Volatile.Write(ref store.pending, Contract.Copy(next));
                targets = expanded.ToArray(); handoff = nextHandoff;
                // A crash here leaves a durable superset. Recovery can finish a partial native handoff.
                if (handoff is not null) native!.TransferOut(handoff);
            }
        }
        public void Reduce(byte[] remaining)
        {
            lock (store.gate)
            {
                CheckOwnership();
                if (remaining.Length == 0 || remaining.Length >= targets.Length || remaining.Distinct().Count() != remaining.Length ||
                    remaining.Any(z => !targets.Contains(z))) throw new IOException("照明の終了対象が不正です");
                var old = store.pending!;
                var moving = handoff?.Before.Entries.Select(e => e.Zone).Intersect(remaining).Order().ToArray() ?? [];
                var nextHandoff = moving.Length == 0 ? null : new AsrockNativeHandoff(handoff!.Before, moving);
                var next = old with { Targets = remaining.ToArray(), Handoff = nextHandoff };
                next = next with { Hash = Hash(next) };
                if (!store.Valid(next)) throw new IOException("終了する照明の復旧記録が不正です");
                // The fixed-color intent was saved with the old superset before sending the stop frame.
                // Only a completed frame may reduce the active target set; the background intent remains.
                ServiceEnvironment.Write(store.path, next); Volatile.Write(ref store.pending, Contract.Copy(next));
                targets = remaining.ToArray(); handoff = nextHandoff;
            }
        }
        public void ClearUnsent()
        {
            lock (store.gate)
            {
                if (store.pending is not { } old) return;
                if (old.Scope != scope) throw new IOException("照明復旧記録の所有者が変更されています");
                store.CheckCurrent(old);
                if (handoff is not null) native!.RestoreUnsentHandoff(handoff);
                store.Complete(old);
            }
        }
    }
    public static string? Check(AsrockBoundRecoveryRecord record, string boot, PhysicalDeviceBinding? current,
        string connection, IdentityConfidence confidence)
        => Check(record.Boot, record.Scope, boot, current, connection, confidence);
    public static string? Check(string recordedBoot, AsrockRecoveryScope scope, string boot, PhysicalDeviceBinding? current,
        string connection, IdentityConfidence confidence)
    {
        if (recordedBoot != boot) return "以前のWindows起動時の照明記録です。自動復元しません";
        if (scope.Binding != current) return "復旧記録のマザー照明の個体・構成が一致しません";
        if (scope.ConnectionId != connection && (scope.Confidence != IdentityConfidence.SerialBound || confidence != IdentityConfidence.SerialBound))
            return "製造番号のない照明機器の接続が変わりました。古い設定を自動復元しません";
        return null;
    }
    public void Complete(AsrockBoundRecoveryRecord expected)
    {
        lock (gate)
        {
            CheckCurrent(expected);
            File.Delete(path); Volatile.Write(ref pending, null);
        }
    }
    public void CheckCurrent(AsrockBoundRecoveryRecord expected)
    {
        lock (gate)
        {
            if (pending is null || pending.Hash != expected.Hash) throw new IOException("照明復旧記録が変更されています");
            // A stale object cannot replay or clear a newer service's obligation.
            var actual = ServiceEnvironment.Read<AsrockBoundRecoveryRecord>(path);
            if (!Valid(actual) || actual.Hash != expected.Hash) throw new IOException("保存済み照明復旧記録が変更されています");
        }
    }
}
