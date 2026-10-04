using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed partial class AsrockDeviceDriver
{
    private readonly AsrockNativeRecovery? nativeJournal;
    private readonly HashSet<byte> nativeOwned = [], nativeFaulted = [];

    private static BoardLightSetting NativeSetting(LightingProgram? program)
    {
        if (!DeviceConfigurationRules.ValidProgram(program) || program!.Reverse || program.LedColors is not null)
            throw new ArgumentException("Native照明の色・明るさ・速度が不正です");
        if (program.EffectId == "off" && program.Colors.Length == 0) return new(0, 0, 0, 0);
        if (program.EffectId == "fixed" && program.Colors.Length == 1)
        {
            var fixedColor = program.Colors[0].Scale(program.Brightness / 100d);
            return new(1, fixedColor.R, fixedColor.G, fixedColor.B);
        }
        var effect = BoardLighting.Effects.SingleOrDefault(e => program.EffectId == "native/asrock-usb/" + e.Id);
        if (effect is null || effect.Color && program.Colors.Length != 1 || !effect.Color && program.Colors.Length > 1 ||
            !effect.Color && effect.Id != 0 && program.Brightness != 100)
            throw new ArgumentException("未対応のNative効果、色数、または明るさです");
        var color = program.Colors.FirstOrDefault() ?? new RgbColor(0, 0, 0);
        if (effect.Color) color = color.Scale(program.Brightness / 100d);
        return new(effect.Id, color.R, color.G, color.B, program.Speed);
    }
    private static BoardLightSetting NativeExit(LightingExitPolicy exit, BoardLightSetting original) => exit.Action switch
    {
        LightingExitAction.Off => new(0, 0, 0, 0),
        LightingExitAction.FixedColor => new(1, exit.Color!.R, exit.Color.G, exit.Color.B),
        LightingExitAction.RestoreOriginal => original,
        _ => throw new IOException("Native照明の終了方法が不正です")
    };
    private void GuardNative(IAsrockRgbSession session, AsrockDeviceConnection c, PhysicalDeviceBinding b,
        AsrockNativeRecoveryRecord record, byte zone, BoardLightSetting expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Verify(c, b) is { } error) throw new IOException(error);
        nativeJournal!.CheckCurrent(record);
        var state = Read(session, c.Path);
        if (!AsrockNativeRecovery.LayoutMatches(record, state) || state.Zones.SingleOrDefault(z => z.Address == zone)?.Setting != expected)
            throw new IOException("送信前にNative照明の構成・色順・設定が変更されました: " +
                AsrockNativeRecovery.MismatchDetails(record, state, zone, expected));
        if (Verify(c, b) is { } changed) throw new IOException(changed);
        nativeJournal.CheckCurrent(record);
        ct.ThrowIfCancellationRequested();
    }
    private static void WriteNative(IAsrockRgbSession session, byte zone, BoardLightSetting value, byte order, Action guard)
    {
        var packet = AsrockRgbControl.Packet(zone, value, order);
        if (session is IAsrockGuardedRgbSession queued) _ = queued.Exchange(packet, guard);
        else { guard(); _ = session.Exchange(packet); }
    }
    private void CacheNativeRead(AsrockDeviceConnection c, AsrockRgbSnapshot state)
    {
        lock (gate)
        {
            if (connection != c) throw new IOException("Native照明の接続が変わりました");
            observed = state; readError = null;
        }
    }
    private async Task<DeviceDriverResult> ApplyNativeAsync(DeviceEndpointAction action, CancellationToken ct)
    {
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (gate) if (active is not null) return new(Outcome.Rejected, "同じ機器の同期を終了してから設定してください");
            await ReadCoreAsync(ct).ConfigureAwait(false);
            AsrockDeviceConnection c; PhysicalDeviceBinding b; LightingExitPolicy exit; AsrockRgbSnapshot state;
            var zone = Zone(action.EndpointId);
            lock (gate)
            {
                if (Validate(action) is { } invalid) return new(Outcome.Rejected, invalid);
                c = connection!; b = Candidate()!; state = observed!; exit = settings[zone].Config.Exit!;
            }
            var desired = NativeSetting(action.Lighting);
            return await Task.Run(() =>
            {
                var previous = nativeJournal!.Pending;
                AsrockNativeRecoveryRecord? prepared = null;
                var attempted = false;
                try
                {
                    backend.CheckConflicts();
                    var scope = previous?.Scope ?? new AsrockRecoveryScope(b, c.ConnectionId, c.Confidence, Guid.NewGuid().ToString("N"));
                    var original = previous?.Entries.SingleOrDefault(e => e.Zone == zone)?.Original ?? state.Zones.Single(z => z.Address == zone).Setting;
                    prepared = nativeJournal.Prepare(boot, scope, state, zone, desired, NativeExit(exit, original));
                    lock (gate) nativeOwned.Add(zone);
                    using var session = backend.Borrow(c);
                    Exception? writeError = null;
                    try
                    {
                        WriteNative(session, zone, desired, prepared.Order, () =>
                        {
                            GuardNative(session, c, b, prepared, zone, prepared.Entries.Single(e => e.Zone == zone).Before, ct);
                            attempted = true;
                        });
                    }
                    catch (Exception e) { writeError = e; }
                    if (!attempted) throw writeError ?? new IOException("Native照明は送信されませんでした");
                    // A lost acknowledgement can still be confirmed by a fresh read from the same retained connection.
                    var actual = Read(session, c.Path);
                    if (Verify(c, b) is { } changed) throw new IOException(changed);
                    CacheNativeRead(c, actual);
                    if (!AsrockNativeRecovery.LayoutMatches(prepared, actual) || actual.Zones.SingleOrDefault(z => z.Address == zone)?.Setting != desired)
                        throw new IOException((writeError?.Message ?? "Native照明の読み戻しが一致しません") + ": " +
                            AsrockNativeRecovery.MismatchDetails(prepared, actual, zone, desired));
                    nativeJournal.CheckCurrent(prepared);
                    return new DeviceDriverResult(Outcome.Confirmed, "Native照明の設定を読み戻しました", ReportedLighting: AsrockRgbStream.ReportedProgram(desired));
                }
                catch (Exception e)
                {
                    if (!attempted)
                    {
                        try
                        {
                            if (prepared is not null) nativeJournal.CancelUnsent(prepared, previous);
                            lock (gate) if (previous?.Entries.Any(p => p.Zone == zone) != true) nativeOwned.Remove(zone);
                            return new DeviceDriverResult(Outcome.Rejected, "Native照明は送信していません: " + e.Message);
                        }
                        catch (Exception cleanup)
                        {
                            lock (gate) nativeFaulted.Add(zone);
                            return new DeviceDriverResult(Outcome.Unknown, "未送信の復旧記録を戻せません: " + cleanup.Message);
                        }
                    }
                    lock (gate) nativeFaulted.Add(zone);
                    return new DeviceDriverResult(Outcome.Unknown, "Native照明の結果を確認できません: " + e.Message);
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) { return new(Outcome.Rejected, "Native照明の送信前確認に失敗しました: " + e.Message); }
        finally { io.Release(); }
    }
    private async Task<DeviceDriverReleaseResult[]> ReleaseNativeAsync(byte[]? selected, CancellationToken ct)
    {
        await io.WaitAsync(ct).ConfigureAwait(false);
        try { return await ReleaseNativeCoreAsync(selected).ConfigureAwait(false); }
        finally { io.Release(); }
    }
    // Caller owns io; used by stream shutdown after it has relinquished the controller-wide Direct session.
    private async Task<DeviceDriverReleaseResult[]> ReleaseNativeCoreAsync(byte[]? selected)
    {
            var saved = nativeJournal?.Pending;
            if (saved is null) return [];
            var zones = saved.Entries.Select(e => e.Zone).Where(z => selected is null || selected.Contains(z)).ToArray();
            DeviceDriverReleaseResult[] Unknown(string message) => zones.Select(z => new DeviceDriverReleaseResult("zone:" + z, new(Outcome.Unknown, message))).ToArray();
            // Direct mode owns the complete controller. Restore it before changing any separately owned native zone.
            if (active is not null || journal?.Pending is not null) return Unknown("同期照明の復旧後にNative照明を復旧します");
            if (journalError is not null) return Unknown(journalError);
            AsrockDeviceConnection? c; PhysicalDeviceBinding? b;
            lock (gate) { c = connection; b = Candidate(); }
            if (c is null || b is null) return Unknown("復旧するNative照明を取得できません");
            if (AsrockBoundRecovery.Check(saved.Boot, saved.Scope, boot, b, c.ConnectionId, c.Confidence) is { } mismatch) return Unknown(mismatch);
            var results = new List<DeviceDriverReleaseResult>();
            foreach (var zone in zones)
            {
                var result = await Task.Run(() => RecoverNativeZone(c, b, zone), CancellationToken.None).ConfigureAwait(false);
                results.Add(new("zone:" + zone, result));
                lock (gate)
                {
                    if (result.Outcome == Outcome.Confirmed) { nativeOwned.Remove(zone); nativeFaulted.Remove(zone); }
                    else nativeFaulted.Add(zone);
                }
            }
            return results.ToArray();
    }
    private DeviceDriverResult RecoverNativeZone(AsrockDeviceConnection c, PhysicalDeviceBinding b, byte zone)
    {
        try
        {
            var saved = nativeJournal!.Pending ?? throw new IOException("Native照明の復旧記録がありません");
            var entry = saved.Entries.Single(e => e.Zone == zone);
            backend.CheckConflicts(); nativeJournal.CheckCurrent(saved);
            using var session = backend.Borrow(c);
            var state = Read(session, c.Path);
            var before = state.Zones.SingleOrDefault(z => z.Address == zone)?.Setting;
            if (!AsrockNativeRecovery.LayoutMatches(saved, state) || before is null ||
                before != entry.Before && before != entry.Intended && before != entry.Exit)
                throw new IOException("Native照明に別の設定または構成が見つかりました。上書きしません: " +
                    AsrockNativeRecovery.MismatchDetails(saved, state, zone, entry.Before, entry.Intended, entry.Exit));
            GuardNative(session, c, b, saved, zone, before, CancellationToken.None);
            Exception? writeError = null;
            if (before != entry.Exit)
            {
                try { WriteNative(session, zone, entry.Exit, saved.Order, () => GuardNative(session, c, b, saved, zone, before, CancellationToken.None)); }
                catch (Exception e) { writeError = e; }
            }
            var actual = Read(session, c.Path);
            if (Verify(c, b) is { } error) throw new IOException(error);
            CacheNativeRead(c, actual);
            if (!AsrockNativeRecovery.LayoutMatches(saved, actual) || actual.Zones.SingleOrDefault(z => z.Address == zone)?.Setting != entry.Exit)
                throw new IOException((writeError?.Message ?? "Native照明の終了設定を確認できません") + ": " +
                    AsrockNativeRecovery.MismatchDetails(saved, actual, zone, entry.Exit));
            nativeJournal.Complete(saved, zone);
            return new(Outcome.Confirmed, "Native照明の終了設定を読み戻しました", ReportedLighting: AsrockRgbStream.ReportedProgram(entry.Exit));
        }
        catch (Exception e) { return new(Outcome.Unknown, "Native照明の復旧を確認できません: " + e.Message); }
    }
}
