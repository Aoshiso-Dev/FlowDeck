using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>Volatile USB Direct frames. Protocol reference: OpenRGB ASRockPolychromeUSBController.
/// Independent packet encoder; never changes LED counts, initializes firmware, or commits to flash.</summary>
public static class AsrockDirectProtocol
{
    // Disable firmware RGSwap during streaming to avoid flicker. Frame encoding must use
    // this active mask; the saved native mask is only for decoding/restoring native settings.
    public const byte StreamingOrder = 0;
    public sealed record Zone(byte Address, int LedCount, RgbColor Color);

    // The payload is a complete device frame, ordered by physical zone address, not selection index.
    // Callers must supply every present zone, including any zones outside the animation selection.
    public static byte[][] Frame(Zone[] zones, byte activeOrder)
    {
        if (zones.Length is < 1 or > 8 || zones.Any(z => z.Address > 7 || z.LedCount is < 1 or > 255 || !z.Color.Valid) ||
            zones.Select(z => z.Address).Distinct().Count() != zones.Length)
            throw new ArgumentException("Directフレームのゾーン・LED数・色が不正です");
        var sorted = zones.OrderBy(z => z.Address).ToArray();
        var colors = new byte[(sorted.Sum(z => z.LedCount) + 8) * 3];
        var offset = 0;
        foreach (var zone in sorted)
        {
            var normal = (activeOrder & (1 << zone.Address)) != 0;
            for (var led = 0; led < zone.LedCount; led++)
            {
                colors[offset++] = (byte)(normal ? zone.Color.R : zone.Color.G);
                colors[offset++] = (byte)(normal ? zone.Color.G : zone.Color.R);
                colors[offset++] = (byte)zone.Color.B;
            }
        }
        var packets = new List<byte[]>();
        for (var read = 0; read < colors.Length;)
        {
            var first = read == 0;
            var packet = new byte[65];
            packet[1] = 0x10; packet[3] = 0xff; packet[4] = first ? (byte)0xe3 : (byte)0xe4; packet[64] = 0x65;
            if (first) packet[7] = 0xff;
            var count = Math.Min(first ? 54 : 57, colors.Length - read);
            Array.Copy(colors, read, packet, first ? 9 : 5, count);
            read += count; packets.Add(packet);
        }
        return packets.ToArray();
    }

    public static byte[] ColorOrder(byte order)
    {
        var packet = new byte[65]; packet[1] = 0x15; packet[3] = 3; packet[4] = order; return packet;
    }

    public static byte[] Enter(byte zone)
    {
        if (zone > 7) throw new ArgumentOutOfRangeException(nameof(zone));
        var packet = new byte[65]; packet[1] = 0x10; packet[3] = zone; packet[4] = 15; packet[8] = 224; packet[9] = 255;
        return packet;
    }
}
