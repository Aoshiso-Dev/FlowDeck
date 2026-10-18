using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed partial class EneMemoryDeviceDriver
{
    private sealed class FrameExpiredException(string message) : IOException(message);
    public string? ValidateLightingStream(string[] endpoints, LightingProgram program)
    {
        lock (gate)
        {
            if (endpoints is not [Endpoint] || !DeviceLighting.ValidAnimation(program)) return "メモリ照明の同期設定が不正です";
            return Ready();
        }
    }
    public async Task<IDeviceLightingStream> OpenLightingStreamAsync(string[] endpoints, LightingProgram program, CancellationToken ct)
    {
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (gate) if (active is not null) throw new IOException("メモリ照明の同期が実行中です");
            await ReadCoreAsync(true, ct).ConfigureAwait(false);
            lock (gate)
            {
                if (ValidateLightingStream(endpoints, program) is { } error) throw new IOException(error);
                active = new(this, connection!, Candidate()!, Contract.Copy(program), Contract.Copy(CurrentExit), journal!.Pending, Contract.Copy(observed!));
                return active;
            }
        }
        finally { io.Release(); }
    }
    private sealed class Stream(EneMemoryDeviceDriver driver, EneMemoryConnection connection, PhysicalDeviceBinding binding,
        LightingProgram program, LightingExitPolicy exit, EneMemoryRecoveryRecord? previous, EneMemoryState baseline) : IPreparedDeviceLightingStream
    {
        private sealed class PreparedFrame(DeviceLightingFrame frame, EneMemoryRecoveryRecord record,
            EneMemoryRecoveryRecord? previous, EneMemoryRecovery.ReadLease lease)
        {
            public readonly DeviceLightingFrame Frame = frame;
            public readonly EneMemoryRecoveryRecord Record = record;
            public readonly EneMemoryRecoveryRecord? Previous = previous;
            public readonly EneMemoryRecovery.ReadLease Lease = lease;
            public bool Consumed, Attempted, Finalized;
            public DeviceDriverReleaseResult[]? Result;
        }
        private readonly EneMemoryRecoveryScope scope = new(binding, connection, Guid.NewGuid().ToString("N"));
        private EneMemoryRecoveryRecord? expected = previous;
        private PreparedFrame? pending;
        private long lastSequence = -1;
        private bool started;
        private RgbColor? normalStopColor;
        private DeviceDriverReleaseResult[]? ended;
        private void Authorize(DeviceLightingFrame frame)
        {
            driver.CheckConnectionPermission();
            var a = frame.Authorize();
            if (a.Expired || a.ValidFor is { } remaining && remaining <= TimeSpan.Zero) throw new FrameExpiredException(a.Error ?? "照明フレームの有効期限を超えました");
            if (a.Error is not null) throw new IOException(a.Error);
            if (a.ValidFor is null) throw new IOException("照明フレームの有効期限がありません");
        }
        public async Task<DeviceDriverReleaseResult[]> PrepareFrameAsync(DeviceLightingFrame frame,
            Func<CancellationToken, Task<DeviceDriverReleaseResult[]>> transmit, CancellationToken ct)
        {
            await driver.io.WaitAsync(ct).ConfigureAwait(false);
            EneMemoryRecoveryRecord? record = null; EneMemoryRecovery.ReadLease? lease = null;
            var beforeRecord = expected; PreparedFrame prepared;
            try
            {
                if (frame.TargetError([Endpoint]) is { } targetError) return Result(new(Outcome.Rejected, targetError));
                lock (driver.gate) if (ended is not null || driver.active != this) return Result(new(Outcome.Rejected, "メモリ照明の同期は終了しました"));
                if (pending is not null) return Result(new(Outcome.Rejected, "前のメモリ照明フレームが完了していません"));
                if (frame.Sequence <= lastSequence || frame.ColorFor(Endpoint)?.Valid != true) return Result(new(Outcome.Rejected, "古いまたは不正なメモリ照明フレームです"));
                lastSequence = frame.Sequence;
                driver.Guard(connection, binding, ct, beforeRecord, () => Authorize(frame));
                var actual = beforeRecord?.Mutation.After ?? baseline;
                record = driver.journal!.Prepare(driver.boot, scope, EneMemoryProtocol.Frame(actual, frame.ColorFor(Endpoint)),
                    beforeRecord?.Exit ?? Exit(actual, exit), beforeRecord);
                lock (driver.gate) driver.owned = true;
                lease = driver.journal.AcquireReadLease(record);
                driver.Guard(connection, binding, ct, extra: () => { lease.Check(); Authorize(frame); });
                prepared = new(frame, record, beforeRecord, lease); pending = prepared;
            }
            catch (Exception e)
            {
                lease?.Dispose(); return Result(driver.FailedMutation(e, false, record, beforeRecord));
            }
            finally { driver.io.Release(); }
            DeviceDriverReleaseResult[] result;
            try
            {
                result = await transmit(ct).ConfigureAwait(false);
                if (result is not { Length: 1 } || result[0]?.EndpointId != Endpoint || result[0].Result is null)
                    result = Result(new(Outcome.Unknown, "照明フレームの端子別結果がありません"));
            }
            catch (Exception e)
            {
                result = Result(new(prepared.Attempted ? Outcome.Unknown : Outcome.Rejected, "照明フレームは完了していません: " + e.Message,
                    Failure: e is FrameExpiredException && !prepared.Attempted ? DeviceDriverFailure.LightingFrameExpired : DeviceDriverFailure.None));
            }
            await driver.io.WaitAsync().ConfigureAwait(false);
            try { return FinishPreparation(prepared, result); }
            finally { driver.io.Release(); }
        }
        // Called only after the scheduled callback has returned, or by End after taking io. No SMBus session is open here.
        private DeviceDriverReleaseResult[] FinishPreparation(PreparedFrame prepared, DeviceDriverReleaseResult[] result)
        {
            if (prepared.Finalized) return Contract.Copy(prepared.Result!);
            prepared.Lease.Dispose();
            if (!prepared.Attempted)
            {
                var cleanup = driver.FailedMutation(new IOException("照明フレームは未送信です"), false, prepared.Record, prepared.Previous);
                if (cleanup.Outcome == Outcome.Unknown) { result = Result(cleanup); expected = prepared.Record; }
                else if (result.Any(r => r.Result.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified)) result = Result(cleanup);
            }
            else
            {
                expected = prepared.Record;
                if (result.Any(r => r.Result.Outcome is not (Outcome.Confirmed or Outcome.WrittenUnverified))) lock (driver.gate) driver.faulted = true;
                else normalStopColor = prepared.Frame.NormalStopColorFor(Endpoint) ?? program.Colors[0].Scale(program.Brightness / 100d);
            }
            prepared.Result = Contract.Copy(result); prepared.Finalized = true;
            if (pending == prepared) pending = null;
            return result;
        }
        public async Task<DeviceDriverReleaseResult[]> WriteAsync(DeviceLightingFrame frame, CancellationToken ct)
        {
            // Direct callers retain the same contract; the common runtime invokes PrepareFrameAsync before reserving hardware.
            if (pending is null) return await PrepareFrameAsync(frame, t => WriteAsync(frame, t), ct).ConfigureAwait(false);
            await driver.io.WaitAsync(ct).ConfigureAwait(false);
            var prepared = pending;
            try
            {
                if (prepared is null || prepared.Finalized || prepared.Consumed || !ReferenceEquals(prepared.Frame, frame))
                    return Result(new(Outcome.Rejected, "準備済みのメモリ照明フレームが一致しません"));
                lock (driver.gate) if (ended is not null || driver.active != this) return Result(new(Outcome.Rejected, "メモリ照明の同期は終了しました"));
                prepared.Consumed = true;
                void Check() => driver.Guard(connection, binding, ct, extra: () => { prepared.Lease.Check(); Authorize(frame); });
                Check();
                var result = await driver.backend.ExecuteAsync(connection, false, session =>
                {
                    Check();
                    EneMemoryProtocol.ExecutePrepared(session, prepared.Record.Mutation, Check, () => { prepared.Attempted = true; started = true; });
                    Check(); driver.Cache(connection, prepared.Record.Mutation.After);
                    return new DeviceDriverResult(Outcome.WrittenUnverified, "メモリ照明フレームを送信しました", ReportedLighting: new("fixed", [frame.ColorFor(Endpoint)]));
                }, ct).ConfigureAwait(false);
                return prepared.Result = Result(result);
            }
            catch (Exception e)
            {
                var attempted = prepared?.Attempted == true;
                var result = Result(new(attempted ? Outcome.Unknown : Outcome.Rejected, "メモリ照明フレームを完了できません: " + e.Message,
                    Failure: e is FrameExpiredException && !attempted ? DeviceDriverFailure.LightingFrameExpired : DeviceDriverFailure.None));
                if (prepared is not null) prepared.Result = result;
                return result;
            }
            finally { driver.io.Release(); }
        }
        public async Task<DeviceDriverReleaseResult[]> EndAsync(DeviceLightingStopReason reason, CancellationToken ct)
        {
            await driver.io.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ended is not null) return Contract.Copy(ended);
                if (pending is { } preparation)
                {
                    var finished = FinishPreparation(preparation, preparation.Result ?? Result(new(Outcome.Rejected, "送信前に同期を終了しました")));
                    if (!preparation.Attempted && finished.Any(r => r.Result.Outcome == Outcome.Unknown))
                    {
                        ended = finished; lock (driver.gate) if (driver.active == this) driver.active = null;
                        return Contract.Copy(ended);
                    }
                }
                // An unstarted stream may still own a native setting from before Open. Release that obligation too.
                var normal = normalStopColor is null ? program : new LightingProgram("fixed", [normalStopColor]);
                ended = Result(await driver.RecoverCoreAsync(started && reason == DeviceLightingStopReason.Normal ? normal : null, CancellationToken.None).ConfigureAwait(false));
                lock (driver.gate) if (driver.active == this) driver.active = null;
                return Contract.Copy(ended);
            }
            finally { driver.io.Release(); }
        }
    }
}
