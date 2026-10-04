using System.Buffers.Binary;
using System.Text;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class EneMemoryGuardedBusTests
{
    private sealed class Transfer
    {
        public readonly List<long[]> Calls = [];
        private ushort selected;
        public long[] Execute(string name, long[] args, int count)
        {
            Assert.Equal("ioctl_smbus_xfer", name); Assert.Equal(0x71, args[0]); Calls.Add(args);
            if (args[1] == 0 && args[2] == 0 && args[3] == 3) selected = BinaryPrimitives.ReverseEndianness((ushort)args[4]);
            if (count == 0) return [];
            Assert.Equal(1, count);
            if (args[2] is >= 0xa0 and <= 0xaf) return [args[2] - 0xa0];
            Assert.Equal(0x81, args[2]);
            if (selected is >= 0x1000 and <= 0x100f)
            {
                var firmware = Encoding.ASCII.GetBytes(EneMemoryDefinition.EightLed.Firmware.PadRight(16, '\0')); return [firmware[selected - 0x1000]];
            }
            if (selected is >= 0x1c00 and <= 0x1c3f) return [Convert.FromHexString(EneMemoryDefinition.EightLed.ConfigurationHex)[selected - 0x1c00]];
            return [0];
        }
    }
    [Theory] [InlineData("read")] [InlineData("byte")] [InlineData("pixel")]
    public void ActualBusMethodsCheckAuthorizationBetweenPointerSelectionAndDataTransfer(string operation)
    {
        var transfer = new Transfer(); using var bus = new EneMemoryRgbBus(transfer.Execute);
        Assert.True(bus.Identify(0x71)); transfer.Calls.Clear(); var guarded = (IEneMemoryGuardedIo)bus;
        void Guard()
        {
            var pointer = Assert.Single(transfer.Calls); Assert.Equal(0, pointer[1]); Assert.Equal(0, pointer[2]); Assert.Equal(3, pointer[3]);
            throw new OperationCanceledException("expired after pointer transfer");
        }
        Assert.Throws<OperationCanceledException>(() =>
        {
            if (operation == "read") _ = guarded.Read(0x71, 0x8020, Guard);
            else if (operation == "byte") guarded.Write(0x71, 0x8020, 1, Guard);
            else guarded.WritePixel(0x71, 0, 17, 34, 51, Guard);
        });
        Assert.Single(transfer.Calls);
    }
    [Fact]
    public void ActualPixelPathPreservesRbgBlockPayloadAfterAuthorization()
    {
        var transfer = new Transfer(); using var bus = new EneMemoryRgbBus(transfer.Execute); Assert.True(bus.Identify(0x71)); transfer.Calls.Clear();
        var authorized = false;
        ((IEneMemoryGuardedIo)bus).WritePixel(0x71, 7, 0x12, 0x34, 0x56, () => { Assert.Single(transfer.Calls); authorized = true; });
        Assert.True(authorized); Assert.Equal(2, transfer.Calls.Count);
        Assert.Equal(0x1581, transfer.Calls[0][4]);
        Assert.Equal(new long[] { 0x71, 0, 3, 5, 0x56341203, 0, 0, 0, 0 }, transfer.Calls[1]);
    }
}
