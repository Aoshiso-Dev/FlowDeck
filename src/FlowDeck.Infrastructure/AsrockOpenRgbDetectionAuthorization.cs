namespace FlowDeck.Infrastructure;

// Exact startup sequence in pinned PolychromeUSBController/SetupZones. There is no startup
// serial-number query in that constructor, so BeforeSerial is deliberately not granted here.
internal sealed class AsrockOpenRgbDetectionPlan
{
    internal sealed record Step(byte[] Request, byte[]? Expected);
    internal Step[] Steps { get; }
    internal AsrockOpenRgbDetectionPlan(AsrockOpenRgbRecoveryRecord record)
    {
        var before = record.Before;
        var steps = new List<Step>
        {
            new(AsrockRgbMonitor.Query(0x14, 2), before.Counts.ToArray()),
            new(AsrockRgbMonitor.Query(0x14, 3), [before.Order]),
            new(AsrockRgbMonitor.Query(0x14, 1), [before.Available]),
            new(AsrockDirectProtocol.ColorOrder(AsrockOpenRgbDetectionRecovery.DetectionOrder(before)), null)
        };
        foreach (var i in Enumerable.Range(0, 8).Where(i => (before.Available & (1 << i)) != 0 && before.Counts[i] != 0x1e))
        {
            byte[] expected;
            if (before.Counts[i] == 0)
            {
                if (record.EmptyZoneConfigurations is not { } empty || !empty.TryGetValue((byte)i, out var bytes) || bytes is not { Length: 5 })
                    throw new IOException("ASRockの空の端子について検出前の応答保存がありません");
                expected = bytes;
            }
            else
            {
                var zone = before.Zones.Single(z => z.Address == i);
                expected = AsrockOpenRgbNativeOperation.ZonePacket(zone.Address, zone.Setting, before.Order)[4..9];
            }
            steps.Add(new(AsrockRgbMonitor.Query(0x11, (byte)i), expected.ToArray()));
        }
        Steps = steps.ToArray();
    }
    internal void CheckResponse(int index, byte[] response)
    {
        if (response.Length != 64 || Steps[index].Expected is { } expected &&
            (response[3] != Steps[index].Request[3] || !response.AsSpan(4, expected.Length).SequenceEqual(expected)))
            throw new IOException("ASRockの検出応答が検出前に保存した状態と一致しません");
    }
    internal void CheckFinal(AsrockOpenRgbIoReceipt io)
    {
        if (io.Attempted != (ulong)Steps.Length || io.Completed != io.Attempted || io.Stage != 3 || io.Faulted ||
            io.WriteResult != 65 || io.ReadResult != 64 || !io.Request.AsSpan().SequenceEqual(Steps[^1].Request))
            throw new IOException("ASRockの検出完了と通信記録が一致しません");
        CheckResponse(Steps.Length - 1, io.Response);
    }
}

internal sealed class AsrockOpenRgbDetectionAuthorization
{
    private readonly object gate = new();
    private readonly AsrockOpenRgbDetectionRecovery journal;
    private readonly AsrockOpenRgbRecoveryIdentity identity;
    private readonly AsrockOpenRgbRecoveryRecord record;
    private readonly AsrockOpenRgbDetectionPlan plan;
    private readonly Action guard;
    private static readonly AsrockAuthorizationOrigin Startup = new(false, 0, 0, 0, 0, 0);
    private bool opened, complete, faulted, busy;
    private int index;
    private ulong lastSequence;
    private AsrockAuthorizationPhase phase = AsrockAuthorizationPhase.BeforeWrite;
    private static IOException Refused() => new("ASRockの検出通信と保存済みの認可条件が一致しません");
    internal AsrockOpenRgbDetectionAuthorization(AsrockOpenRgbDetectionRecovery journal, AsrockOpenRgbRecoveryIdentity identity, Action guard)
    {
        this.journal = journal; this.identity = identity; this.guard = guard;
        guard(); record = journal.ReadDetection(identity); plan = new(record); guard();
    }
    internal void RequireCompleted()
    { lock (gate) if (!complete || faulted || busy) throw Refused(); }
    internal void Authorize(AsrockAuthorizationRequest request, CancellationToken ct)
    {
        try
        {
            request = request with { Io = request.Io.Copy() };
            AsrockOpenRgbAuthorizationProtocol.Validate(request.Phase, request.Io);
            lock (gate)
            {
                if (faulted || complete || busy || request.Path != record.Before.Identity || request.Origin != Startup ||
                    request.Sequence <= lastSequence || request.Sequence == ulong.MaxValue ||
                    (opened ? request.Phase != phase : request.Phase != AsrockAuthorizationPhase.BeforeOpen)) throw Refused();
                busy = true;
            }
            // These callbacks can inspect SDK/process state. Never hold the policy or journal
            // lock while calling them; SDK before-send checks acquire those locks in the other order.
            ct.ThrowIfCancellationRequested(); guard();
            _ = journal.ReadDetection(identity, record.Hash);
            guard(); ct.ThrowIfCancellationRequested();
            AsrockOpenRgbIoReceipt? finished = null;
            lock (gate)
            {
                if (faulted) throw Refused();
                if (opened)
                {
                    var io = request.Io;
                    if (index >= plan.Steps.Length || io.Attempted != (ulong)index + 1 || io.Completed != (ulong)index ||
                        !io.Request.AsSpan().SequenceEqual(plan.Steps[index].Request)) throw Refused();
                    if (phase == AsrockAuthorizationPhase.AfterRead)
                    {
                        plan.CheckResponse(index, io.Response);
                        if (index + 1 == plan.Steps.Length) finished = io with { Stage = 3, Completed = io.Attempted };
                    }
                }
            }
            if (finished is not null)
            {
                guard(); ct.ThrowIfCancellationRequested();
                journal.CompleteDetection(identity, record.Hash, finished);
                guard(); ct.ThrowIfCancellationRequested();
            }
            lock (gate)
            {
                if (faulted) throw Refused();
                ct.ThrowIfCancellationRequested();
                if (!opened) opened = true;
                else if (phase == AsrockAuthorizationPhase.AfterRead) { index++; phase = AsrockAuthorizationPhase.BeforeWrite; }
                else phase = phase == AsrockAuthorizationPhase.BeforeWrite ? AsrockAuthorizationPhase.BeforeRead : AsrockAuthorizationPhase.AfterRead;
                complete = finished is not null; lastSequence = request.Sequence; busy = false;
            }
        }
        catch { lock (gate) { faulted = true; busy = false; } throw; }
    }
}
