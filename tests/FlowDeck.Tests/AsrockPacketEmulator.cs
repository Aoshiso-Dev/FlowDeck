using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

// Models native authorization boundaries while the SDK emulator awaits its reply. No HID opens.
internal sealed class AsrockPacketEmulator
{
    public ulong Sequence;
    public AsrockOpenRgbIoReceipt? Io;
    public AsrockOpenRgbOperationReceipt Execute(AsrockOpenRgbRecoveryRecord record, AsrockOpenRgbOperationReceipt before,
        OpenRgbSdkTestServer server, uint controller, Action<AsrockAuthorizationRequest, CancellationToken> authorize, Func<byte[], byte[]> exchange)
    {
        var mutation = record.Operation!.Mutation;
        var origin = new AsrockAuthorizationOrigin(true, 6, mutation.Command, controller, checked((ushort)server.Port), server.PeerPort);
        Io = before.After.Copy();
        void Check(AsrockAuthorizationPhase phase, AsrockOpenRgbIoReceipt io) =>
            authorize(new(++Sequence, phase, record.Before.Identity, origin, io.Copy()), default);
        foreach (var packet in mutation.Packets)
        {
            var candidate = new AsrockOpenRgbIoReceipt(Io.Attempted + 1, Io.Completed, 1, false, -2, -2, packet.ToArray(), new byte[64]);
            Check(AsrockAuthorizationPhase.BeforeWrite, candidate); Io = candidate;
            var response = exchange(packet)[1..];
            Io = Io with { Stage = 2, WriteResult = 65 };
            Check(AsrockAuthorizationPhase.BeforeRead, Io);
            Io = Io with { ReadResult = 64, Response = response };
            Check(AsrockAuthorizationPhase.AfterRead, Io);
            Io = Io with { Completed = Io.Attempted, Stage = 3 };
        }
        return new(before.Sequence + 1, mutation.Command, AsrockOpenRgbOperationStage.Completed, before.After.Copy(), Io.Copy());
    }
}
