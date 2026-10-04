using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed record AsrockNativeOffRecoveryResult(string PreviousHash, string PreparedHash,
    BoardLightSetting Before, BoardLightSetting After);

// Only the offline admin recovery helper calls this. Its caller must first stop SCM normally,
// confirm the old process exited, hold the normal ServiceHardwareLease, and restrict the backend
// to the explicitly selected controller. The caller owns backend disposal; no driver is created
// here, so closing the backend cannot retry a failed hardware restoration.
internal static class AsrockNativeOffRecovery
{
    internal static async Task<AsrockNativeOffRecoveryResult> RecoverAsync(string installation, string directory,
        string deviceId, string expectedHash, string expectedBoot, byte zone, BoardLightSetting approvedBefore,
        IAsrockDeviceBackend backend, Func<string> currentBoot, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(installation, "N", out _) || approvedBefore != new BoardLightSetting(1, 0, 0, 0))
            throw new ArgumentException("この消灯復旧は明示した固定黒の現在値にのみ適用できます");
        var journal = new AsrockNativeRecovery(directory, deviceId);
        var saved = journal.Pending ?? throw new IOException("消灯復旧の記録がありません");
        if (saved.Hash != expectedHash || saved.Boot != expectedBoot || !saved.Entries.Any(e => e.Zone == zone))
            throw new IOException("確認済みの消灯復旧記録が変更されています");
        void CheckBoot()
        {
            if (currentBoot() != expectedBoot) throw new IOException("Windowsの起動識別が変更されています");
        }
        void CheckDirect()
        {
            if (new AsrockBoundRecovery(directory, deviceId).Pending is not null)
                throw new IOException("同期照明の復旧記録が残っています");
        }
        CheckBoot(); CheckDirect(); ct.ThrowIfCancellationRequested();
        var connections = await backend.DiscoverAsync(ct).ConfigureAwait(false);
        var matches = connections.Where(c => DeviceIdentity.Create(installation, "asrock-usb", c.NativeIdentity) == deviceId).ToArray();
        if (matches.Length != 1 || matches[0].Confidence != IdentityConfidence.SerialBound)
            throw new IOException("製造番号で一致する消灯復旧対象を一意に取得できません");
        var connection = matches[0];
        PhysicalDeviceBinding Binding(AsrockRgbSnapshot state) => new(deviceId, connection.NativeIdentity, AsrockDeviceProvider.Definition,
            SettingsTransfer.Hash(new { AsrockDeviceProvider.Definition, DefinitionDigest = AsrockDeviceProvider.DefinitionData.Digest,
                connection.NativeIdentity, Connection = (string?)null, connection.DeviceRevision, state.Available, state.Counts }));
        backend.CheckConflicts(); backend.Check(connection);
        using var session = backend.Borrow(connection);
        AsrockRgbSnapshot Read() => new AsrockVolatileTransaction(connection.Path, session.Exchange).Snapshot;
        var before = Read();
        var binding = Binding(before);
        if (AsrockBoundRecovery.Check(saved.Boot, saved.Scope, expectedBoot, binding, connection.ConnectionId, connection.Confidence) is { } mismatch)
            throw new IOException(mismatch);
        CheckBoot(); CheckDirect(); backend.Check(connection); ct.ThrowIfCancellationRequested();
        var prepared = journal.PrepareOffRecovery(saved, expectedBoot, binding, connection.ConnectionId, connection.Confidence,
            before, zone, approvedBefore);
        var off = new BoardLightSetting(0, 0, 0, 0);
        var attempted = false;
        void Guard()
        {
            ct.ThrowIfCancellationRequested(); CheckBoot(); CheckDirect(); backend.CheckConflicts(); backend.Check(connection); journal.CheckCurrent(prepared);
            var state = Read();
            if (Binding(state) != binding || !AsrockNativeRecovery.LayoutMatches(prepared, state) ||
                state.Zones.SingleOrDefault(z => z.Address == zone)?.Setting != approvedBefore)
                throw new IOException("送信前に消灯復旧の対象設定が変更されました");
            backend.Check(connection); journal.CheckCurrent(prepared); ct.ThrowIfCancellationRequested();
            attempted = true;
        }
        Exception? writeError = null;
        try
        {
            var packet = AsrockRgbControl.Packet(zone, off, prepared.Order);
            if (session is IAsrockGuardedRgbSession guarded) _ = guarded.Exchange(packet, Guard);
            else { Guard(); _ = session.Exchange(packet); }
        }
        catch (Exception e) { writeError = e; }
        if (!attempted) throw new IOException("消灯復旧は送信前の確認で停止しました", writeError);
        // A failed attempt keeps the revised Off journal. A lost acknowledgement is accepted only
        // when a fresh read of this same retained connection proves exact Off, including mode/speed.
        var after = Read();
        backend.Check(connection); CheckBoot(); CheckDirect(); journal.CheckCurrent(prepared);
        var actual = after.Zones.SingleOrDefault(z => z.Address == zone)?.Setting;
        if (Binding(after) != binding || !AsrockNativeRecovery.LayoutMatches(prepared, after) || actual != off)
            throw new IOException("消灯の読み戻しを確認できません: " + AsrockNativeRecovery.MismatchDetails(prepared, after, zone, off), writeError);
        journal.Complete(prepared, zone);
        return new(saved.Hash, prepared.Hash, approvedBefore, actual);
    }
}
