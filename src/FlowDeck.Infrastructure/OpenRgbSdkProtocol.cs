using System.Buffers.Binary;
using System.Text;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed record OpenRgbSdkMode(string Name, uint Flags, uint SpeedMin, uint SpeedMax, uint BrightnessMin, uint BrightnessMax,
    uint ColorsMin, uint ColorsMax, uint Speed, uint Brightness, uint Direction, uint ColorMode, RgbColor[] Colors);
internal sealed record OpenRgbSdkMatrix(uint Height, uint Width, uint[] Cells);
internal sealed record OpenRgbSdkSegment(string Name, uint Type, uint Start, uint Count, OpenRgbSdkMatrix? Matrix, uint Flags);
internal sealed record OpenRgbSdkZone(string Name, uint Type, uint MinimumLeds, uint MaximumLeds, uint LedCount,
    OpenRgbSdkMatrix? Matrix, OpenRgbSdkSegment[] Segments, uint Flags, int ActiveMode, OpenRgbSdkMode[] Modes, string DisplayName);
internal sealed record OpenRgbSdkController(uint Id, uint Type, string Name, string Vendor, string Description, string Version,
    string Serial, string Location, int ActiveMode, OpenRgbSdkMode[] Modes, OpenRgbSdkZone[] Zones,
    string[] Leds, RgbColor[] Colors, string[] LedDisplayNames, uint Flags, string DisplayName, string Configuration);

