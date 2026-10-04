using System.Collections.Concurrent;
using System.Diagnostics;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>A bounded, dedicated HID thread keeps the named mutex on its owning thread.</summary>
public sealed class AsrockRgbStream : IAsyncDisposable, IDeviceLightingStream
{
    internal sealed record Expansion(RgbZone[] Zones, Dictionary<byte, LightingExitPolicy> Exits, Dictionary<byte, BoardLightSetting> Originals);
    private sealed record Frame(RgbColor Color, TaskCompletionSource<bool> Completion, Func<LightingFrameAuthorization>? Authorize = null,
        DeviceLightingFrame? EndpointFrame = null, Expansion? Expansion = null, byte[]? Reduction = null);
    private sealed record Controlled(LightingProgram Program, Dictionary<byte, LightingExitPolicy> Exits, Func<string?> PhysicalGuard,
        Dictionary<byte, BoardLightSetting> Originals);
    private sealed class FrameExpiredException(string message) : IOException(message);
    private readonly BlockingCollection<Frame> frames = new(1);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task worker;
    private Exception? failure;
    private int ended;
    private bool keepLast;
    private Controlled? controlled;
    private RgbZone[] selected;
    private AsrockRgbRestoreResult? restoredResult;
    private bool hardwareAttempted;
    public string? RecoveryError { get; private set; }
    internal bool OwnsTargets(byte[] targets) => selected.Select(z => z.HardwareZone!.Value).ToHashSet().SetEquals(targets);
    private AsrockRgbStream(string identity, RgbZone[] zones, bool direct, string recoveryDirectory,
        Func<IAsrockRgbSession> connect, Func<string> bootIdentity, Action checkConflicts, Controlled? controlled = null, IAsrockStreamRecovery? recovery = null)
    {
        this.controlled = controlled; selected = Contract.Copy(zones);
        worker = Task.Factory.StartNew(() => Run(identity, zones, direct, recoveryDirectory, connect, bootIdentity, checkConflicts, recovery), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    public static Task<AsrockRgbStream> OpenAsync(string identity, RgbZone[] zones, bool direct, string recoveryDirectory)
        => OpenAsync(identity, zones, direct, recoveryDirectory, () => new AsrockRgbSession(identity), AsrockRgbRecovery.BootIdentity, AsrockRgbMonitor.CheckConflicts);
    internal static async Task<AsrockRgbStream> OpenAsync(string identity, RgbZone[] zones, bool direct, string recoveryDirectory,
        Func<IAsrockRgbSession> connect, Func<string> bootIdentity, Action checkConflicts)
    {
        var instance = new AsrockRgbStream(identity, zones, direct, recoveryDirectory, connect, bootIdentity, checkConflicts);
        try { await instance.ready.Task; return instance; }
        catch { await instance.DisposeAsync(); throw; }
    }
    internal static async Task<AsrockRgbStream> OpenControlledAsync(string identity, RgbZone[] zones, string recoveryDirectory,
        LightingProgram program, IReadOnlyDictionary<byte, LightingExitPolicy> exits, Func<string?> physicalGuard,
        Func<IAsrockRgbSession> connect, Func<string> bootIdentity, Action checkConflicts, IAsrockStreamRecovery? recovery = null,
        IReadOnlyDictionary<byte, BoardLightSetting>? originals = null)
    {
        if (!DeviceLighting.ValidAnimation(program) || zones is not { Length: > 0 and <= 8 } ||
            zones.Any(z => z is null || z.HardwareZone is null) || zones.DistinctBy(z => z.HardwareZone).Count() != zones.Length ||
            exits.Count != zones.Length || zones.Any(z => !exits.TryGetValue(z.HardwareZone!.Value, out var exit) || !DeviceLightingConfiguration.ValidExit(exit)) ||
            originals is not null && (originals.Count > zones.Length || originals.Any(p => !zones.Any(z => z.HardwareZone == p.Key) ||
                !BoardLighting.IsValid(p.Value) || p.Value.MemoryPalette is not null)))
            throw new ArgumentException("マザー照明の同期・終了設定が不正です");
        var options = new Controlled(Contract.Copy(program), Contract.Copy(exits.ToDictionary()), physicalGuard, Contract.Copy(originals?.ToDictionary() ?? []));
        var instance = new AsrockRgbStream(identity, Contract.Copy(zones), true, recoveryDirectory, connect, bootIdentity, checkConflicts, options, recovery);
        try { await instance.ready.Task; return instance; }
        catch { await instance.DisposeAsync(); throw; }
    }
    private void CheckPhysical()
    { if (controlled?.PhysicalGuard() is { } error) throw new IOException(error); }
    private static void CheckAuthorization(Frame frame)
    {
        if (frame.Authorize is null) return;
        var authorization = frame.Authorize();
        if (authorization.Expired || authorization.ValidFor is { } remaining && remaining <= TimeSpan.Zero)
            throw new FrameExpiredException(authorization.Error ?? "照明フレームの有効期限を超えました");
        if (authorization.Error is { } denied) throw new IOException(denied);
        if (authorization.ValidFor is null) throw new IOException("照明フレームの有効期限がありません");
    }
    private void Run(string identity, RgbZone[] zones, bool direct, string recoveryDirectory,
        Func<IAsrockRgbSession> connect, Func<string> bootIdentity, Action checkConflicts, IAsrockStreamRecovery? recovery)
    {
        try
        {
            if (zones.Length is < 1 or > 8 || zones.Any(z => z.HardwareZone is null || z.Configuration is null || z.LedCount == 0))
                throw new IOException("同期するRGBゾーンが不正です");
            using var session = connect();
            CheckPhysical();
            Frame? activeFrame = null; var wroteFrame = false;
            IAsrockStreamRecovery? journal = null;
            byte[] Exchange(byte[] packet)
            {
                void BeforeExchange()
                {
                if (packet[1] is 0x14 or 0x11 && activeFrame is { } readingFrame)
                {
                    CheckPhysical(); CheckAuthorization(readingFrame);
                }
                if (packet[1] is not (0x14 or 0x11))
                {
                    // The retained HID handle is never replaced during a stream. Recheck its binding before
                    // recovery packets too; a callback may discover a disconnect during the preceding read.
                    CheckPhysical();
                    if (direct && (activeFrame is not null || hardwareAttempted)) journal!.CheckOwnership();
                    if (activeFrame is { } frame)
                    {
                        // Includes direct-mode entry and every packet, after queries and durable journal writes.
                        CheckAuthorization(frame); wroteFrame = true; hardwareAttempted = true;
                    }
                }
                }
                if (session is IAsrockGuardedRgbSession queued) return queued.Exchange(packet, BeforeExchange);
                BeforeExchange();
                return session.Exchange(packet);
            }
            var current = zones.ToDictionary(z => z.HardwareZone!.Value, z => z.Configuration!);
            journal = recovery ?? new AsrockRgbRecoveryStore(recoveryDirectory);
            if (journal.Exists) throw new IOException("マザー照明の復旧記録があります。照明を再取得してください");
            var transaction = direct ? new AsrockVolatileTransaction(identity, Exchange) : null;
            var plan = transaction is not null ? new AsrockDirectPlan(transaction.Snapshot, zones) : null;
            var settledColors = new Dictionary<byte, RgbColor>();
            Dictionary<byte, BoardLightSetting>? FailureSettings(Controlled? options) => options is null ? null : transaction!.Snapshot.Zones.ToDictionary(z => z.Address, z =>
                options.Exits.TryGetValue(z.Address, out var exit) ? exit.Action switch
                {
                    LightingExitAction.FixedColor => new BoardLightSetting(1, exit.Color!.R, exit.Color.G, exit.Color.B),
                    LightingExitAction.Off => new BoardLightSetting(0, 0, 0, 0),
                    _ => options.Originals.GetValueOrDefault(z.Address) ?? z.Setting
                } : settledColors.TryGetValue(z.Address, out var fixedColor) ? new BoardLightSetting(1, fixedColor.R, fixedColor.G, fixedColor.B) : z.Setting);
            var failureSettings = FailureSettings(controlled);
            var boot = direct ? bootIdentity() : "";
            var began = false; RgbColor? last = null; var lastStopColors = new Dictionary<byte, RgbColor>();
            var lastColors = new Dictionary<byte, RgbColor>();
            ready.TrySetResult();
            var conflictCheck = DateTimeOffset.MinValue;
            try
            {
            foreach (var frame in frames.GetConsumingEnumerable())
            {
                try
                {
                    activeFrame = frame; wroteFrame = false; CheckPhysical(); CheckAuthorization(frame);
                    if (DateTimeOffset.UtcNow - conflictCheck > TimeSpan.FromSeconds(1))
                    { checkConflicts(); conflictCheck = DateTimeOffset.UtcNow; }
                    if (transaction is not null)
                    {
                        if (frame.Reduction is { } ending)
                        {
                            if (controlled is null || !began || !hardwareAttempted || !journal.SupportsReduction ||
                                ending.Length == 0 || ending.Length >= zones.Length || ending.Distinct().Count() != ending.Length ||
                                ending.Any(z => !lastStopColors.ContainsKey(z) || !zones.Any(s => s.HardwareZone == z)))
                                throw new IOException("照明ストリームの終了条件が変更されています");
                            transaction.CheckFrameOwnership();
                            var remaining = zones.Where(z => !ending.Contains(z.HardwareZone!.Value)).ToArray();
                            var background = settledColors.ToDictionary();
                            foreach (var z in ending) background[z] = lastStopColors[z];
                            var reducedPlan = new AsrockDirectPlan(transaction.Snapshot, remaining, background);
                            var desired = failureSettings!.ToDictionary();
                            foreach (var z in ending) { var c = lastStopColors[z]; desired[z] = new(1, c.R, c.G, c.B); }
                            journal.Save(boot, transaction.Snapshot, desired); failureSettings = desired;
                            transaction.CheckFrameOwnership();
                            var reductionStarted = Stopwatch.GetTimestamp();
                            var reducedColors = lastColors.Where(p => !ending.Contains(p.Key)).ToDictionary();
                            foreach (var packet in AsrockDirectProtocol.Frame(reducedPlan.FrameByZone(reducedColors), AsrockDirectProtocol.StreamingOrder))
                            {
                                if (Stopwatch.GetElapsedTime(reductionStarted).TotalMilliseconds > 500) throw new IOException("マザー照明の終了色送信が時間内に完了しませんでした");
                                _ = Exchange(packet);
                            }
                            journal.Reduce(remaining.Select(z => z.HardwareZone!.Value).ToArray());
                            controlled = controlled with { Exits = controlled.Exits.Where(p => !ending.Contains(p.Key)).ToDictionary(),
                                Originals = controlled.Originals.Where(p => !ending.Contains(p.Key)).ToDictionary() };
                            selected = zones = remaining; plan = reducedPlan; settledColors = background; lastColors = reducedColors;
                            foreach (var z in ending) lastStopColors.Remove(z);
                            frame.Completion.TrySetResult(true); continue;
                        }
                        if (frame.Expansion is { } expansion)
                        {
                            if (controlled is null || !began || !hardwareAttempted || expansion.Zones.Length <= zones.Length ||
                                zones.Any(z => !expansion.Zones.Any(n => n.HardwareZone == z.HardwareZone))) throw new IOException("照明ストリームの追加条件が変更されています");
                            var newTargets = expansion.Zones.Select(z => z.HardwareZone!.Value).ToHashSet();
                            var originals = expansion.Originals.ToDictionary();
                            foreach (var entry in controlled.Originals) originals[entry.Key] = entry.Value;
                            foreach (var entry in settledColors.Where(p => newTargets.Contains(p.Key)))
                                originals[entry.Key] = new(1, entry.Value.R, entry.Value.G, entry.Value.B);
                            var background = settledColors.Where(p => !newTargets.Contains(p.Key)).ToDictionary();
                            var expandedPlan = new AsrockDirectPlan(transaction.Snapshot, expansion.Zones, background);
                            var options = controlled with { Exits = expansion.Exits, Originals = originals };
                            var expandedFailure = FailureSettings(options)!;
                            journal.Expand(expansion.Zones.Select(z => z.HardwareZone!.Value).ToArray(), expandedFailure);
                            controlled = options; selected = zones = expansion.Zones; plan = expandedPlan; failureSettings = expandedFailure; settledColors = background;
                        }
                        if (!began)
                        {
                            began = true;
                            transaction.Begin(snapshot => journal.Save(boot, snapshot, failureSettings), true);
                        }
                        transaction.CheckFrameOwnership();
                        var started = Stopwatch.GetTimestamp();
                        var colors = zones.ToDictionary(z => z.HardwareZone!.Value, z => frame.EndpointFrame?.ColorFor("zone:" + z.HardwareZone) ?? frame.Color);
                        foreach (var packet in AsrockDirectProtocol.Frame(plan!.FrameByZone(colors), AsrockDirectProtocol.StreamingOrder))
                        {
                            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > 500) throw new IOException("マザー照明の色送信が時間内に完了しませんでした");
                            _ = Exchange(packet);
                        }
                    }
                    else
                    {
                        var value = new BoardLightSetting(1, frame.Color.R, frame.Color.G, frame.Color.B);
                        var changes = current.Select(z => new BoardLightChange(z.Key, z.Value, value)).ToArray();
                        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                        var result = AsrockRgbControl.Apply(session.Exchange, changes, deadline.Token);
                        if (!result.Verified) throw new IOException("マザー照明の同期を停止しました。設定の読み戻しが一致しません");
                        foreach (var key in current.Keys.ToArray()) current[key] = value;
                    }
                    last = frame.Color;
                    foreach (var zone in zones)
                    {
                        lastStopColors[zone.HardwareZone!.Value] = frame.EndpointFrame?.NormalStopColorFor("zone:" + zone.HardwareZone) ??
                            (controlled is null ? last : DeviceLighting.StopColor(controlled.Program));
                        lastColors[zone.HardwareZone!.Value] = frame.EndpointFrame?.ColorFor("zone:" + zone.HardwareZone) ?? frame.Color;
                    }
                    frame.Completion.TrySetResult(true);
                }
                catch (FrameExpiredException) when (!wroteFrame)
                {
                    try
                    {
                        if (!hardwareAttempted && transaction is not null && began)
                        {
                            // Begin may have saved its intent before the first mutation was denied. Rebuild the
                            // untouched transaction, rather than leaving a half-started state or sending an exit color.
                            activeFrame = null; journal.ClearUnsent(); began = false; transaction = new(identity, Exchange);
                            plan = new(transaction.Snapshot, zones); failureSettings = FailureSettings(controlled);
                        }
                        frame.Completion.TrySetResult(false);
                    }
                    catch (Exception e) { failure = e; frame.Completion.TrySetException(e); throw; }
                }
                catch (Exception e) { failure = e; frame.Completion.TrySetException(e); throw; }
                finally { activeFrame = null; }
            }
            }
            finally
            {
                if (transaction is not null && began && !hardwareAttempted)
                {
                    // A denied first write must not cause restoration itself to change the device.
                    try { if (journal.Exists) journal.ClearUnsent(); }
                    catch (Exception e) { RecoveryError = "送信前の照明復旧記録を削除できません。" + e.Message; }
                }
                else if (transaction is not null && began)
                {
                    try
                    {
                        CheckPhysical(); checkConflicts(); journal.CheckOwnership();
                        Dictionary<byte, BoardLightSetting>? desired = failureSettings;
                        if (keepLast && failure is null && last is not null)
                        {
                            desired = transaction.Snapshot.Zones.ToDictionary(z => z.Address, z => lastStopColors.TryGetValue(z.Address, out var color)
                                ? new BoardLightSetting(1, color.R, color.G, color.B) : failureSettings?.GetValueOrDefault(z.Address) ?? z.Setting);
                            // A crash halfway through normal stop must finish the same native settings.
                            try { journal.Save(boot, transaction.Snapshot, desired); }
                            catch (Exception e)
                            {
                                // If a new intent cannot be saved, the original durable intent is still valid.
                                desired = failureSettings; RecoveryError = "固定色の復旧記録を保存できないため、保存済みの終了設定へ戻しました。" + e.Message;
                            }
                        }
                        var restored = transaction.Restore(desired);
                        restoredResult = restored;
                        if (restored.Restored) journal.Clear();
                        else RecoveryError = "マザー照明の固定色への復帰を確認できません。再取得してください。" +
                            string.Join(" / ", restored.Errors.Concat(restored.Zones.Where(z => z.Status != "verified").Select(z => $"ゾーン{z.Zone}: {z.Status}")));
                    }
                    catch (Exception e) { RecoveryError = "マザー照明の復旧記録を保持しました。" + e.Message; }
                }
            }
        }
        catch (Exception e) { failure = e; ready.TrySetException(e); }
        finally
        {
            frames.CompleteAdding();
            while (frames.TryTake(out var frame)) frame.Completion.TrySetException(failure ?? new IOException("同期を終了しました"));
        }
    }
    public Task WriteAsync(RgbColor color)
    {
        if (controlled is not null) return Task.FromException(new IOException("サービス管理の同期には期限付きフレームが必要です"));
        return Enqueue(color, null);
    }
    private Task<bool> Enqueue(RgbColor color, Func<LightingFrameAuthorization>? authorize, DeviceLightingFrame? endpointFrame = null, Expansion? expansion = null, byte[]? reduction = null)
    {
        if (color is null || !color.Valid) return Task.FromException<bool>(new ArgumentException("不正な照明色です"));
        if (failure is { } error) return Task.FromException<bool>(error);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (Volatile.Read(ref ended) != 0 || !frames.TryAdd(new(color, completion, authorize, endpointFrame, expansion, reduction))) throw new IOException("照明の送信が進行中か終了しています");
        }
        catch (InvalidOperationException) { completion.TrySetException(new IOException("照明の同期が終了しました")); }
        catch (IOException e) { completion.TrySetException(e); }
        return completion.Task;
    }
    Task<DeviceDriverReleaseResult[]> IDeviceLightingStream.WriteAsync(DeviceLightingFrame frame, CancellationToken ct) => WriteExpandedAsync(frame, null, ct);
    internal async Task<DeviceDriverReleaseResult[]> WriteExpandedAsync(DeviceLightingFrame frame, Expansion? expansion, CancellationToken ct)
    {
        if (controlled is null) throw new IOException("共通サービスの照明設定がありません");
        ct.ThrowIfCancellationRequested();
        var targets = expansion?.Zones ?? selected;
        if (frame.TargetError(targets.Select(z => "zone:" + z.HardwareZone)) is { } invalid)
            return targets.Select(z => new DeviceDriverReleaseResult("zone:" + z.HardwareZone, new(Outcome.Rejected, invalid))).ToArray();
        DeviceDriverResult result;
        try
        {
            var sent = await Enqueue(frame.Color, () => ct.IsCancellationRequested ? new("照明フレームを取り消しました") : frame.Authorize(), frame, expansion).ConfigureAwait(false);
            result = sent ? new(Outcome.WrittenUnverified, "マザー照明のフレームを送信しました") :
                new(Outcome.Rejected, "照明フレームの有効期限を超えました", Failure: DeviceDriverFailure.LightingFrameExpired);
        }
        catch (Exception e) { result = new(Outcome.Unknown, "マザー照明の送信結果は未確認です: " + e.Message); }
        return targets.Select(z => new DeviceDriverReleaseResult("zone:" + z.HardwareZone, result.Outcome == Outcome.WrittenUnverified
            ? result with { ReportedLighting = new("fixed", [frame.ColorFor("zone:" + z.HardwareZone)]) } : result)).ToArray();
    }
    internal async Task<DeviceDriverReleaseResult[]> EndSelectedAsync(byte[] targets, CancellationToken ct)
    {
        if (controlled is null || targets.Length == 0 || targets.Length >= selected.Length || targets.Distinct().Count() != targets.Length ||
            targets.Any(z => !selected.Any(s => s.HardwareZone == z))) throw new IOException("終了する照明ゾーンが不正です");
        ct.ThrowIfCancellationRequested();
        try
        {
            await Enqueue(new(0, 0, 0), null, reduction: targets.ToArray()).ConfigureAwait(false);
            // The durable desired values are the accepted fixed colors, not the last animated sample.
            return targets.Select(z => new DeviceDriverReleaseResult("zone:" + z,
                new(Outcome.WrittenUnverified, "選択したマザー照明を固定色にしました・点灯未確認"))).ToArray();
        }
        catch (Exception e) { return targets.Select(z => new DeviceDriverReleaseResult("zone:" + z,
            new(Outcome.Unknown, "マザー照明の個別終了を確認できません: " + e.Message))).ToArray(); }
    }
    async Task<DeviceDriverReleaseResult[]> IDeviceLightingStream.EndAsync(DeviceLightingStopReason reason, CancellationToken ct)
    {
        if (controlled is null) throw new IOException("共通サービスの照明設定がありません");
        // Once output may have changed, recovery must finish independently of an animation cancellation token.
        await FinishAsync(reason == DeviceLightingStopReason.Normal).ConfigureAwait(false);
        var restored = restoredResult;
        if (!hardwareAttempted) return selected.Select(z => new DeviceDriverReleaseResult("zone:" + z.HardwareZone,
            new(RecoveryError is null ? Outcome.Confirmed : Outcome.Unknown,
                RecoveryError ?? "照明出力を変更せず同期準備を終了しました"))).ToArray();
        return RestoreResults(restored, selected.Select(z => z.HardwareZone!.Value).ToArray(), RecoveryError);
    }
    internal static DeviceDriverReleaseResult[] RestoreResults(AsrockRgbRestoreResult? restored, byte[] selected, string? recoveryError)
    {
        var addresses = selected.ToHashSet();
        var globalFailure = restored is null || !restored.OrderRestored || !restored.TopologyUnchanged ||
            restored.Errors.Length != 0 || restored.Restored && recoveryError is not null ||
            restored.Zones.Any(z => !addresses.Contains(z.Zone) && z.Status != "verified");
        return selected.Select(z =>
        {
            var actual = restored?.Zones.SingleOrDefault(r => r.Zone == z);
            var ok = !globalFailure && actual?.Status == "verified";
            var color = actual?.Reported;
            var program = color is null ? null : ReportedProgram(color);
            return new DeviceDriverReleaseResult("zone:" + z, new(ok ? Outcome.Confirmed : Outcome.Unknown,
                ok ? "マザー照明の終了設定を読み戻しました" : recoveryError ?? "マザー照明の終了結果は未確認です", ReportedLighting: program));
        }).ToArray();
    }
    internal static LightingProgram ReportedProgram(BoardLightSetting color) => new(
        color.Mode switch { 0 => "off", 1 => "fixed", _ => "native/asrock-usb/" + color.Mode },
        color.Mode == 0 ? [] : [new(color.Red, color.Green, color.Blue)], color.Speed);
    public async Task FinishAsync(bool keepLastFrame)
    {
        if (Interlocked.Exchange(ref ended, 1) == 0) { keepLast = keepLastFrame; frames.CompleteAdding(); }
        await worker;
    }
    public async ValueTask DisposeAsync() => await FinishAsync(false);
}
