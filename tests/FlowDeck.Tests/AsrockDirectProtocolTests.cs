using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class AsrockDirectProtocolTests
{
    [Fact] public void SparseZonesKeepPhysicalOrderAndExistingChannelOrder()
    {
        var packets = AsrockDirectProtocol.Frame([new(7, 1, new(4, 5, 6)), new(2, 1, new(1, 2, 3))], 1 << 2);
        var packet = Assert.Single(packets);
        Assert.Equal(new byte[] { 1, 2, 3, 5, 4, 6 }, packet[9..15]);
        Assert.All(packet[15..63], b => Assert.Equal(0, b));
        Assert.Equal(0xff, packet[3]); Assert.Equal(0xe3, packet[4]); Assert.Equal(0x65, packet[64]);
    }
    [Theory] [InlineData(1)] [InlineData(10)] [InlineData(11)] [InlineData(18)] [InlineData(19)] [InlineData(265)]
    public void CompleteFrameRetainsColorsAcrossChunkBoundariesAndPadsEightLeds(int count)
    {
        var zones = count > 255 ? new[] { new AsrockDirectProtocol.Zone(2, 250, new(11, 22, 33)), new AsrockDirectProtocol.Zone(7, count - 250, new(11, 22, 33)) }
            : [new AsrockDirectProtocol.Zone(2, count, new(11, 22, 33))];
        var packets = AsrockDirectProtocol.Frame(zones, 0xff);
        var body = packets.SelectMany((p, i) => i == 0 ? p[9..63] : p[5..62]).ToArray();
        Assert.Equal(Enumerable.Range(0, count * 3).Select(i => (byte)((i % 3 + 1) * 11)), body.Take(count * 3));
        Assert.All(body.Skip(count * 3), b => Assert.Equal(0, b));
        Assert.True(body.Length >= (count + 8) * 3);
        Assert.All(packets, p => { Assert.Equal(65, p.Length); Assert.Equal(0, p[0]); Assert.Equal(0x10, p[1]); Assert.Equal(0x65, p[64]); });
        Assert.All(packets.Skip(1), p => { Assert.Equal(0xe4, p[4]); Assert.Equal(0, p[63]); });
    }
    [Fact] public void InvalidInputsCannotProducePackets()
    {
        Assert.Throws<ArgumentException>(() => AsrockDirectProtocol.Frame([], 0));
        Assert.Throws<ArgumentException>(() => AsrockDirectProtocol.Frame([new(8, 1, new(0, 0, 0))], 0));
        Assert.Throws<ArgumentException>(() => AsrockDirectProtocol.Frame([new(1, 0, new(0, 0, 0))], 0));
        Assert.Throws<ArgumentException>(() => AsrockDirectProtocol.Frame([new(1, 256, new(0, 0, 0))], 0));
        Assert.Throws<ArgumentException>(() => AsrockDirectProtocol.Frame([new(1, 1, new(-1, 0, 0))], 0));
        Assert.Throws<ArgumentException>(() => AsrockDirectProtocol.Frame([new(1, 1, new(0, 0, 0)), new(1, 1, new(0, 0, 0))], 0));
    }
    [Fact] public void OrderWriteOnlyTargetsTheVolatileOrderTable()
    {
        Assert.Equal(new byte[] { 0, 0x15, 0, 3, 0xfd }, AsrockDirectProtocol.ColorOrder(0xfd)[..5]);
        Assert.All(AsrockDirectProtocol.ColorOrder(0xfd)[5..], b => Assert.Equal(0, b));
        Assert.Equal(15, AsrockDirectProtocol.Enter(7)[4]);
        Assert.Throws<ArgumentOutOfRangeException>(() => AsrockDirectProtocol.Enter(8));
    }
}
