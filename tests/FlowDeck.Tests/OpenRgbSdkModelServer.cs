using System.Buffers.Binary;
using System.Text;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using static FlowDeck.Tests.OpenRgbSdkTestServer;

namespace FlowDeck.Tests;

// Mutable SDK cache emulator. Decodes requests and encodes replies independently of the plan/executor.
internal sealed class OpenRgbSdkModelServer : IAsyncDisposable
{
    public readonly OpenRgbSdkTestServer Wire = new();
    public readonly Dictionary<uint, OpenRgbSdkController> Devices = [];
    public int WriteCount, IgnoreWriteNumber, DisconnectWriteNumber;
    public bool Echo;
    public Func<Packet, Task>? BeforeWrite;
    public Func<Packet, Task>? DuringRead;
    public string Detectors = "{\"detectors\":{\"Fixture controller\":true},\"hid_safe_mode\":true}";
    public string DetectorSchema = "{\"hid_safe_mode\":{\"type\":\"bool\"}}";
    public string Drivers = "{\"shared_smbus_access\":true,\"smbus_sleep_mode\":2}";
    public string ServerSettings = "{\"all_controllers\":false}";
    public readonly Dictionary<string, string> StartupSettings = [];
    public byte[] Plugins = Sized(w => w.Write((ushort)0));
    public int Port => Wire.Port;
    public OpenRgbSdkModelServer(params OpenRgbSdkController[] devices)
    {
        Wire.Flags = 25;
        foreach (var device in devices) Devices.Add(device.Id, OpenRgbSdkProtocol.Copy(device));
        Wire.Devices.Clear(); foreach (var pair in Devices) Wire.Devices.Add(pair.Key, Encode(pair.Value));
        Wire.Intercept = ProcessAsync;
    }
    private async Task<bool> ProcessAsync(Packet packet)
    {
        if (packet.Command == 52 && BinaryPrimitives.ReadUInt32LittleEndian(packet.Data) is 17 or 25)
        { await Wire.ReplyAsync(packet, 53, Numbers(Wire.Flags)); return true; }
        if (packet.Command is 250 or 251)
        {
            var key = Encoding.UTF8.GetString(packet.Data); Assert.Equal(0u, packet.Device);
            var value = packet.Command == 251 ? DetectorSchema : key switch
            {
                "Detectors\0" => Detectors, "Drivers\0" => Drivers, "Server\0" => ServerSettings,
                _ => StartupSettings[key.TrimEnd('\0')]
            };
            await Wire.ReplyAsync(packet, packet.Command, Encoding.UTF8.GetBytes(value + '\0')); return true;
        }
        if (packet.Command == 200) { await Wire.ReplyAsync(packet, 200, Plugins); return true; }
        if (packet.Command is 1 or 1050 or 1051 or 1101 or 1103 && !Devices.ContainsKey(packet.Device))
        { await Wire.AckAsync(packet, 4); return true; }
        if (packet.Command == 1)
        {
            await Wire.SendAsync(packet.Device, 1, Encode(Devices[packet.Device]));
            if (DuringRead is not null) await DuringRead(packet);
            await Wire.AckAsync(packet); return true;
        }
        if (packet.Command is not (1050 or 1051 or 1101 or 1103)) return false;
        WriteCount++; if (BeforeWrite is not null) await BeforeWrite(packet);
        if (WriteCount != IgnoreWriteNumber) Devices[packet.Device] = Apply(Devices[packet.Device], packet);
        if (WriteCount == DisconnectWriteNumber) { Wire.Disconnect(); return true; }
        if (Echo)
        {
            var device = Devices[packet.Device];
            if (packet.Command is 1050 or 1051) await Wire.SendAsync(packet.Device, 1150, SignalColors(device.Colors.Select(c => (uint)(c.R | c.G << 8 | c.B << 16)).ToArray()));
            else await Wire.SendAsync(packet.Device, 1150, SignalController(1, Encode(device)));
        }
        await Wire.AckAsync(packet); return true;
    }
    private static string Text(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(reader.ReadUInt16()); Assert.Equal(0, bytes[^1]); return Encoding.UTF8.GetString(bytes, 0, bytes.Length - 1);
    }
    private static RgbColor[] Colors(BinaryReader r)
    {
        var values = new RgbColor[r.ReadUInt16()];
        for (var i = 0; i < values.Length; i++) { var color = r.ReadUInt32(); values[i] = new((int)(color & 255), (int)(color >> 8 & 255), (int)(color >> 16 & 255)); }
        return values;
    }
    private static OpenRgbSdkMode Mode(BinaryReader r) => new(Text(r), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(),
        r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), Colors(r));
    private static OpenRgbSdkController Apply(OpenRgbSdkController device, Packet packet)
    {
        device = OpenRgbSdkProtocol.Copy(device);
        using var stream = new MemoryStream(packet.Data); using var reader = new BinaryReader(stream);
        Assert.Equal(packet.Data.Length, reader.ReadInt32()); int? zone = packet.Command is 1051 or 1103 ? reader.ReadInt32() : null;
        if (packet.Command is 1050 or 1051)
        {
            var colors = Colors(reader); var offset = zone is { } z ? (int)device.Zones.Take(z).Sum(v => (long)v.LedCount) : 0;
            Assert.Equal(zone is { } index ? (int)device.Zones[index].LedCount : device.Colors.Length, colors.Length); colors.CopyTo(device.Colors, offset);
        }
        else
        {
            var index = reader.ReadInt32(); var mode = index >= 0 ? Mode(reader) : null;
            if (zone is { } z)
            {
                var current = device.Zones[z]; if (mode is not null) current.Modes[index] = mode;
                device.Zones[z] = current with { ActiveMode = index };
            }
            else { Assert.NotNull(mode); device.Modes[index] = mode; device = device with { ActiveMode = index }; }
        }
        Assert.Equal(stream.Length, stream.Position); return device;
    }
    public static byte[] Encode(OpenRgbSdkController device) => Sized(w =>
    {
        void WriteColors(RgbColor[] colors) { w.Write((ushort)colors.Length); foreach (var c in colors) w.Write((uint)(c.R | c.G << 8 | c.B << 16)); }
        void WriteMode(OpenRgbSdkMode m)
        {
            OpenRgbSdkTestServer.Text(w, m.Name); w.Write(m.Flags); w.Write(m.SpeedMin); w.Write(m.SpeedMax); w.Write(m.BrightnessMin); w.Write(m.BrightnessMax);
            w.Write(m.ColorsMin); w.Write(m.ColorsMax); w.Write(m.Speed); w.Write(m.Brightness); w.Write(m.Direction); w.Write(m.ColorMode); WriteColors(m.Colors);
        }
        void Matrix(OpenRgbSdkMatrix? m)
        {
            if (m is null) { w.Write((ushort)0); return; }
            w.Write((ushort)(8 + m.Cells.Length * 4)); w.Write(m.Height); w.Write(m.Width); foreach (var cell in m.Cells) w.Write(cell);
        }
        w.Write(device.Type); foreach (var text in new[] { device.Name, device.Vendor, device.Description, device.Version, device.Serial, device.Location }) OpenRgbSdkTestServer.Text(w, text);
        w.Write((ushort)device.Modes.Length); w.Write(device.ActiveMode); foreach (var mode in device.Modes) WriteMode(mode);
        w.Write((ushort)device.Zones.Length);
        foreach (var z in device.Zones)
        {
            OpenRgbSdkTestServer.Text(w, z.Name); w.Write(z.Type); w.Write(z.MinimumLeds); w.Write(z.MaximumLeds); w.Write(z.LedCount); Matrix(z.Matrix);
            w.Write((ushort)z.Segments.Length);
            foreach (var segment in z.Segments)
            { OpenRgbSdkTestServer.Text(w, segment.Name); w.Write(segment.Type); w.Write(segment.Start); w.Write(segment.Count); Matrix(segment.Matrix); w.Write(segment.Flags); }
            w.Write(z.Flags); w.Write(z.ActiveMode); w.Write((ushort)z.Modes.Length); foreach (var mode in z.Modes) WriteMode(mode); OpenRgbSdkTestServer.Text(w, z.DisplayName);
        }
        w.Write((ushort)device.Leds.Length); foreach (var led in device.Leds) OpenRgbSdkTestServer.Text(w, led); WriteColors(device.Colors);
        w.Write((ushort)device.LedDisplayNames.Length); foreach (var label in device.LedDisplayNames) OpenRgbSdkTestServer.Text(w, label);
        w.Write(device.Flags); OpenRgbSdkTestServer.Text(w, device.DisplayName); OpenRgbSdkTestServer.Text(w, device.Configuration, true);
    });
    public ValueTask DisposeAsync() => Wire.DisposeAsync();
}
