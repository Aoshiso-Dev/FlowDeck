using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed record AsrockOpenRgbRecoveryIdentity(string Installation, string DeviceId, string Boot, string Handover, int DeviceRevision);
internal enum AsrockOpenRgbRecoveryPhase { Detecting, ExitReady, Restoring, Restored }
internal sealed record AsrockOpenRgbRecoveryRecord(int Version, string SourceRevision, AsrockOpenRgbRecoveryIdentity Scope,
    AsrockRgbSnapshot Before, AsrockRgbSnapshot Expected, Dictionary<byte, BoardLightSetting> Desired,
    AsrockOpenRgbRecoveryPhase Phase, string Hash)
{
    // Omit absent additions so existing detection-only recovery records retain their hash.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AsrockOpenRgbOperationReceipt? Receipt { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AsrockOpenRgbPendingOperation? Operation { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public OpenRgbSdkRecoveryLink[]? SdkLinks { get; init; }
    // Empty but present zones are queried by the pinned RGB constructor. They are not output
    // targets or restoration obligations, but their raw mode/color/speed must also be captured.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<byte, byte[]>? EmptyZoneConfigurations { get; init; }
}
internal sealed record AsrockOpenRgbPendingOperation(AsrockOpenRgbNativeOperation Mutation, AsrockOpenRgbOperationReceipt Before)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousHash { get; init; }
}