// Independently implemented wire codec. Pinned reference: OpenRGB 0f8f2dccc46576f0a9dac6b70adcbf49901c6b73.
// Protocol 6 uses unique controller IDs. The legacy index-based inventory codec is kept separate.
internal static partial class OpenRgbSdkProtocol
{
    public const uint Version = 6;
    public const int MaximumPacket = 1024 * 1024, MaximumControllers = 32, MaximumLeds = 4096;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static byte[] UInt(uint value) { var bytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); return bytes; }
    public static uint UInt(byte[] data) => data.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : throw new IOException("OpenRGBの数値応答が不正です");
    public static byte[] Packet(uint device, uint command, byte[] data)
    {
        if (data.Length > MaximumPacket) throw new IOException("OpenRGBのパケット上限を超えました");
        var packet = new byte[16 + data.Length]; "ORGB"u8.CopyTo(packet);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), device); BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), command);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12), data.Length); data.CopyTo(packet, 16); return packet;
    }
    public static (uint Device, uint Command, int Length) Header(byte[] header)
    {
        if (header.Length != 16 || !header.AsSpan(0, 4).SequenceEqual("ORGB"u8)) throw new IOException("OpenRGBのパケット識別が不正です");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        if (length > MaximumPacket) throw new IOException("OpenRGBのパケット上限を超えました");
        return (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)), BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)), (int)length);
    }
    public static uint[] ControllerIds(byte[] data)
    {
        if (data.Length < 4) throw new IOException("OpenRGBの機器一覧が不完全です");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (count > MaximumControllers || data.Length != 4 + count * 4) throw new IOException("OpenRGBの機器一覧サイズが不正です");
        var ids = Enumerable.Range(0, (int)count).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4 + i * 4))).ToArray();
        if (ids.Distinct().Count() != ids.Length) throw new IOException("OpenRGBの機器IDが重複しています");
        return ids;
    }
    public static string NullText(byte[] bytes, int maximum = 4096)
    {
        if (bytes.Length is < 1 || bytes.Length > maximum || bytes[^1] != 0 || bytes.AsSpan(0, bytes.Length - 1).Contains((byte)0))
            throw new IOException("OpenRGBの文字列が不正です");
        try { return Utf8.GetString(bytes, 0, bytes.Length - 1); }
        catch (DecoderFallbackException e) { throw new IOException("OpenRGBの文字コードが不正です", e); }
    }
    private sealed class Reader(byte[] data) : IDisposable
    {
        private readonly BinaryReader reader = new(new MemoryStream(data, false));
        public uint U32() => reader.ReadUInt32();
        public int I32() => reader.ReadInt32();
        public int Count(int maximum) { var count = reader.ReadUInt16(); return count <= maximum ? count : throw new IOException("OpenRGBの項目数が上限を超えました"); }
        public string Text(bool wide = false)
        {
            var maximum = wide ? 65536 : 4096; var count = wide ? U32() : reader.ReadUInt16();
            if (count is < 1 || count > maximum || count > reader.BaseStream.Length - reader.BaseStream.Position) throw new IOException("OpenRGBの文字列サイズが不正です");
            return NullText(reader.ReadBytes((int)count), maximum);
        }
        public RgbColor[] Colors()
        {
            var result = new RgbColor[Count(MaximumLeds)];
            for (var i = 0; i < result.Length; i++)
            {
                var color = U32(); if ((color & 0xff000000) != 0) throw new IOException("OpenRGBのRGB値が不正です");
                result[i] = new((int)(color & 255), (int)((color >> 8) & 255), (int)((color >> 16) & 255));
            }
            return result;
        }
        public OpenRgbSdkMode Mode() => new(Text(), U32(), U32(), U32(), U32(), U32(), U32(), U32(), U32(), U32(), U32(), U32(), Colors());
        public OpenRgbSdkMode[] Modes(int count) => Enumerable.Range(0, count).Select(_ => Mode()).ToArray();
        public OpenRgbSdkMatrix? Matrix(uint count)
        {
            var size = reader.ReadUInt16(); if (size == 0) return null;
            if (size < 8 || size % 4 != 0) throw new IOException("OpenRGBの配置データサイズが不正です");
            var height = U32(); var width = U32(); var cells = (size - 8) / 4;
            if ((ulong)height * width != (ulong)cells || height == 0 || width == 0) throw new IOException("OpenRGBの配置データが不正です");
            var result = new uint[cells];
            for (var i = 0; i < cells; i++) { var index = U32(); if (index != uint.MaxValue && index >= count) throw new IOException("OpenRGBの配置先が範囲外です"); result[i] = index; }
            return new(height, width, result);
        }
        public OpenRgbSdkZone Zone()
        {
            var name = Text(); var type = U32(); var min = U32(); var max = U32(); var count = U32();
            if (min > max || count > max || count > MaximumLeds) throw new IOException("OpenRGBのLED数が不正です");
            var matrix = Matrix(count); var segments = new OpenRgbSdkSegment[Count(256)];
            for (var i = 0; i < segments.Length; i++)
            {
                var segmentName = Text(); var segmentType = U32(); var start = U32(); var length = U32();
                if ((ulong)start + length > count) throw new IOException("OpenRGBの区間がゾーン範囲外です");
                segments[i] = new(segmentName, segmentType, start, length, Matrix(length), U32());
            }
            var flags = U32(); var active = I32(); var modes = Modes(Count(256)); var displayName = Text();
            CheckActive(active, modes.Length);
            return new(name, type, min, max, count, matrix, segments, flags, active, modes, displayName);
        }
        public void Finish() { if (reader.BaseStream.Position != reader.BaseStream.Length) throw new IOException("OpenRGBに未対応の末尾データがあります"); }
        public void Dispose() => reader.Dispose();
    }
    private static void CheckActive(int active, int count)
    {
        // Controllers/zones without local modes commonly report either -1 or the default zero.
        if (active < -1 || count == 0 && active > 0 || count > 0 && active >= count) throw new IOException("OpenRGBの効果選択が範囲外です");
    }
    public static OpenRgbSdkController Controller(uint id, byte[] data)
    {
        if (data.Length is < 4 or > MaximumPacket || BinaryPrimitives.ReadUInt32LittleEndian(data) != data.Length) throw new IOException("OpenRGBの機器情報サイズが不正です");
        try
        {
            using var r = new Reader(data); _ = r.U32(); var type = r.U32(); var name = r.Text(); var vendor = r.Text(); var description = r.Text();
            var version = r.Text(); var serial = r.Text(); var location = r.Text(); var modeCount = r.Count(256); var active = r.I32(); var modes = r.Modes(modeCount);
            CheckActive(active, modes.Length); var zones = Enumerable.Range(0, r.Count(256)).Select(_ => r.Zone()).ToArray();
            var leds = Enumerable.Range(0, r.Count(MaximumLeds)).Select(_ => r.Text()).ToArray(); var colors = r.Colors();
            if (colors.Length != leds.Length) throw new IOException("OpenRGBのLEDと色の数が一致しません");
            var names = Enumerable.Range(0, r.Count(MaximumLeds)).Select(_ => r.Text()).ToArray();
            if (names.Length > leds.Length) throw new IOException("OpenRGBのLED表示名の数が不正です");
            var result = new OpenRgbSdkController(id, type, name, vendor, description, version, serial, location, active, modes, zones,
                leds, colors, names, r.U32(), r.Text(), r.Text(true)); r.Finish(); return result;
        }
        catch (EndOfStreamException e) { throw new IOException("OpenRGBの機器情報が不完全です", e); }
    }
    public static string Structure(OpenRgbSdkController device)
    {
        object Mode(OpenRgbSdkMode m) => new { m.Name, m.Flags, m.SpeedMin, m.SpeedMax, m.BrightnessMin, m.BrightnessMax, m.ColorsMin, m.ColorsMax };
        return SettingsTransfer.Hash(new { device.Type, device.Name, device.Vendor, device.Description, device.Version, device.Serial, device.Location,
            Modes = device.Modes.Select(Mode).ToArray(), Zones = device.Zones.Select(z => new { z.Name, z.Type, z.MinimumLeds, z.MaximumLeds, z.LedCount,
                z.Matrix, z.Segments, z.Flags, Modes = z.Modes.Select(Mode).ToArray() }).ToArray(), device.Leds, device.Flags, device.Configuration });
    }
    public static OpenRgbSdkController Copy(OpenRgbSdkController device)
    {
        OpenRgbSdkMode Mode(OpenRgbSdkMode m) => m with { Colors = m.Colors.ToArray() };
        OpenRgbSdkMatrix? Matrix(OpenRgbSdkMatrix? m) => m is null ? null : m with { Cells = m.Cells.ToArray() };
        return device with { Modes = device.Modes.Select(Mode).ToArray(), Zones = device.Zones.Select(z => z with
        { Matrix = Matrix(z.Matrix), Segments = z.Segments.Select(s => s with { Matrix = Matrix(s.Matrix) }).ToArray(), Modes = z.Modes.Select(Mode).ToArray() }).ToArray(),
            Leds = device.Leds.ToArray(), Colors = device.Colors.ToArray(), LedDisplayNames = device.LedDisplayNames.ToArray() };
    }
    public static (uint Reason, OpenRgbSdkController? Controller, RgbColor[]? Colors) Signal(uint id, byte[] data)
    {
        if (data.Length is < 8 or > MaximumPacket || BinaryPrimitives.ReadUInt32LittleEndian(data) != data.Length)
            throw new IOException("OpenRGBの変更通知サイズが不正です");
        var reason = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        if (reason > 11) throw new IOException("OpenRGBの変更通知に未対応です");
        if (reason == 0)
        {
            try { using var reader = new Reader(data[8..]); var colors = reader.Colors(); reader.Finish(); return (reason, null, colors); }
            catch (EndOfStreamException e) { throw new IOException("OpenRGBの変更通知が不完全です", e); }
        }
        var description = new byte[data.Length - 4]; BinaryPrimitives.WriteInt32LittleEndian(description, description.Length);
        data.AsSpan(8).CopyTo(description.AsSpan(4)); return (reason, Controller(id, description), null);
    }
    public static void DetectionProgress(byte[] data)
    {
        if (data.Length is < 11 or > MaximumPacket || BinaryPrimitives.ReadUInt32LittleEndian(data) != data.Length)
            throw new IOException("OpenRGBの検出通知サイズが不正です");
        try
        {
            using var r = new Reader(data); _ = r.U32(); if (r.U32() > 100) throw new IOException("OpenRGBの検出進捗が不正です");
            _ = r.Text(); r.Finish();
        }
        catch (EndOfStreamException e) { throw new IOException("OpenRGBの検出通知が不完全です", e); }
    }
    private static void Text(BinaryWriter writer, string text)
    {
        var bytes = Utf8.GetBytes(text); if (bytes.Length >= 4096 || text.Contains('\0')) throw new IOException("OpenRGBの文字列が不正です");
        writer.Write((ushort)(bytes.Length + 1)); writer.Write(bytes); writer.Write((byte)0);
    }
    private static void Colors(BinaryWriter writer, RgbColor[] colors)
    {
        if (colors.Length > MaximumLeds || colors.Any(c => c?.Valid != true)) throw new IOException("OpenRGBの色配列が不正です");
        writer.Write((ushort)colors.Length);
        foreach (var c in colors) writer.Write((uint)(c.R | c.G << 8 | c.B << 16));
    }
    private static byte[] Body(Action<BinaryWriter> content)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(0); content(writer); var data = stream.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(data, data.Length); return data;
    }
    public static byte[] UpdateColors(RgbColor[] colors, int? zone = null) => Body(w =>
    { if (zone is { } z) { if (z < 0) throw new IOException("ゾーン番号が不正です"); w.Write(z); } Colors(w, colors); });
    public static byte[] UpdateMode(int index, OpenRgbSdkMode mode, int? zone = null) => Body(w =>
    {
        if (index < 0 || zone is < 0) throw new IOException("効果・ゾーン番号が不正です"); if (zone is { } z) w.Write(z); w.Write(index);
        Text(w, mode.Name); w.Write(mode.Flags); w.Write(mode.SpeedMin); w.Write(mode.SpeedMax); w.Write(mode.BrightnessMin); w.Write(mode.BrightnessMax);
        w.Write(mode.ColorsMin); w.Write(mode.ColorsMax); w.Write(mode.Speed); w.Write(mode.Brightness); w.Write(mode.Direction); w.Write(mode.ColorMode); Colors(w, mode.Colors);
    });
    public static byte[] InheritControllerMode(int zone) => Body(w =>
    { if (zone < 0) throw new IOException("ゾーン番号が不正です"); w.Write(zone); w.Write(-1); });
}
