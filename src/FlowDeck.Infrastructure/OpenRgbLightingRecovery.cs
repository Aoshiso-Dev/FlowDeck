using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal enum OpenRgbRecoveryPhase { Active, Recovering, Accepted }
internal sealed record OpenRgbRecoveryScope(PhysicalDeviceBinding Binding, OpenRgbSdkTarget Target, IdentityConfidence Confidence, string Owner, int? Zone);
internal sealed record OpenRgbRecoveryRecord(int Version, string Boot, OpenRgbRecoveryScope Scope, string Revision, uint[] ZoneLeds,
    OpenRgbLightingState Original, OpenRgbLightingState Exit, OpenRgbLightingMutation Mutation, OpenRgbRecoveryPhase Phase, string? PreviousHash, string Hash);

// This records SDK obligations. Accepted means acknowledged by the SDK and matched against its reported state,
// never physical RGB readback. A reconnect may only be rebound by a separately verified serial binding.
internal sealed partial class OpenRgbLightingRecovery
{
    private readonly object gate = new();
    private readonly string path, deviceId;
    private readonly int? zone;
    private OpenRgbRecoveryRecord? pending;
    public OpenRgbRecoveryRecord? Pending { get { lock (gate) return pending is null ? null : Contract.Copy(pending); } }
    public OpenRgbLightingRecovery(string directory, string deviceId, int? zone)
    {
        if (!DeviceConfigurationRules.Key(deviceId) || zone is < 0 or >= 256) throw new ArgumentException("OpenRGBの復旧対象が不正です");
        this.deviceId = deviceId; this.zone = zone;
        path = Path.Combine(directory, "openrgb-lighting-" + SettingsTransfer.Hash(new { deviceId, zone }).ToLowerInvariant() + ".json");
        if (!File.Exists(path)) return;
        pending = ServiceEnvironment.Read<OpenRgbRecoveryRecord>(path);
        if (!Valid(pending) || pending.Scope.Zone != zone) throw new IOException("OpenRGBの復旧記録が不正です");
    }
    private static string Hash(OpenRgbRecoveryRecord r) => SettingsTransfer.Hash(r with { Hash = "" });
    private static bool Hex(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool ValidState(OpenRgbLightingState? state)
    {
        bool Modes(OpenRgbModeState? group) => group is not null && group.Modes is { Length: <= 256 } && group.Active >= -1 &&
            (group.Modes.Length == 0 ? group.Active <= 0 : group.Active < group.Modes.Length) && group.Modes.All(m => m is not null && m.Name is { Length: <= 4096 } &&
                m.Colors is { Length: <= 4096 } && m.Colors.All(c => c?.Valid == true));
        return state is not null && Modes(state.Device) && state.Zones is { Length: <= 256 } && state.Zones.All(Modes) &&
            state.Colors is { Length: > 0 and <= 4096 } && state.Colors.All(c => c?.Valid == true);
    }
    private static OpenRgbSdkController Shape(OpenRgbRecoveryRecord record, OpenRgbLightingState state) => new(record.Scope.Target.ControllerId, 0,
        "", "", "", "", "", "", state.Device.Active, state.Device.Modes,
        record.ZoneLeds.Select((count, i) => new OpenRgbSdkZone("", 0, count, count, count, null, [], 0, state.Zones[i].Active, state.Zones[i].Modes, "")).ToArray(),
        Enumerable.Repeat("", state.Colors.Length).ToArray(), state.Colors, [], 0, "", "");
    private bool Valid(OpenRgbRecoveryRecord? r)
    {
        try
        {
            if (r is not { Version: 2, Scope: { Binding: { }, Target: { } }, Mutation: { } } || r.Scope.Binding.DeviceId != deviceId ||
                !DeviceConfigurationRules.Key(r.Scope.Binding.ProviderIdentity) || !DeviceConfigurationRules.Key(r.Scope.Binding.DefinitionId) || !Hex(r.Scope.Binding.Fingerprint) ||
                r.Scope.Confidence == IdentityConfidence.SerialBound && (!r.Scope.Binding.ProviderIdentity.StartsWith("serial:", StringComparison.Ordinal) || !Hex(r.Scope.Binding.ProviderIdentity[7..])) ||
                r.Scope.Target.Connection == Guid.Empty || r.Scope.Target.InventoryRevision <= 0 || !Hex(r.Scope.Target.Structure) || !Enum.IsDefined(r.Scope.Confidence) ||
                !DeviceServiceProtocol.Id(r.Scope.Owner) || !DeviceServiceProtocol.Id(r.Revision) || !DeviceConfigurationRules.Text(r.Boot, 128) || !Enum.IsDefined(r.Phase) ||
                r.ZoneLeds is not { Length: <= 256 } || r.ZoneLeds.Any(n => n > 4096) || r.Scope.Zone != zone || r.Scope.Zone is < 0 || r.Scope.Zone >= r.ZoneLeds.Length ||
                !ValidState(r.Original) || !ValidState(r.Exit) || !ValidState(r.Mutation.Before) || r.Original.Zones.Length != r.ZoneLeds.Length ||
                r.Exit.Zones.Length != r.ZoneLeds.Length || r.Mutation.Before.Zones.Length != r.ZoneLeds.Length ||
                r.Mutation.Steps is not { Length: <= 512 } || r.Mutation.Prefixes is not { Length: <= 512 } || r.Mutation.Steps.Length != r.Mutation.Prefixes.Length ||
                !r.Mutation.Prefixes.All(ValidState) || (r.PreviousHash is not null && !Hex(r.PreviousHash)) || Hash(r) != r.Hash) return false;
            var rebuilt = OpenRgbLightingPlan.Build(Shape(r, r.Mutation.Before), r.Mutation.Before, r.Mutation.Steps);
            if (SettingsTransfer.Hash(rebuilt) != SettingsTransfer.Hash(r.Mutation)) return false;
            // A journal for one zone may never contain whole-device or sibling writes.
            return r.Scope.Zone is null || r.Mutation.Steps.All(s => s.Zone == r.Scope.Zone);
        }
        catch (Exception e) when (e is IOException or ArgumentException or NullReferenceException or IndexOutOfRangeException or OverflowException) { return false; }
    }
    public static string? Check(OpenRgbRecoveryRecord record, string boot, PhysicalDeviceBinding binding, OpenRgbSdkTarget target)
    {
        if (record.Boot != boot) return "以前のWindows起動時のOpenRGB記録です";
        if (record.Scope.Binding != binding || record.Scope.Target.Structure != target.Structure) return "OpenRGBの物理関連付け・構成が一致しません";
        if (record.Scope.Target.Connection == target.Connection && record.Scope.Target.ControllerId == target.ControllerId &&
            record.Scope.Target.InventoryRevision == target.InventoryRevision) return null;
        // A fresh provider match can rebind a unique serial on either a new connection or a new list generation.
        // Every actual packet still uses the newly issued target and its live connection/inventory checks.
        if (record.Scope.Confidence == IdentityConfidence.SerialBound) return null;
        return record.Scope.Target.Connection == target.Connection ? "OpenRGBの一覧が変更されています" : "OpenRGBの接続が変わりました。再関連付けが必要です";
    }
    internal static bool SameScope(OpenRgbRecoveryRecord record, OpenRgbLightingState a, OpenRgbLightingState b)
    {
        if (record.Scope.Zone is not { } zone) return OpenRgbLightingPlan.Same(a, b);
        if (zone >= a.Zones.Length || zone >= b.Zones.Length || a.Colors.Length != b.Colors.Length) return false;
        var offset = checked((int)record.ZoneLeds.Take(zone).Sum(n => (long)n)); var count = (int)record.ZoneLeds[zone];
        return SettingsTransfer.Hash(a.Device) == SettingsTransfer.Hash(b.Device) && SettingsTransfer.Hash(a.Zones[zone]) == SettingsTransfer.Hash(b.Zones[zone]) &&
            a.Colors.Skip(offset).Take(count).SequenceEqual(b.Colors.Skip(offset).Take(count));
    }
    internal static bool OwnedPrefix(OpenRgbRecoveryRecord record, OpenRgbLightingState state) =>
        record.Mutation.Prefixes.Prepend(record.Mutation.Before).Any(p => SameScope(record, p, state));
    public void CheckCurrent(OpenRgbRecoveryRecord expected)
    {
        lock (gate)
        {
            if (!Valid(expected) || pending?.Hash != expected.Hash || !File.Exists(path)) throw new IOException("OpenRGBの操作記録が変更されています");
            var actual = ServiceEnvironment.Read<OpenRgbRecoveryRecord>(path);
            if (!Valid(actual) || actual.Hash != expected.Hash) throw new IOException("保存済みOpenRGB操作記録が変更されています");
        }
    }
    internal sealed class ReadLease(OpenRgbLightingRecovery owner, string hash, FileStream handle) : IDisposable
    {
        private FileStream? stream = handle;
        public void Check() { lock (owner.gate) if (stream is null || owner.pending?.Hash != hash) throw new IOException("OpenRGBの保存済み操作権が失効しました"); }
        public void Dispose() { lock (owner.gate) { stream?.Dispose(); stream = null; } }
    }
    public ReadLease AcquireReadLease(OpenRgbRecoveryRecord expected)
    {
        lock (gate)
        {
            ServiceEnvironment.NoLinks(path); var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try { CheckCurrent(expected); return new(this, expected.Hash, handle); } catch { handle.Dispose(); throw; }
        }
    }
    public OpenRgbRecoveryRecord Prepare(string boot, OpenRgbRecoveryScope scope, OpenRgbSdkController shape,
        OpenRgbLightingMutation mutation, OpenRgbLightingState exit, OpenRgbRecoveryRecord? expected = null, bool replaceWholeExit = false)
    {
        lock (gate)
        {
            if (expected is null) { if (pending is not null || File.Exists(path)) throw new IOException("前回のOpenRGB操作を復旧してください"); }
            else
            {
                CheckCurrent(expected);
                if (expected.Phase != OpenRgbRecoveryPhase.Active || expected.Scope != scope || Check(expected, boot, scope.Binding, scope.Target) is not null ||
                    !SameScope(expected, expected.Mutation.After, mutation.Before) ||
                    !(replaceWholeExit && scope.Zone is null) && !SameScope(expected, expected.Exit, exit))
                    throw new IOException("OpenRGBの直前値・終了条件を引き継げません");
            }
            if (shape.Id != scope.Target.ControllerId || OpenRgbSdkProtocol.Structure(shape) != scope.Target.Structure ||
                !OpenRgbLightingPlan.Same(OpenRgbLightingPlan.State(shape), mutation.Before)) throw new IOException("OpenRGBの取得状態と変更前提が一致しません");
            if (SettingsTransfer.Hash(OpenRgbLightingPlan.Build(shape, mutation.Before, mutation.Steps)) != SettingsTransfer.Hash(mutation))
                throw new IOException("OpenRGBの変更手順と途中状態が一致しません");
            // A recoverable intent is required before the first output, including every possible partial-send prefix.
            foreach (var prefix in mutation.Prefixes.Prepend(mutation.Before))
                _ = OpenRgbLightingPlan.Restore(OpenRgbLightingPlan.WithState(shape, prefix), exit, scope.Zone);
            var record = new OpenRgbRecoveryRecord(2, boot, scope, Guid.NewGuid().ToString("N"), shape.Zones.Select(z => z.LedCount).ToArray(),
                expected?.Original ?? mutation.Before, exit, mutation, OpenRgbRecoveryPhase.Active, expected?.Hash, "");
            return Save(record);
        }
    }
    public OpenRgbRecoveryRecord PrepareRecovery(OpenRgbRecoveryRecord expected, string boot, PhysicalDeviceBinding binding,
        OpenRgbSdkTarget target, OpenRgbSdkController current, OpenRgbLightingState? normalTarget = null)
    {
        lock (gate)
        {
            CheckCurrent(expected);
            if (Check(expected, boot, binding, target) is { } error) throw new IOException(error);
            if (expected.Phase == OpenRgbRecoveryPhase.Accepted || current.Id != target.ControllerId || OpenRgbSdkProtocol.Structure(current) != target.Structure ||
                !OwnedPrefix(expected, OpenRgbLightingPlan.State(current))) throw new IOException("OpenRGBの復旧前状態が一致しません");
            if (expected.Phase == OpenRgbRecoveryPhase.Recovering && normalTarget is not null) throw new IOException("開始済みのOpenRGB復元先は変更できません");
            var destination = expected.Phase == OpenRgbRecoveryPhase.Recovering ? expected.Mutation.After : normalTarget ?? expected.Exit;
            var mutation = OpenRgbLightingPlan.Restore(current, destination, expected.Scope.Zone);
            return Save(expected with { Revision = Guid.NewGuid().ToString("N"), PreviousHash = expected.Hash, Mutation = mutation, Phase = OpenRgbRecoveryPhase.Recovering });
        }
    }
    public OpenRgbRecoveryRecord AcceptRecovery(OpenRgbRecoveryRecord expected, OpenRgbLightingState reported, int acknowledgedSteps)
    {
        lock (gate)
        {
            CheckCurrent(expected);
            if (expected.Phase != OpenRgbRecoveryPhase.Recovering || acknowledgedSteps != expected.Mutation.Steps.Length ||
                !SameScope(expected, expected.Mutation.After, reported)) throw new IOException("OpenRGBの復元要求・報告値を確認できません");
            return Save(expected with { Revision = Guid.NewGuid().ToString("N"), PreviousHash = expected.Hash, Phase = OpenRgbRecoveryPhase.Accepted });
        }
    }
    public void Complete(OpenRgbRecoveryRecord expected)
    {
        lock (gate) { CheckCurrent(expected); if (expected.Phase != OpenRgbRecoveryPhase.Accepted) throw new IOException("OpenRGBの復元確認が完了していません"); Store(null); }
    }
    public void CancelUnsent(OpenRgbRecoveryRecord expected, OpenRgbRecoveryRecord? previous)
    {
        lock (gate)
        {
            CheckCurrent(expected);
            if (expected.Phase != OpenRgbRecoveryPhase.Active || expected.PreviousHash != previous?.Hash || previous is not null &&
                (!Valid(previous) || previous.Scope != expected.Scope || previous.Boot != expected.Boot || !OpenRgbLightingPlan.Same(previous.Original, expected.Original)))
                throw new IOException("OpenRGBの未送信記録を取り消せません");
            Store(previous);
        }
    }
    private OpenRgbRecoveryRecord Save(OpenRgbRecoveryRecord value)
    {
        value = Contract.Copy(value with { Hash = Hash(value) });
        if (!Valid(value) || JsonSerializer.SerializeToUtf8Bytes(value, Contract.Json).Length > 1024 * 1024) throw new IOException("OpenRGBの復旧記録の形式・サイズが不正です");
        Store(value); return Contract.Copy(value);
    }
    private void Store(OpenRgbRecoveryRecord? value)
    {
        ServiceEnvironment.NoLinks(path); if (value is null) File.Delete(path); else ServiceEnvironment.Write(path, value);
        pending = value is null ? null : Contract.Copy(value);
    }
}
