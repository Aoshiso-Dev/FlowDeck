using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

// Startup fixture follows the pinned C++ header-query/order/zone-query sequence. It emits
// real authorization requests but uses only the caller's fake device exchange function.
internal sealed class AsrockDetectionEmulator
{
    public ulong Sequence;
    public AsrockOpenRgbIoReceipt Io = new(0, 0, 0, false, -2, -2, new byte[65], new byte[64]);
    public List<AsrockAuthorizationRequest> Requests = [];
    public AsrockOpenRgbOperationReceipt Execute(string path, byte available, byte[] counts, byte order,
        Action<AsrockAuthorizationRequest, CancellationToken> authorize, Func<byte[], byte[]> exchange)
    {
        void Check(AsrockAuthorizationPhase phase, AsrockOpenRgbIoReceipt state)
        {
            var request = new AsrockAuthorizationRequest(++Sequence, phase, path, new(false, 0, 0, 0, 0, 0), state.Copy());
            Requests.Add(request); authorize(request, default);
        }
        Check(AsrockAuthorizationPhase.BeforeOpen, Io);
        var packets = new List<byte[]>();
        byte[] Packet(byte command, byte selector, byte value = 0)
        { var packet = new byte[65]; packet[1] = command; packet[3] = selector; packet[4] = value; return packet; }
        packets.Add(Packet(0x14, 2)); packets.Add(Packet(0x14, 3)); packets.Add(Packet(0x14, 1));
        var mask = Enumerable.Range(0, 8).Where(i => (available & (1 << i)) != 0 && counts[i] != 0x1e).Sum(i => 1 << i);
        packets.Add(Packet(0x15, 3, (byte)(order & mask)));
        packets.AddRange(Enumerable.Range(0, 8).Where(i => (available & (1 << i)) != 0 && counts[i] != 0x1e).Select(i => Packet(0x11, (byte)i)));
        foreach (var packet in packets)
        {
            var candidate = new AsrockOpenRgbIoReceipt(Io.Attempted + 1, Io.Completed, 1, false, -2, -2, packet, new byte[64]);
            Check(AsrockAuthorizationPhase.BeforeWrite, candidate); Io = candidate;
            var response = exchange(packet)[1..]; Io = Io with { Stage = 2, WriteResult = 65 };
            Check(AsrockAuthorizationPhase.BeforeRead, Io);
            Io = Io with { ReadResult = 64, Response = response }; Check(AsrockAuthorizationPhase.AfterRead, Io);
            Io = Io with { Stage = 3, Completed = Io.Attempted };
        }
        return new(0, 0, AsrockOpenRgbOperationStage.Idle, Io.Copy(), Io.Copy());
    }
}
