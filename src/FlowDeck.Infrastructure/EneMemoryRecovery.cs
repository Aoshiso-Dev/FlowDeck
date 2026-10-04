using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal enum EneMemoryRecoveryPhase { Active, Recovering, Recovered }
internal sealed record EneMemoryRecoveryScope(PhysicalDeviceBinding Binding, EneMemoryConnection Connection, string OwnerId);
internal sealed record EneMemoryRecoveryRecord(int Version, string Boot, EneMemoryRecoveryScope Scope, string Revision,
    EneMemoryState Original, EneMemoryState Exit, EneMemoryMutation Mutation, EneMemoryRecoveryPhase Phase, string? PreviousHash, string Hash);

// Native and Direct share one obligation per DIMM. Location identifies a connection, never a DIMM serial number.
internal sealed class EneMemoryRecovery
{
    private readonly object gate = new();
    private readonly string path, deviceId;
    private EneMemoryRecoveryRecord? pending;
    public EneMemoryRecoveryRecord? Pending
    {
        get { var value = Volatile.Read(ref pending); return value is null ? null : Contract.Copy(value); }
    }
    public EneMemoryRecovery(string directory, string deviceId)
    {
        if (!DeviceConfigurationRules.Key(deviceId)) throw new ArgumentException("メモリ照明の個体IDが不正です");
        this.deviceId = deviceId;
        path = Path.Combine(directory, "ene-lighting-" + SettingsTransfer.Hash(deviceId).ToLowerInvariant() + ".json");
        if (!File.Exists(path)) return;
        var record = ServiceEnvironment.Read<EneMemoryRecoveryRecord>(path);
        if (!Valid(record)) throw new IOException("メモリ照明の復旧記録が不正です");
        pending = record;
    }
    private static bool Hex(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    internal static PhysicalDeviceBinding BindingFor(string deviceId, EneMemoryConnection connection) =>
        new(deviceId, connection.NativeIdentity, connection.DefinitionId,
            SettingsTransfer.Hash(new { Connection = connection, Definition = EneMemoryDefinition.Find(connection.DefinitionId)?.Digest }));
    internal static bool ValidConnection(EneMemoryConnection? c)
    {
        if (c is null) return false;
        if (EneMemoryDefinition.Find(c.DefinitionId) is not { } definition) return false;
        return DeviceConfigurationRules.Key(c.BusId) && EneMemoryRgbProbe.Addresses.Contains(c.Address) &&
            c.NativeIdentity == "location:" + SettingsTransfer.Hash(new { Bus = c.BusId, c.Address }) &&
            DeviceServiceProtocol.Id(c.ConnectionId) && Hex(c.PlatformFingerprint) && c.DefinitionId == definition.Id &&
            c.ControllerFingerprint == EneMemoryDefinition.Proof(new(definition.Match.Firmware!, definition.Match.ConfigurationHex!));
    }
    private bool ValidScope(EneMemoryRecoveryScope? scope) => scope is not null && DeviceServiceProtocol.Id(scope.OwnerId) &&
        ValidConnection(scope.Connection) && scope.Binding == BindingFor(deviceId, scope.Connection);
    private bool Valid(EneMemoryRecoveryRecord? r) => r is { Version: 2 } && DeviceConfigurationRules.Text(r.Boot, 128) &&
        ValidScope(r.Scope) && DeviceServiceProtocol.Id(r.Revision) && EneMemoryProtocol.Valid(r.Original) && r.Original.Control[0] == 0 &&
        EneMemoryProtocol.Valid(r.Exit) && r.Exit.Control[0] == 0 && EneMemoryProtocol.ValidMutation(r.Mutation) &&
        Enum.IsDefined(r.Phase) && (r.Phase == EneMemoryRecoveryPhase.Active || r.Mutation.After.Control[0] == 0) &&
        (r.PreviousHash is null || Hex(r.PreviousHash)) && r.Hash == Hash(r);
    private static string Hash(EneMemoryRecoveryRecord r) => SettingsTransfer.Hash(new
        { r.Version, r.Boot, r.Scope, r.Revision, r.Original, r.Exit, r.Mutation, r.Phase, r.PreviousHash });
    private static bool SameLifetime(EneMemoryRecoveryRecord a, EneMemoryRecoveryRecord b) =>
        a.Boot == b.Boot && a.Scope.Binding == b.Scope.Binding && a.Scope.Connection == b.Scope.Connection &&
        EneMemoryProtocol.Same(a.Original, b.Original) && EneMemoryProtocol.Same(a.Exit, b.Exit);
    public static string? Check(EneMemoryRecoveryRecord record, string boot, PhysicalDeviceBinding? binding, EneMemoryConnection connection)
    {
        if (record.Boot != boot) return "以前のWindows起動時のメモリ照明記録です";
        if (record.Scope.Connection != connection) return "メモリ照明の接続・構成が変わりました。再関連付けが必要です";
        return record.Scope.Binding != binding ? "メモリ照明の個体関連付けが一致しません" : null;
    }
    private void Expect(EneMemoryRecoveryRecord? expected)
    {
        if (expected is not null) CheckCurrent(expected);
        else if (pending is not null || File.Exists(path)) throw new IOException("別のメモリ照明復旧記録があります");
    }
    public void CheckCurrent(EneMemoryRecoveryRecord expected)
    {
        lock (gate)
        {
            if (!Valid(expected) || pending?.Hash != expected.Hash) throw new IOException("メモリ照明の所有記録が変更されています");
            var actual = ServiceEnvironment.Read<EneMemoryRecoveryRecord>(path);
            if (!Valid(actual) || actual.Hash != expected.Hash) throw new IOException("保存済みメモリ照明の所有記録が変更されています");
        }
    }
    // Windows sharing rules prevent replacement, modification and deletion while this read handle is open.
    // Validate once before hardware reservation; Check performs no filesystem access during data transfers.
    internal sealed class ReadLease : IDisposable
    {
        private readonly EneMemoryRecovery owner;
        private readonly string hash;
        private FileStream? handle;
        internal ReadLease(EneMemoryRecovery owner, string hash, FileStream handle) { this.owner = owner; this.hash = hash; this.handle = handle; }
        public void Check()
        {
            lock (owner.gate)
            {
                if (handle is null || owner.pending?.Hash != hash) throw new IOException("メモリ照明の保存済み操作権が失効しました");
            }
        }
        public void Dispose() { lock (owner.gate) { handle?.Dispose(); handle = null; } }
    }
    internal ReadLease AcquireReadLease(EneMemoryRecoveryRecord expected)
    {
        lock (gate)
        {
            ServiceEnvironment.NoLinks(path);
            var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try { CheckCurrent(expected); return new(this, expected.Hash, handle); }
            catch { handle.Dispose(); throw; }
        }
    }
    public EneMemoryRecoveryRecord Prepare(string boot, EneMemoryRecoveryScope scope, EneMemoryMutation mutation,
        EneMemoryState exit, EneMemoryRecoveryRecord? expected = null)
    {
        lock (gate)
        {
            Expect(expected);
            if (!EneMemoryProtocol.ValidMutation(mutation)) throw new IOException("メモリ照明の送信予定が不正です");
            if (expected is not null && (expected.Phase != EneMemoryRecoveryPhase.Active ||
                Check(expected, boot, scope.Binding, scope.Connection) is not null ||
                !EneMemoryProtocol.Same(expected.Mutation.After, mutation.Before) || !EneMemoryProtocol.Same(expected.Exit, exit)))
                throw new IOException("メモリ照明の元設定・終了方法・直前値を引き継げません");
            var next = new EneMemoryRecoveryRecord(2, boot, scope, Guid.NewGuid().ToString("N"), expected?.Original ?? mutation.Before,
                exit, mutation, EneMemoryRecoveryPhase.Active, expected?.Hash, "");
            return Save(next);
        }
    }
    // The live read must be a prefix of the last recorded operation. Persist the recovery itself before issuing it.
    public EneMemoryRecoveryRecord PrepareRecovery(EneMemoryRecoveryRecord expected, string boot, PhysicalDeviceBinding binding,
        EneMemoryConnection connection, EneMemoryState actual, EneMemoryState? normalStopTarget = null)
    {
        lock (gate)
        {
            CheckCurrent(expected);
            if (Check(expected, boot, binding, connection) is { } problem) throw new IOException(problem);
            if (expected.Phase == EneMemoryRecoveryPhase.Recovered || !EneMemoryProtocol.OwnedPrefix(actual, expected.Mutation))
                throw new IOException("メモリ照明の復旧前状態が一致しません");
            if (expected.Phase == EneMemoryRecoveryPhase.Recovering && normalStopTarget is not null)
                throw new IOException("開始済みのメモリ照明復旧先は変更できません");
            var target = expected.Phase == EneMemoryRecoveryPhase.Recovering ? expected.Mutation.After : normalStopTarget ?? expected.Exit;
            var next = expected with { Revision = Guid.NewGuid().ToString("N"), PreviousHash = expected.Hash,
                Mutation = EneMemoryProtocol.Native(actual, target), Phase = EneMemoryRecoveryPhase.Recovering };
            return Save(next);
        }
    }
    // The caller must have completed the apply command and then read the registers back on the same connection.
    // Matching register bytes alone does not prove that the apply command reached the controller, or measure emitted light.
    public EneMemoryRecoveryRecord ConfirmRecovery(EneMemoryRecoveryRecord expected, EneMemoryState actual)
    {
        lock (gate)
        {
            CheckCurrent(expected);
            if (expected.Phase != EneMemoryRecoveryPhase.Recovering || !EneMemoryProtocol.Valid(actual) ||
                !EneMemoryProtocol.Same(actual, expected.Mutation.After)) throw new IOException("メモリ照明の復旧を確認できません");
            return Save(expected with { Revision = Guid.NewGuid().ToString("N"), PreviousHash = expected.Hash, Phase = EneMemoryRecoveryPhase.Recovered });
        }
    }
    public void Complete(EneMemoryRecoveryRecord expected)
    {
        lock (gate)
        {
            CheckCurrent(expected);
            if (expected.Phase != EneMemoryRecoveryPhase.Recovered) throw new IOException("未確認のメモリ照明復旧記録は消去できません");
            Store(null);
        }
    }
    // Only for a transport-proven unsent operation; never for a failed or lost acknowledgement after data transfer began.
    public void CancelUnsent(EneMemoryRecoveryRecord expected, EneMemoryRecoveryRecord? previous)
    {
        lock (gate)
        {
            CheckCurrent(expected);
            if (expected.Phase == EneMemoryRecoveryPhase.Recovered || expected.PreviousHash != previous?.Hash ||
                previous is not null && (!Valid(previous) || !SameLifetime(expected, previous)))
                throw new IOException("メモリ照明の未送信記録を元に戻せません");
            Store(previous);
        }
    }
    private EneMemoryRecoveryRecord Save(EneMemoryRecoveryRecord next)
    {
        next = Contract.Copy(next with { Hash = Hash(next) });
        if (!Valid(next)) throw new IOException("メモリ照明の復旧条件が不正です");
        Store(next); return Contract.Copy(next);
    }
    private void Store(EneMemoryRecoveryRecord? next)
    {
        if (next is null) File.Delete(path); else ServiceEnvironment.Write(path, next);
        Volatile.Write(ref pending, next is null ? null : Contract.Copy(next));
    }
}
