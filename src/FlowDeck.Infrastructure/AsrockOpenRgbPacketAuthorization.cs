namespace FlowDeck.Infrastructure;

// Authorization of one durable SDK mutation, not startup/detection permission. External checks
// never run under gate: RequestAsync calls Start under the SDK lock, and the pipe is another task.
internal sealed class AsrockOpenRgbPacketAuthorization(AsrockOpenRgbDetectionRecovery recovery, AsrockOpenRgbRecoveryIdentity scope)
{
    private readonly AsrockOpenRgbDetectionRecovery journal = recovery;
    private readonly AsrockOpenRgbRecoveryIdentity identity = scope;
    private readonly object gate = new();
    private Lease? active;
    private bool faulted;
    private ulong lastSequence;
    private static IOException Refused() => new("ASRockの通信と保存済み操作の認可条件が一致しません");

    internal Lease Arm(OpenRgbSdkConnection connection, OpenRgbSdkTarget target, string hash, Action authorize, CancellationToken ct)
    {
        void Guard() { ct.ThrowIfCancellationRequested(); authorize(); ct.ThrowIfCancellationRequested(); }
        Guard();
        var record = journal.ReadPrepared(identity, hash, static () => { });
        Guard();
        var operation = record.Operation!;
        if (!operation.Mutation.Valid() || operation.Before.Sequence == ulong.MaxValue ||
            operation.Before.After.Attempted >= ulong.MaxValue - (ulong)operation.Mutation.Packets.Length) throw Refused();
        var origin = connection.AuthorizationOrigin(target, operation.Mutation.Command);
        var lease = new Lease(this, connection, target, record, origin, Guard);
        lock (gate)
        {
            if (faulted || active is not null) throw Refused();
            active = lease;
        }
        return lease;
    }

    internal void Authorize(AsrockAuthorizationRequest request, CancellationToken ct)
    {
        Lease? lease = null;
        try
        {
            // Freeze buffers before crossing external ownership/continuity callbacks.
            request = request with { Io = request.Io.Copy() };
            lock (gate)
            {
                lease = active;
                if (faulted || lease is null || !lease.Started || lease.Finished || lease.Busy ||
                    lease.Index >= lease.Operation.Mutation.Packets.Length) throw Refused();
                lease.Busy = true;
            }
            ct.ThrowIfCancellationRequested();
            lease.Check(true);
            lock (gate)
            {
                ct.ThrowIfCancellationRequested();
                if (faulted || active != lease || lease.Finished || request.Sequence <= lastSequence || request.Sequence == ulong.MaxValue ||
                    request.Path != lease.Record.Before.Identity || request.Origin != lease.Origin ||
                    lease.Index >= lease.Operation.Mutation.Packets.Length || request.Phase != lease.Phase) throw Refused();
                var io = request.Io;
                var completed = lease.Operation.Before.After.Attempted + (ulong)lease.Index;
                if (io.Faulted || io.Completed != completed || io.Attempted != completed + 1 || io.Response.Length != 64 ||
                    !io.Request.AsSpan().SequenceEqual(lease.Operation.Mutation.Packets[lease.Index])) throw Refused();
                if (lease.Phase == AsrockAuthorizationPhase.BeforeWrite)
                {
                    if (io.Stage != 1 || io.WriteResult != -2 || io.ReadResult != -2 || io.Response.Any(b => b != 0)) throw Refused();
                    lease.Phase = AsrockAuthorizationPhase.BeforeRead;
                }
                else
                {
                    if (io.Stage != 2 || io.WriteResult != 65 || (lease.Phase == AsrockAuthorizationPhase.BeforeRead
                        ? io.ReadResult != -2 || io.Response.Any(b => b != 0) : io.ReadResult != 64)) throw Refused();
                    if (lease.Phase == AsrockAuthorizationPhase.BeforeRead) lease.Phase = AsrockAuthorizationPhase.AfterRead;
                    else
                    {
                        lease.FinalIo = io with { Stage = 3, Completed = io.Attempted };
                        lease.Index++;
                        lease.Phase = AsrockAuthorizationPhase.BeforeWrite;
                    }
                }
                lastSequence = request.Sequence;
                lease.Busy = false;
            }
        }
        catch
        {
            lock (gate) { faulted = true; if (lease is not null) lease.Busy = false; }
            throw;
        }
    }

    internal sealed class Lease : IDisposable
    {
        private readonly AsrockOpenRgbPacketAuthorization owner;
        private readonly OpenRgbSdkConnection connection;
        private readonly OpenRgbSdkTarget target;
        private readonly Action guard;
        internal readonly AsrockOpenRgbRecoveryRecord Record;
        internal AsrockOpenRgbPendingOperation Operation => Record.Operation!;
        internal readonly AsrockAuthorizationOrigin Origin;
        internal bool Started, Finished, Busy;
        internal int Index;
        internal AsrockAuthorizationPhase Phase = AsrockAuthorizationPhase.BeforeWrite;
        internal AsrockOpenRgbIoReceipt? FinalIo;
        internal Lease(AsrockOpenRgbPacketAuthorization owner, OpenRgbSdkConnection connection, OpenRgbSdkTarget target,
            AsrockOpenRgbRecoveryRecord record, AsrockAuthorizationOrigin origin, Action guard)
        { this.owner = owner; this.connection = connection; this.target = target; Record = record; Origin = origin; this.guard = guard; }
        internal void Check(bool inFlight)
        {
            // Provider guards may inspect SDK state. Never invoke them under the journal lock:
            // the SDK request thread holds its connection lock before checking this journal.
            guard();
            owner.journal.CheckPrepared(owner.identity, Record.Hash, static () => { });
            if (connection.AuthorizationOrigin(target, Operation.Mutation.Command, inFlight) != Origin) throw Refused();
            guard();
        }
        internal void Start()
        {
            Check(false);
            lock (owner.gate)
            {
                if (owner.faulted || owner.active != this || Started || Finished) throw Refused();
                Started = true;
            }
        }
        internal void Complete(AsrockOpenRgbExecution execution)
        {
            Check(false);
            lock (owner.gate)
            {
                if (owner.faulted || owner.active != this || !Started || Finished || Busy || Index != Operation.Mutation.Packets.Length ||
                    execution.Target != target || !execution.Before.Same(Operation.Before) || FinalIo is null || !FinalIo.Same(execution.After.After)) throw Refused();
                Operation.Mutation.CheckCompleted(execution);
                Finished = true;
            }
        }
        public void Dispose()
        {
            lock (owner.gate)
            {
                if (owner.active != this) return;
                if (Started && !Finished || Busy) owner.faulted = true;
                owner.active = null;
            }
        }
    }
}
