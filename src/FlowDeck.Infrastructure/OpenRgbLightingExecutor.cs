using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed class OpenRgbLightingExpiredException(string message) : IOException(message);
internal sealed class OpenRgbLightingOwnershipException(string message) : IOException(message);
internal sealed record OpenRgbLightingExecution(DeviceDriverResult Result, OpenRgbSdkSnapshot? Snapshot = null);

// One logical scope, serialized by its owning driver. The driver must also exclude whole-device/zone
// overlap, establish physical binding and reserve the SDK resource before invoking this executor.
internal sealed class OpenRgbLightingExecutor
{
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly OpenRgbSdkConnection connection;
    private readonly OpenRgbLightingRecovery journal;
    private readonly OpenRgbRecoveryScope scope;
    private readonly string boot;
    private readonly Action verifyBinding;
    private readonly IOpenRgbLightingTransport? transport;
    private string? ownedHash;
    private bool faulted;
    private sealed class Progress { public bool Attempted; public int Acknowledged; }
    public OpenRgbRecoveryRecord? Pending => journal.Pending;
    public bool NeedsRecovery => faulted || transport?.NeedsRecovery == true || Pending is { } p && (p.Hash != ownedHash || p.Phase != OpenRgbRecoveryPhase.Active);

    public OpenRgbLightingExecutor(OpenRgbSdkConnection connection, OpenRgbLightingRecovery journal,
        OpenRgbRecoveryScope scope, string boot, Action verifyBinding, IOpenRgbLightingTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(verifyBinding);
        this.connection = connection; this.journal = journal; this.scope = Contract.Copy(scope); this.boot = boot; this.verifyBinding = verifyBinding;
        this.transport = transport;
    }
    private void Check(Action authorize, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); verifyBinding(); authorize(); ct.ThrowIfCancellationRequested();
    }
    private async Task<OpenRgbSdkSnapshot> ReadAsync(Action authorize, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            Check(authorize, ct);
            try
            {
                var value = await connection.ReadAsync(scope.Target, ct, () => Check(authorize, ct)).ConfigureAwait(false);
                Check(authorize, ct); return value;
            }
            // Only a read raced by a same-device notification can be repeated. Never repeat a write.
            catch (OpenRgbSdkObservationChangedException) when (attempt < 2) { }
        }
    }
    private static OpenRgbLightingState Exit(OpenRgbSdkController current, OpenRgbRecoveryRecord? previous, int? zone, LightingExitPolicy policy)
    {
        var original = previous?.Original ?? OpenRgbLightingPlan.State(current);
        return policy.Action == LightingExitAction.RestoreOriginal ? Contract.Copy(original) :
            OpenRgbLightingPlan.Create(OpenRgbLightingPlan.WithState(current, original), zone, DeviceLightingConfiguration.ExitProgram(policy)).After;
    }
    private async Task<OpenRgbSdkSnapshot> SendAsync(OpenRgbRecoveryRecord record, OpenRgbLightingExitIntent? exit,
        Action authorize, Progress progress, CancellationToken ct)
    {
        using var lease = journal.AcquireReadLease(record);
        if (transport is not null) await transport.TrackAsync(record, () => { Check(authorize, ct); lease.Check(); }, ct).ConfigureAwait(false);
        OpenRgbSdkSnapshot? latest = null;
        foreach (var step in record.Mutation.Steps)
        {
            void BeforeSend()
            {
                Check(authorize, ct); lease.Check();
                var observed = OpenRgbLightingPlan.State(connection.Reported(scope.Target).Controller);
                if (!record.Mutation.Prefixes.Take(progress.Acknowledged).Prepend(record.Mutation.Before)
                    .Any(p => OpenRgbLightingRecovery.SameScope(record, p, observed)))
                    throw new OpenRgbLightingOwnershipException("OpenRGBに別の設定が見つかりました");
                // At this point the transport is about to write. Any subsequent failure keeps the obligation.
                progress.Attempted = true;
            }
            if (transport is null) await OpenRgbLightingPlan.SendStepAsync(connection, scope.Target, step, BeforeSend, ct).ConfigureAwait(false);
            else await transport.SendAsync(connection.Reported(scope.Target).Controller, step, exit,
                () => { Check(authorize, ct); lease.Check(); }, BeforeSend, ct).ConfigureAwait(false);
            progress.Acknowledged++;
            latest = await ReadAsync(authorize, ct).ConfigureAwait(false); lease.Check();
            if (!OpenRgbLightingRecovery.SameScope(record, record.Mutation.Prefixes[progress.Acknowledged - 1], OpenRgbLightingPlan.State(latest.Controller)))
                throw new OpenRgbLightingOwnershipException("OpenRGBは受理しましたが、報告された設定が一致しません");
        }
        return latest ?? await ReadAsync(authorize, ct).ConfigureAwait(false);
    }
    public Task<OpenRgbLightingExecution> ApplyAsync(LightingProgram program, LightingExitPolicy exit, Action authorize,
        bool frame = false, CancellationToken ct = default)
    {
        program = Contract.Copy(program); exit = Contract.Copy(exit);
        return ApplyCoreAsync((controller, previous) =>
        {
            if (!DeviceConfigurationRules.ValidProgram(program) || !DeviceLightingConfiguration.ValidExit(exit)) throw new IOException("OpenRGBの照明設定・終了方法が不正です");
            return (OpenRgbLightingPlan.Create(controller, scope.Zone, program, frame), Exit(controller, previous, scope.Zone, exit));
        }, new(scope.Zone, exit), authorize, false, program, ct);
    }
    public Task<OpenRgbLightingExecution> ApplySharedAsync(IReadOnlyDictionary<int, RgbColor> colors, IReadOnlyDictionary<int, RgbColor> exits,
        Action authorize, CancellationToken ct = default, IReadOnlyDictionary<int, LightingExitPolicy>? exitPolicies = null)
    {
        var frozen = colors.ToDictionary(p => p.Key, p => p.Value with { }); var ending = exits.ToDictionary(p => p.Key, p => p.Value with { });
        var policies = exitPolicies?.ToDictionary(p => p.Key, p => Contract.Copy(p.Value)) ??
            ending.ToDictionary(p => p.Key, p => new LightingExitPolicy(LightingExitAction.FixedColor, p.Value));
        return ApplyCoreAsync((controller, previous) =>
        {
            if (scope.Zone is not null || !frozen.Keys.ToHashSet().SetEquals(ending.Keys)) throw new IOException("OpenRGBの共有フレーム・終了対象が一致しません");
            if (!policies.Keys.ToHashSet().SetEquals(ending.Keys) || policies.Any(p => !DeviceLightingConfiguration.ValidExit(p.Value) ||
                p.Value.Action is not (LightingExitAction.FixedColor or LightingExitAction.Off) ||
                DeviceLighting.StopColor(DeviceLightingConfiguration.ExitProgram(p.Value)) != ending[p.Key]))
                throw new IOException("OpenRGBの共有フレームの終了方法と色が一致しません");
            var mutation = OpenRgbLightingPlan.SharedFrame(controller, frozen);
            return (mutation, OpenRgbLightingPlan.SharedExit(controller, mutation, previous, ending));
        }, new(null, null, policies), authorize, true, null, ct);
    }
    private async Task<OpenRgbLightingExecution> ApplyCoreAsync(
        Func<OpenRgbSdkController, OpenRgbRecoveryRecord?, (OpenRgbLightingMutation Mutation, OpenRgbLightingState Exit)> plan,
        OpenRgbLightingExitIntent exitIntent, Action authorize, bool replaceWholeExit, LightingProgram? reportedProgram, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        var entered = false; var progress = new Progress(); OpenRgbRecoveryRecord? previous = null, prepared = null;
        try
        {
            await operations.WaitAsync(ct).ConfigureAwait(false); entered = true;
            previous = journal.Pending;
            if (faulted || previous is not null && (previous.Hash != ownedHash || previous.Phase != OpenRgbRecoveryPhase.Active))
                throw new IOException("前回のOpenRGB操作を復旧してください");
            Check(authorize, ct);
            var before = await ReadAsync(authorize, ct).ConfigureAwait(false);
            if (previous is not null)
            {
                journal.CheckCurrent(previous);
                if (OpenRgbLightingRecovery.Check(previous, boot, scope.Binding, scope.Target) is { } error) throw new OpenRgbLightingOwnershipException(error);
                if (!OpenRgbLightingRecovery.SameScope(previous, previous.Mutation.After, OpenRgbLightingPlan.State(before.Controller)))
                    throw new OpenRgbLightingOwnershipException("OpenRGBの直前の報告値が一致しません");
            }
            var mutation = plan(before.Controller, previous);
            prepared = journal.Prepare(boot, scope, before.Controller, mutation.Mutation, mutation.Exit, previous, replaceWholeExit);
            var reported = await SendAsync(prepared, exitIntent, authorize, progress, ct).ConfigureAwait(false);
            journal.CheckCurrent(prepared); Check(authorize, ct);
            ownedHash = prepared.Hash;
            return new(new(Outcome.WrittenUnverified, "OpenRGBの報告設定と一致しました", ReportedLighting: reportedProgram), reported);
        }
        catch (Exception e)
        {
            if (progress.Attempted || transport?.HasPendingWrite == true)
            {
                faulted = true; ownedHash = prepared?.Hash;
                return new(new(Outcome.Unknown, "OpenRGBの変更を完了できません: " + e.Message));
            }
            try
            {
                if (prepared is not null) journal.CancelUnsent(prepared, previous);
                if (e is OpenRgbLightingOwnershipException || !connection.IsConnected) faulted = true;
                return new(new(Outcome.Rejected, "OpenRGBには送信していません: " + e.Message,
                    Failure: e is OpenRgbLightingExpiredException ? DeviceDriverFailure.LightingFrameExpired : DeviceDriverFailure.None));
            }
            catch (Exception cleanup)
            {
                faulted = true;
                return new(new(Outcome.Unknown, "OpenRGBの未送信記録を戻せません: " + cleanup.Message));
            }
        }
        finally { if (entered) operations.Release(); }
    }
    public async Task<OpenRgbLightingExecution> RecoverAsync(Action authorize, CancellationToken ct = default, LightingProgram? normalStop = null)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        var entered = false;
        try
        {
            await operations.WaitAsync(ct).ConfigureAwait(false); entered = true;
            var pending = journal.Pending;
            if (pending is null) { ownedHash = null; faulted = false; return new(new(Outcome.Confirmed, "OpenRGBの未終了操作はありません")); }
            journal.CheckCurrent(pending);
            if (pending.Phase == OpenRgbRecoveryPhase.Accepted)
            {
                // The persisted acceptance already completed the SDK obligation. File cleanup must not
                // overwrite a later external change or require a now-disconnected lighting device.
                journal.Complete(pending); ownedHash = null; faulted = false;
                return new(new(Outcome.WrittenUnverified, "OpenRGBの終了記録を整理しました"));
            }
            Check(authorize, ct);
            if (OpenRgbLightingRecovery.Check(pending, boot, scope.Binding, scope.Target) is { } error) throw new IOException(error);
            var current = await ReadAsync(authorize, ct).ConfigureAwait(false);
            OpenRgbLightingState? normalTarget = null;
            if (normalStop is not null)
            {
                if (pending.Hash != ownedHash || pending.Phase != OpenRgbRecoveryPhase.Active || !DeviceLighting.FrameProgram(normalStop) || DeviceLighting.Animated(normalStop))
                    throw new IOException("OpenRGBの通常停止の対象が一致しません");
                normalTarget = OpenRgbLightingPlan.Create(current.Controller, scope.Zone, Contract.Copy(normalStop)).After;
            }
            var recovery = journal.PrepareRecovery(pending, boot, scope.Binding, scope.Target, current.Controller, normalTarget);
            OpenRgbLightingExitIntent? ending = normalStop is null ? null : new(scope.Zone,
                normalStop.EffectId == "off" ? new(LightingExitAction.Off) : new(LightingExitAction.FixedColor, DeviceLighting.StopColor(normalStop)));
            var progress = new Progress(); var reported = await SendAsync(recovery, ending, authorize, progress, ct).ConfigureAwait(false);
            Check(authorize, ct);
            var accepted = journal.AcceptRecovery(recovery, OpenRgbLightingPlan.State(reported.Controller), progress.Acknowledged);
            journal.Complete(accepted); ownedHash = null; faulted = false;
            return new(new(Outcome.WrittenUnverified, "OpenRGBの終了設定と報告値が一致しました", ReportedLighting: normalStop), reported);
        }
        catch (Exception e)
        {
            if (entered) faulted = true;
            return new(new(entered ? Outcome.Unknown : Outcome.Rejected, "OpenRGBの終了処理を完了できません: " + e.Message));
        }
        finally { if (entered) operations.Release(); }
    }
}
