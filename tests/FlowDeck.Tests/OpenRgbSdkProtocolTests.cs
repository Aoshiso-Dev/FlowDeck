using System.Buffers.Binary;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using static FlowDeck.Tests.OpenRgbSdkTestServer;

namespace FlowDeck.Tests;

public sealed class OpenRgbSdkProtocolTests
{
    [Fact]
    public void VersionSixDescriptionKeepsCapabilitiesZonesSegmentsSparseNamesAndRgbOrder()
    {
        var device = OpenRgbSdkProtocol.Controller(700, Description());
        Assert.Equal(700u, device.Id); Assert.Equal("SERIAL", device.Serial); Assert.Equal("{}", device.Configuration);
        Assert.Equal(new RgbColor(17, 34, 51), device.Colors[0]); Assert.Equal(2115u, device.Modes[1].Flags);
        Assert.Equal("Native", device.Modes[1].Name); Assert.Equal(80u, device.Modes[1].Brightness);
        var zone = Assert.Single(device.Zones); Assert.Equal(-1, zone.ActiveMode); Assert.Equal("Zone label", zone.DisplayName);
        Assert.Equal(new uint[] { 0, 1 }, zone.Matrix!.Cells); Assert.Equal(1u, Assert.Single(zone.Segments).Start);
        Assert.Equal("First LED", Assert.Single(device.LedDisplayNames));
    }
    [Fact]
    public void EveryTruncatedDescriptionAndUnknownSuffixIsRejected()
    {
        var data = Description();
        for (var count = 4; count < data.Length; count++)
        {
            var truncated = data[..count]; BinaryPrimitives.WriteInt32LittleEndian(truncated, count);
            Assert.ThrowsAny<IOException>(() => OpenRgbSdkProtocol.Controller(41, truncated));
        }
        var extra = data.Concat(new byte[] { 0 }).ToArray(); BinaryPrimitives.WriteInt32LittleEndian(extra, extra.Length);
        Assert.ThrowsAny<IOException>(() => OpenRgbSdkProtocol.Controller(41, extra));
    }
    [Theory]
    [InlineData(2u)] [InlineData(4294967294u)]
    public void MatrixCannotReferenceOutsideZone(uint index) => Assert.ThrowsAny<IOException>(() => OpenRgbSdkProtocol.Controller(41, Description(matrixIndex: index)));
    [Fact]
    public void MatrixAllowsHole() => Assert.Equal(uint.MaxValue, OpenRgbSdkProtocol.Controller(41, Description(matrixIndex: uint.MaxValue)).Zones[0].Matrix!.Cells[0]);
    [Fact]
    public void InventoryRequiresUniqueExplicitIdsAndBoundedExactLength()
    {
        Assert.Equal(new uint[] { 41, 700 }, OpenRgbSdkProtocol.ControllerIds(Numbers(2, 41, 700)));
        Assert.ThrowsAny<IOException>(() => OpenRgbSdkProtocol.ControllerIds(Numbers(2, 41, 41)));
        Assert.ThrowsAny<IOException>(() => OpenRgbSdkProtocol.ControllerIds(Numbers(1)));
        Assert.ThrowsAny<IOException>(() => OpenRgbSdkProtocol.ControllerIds(Numbers(0, 1)));
        Assert.ThrowsAny<IOException>(() => OpenRgbSdkProtocol.ControllerIds(Numbers(uint.MaxValue)));
    }
    [Theory]
    [InlineData(1048577u)] [InlineData(4294967295u)]
    public void HeaderRejectsOversizedPayloadBeforeAllocation(uint size)
    {
        var header = OpenRgbSdkProtocol.Packet(41, 1, []); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), size);
        Assert.ThrowsAny<IOException>(() => OpenRgbSdkProtocol.Header(header));
    }
    [Theory]
    [InlineData(new byte[] { 65 })] [InlineData(new byte[] { 65, 0, 0 })] [InlineData(new byte[] { 255, 0 })]
    public void StringsRequireOneTerminatorAndValidUtf8(byte[] bytes) => Assert.ThrowsAny<IOException>(() => OpenRgbSdkProtocol.NullText(bytes));
    [Fact]
    public void ColorsAndModePayloadHaveProtocolSixLayout()
    {
        Assert.Equal(new byte[] { 14, 0, 0, 0, 3, 0, 0, 0, 1, 0, 17, 34, 51, 0 }, OpenRgbSdkProtocol.UpdateColors([new(17, 34, 51)], 3));
        var mode = OpenRgbSdkProtocol.Controller(41, Description()).Modes[1];
        var expected = Sized(w => { w.Write(0); w.Write(1); Mode(w, false); });
        Assert.Equal(expected, OpenRgbSdkProtocol.UpdateMode(1, mode, 0));
        Assert.ThrowsAny<IOException>(() => OpenRgbSdkProtocol.UpdateColors([new(256, 0, 0)]));
    }
    [Fact]
    public void StructureExcludesObservationsButIncludesPhysicalLayoutAndCapabilities()
    {
        var device = OpenRgbSdkProtocol.Controller(41, Description()); var basis = OpenRgbSdkProtocol.Structure(device);
        Assert.Equal(basis, OpenRgbSdkProtocol.Structure(device with { ActiveMode = 1, Colors = [new(0, 0, 0), new(0, 0, 0)], DisplayName = "changed",
            Modes = [device.Modes[0], device.Modes[1] with { Speed = 42, Colors = [new(1, 2, 3)] }] }));
        Assert.NotEqual(basis, OpenRgbSdkProtocol.Structure(device with { Configuration = "{\"size\":3}" }));
        Assert.NotEqual(basis, OpenRgbSdkProtocol.Structure(OpenRgbSdkProtocol.Controller(41, Description(count: 3))));
        Assert.NotEqual(basis, OpenRgbSdkProtocol.Structure(device with { Modes = [device.Modes[0] with { Flags = 0 }] }));
    }
    [Fact]
    public void SignalUsesColorDescriptionOrControllerWithoutNestedSize()
    {
        var colors = OpenRgbSdkProtocol.Signal(41, SignalColors(0x030201, 0x060504)); Assert.Equal(new RgbColor(1, 2, 3), colors.Colors![0]);
        var mode = OpenRgbSdkProtocol.Signal(41, SignalController(1, Description())); Assert.Equal("Device", mode.Controller!.Name);
        Assert.ThrowsAny<IOException>(() => OpenRgbSdkProtocol.Signal(41, SignalController(99, Description())));
        Assert.ThrowsAny<IOException>(() => OpenRgbSdkProtocol.Signal(41, SignalColors(0xff000000)));
    }
    [Fact]
    public void DetectionProgressHasSizeAndLengthPrefixedText()
    {
        var progress = Sized(w => { w.Write(50u); Text(w, "Detecting"); }); OpenRgbSdkProtocol.DetectionProgress(progress);
        BinaryPrimitives.WriteUInt32LittleEndian(progress.AsSpan(4), 101); Assert.ThrowsAny<IOException>(() => OpenRgbSdkProtocol.DetectionProgress(progress));
    }
}
