using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// One controller-wide operation owner. Called under the product resource/physical-identity guard;
// it does not grant that ownership or enable the still-gated packaged hardware route by itself.
internal sealed class AsrockOpenRgbNativeExecutor(OpenRgbSdkConnection connection, OpenRgbSdkTarget target,
    AsrockOpenRgbNativePlan plan, AsrockOpenRgbDetectionRecovery journal, AsrockOpenRgbRecoveryIdentity identity) : IOpenRgbLightingTransport
{
    private readonly SemaphoreSlim operations = new(1, 1);
    private volatile bool faulted;
    internal AsrockOpenRgbPacketAuthorization Authorization { get; } = new(journal, identity);
    public bool NeedsRecovery => faulted || HasPendingWrite;
    public bool HasPendingWrite => journal.Pending?.Operation is not null;
    public async Task TrackAsync(OpenRgbRecoveryRecord record, Action authorize, CancellationToken ct)
    {
        var link = OpenRgbSdkRecoveryLink.From(record);
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (link.Scope.Target != target || link.Boot != identity.Boot) throw new IOException("ASRockのSDK復旧記録の接続・起動が一致しません");
            journal.TrackSdk(identity, link, () => { ct.ThrowIfCancellationRequested(); authorize(); });
        }
        finally { operations.Release(); }
    }
    internal Task<AsrockOpenRgbExecution> SendAsync(OpenRgbSdkController controller, OpenRgbLightingStep step,
        IReadOnlyDictionary<byte, BoardLightSetting> desired, Action authorize, CancellationToken ct = default)
    {
        var ending = desired.ToDictionary(p => p.Key, p => Contract.Copy(p.Value));
        return SendCoreAsync(controller, step, _ => ending, authorize, null, ct);
    }
    async Task IOpenRgbLightingTransport.SendAsync(OpenRgbSdkController controller, OpenRgbLightingStep step, OpenRgbLightingExitIntent? exit,
        Action authorize, Action beforeWrite, CancellationToken ct)
    {
        var frozen = exit is null ? null : Contract.Copy(exit);
        _ = await SendCoreAsync(controller, step, pending => plan.MergeExit(pending.Desired, frozen), authorize, beforeWrite, ct).ConfigureAwait(false);
    }
    private async Task<AsrockOpenRgbExecution> SendCoreAsync(OpenRgbSdkController controller, OpenRgbLightingStep step,
        Func<AsrockOpenRgbRecoveryRecord, Dictionary<byte, BoardLightSetting>> chooseExit, Action authorize, Action? beforeWrite, CancellationToken ct)
    {
        var frozenController = OpenRgbSdkProtocol.Copy(controller); var frozenStep = Contract.Copy(step);
        await operations.WaitAsync(ct).ConfigureAwait(false);
        AsrockOpenRgbRecoveryRecord? previous = null, prepared = null; var writing = false;
        try
        {
            if (faulted || frozenController.Id != target.ControllerId) throw new IOException("ASRock操作の復旧または対象の再確認が必要です");
            void Guard() { ct.ThrowIfCancellationRequested(); authorize(); ct.ThrowIfCancellationRequested(); }
            Guard();
            var reported = await connection.ReadAsync(target, ct, Guard).ConfigureAwait(false);
            if (OpenRgbSdkProtocol.Structure(reported.Controller) != OpenRgbSdkProtocol.Structure(frozenController) ||
                !OpenRgbLightingPlan.Same(OpenRgbLightingPlan.State(reported.Controller), OpenRgbLightingPlan.State(frozenController)))
                throw new IOException("ASRock操作前のSDK報告値が変わりました");
            var pending = previous = journal.Pending ?? throw new IOException("ASRockの検出前復旧記録がありません");
            var ending = chooseExit(pending);
            var mutation = plan.Prepare(pending.Expected, frozenController, frozenStep);
            var before = await connection.ReadAsrockOperationAsync(target, Guard, ct).ConfigureAwait(false);
            prepared = journal.PrepareOperation(identity, mutation, before, ending, Guard);
            using var permit = Authorization.Arm(connection, target, prepared.Hash, Guard, ct);
            void BeforeSend() => journal.CheckPrepared(identity, prepared.Hash, Guard);
            void Writing() { beforeWrite?.Invoke(); permit.Start(); writing = true; }
            AsrockOpenRgbExecution execution;
            if (frozenStep.ModeIndex is { } index && frozenStep.Mode is { } mode)
                execution = await connection.WriteAsrockModeAsync(target, before, index, mode, BeforeSend, ct, Writing).ConfigureAwait(false);
            else if (frozenStep.Colors is { } colors)
                execution = await connection.WriteAsrockColorsAsync(target, before, colors, BeforeSend, frozenStep.Zone, ct, Writing).ConfigureAwait(false);
            else throw new IOException("ASRockの送信手順がありません");
            permit.Complete(execution);
            journal.CompleteOperation(identity, prepared.Hash, execution, Guard);
            return execution;
        }
        catch (Exception error)
        {
            faulted = writing || !connection.IsConnected || !(error is OpenRgbLightingExpiredException || error is OperationCanceledException && ct.IsCancellationRequested);
            if (!writing && prepared is not null && previous is not null)
            {
                try { journal.CancelUnsentOperation(identity, prepared.Hash, previous); }
                catch (Exception cleanup) { faulted = true; throw new IOException("ASRockの未送信記録を戻せません", new AggregateException(error, cleanup)); }
            }
            throw;
        }
        finally { operations.Release(); }
    }
}