// Per-device write-ahead record for side effects of the pinned OpenRGB detector. This does not
// authorize a USB path or prove the helper has exited. The compiled detection scope must establish
// both and supply its identity/ownership guard before every exchange. Never instantiated from IPC.
internal sealed class AsrockOpenRgbDetectionRecovery
{
    internal const string FilePrefix = "openrgb-asrock-detection-";
    internal static IReadOnlyList<string> ZoneNames { get; } = Array.AsReadOnly(new[]
    { "RGB LED 1 Header", "RGB LED 2 Header", "Addressable Header 1", "Addressable Header 2", "PCH", "IO Cover", "PCB", "Addressable Header 3/Audio" });
    private readonly object gate = new();
    private readonly string path, deviceId, directory;
    private readonly Action<string, AsrockOpenRgbRecoveryRecord> write;
    private AsrockOpenRgbRecoveryRecord? pending;
    public AsrockOpenRgbRecoveryRecord? Pending { get { lock (gate) return pending is null ? null : Contract.Copy(pending); } }
    public AsrockOpenRgbDetectionRecovery(string directory, string deviceId,
        Action<string, AsrockOpenRgbRecoveryRecord>? write = null)
    {
        if (!Path.IsPathFullyQualified(directory) || !DeviceConfigurationRules.Key(deviceId)) throw new ArgumentException("検出前の照明復旧領域が不正です");
        this.deviceId = deviceId; this.directory = directory; this.write = write ?? ServiceEnvironment.Write;
        path = Path.Combine(directory, FilePrefix + SettingsTransfer.Hash(deviceId).ToLowerInvariant() + ".json");
        if (File.Exists(path)) pending = Load();
    }
    private static bool Setting(BoardLightSetting? s, bool direct) => s is not null && s.MemoryPalette is null &&
        s.Red is >= 0 and <= 255 && s.Green is >= 0 and <= 255 && s.Blue is >= 0 and <= 255 && s.Speed is >= 0 and <= 255 &&
        (BoardLighting.IsValid(s) || direct && s.Mode is 15 or 0xe2 or 0xe3);
    private static bool Snapshot(AsrockRgbSnapshot? s, bool direct) => s is { Counts.Length: 8, Zones.Length: > 0 and <= 8 } &&
        DeviceConfigurationRules.Text(s.Identity, 2048) && s.Zones.All(z => z is not null && z.Address < 8 && z.Count == s.Counts[z.Address] &&
            z.Count is > 0 and <= 255 && z.Count != 0x1e && (s.Available & (1 << z.Address)) != 0 && Setting(z.Setting, direct)) &&
        s.Zones.DistinctBy(z => z.Address).Count() == s.Zones.Length &&
        Enumerable.Range(0, 8).Count(i => (s.Available & (1 << i)) != 0 && s.Counts[i] is not (0 or 0x1e)) == s.Zones.Length;
    private static bool Layout(AsrockRgbSnapshot a, AsrockRgbSnapshot b) => a.Identity == b.Identity && a.Available == b.Available &&
        a.Counts.SequenceEqual(b.Counts) && a.Zones.Select(z => z.Address).Order().SequenceEqual(b.Zones.Select(z => z.Address).Order());
    private static string Hash(AsrockOpenRgbRecoveryRecord r) => SettingsTransfer.Hash(r with { Hash = "" });
    private bool ValidScope(AsrockOpenRgbRecoveryIdentity? scope) => scope is not null && DeviceServiceProtocol.Id(scope.Installation) &&
        scope.DeviceId == deviceId && DeviceConfigurationRules.Text(scope.Boot, 128) && DeviceServiceProtocol.Id(scope.Handover) &&
        scope.DeviceRevision is >= 0 and <= 65535;
    private bool Valid(AsrockOpenRgbRecoveryRecord r) => r is { Version: 1, Scope: not null } && r.SourceRevision == OpenRgbStartupProfile.SourceRevision &&
        ValidScope(r.Scope) && Enum.IsDefined(r.Phase) &&
        Snapshot(r.Before, false) && Snapshot(r.Expected, r.Phase != AsrockOpenRgbRecoveryPhase.Detecting) && Layout(r.Before, r.Expected) &&
        (r.Expected.Order == r.Before.Order || r.Expected.Order == DetectionOrder(r.Before) || r.Expected.Order == 0) &&
        (r.EmptyZoneConfigurations is null || r.EmptyZoneConfigurations.Count > 0 &&
            r.EmptyZoneConfigurations.Keys.Order().SequenceEqual(Enumerable.Range(0, 8).Where(i => (r.Before.Available & (1 << i)) != 0 && r.Before.Counts[i] == 0).Select(i => (byte)i)) &&
            r.EmptyZoneConfigurations.Values.All(bytes => bytes is { Length: 5 })) &&
        r.Desired is not null && r.Desired.Count == r.Before.Zones.Length && r.Before.Zones.All(z =>
            r.Desired.TryGetValue(z.Address, out var value) && Setting(value, false)) &&
        (r.Phase != AsrockOpenRgbRecoveryPhase.Detecting || r.Expected.Order == DetectionOrder(r.Before) &&
            r.Before.Zones.All(z => r.Expected.Zones.Single(e => e.Address == z.Address).Setting == z.Setting && r.Desired[z.Address] == z.Setting)) &&
        (r.SdkLinks is null || r.SdkLinks is { Length: > 0 and <= 9 } && r.SdkLinks.All(s => s is not null && s.Valid && s.Boot == r.Scope.Boot) &&
            r.SdkLinks.DistinctBy(s => (s.Scope.Binding.DeviceId, s.Scope.Zone)).Count() == r.SdkLinks.Length) &&
        (r.Phase != AsrockOpenRgbRecoveryPhase.Restored || r.Operation is null && r.Expected.Order == r.Before.Order &&
            r.Expected.Zones.All(z => z.Setting == r.Desired[z.Address])) && ValidOperation(r) && r.Hash == Hash(r);
    private static bool Healthy(AsrockOpenRgbOperationReceipt? receipt) => receipt is not null &&
        (receipt.Stage == AsrockOpenRgbOperationStage.Idle && receipt.Sequence == 0 && receipt.Command == 0 && receipt.Before.Same(receipt.After) ||
         receipt.Stage == AsrockOpenRgbOperationStage.Completed && receipt.Sequence > 0 && receipt.Command is 1050 or 1051 or 1101) && receipt.Sequence < ulong.MaxValue &&
        receipt.After is { Stage: 3, Faulted: false, WriteResult: 65, ReadResult: 64, Request.Length: 65, Response.Length: 64 } io &&
        io.Attempted > 0 && io.Attempted == io.Completed;
    private static bool ValidOperation(AsrockOpenRgbRecoveryRecord r)
    {
        if (r.Receipt is not null && (!Healthy(r.Receipt) || r.Phase == AsrockOpenRgbRecoveryPhase.Detecting)) return false;
        if (r.Operation is not { } operation) return true;
        var mutation = operation.Mutation;
        return r.Phase == AsrockOpenRgbRecoveryPhase.ExitReady && mutation is not null && Healthy(operation.Before) &&
            (operation.PreviousHash is null || operation.PreviousHash.Length == 64 && operation.PreviousHash.All(Uri.IsHexDigit)) &&
            (r.Receipt is null ? operation.Before.Stage == AsrockOpenRgbOperationStage.Idle : operation.Before.Same(r.Receipt)) &&
            AsrockOpenRgbNativeOperation.Same(r.Expected, mutation.Before) && mutation.Valid() &&
            mutation.Prefixes.All(p => Snapshot(p, true) && Layout(r.Before, p) && (p.Order == 0 || p.Order == DetectionOrder(r.Before)));
    }
    private AsrockOpenRgbRecoveryRecord Load()
    {
        var value = ServiceEnvironment.Read<AsrockOpenRgbRecoveryRecord>(path);
        if (!Valid(value)) throw new IOException("OpenRGB検出前の照明復旧記録が不正です");
        return value;
    }
    private void Current(AsrockOpenRgbRecoveryIdentity scope)
    {
        if (pending is null || pending.Scope != scope || Load().Hash != pending.Hash)
            throw new IOException("OpenRGB検出前の照明所有者・復旧記録が変わりました");
    }
    private void Save(AsrockOpenRgbRecoveryRecord value)
    {
        var next = Contract.Copy(value with { Hash = Hash(value) });
        if (!Valid(next)) throw new IOException("OpenRGB検出の復旧条件が不正です");
        if (pending is not null) Current(pending.Scope);
        else if (File.Exists(path)) throw new IOException("未終了のOpenRGB検出復旧記録があります");
        write(path, next); pending = Contract.Copy(next);
    }
    // With all eight configured RGSwap overrides false, the pinned constructor preserves present
    // channel bits and clears absent channels. Zero-length present channels still enter that loop.
    internal static byte DetectionOrder(AsrockRgbSnapshot before)
    {
        var mask = Enumerable.Range(0, 8).Where(i => (before.Available & (1 << i)) != 0 && before.Counts[i] != 0x1e)
            .Aggregate(0, (bits, i) => bits | (1 << i));
        return (byte)(before.Order & mask);
    }
    private static AsrockRgbSnapshot Read(string identity, Func<byte[], byte[]> exchange, Action guard,
        Action<Dictionary<byte, byte[]>>? captureEmpty = null)
    {
        byte[] Query(byte operation, byte selector)
        {
            guard(); var bytes = exchange(AsrockRgbMonitor.Query(operation, selector));
            return AsrockRgbMonitor.Response(bytes, bytes.Length, selector);
        }
        var available = Query(0x14, 1)[4]; var counts = Query(0x14, 2)[4..12]; var order = Query(0x14, 3)[4];
        var zones = Enumerable.Range(0, 8).Where(i => (available & (1 << i)) != 0 && counts[i] is not (0 or 0x1e))
            .Select(i => new AsrockZoneSnapshot((byte)i, counts[i], AsrockRgbControl.Decode(Query(0x11, (byte)i), order))).ToArray();
        var empty = captureEmpty is null ? null : Enumerable.Range(0, 8).Where(i => (available & (1 << i)) != 0 && counts[i] == 0)
            .ToDictionary(i => (byte)i, i => Query(0x11, (byte)i)[4..9]);
        var value = new AsrockRgbSnapshot(identity, available, order, counts, zones);
        if (!Snapshot(value, true) || Query(0x14, 1)[4] != available || !Query(0x14, 2).AsSpan(4, 8).SequenceEqual(counts) || Query(0x14, 3)[4] != order)
            throw new IOException("ASRockの検出前状態を一貫して取得できません");
        guard(); if (empty is not null) captureEmpty!(empty); return value;
    }
    public AsrockRgbSnapshot Capture(AsrockOpenRgbRecoveryIdentity scope, string identity, Func<byte[], byte[]> exchange, Action checkOwnership)
    {
        lock (gate)
        {
            if (!ValidScope(scope) || !DeviceConfigurationRules.Text(identity, 2048)) throw new IOException("検出前の機器・起動・引き渡し識別が不正です");
            if (pending is not null || File.Exists(path)) throw new IOException("以前の検出復旧を完了してください");
            Dictionary<byte, byte[]> empty = [];
            var before = Read(identity, exchange, checkOwnership, value => empty = value);
            // Direct pixels cannot be read back. Do not invent their original colors from native registers.
            if (!Snapshot(before, false)) throw new IOException("検出前の照明を復元可能なNative設定へ戻してください");
            checkOwnership();
            Save(new(1, OpenRgbStartupProfile.SourceRevision, scope, before, before with { Order = DetectionOrder(before) },
                before.Zones.ToDictionary(z => z.Address, z => z.Setting), AsrockOpenRgbRecoveryPhase.Detecting, "")
                { EmptyZoneConfigurations = empty.Count == 0 ? null : empty });
            void Guard() { checkOwnership(); Current(scope); }
            Dictionary<byte, byte[]> verifiedEmpty = [];
            if (SettingsTransfer.Hash(Read(identity, exchange, Guard, value => verifiedEmpty = value)) != SettingsTransfer.Hash(before) ||
                SettingsTransfer.Hash(empty) != SettingsTransfer.Hash(verifiedEmpty))
                throw new IOException("復旧記録の保存中に照明が変わりました。OpenRGBを開始できません");
            return Contract.Copy(before);
        }
    }
    internal AsrockOpenRgbRecoveryRecord ReadDetection(AsrockOpenRgbRecoveryIdentity scope, string? hash = null)
    {
        lock (gate)
        {
            Current(scope);
            if (pending!.Phase != AsrockOpenRgbRecoveryPhase.Detecting || pending.Operation is not null || pending.Receipt is not null ||
                pending.SdkLinks is not null || hash is not null && pending.Hash != hash)
                throw new IOException("ASRockの検出認可と検出前保存が一致しません");
            return Contract.Copy(pending);
        }
    }
    internal void CompleteDetection(AsrockOpenRgbRecoveryIdentity scope, string hash, AsrockOpenRgbIoReceipt io)
    {
        lock (gate)
        {
            var record = ReadDetection(scope, hash);
            new AsrockOpenRgbDetectionPlan(record).CheckFinal(io);
            var baseline = new AsrockOpenRgbOperationReceipt(0, 0, AsrockOpenRgbOperationStage.Idle, io.Copy(), io.Copy());
            Save(record with { Phase = AsrockOpenRgbRecoveryPhase.ExitReady, Receipt = baseline });
            Current(scope);
        }
    }
    // The eventual hardware-specific SDK adapter must derive this expected register state and the
    // desired native exit state from owned operations. Never pass an arbitrary live snapshot here.
    // In particular, raw native fields in Direct mode are not evidence of per-LED colors.
    public void PrepareExit(AsrockOpenRgbRecoveryIdentity scope, AsrockRgbSnapshot expected,
        IReadOnlyDictionary<byte, BoardLightSetting> desired, Action checkOwnership)
    {
        lock (gate)
        {
            checkOwnership(); Current(scope);
            if (pending!.Phase is AsrockOpenRgbRecoveryPhase.Restoring or AsrockOpenRgbRecoveryPhase.Restored || pending.Operation is not null || pending.Receipt is not null)
                throw new IOException("開始した照明操作・復旧の記録を置き換えられません");
            Save(pending with { Expected = Contract.Copy(expected), Desired = desired.ToDictionary(p => p.Key, p => Contract.Copy(p.Value)),
                Phase = AsrockOpenRgbRecoveryPhase.ExitReady });
        }
    }
    internal AsrockOpenRgbRecoveryRecord PrepareOperation(AsrockOpenRgbRecoveryIdentity scope, AsrockOpenRgbNativeOperation operation,
        AsrockOpenRgbOperationReceipt before, IReadOnlyDictionary<byte, BoardLightSetting> desired, Action checkOwnership)
    {
        lock (gate)
        {
            checkOwnership(); Current(scope);
            if (pending!.Phase is AsrockOpenRgbRecoveryPhase.Restoring or AsrockOpenRgbRecoveryPhase.Restored || pending.Operation is not null ||
                !AsrockOpenRgbNativeOperation.Same(pending.Expected, operation.Before))
                throw new IOException("前回のASRock操作を確認・復旧するまで新しい操作は開始できません");
            Save(pending with { Phase = AsrockOpenRgbRecoveryPhase.ExitReady,
                Desired = desired.ToDictionary(p => p.Key, p => Contract.Copy(p.Value)),
                Operation = new(Contract.Copy(operation), before.Copy()) { PreviousHash = pending.Hash } });
            checkOwnership(); Current(scope); return Contract.Copy(pending!);
        }
    }
    internal void CheckPrepared(AsrockOpenRgbRecoveryIdentity scope, string hash, Action checkOwnership)
    {
        lock (gate)
        {
            checkOwnership(); Current(scope);
            if (pending!.Hash != hash || pending.Operation is null) throw new IOException("ASRockの送信前の復旧記録が変わりました");
        }
    }
    internal AsrockOpenRgbRecoveryRecord ReadPrepared(AsrockOpenRgbRecoveryIdentity scope, string hash, Action checkOwnership)
    {
        lock (gate) { CheckPrepared(scope, hash, checkOwnership); return Contract.Copy(pending!); }
    }
    internal void CompleteOperation(AsrockOpenRgbRecoveryIdentity scope, string hash, AsrockOpenRgbExecution execution, Action checkOwnership)
    {
        lock (gate)
        {
            CheckPrepared(scope, hash, checkOwnership); var operation = pending!.Operation!;
            if (!operation.Before.Same(execution.Before)) throw new IOException("ASRockの操作前記録が一致しません");
            operation.Mutation.CheckCompleted(execution);
            Save(pending with { Expected = Contract.Copy(operation.Mutation.After), Receipt = execution.After.Copy(), Operation = null });
        }
    }
    // Called only when the wire callback was never entered. This changes no hardware; physical
    // ownership or an expired frame need not be reacquired merely to undo this instance's intent.
    internal void CancelUnsentOperation(AsrockOpenRgbRecoveryIdentity scope, string hash, AsrockOpenRgbRecoveryRecord previous)
    {
        lock (gate)
        {
            Current(scope);
            if (pending!.Hash != hash || pending.Operation?.PreviousHash != previous.Hash || !Valid(previous) || previous.Scope != scope ||
                previous.Operation is not null || previous.Phase is AsrockOpenRgbRecoveryPhase.Restoring or AsrockOpenRgbRecoveryPhase.Restored ||
                !AsrockOpenRgbNativeOperation.Same(previous.Before, pending.Before) || !AsrockOpenRgbNativeOperation.Same(previous.Expected, pending.Expected))
                throw new IOException("ASRockの未送信記録を取り消せません");
            Save(previous);
        }
    }
    internal void TrackSdk(AsrockOpenRgbRecoveryIdentity scope, OpenRgbSdkRecoveryLink link, Action checkOwnership)
    {
        lock (gate)
        {
            checkOwnership(); Current(scope);
            if (!link.Valid || link.Boot != scope.Boot || pending!.Phase is AsrockOpenRgbRecoveryPhase.Restoring or AsrockOpenRgbRecoveryPhase.Restored)
                throw new IOException("ASRockの復旧記録へSDK操作を追加できません");
            var links = (pending.SdkLinks ?? []).Where(old => !old.SameSlot(link)).Append(Contract.Copy(link)).ToArray();
            if (SettingsTransfer.Hash(links) != SettingsTransfer.Hash(pending.SdkLinks)) Save(pending with { SdkLinks = links });
        }
    }
    internal void CheckSdkCoverage(AsrockOpenRgbRecoveryIdentity scope, OpenRgbRecoveryRecord[] records)
    {
        lock (gate)
        {
            Current(scope);
            foreach (var record in records)
                if (!(pending!.SdkLinks ?? []).Any(link => link.Matches(record)))
                    throw new IOException("Native復旧とSDK操作の保存記録が一致しません");
        }
    }
    internal void CompleteSdkSettlement(AsrockOpenRgbRecoveryIdentity scope)
    {
        lock (gate)
        {
            void Verified()
            {
                Current(scope);
                if (pending!.Phase != AsrockOpenRgbRecoveryPhase.Restored) throw new IOException("ASRockのNative復旧が確認されていません");
            }
            Verified();
            // A crash after one deletion leaves the verified native proof. Retry only file cleanup;
            // never repeat native writes merely because SDK journal cleanup was interrupted.
            foreach (var link in pending!.SdkLinks ?? [])
                new OpenRgbLightingRecovery(directory, link.Scope.Binding.DeviceId, link.Scope.Zone).SettleNative(link, Verified);
            Verified(); File.Delete(path); pending = null;
        }
    }
    private static byte[] Raw(BoardLightSetting value, byte order, byte address)
    {
        var normal = (order & (1 << address)) != 0;
        return [value.Mode, (byte)(normal ? value.Red : value.Green), (byte)(normal ? value.Green : value.Red), (byte)value.Blue, (byte)value.Speed];
    }
    public AsrockRgbRestoreResult Restore(AsrockOpenRgbRecoveryIdentity scope, Func<byte[], byte[]> exchange, Action checkOwnership)
    {
        lock (gate)
        {
            void Guard() { checkOwnership(); Current(scope); }
            Guard(); var record = pending!;
            if (record.Phase == AsrockOpenRgbRecoveryPhase.Restored)
                return new(true, record.Before.Zones.Select(z => new BoardLightResult(z.Address, "verified", record.Desired[z.Address], null)).ToArray(), true, []);
            if (record.Operation is { } operation)
            {
                // Select one complete saved prefix, not a per-zone combination that never occurred.
                // Once selected, the ordinary native restore may independently settle each zone.
                var actual = Read(record.Before.Identity, exchange, Guard);
                var states = operation.Mutation.Prefixes.Prepend(operation.Mutation.Before);
                if (!states.Any(s => AsrockOpenRgbNativeOperation.Same(s, actual)))
                    throw new IOException("ASRockの途中状態が保存済み操作と一致しません。上書きしません");
                Save(record with { Expected = actual, Operation = null, Phase = AsrockOpenRgbRecoveryPhase.Restoring });
                record = pending!;
            }
            var expected = record.Expected.Zones.ToDictionary(z => z.Address, z => Raw(z.Setting, record.Expected.Order, z.Address));
            var desired = record.Desired.ToDictionary(p => p.Key, p => Raw(p.Value, record.Before.Order, p.Key));
            AsrockRgbSnapshot Check()
            {
                var state = Read(record.Before.Identity, exchange, Guard);
                if (!Layout(record.Before, state) || state.Order != record.Expected.Order && state.Order != record.Before.Order ||
                    state.Zones.Any(z => !Raw(z.Setting, state.Order, z.Address).AsSpan().SequenceEqual(expected[z.Address]) &&
                        !Raw(z.Setting, state.Order, z.Address).AsSpan().SequenceEqual(desired[z.Address])))
                    throw new IOException("ASRockの構成または照明が別の状態です。保存値で上書きしません");
                return state;
            }
            var state = Check();
            if (pending!.Phase != AsrockOpenRgbRecoveryPhase.Restoring) Save(pending with { Phase = AsrockOpenRgbRecoveryPhase.Restoring });
            // Check complete known state immediately before each write, including after a lost ack.
            Exception? orderError = null;
            if (state.Order != record.Before.Order)
            {
                try { _ = Check(); Guard(); _ = exchange(AsrockDirectProtocol.ColorOrder(record.Before.Order)); }
                catch (Exception e) { orderError = e; }
            }
            state = Check();
            if (state.Order != record.Before.Order) return new(false, [], true, [orderError?.Message ?? "色順の復旧が未確認です"]);
            var results = new List<BoardLightResult>();
            foreach (var zone in record.Before.Zones)
            {
                Exception? failure = null;
                try
                {
                    state = Check();
                    if (state.Zones.Single(z => z.Address == zone.Address).Setting != record.Desired[zone.Address])
                    { Guard(); _ = exchange(AsrockRgbControl.Packet(zone.Address, record.Desired[zone.Address], record.Before.Order)); }
                }
                catch (Exception e) { failure = e; }
                state = Check();
                var actual = state.Zones.Single(z => z.Address == zone.Address).Setting;
                results.Add(new(zone.Address, actual == record.Desired[zone.Address] ? "verified" : "mismatch", actual, failure?.Message));
            }
            var result = new AsrockRgbRestoreResult(true, results.ToArray(), true, []);
            if (result.Restored)
            {
                state = Check(); Guard();
                if (state.Order != record.Before.Order || state.Zones.Any(z => z.Setting != record.Desired[z.Address]))
                    throw new IOException("照明復旧の最終読み戻しが一致しません");
                if (record.SdkLinks is { Length: > 0 })
                    Save(pending! with { Expected = state, Operation = null, Phase = AsrockOpenRgbRecoveryPhase.Restored });
                else { File.Delete(path); pending = null; }
            }
            return result;
        }
    }
}
