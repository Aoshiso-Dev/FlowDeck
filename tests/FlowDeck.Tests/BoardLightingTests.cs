using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class BoardLightingTests
{
    private static readonly BoardLightSetting Original = new(14, 0, 0, 255, 128);
    private static readonly BoardLightSetting Blue = new(1, 52, 137, 255, 224);
    private sealed class Device
    {
        public Dictionary<byte, BoardLightSetting> Values = new() { [2] = Original, [5] = Original, [6] = Original };
        public List<byte[]> Writes = [];
        public byte Order = 0xfd;
        public byte? FailZone, MismatchZone;
        public byte[] Exchange(byte[] packet)
        {
            var response = new byte[65]; var zone = packet[3]; response[4] = zone;
            if (packet[1] == 0x14)
            {
                if (zone == 1) response[5] = 0x64;
                if (zone == 2) { response[7] = 80; response[10] = 15; response[11] = 9; }
                if (zone == 3) response[5] = Order;
            }
            else if (packet[1] == 0x11)
            {
                var c = Values[zone]; var normal = (Order & (1 << zone)) != 0;
                response[5] = c.Mode; response[6] = (byte)(normal ? c.Red : c.Green);
                response[7] = (byte)(normal ? c.Green : c.Red); response[8] = (byte)c.Blue; response[9] = (byte)c.Speed;
            }
            else if (packet[1] == 0x10)
            {
                Writes.Add(packet);
                if (FailZone == zone) throw new TimeoutException("ack timeout");
                var normal = (Order & (1 << zone)) != 0;
                if (MismatchZone != zone) Values[zone] = new(packet[4], packet[normal ? 5 : 6], packet[normal ? 6 : 5], packet[7], packet[8]);
            }
            else throw new InvalidOperationException("Unapproved command");
            return response;
        }
    }

    [Theory] [InlineData(6)] [InlineData(15)] [InlineData(0xe3)] [InlineData(0xff)]
    public void UnsupportedStreamingAndMusicModesNeverWrite(byte mode)
    {
        var device = new Device();
        Assert.Throws<ArgumentException>(() => AsrockRgbControl.Apply(device.Exchange, [new(5, Original, Blue with { Mode = mode })]));
        Assert.Empty(device.Writes);
    }
    [Theory] [InlineData(0)] [InlineData(0xff)]
    public void IndividualZoneUsesExistingColorOrderAndNeverBroadcasts(byte order)
    {
        var device = new Device { Order = order };
        var result = AsrockRgbControl.Apply(device.Exchange, [new(5, Original, Blue)]);
        Assert.True(result.Verified); Assert.Equal(Blue, device.Values[5]); Assert.Equal(Original, device.Values[2]);
        var packet = Assert.Single(device.Writes); Assert.Equal(5, packet[3]); Assert.Equal(0, packet[16]);
        Assert.Equal(order == 0 ? Blue.Green : Blue.Red, packet[5]);
    }
    [Fact] public void SparseHardwareAddressesArePreservedForAllZones()
    {
        var device = new Device();
        Assert.True(AsrockRgbControl.Apply(device.Exchange, [new(2, Original, Blue), new(5, Original, Blue), new(6, Original, Blue)]).Verified);
        Assert.Equal(new byte[] { 2, 5, 6 }, device.Writes.Select(p => p[3]));
    }
    [Fact] public void StaleSelectionRejectsEveryWrite()
    {
        var device = new Device(); device.Values[6] = Blue;
        Assert.Throws<IOException>(() => AsrockRgbControl.Apply(device.Exchange, [new(5, Original, Blue), new(6, Original, Blue)]));
        Assert.Empty(device.Writes);
    }
    [Fact] public void UnavailableZoneRejectsEveryWrite()
    {
        var device = new Device();
        Assert.Throws<IOException>(() => AsrockRgbControl.Apply(device.Exchange, [new(0, Original, Blue)])); Assert.Empty(device.Writes);
    }
    [Fact] public void AckFailureIsUnknownAndStopsFollowingWrites()
    {
        var device = new Device { FailZone = 5 };
        var result = AsrockRgbControl.Apply(device.Exchange, [new(2, Original, Blue), new(5, Original, Blue), new(6, Original, Blue)]);
        Assert.False(result.Verified); Assert.Equal(new[] { "verified", "unknown", "notWritten" }, result.Zones.Select(z => z.Status));
        Assert.Equal(Original, device.Values[6]); Assert.Equal(2, device.Writes.Count);
    }
    [Fact] public void ReadbackMismatchIsNeverReportedSuccessful()
    {
        var device = new Device { MismatchZone = 5 };
        var result = AsrockRgbControl.Apply(device.Exchange, [new(5, Original, Blue), new(6, Original, Blue)]);
        Assert.Equal(new[] { "mismatch", "notWritten" }, result.Zones.Select(z => z.Status)); Assert.Single(device.Writes);
    }
    [Fact] public void InvalidValueOrDuplicateTargetsNeverWrite()
    {
        var device = new Device();
        Assert.Throws<ArgumentException>(() => AsrockRgbControl.Apply(device.Exchange, [new(5, Original, Blue with { Red = -1 })]));
        Assert.Throws<ArgumentException>(() => AsrockRgbControl.Apply(device.Exchange, [new(5, Original, Blue), new(5, Original, Blue)]));
        Assert.Empty(device.Writes);
    }
    [Fact] public void RefreshKeepsOldZoneAnnotationsAndLightingSurvivesJson()
    {
        var old = new RgbDevice(0, "ASRock USB RGB", "ASRock", "", "", "USB 26CE:01A2 / 読み取り専用", [], -1, []);
        var updated = old with { Location = "USB 26CE:01A2", ControlIdentity = "test" };
        var id = old.ZoneRegistrationId("Onboard IO", 3);
        Assert.Equal(id, updated.ZoneRegistrationId("Onboard IO", 3));
        var settings = new AppSettings { Motherboard = new(RgbZones: [new(id, "本体", 15, Blue)]) };
        Assert.Equal(Blue, Contract.Copy(settings).Motherboard.RgbZones![0].LastLighting);
        Assert.Null(MotherboardDisplay.Validate(settings.Motherboard));
        Assert.NotNull(MotherboardDisplay.Validate(settings.Motherboard with { RgbZones = [new(id, LastLighting: Blue with { Speed = 256 })] }));
    }
}
