using FlowDeck.Core;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FlowDeck.Infrastructure;

public record RgbZone(string Name, uint Type, uint LedCount, uint? MinimumLeds, uint? MaximumLeds, string? ReportedMode = null, byte? HardwareZone = null, BoardLightSetting? Configuration = null);
public record RgbDevice(uint Index, string Name, string Vendor, string Description, string Version,
    string Location, string[] Modes, int ActiveMode, RgbZone[] Zones, string? ControlIdentity = null)
{
    private string RegistrationLocation => Location == "USB 26CE:01A2" ? "USB 26CE:01A2 / 読み取り専用" : Location;
    public string Mode => ActiveMode >= 0 && ActiveMode < Modes.Length ? Modes[ActiveMode] : "未取得";
    public bool IsAsrock => Vendor.Contains("ASRock", StringComparison.OrdinalIgnoreCase) || Name.Contains("ASRock", StringComparison.OrdinalIgnoreCase);
    public bool IsMemory => MemoryLighting.IsIdentity(ControlIdentity);
    // Discovery indexes can change. Display-only annotations follow the source and zone, not that index.
    public string ZoneRegistrationId(string zoneName, int zoneIndex) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        Encoding.UTF8.GetBytes($"{Vendor}\0{Name}\0{RegistrationLocation}\0{zoneIndex}\0{zoneName}")));
}
public record RgbInventory(RgbDevice[] Devices, DateTimeOffset? UpdatedAt = null, string? Error = null);

/// <summary>Loopback-only SDK inventory. The only wire commands are version, name, count and data requests.</summary>
public sealed class OpenRgbMonitor
{
    private const uint Protocol = 2;
    private const int MaximumPacket = 1024 * 1024;
    public async Task<RgbInventory> ReadAsync(int port = 6742, CancellationToken ct = default)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
        using var stream = client.GetStream();
        await SendAsync(stream, 0, 40, BitConverter.GetBytes(Protocol), deadline.Token);
        var version = await ReceiveAsync(stream, 0, 40, deadline.Token);
        if (version.Length != 4 || BinaryPrimitives.ReadUInt32LittleEndian(version) < Protocol)
            throw new IOException("OpenRGB SDK protocol 2以降が必要です");
        await SendAsync(stream, 0, 50, Encoding.UTF8.GetBytes("FlowDeck - read only\0"), deadline.Token);
        await SendAsync(stream, 0, 0, [], deadline.Token);
        var counts = await ReceiveAsync(stream, 0, 0, deadline.Token);
        if (counts.Length != 4) throw new IOException("OpenRGBの機器数応答が不正です");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(counts);
        if (count > 128) throw new IOException("OpenRGBの機器数が上限を超えました");
        var devices = new List<RgbDevice>();
        for (uint index = 0; index < count; index++)
        {
            await SendAsync(stream, index, 1, BitConverter.GetBytes(Protocol), deadline.Token);
            devices.Add(ParseDevice(index, await ReceiveAsync(stream, index, 1, deadline.Token)));
        }
        return new(devices.ToArray(), DateTimeOffset.UtcNow);
    }
    private static async Task SendAsync(NetworkStream stream, uint device, uint command, byte[] payload, CancellationToken ct)
    {
        var packet = new byte[16 + payload.Length]; "ORGB"u8.CopyTo(packet);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), device);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), command);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12), payload.Length);
        payload.CopyTo(packet, 16); await stream.WriteAsync(packet, ct);
    }
    private static async Task<byte[]> ReceiveAsync(NetworkStream stream, uint device, uint command, CancellationToken ct)
    {
        var header = new byte[16]; await stream.ReadExactlyAsync(header, ct);
        if (!header.AsSpan(0, 4).SequenceEqual("ORGB"u8)) throw new IOException("OpenRGBの応答ヘッダーが不正です");
        var response = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        // Indexes cease to identify devices after a list-change event. Discard this entire scan.
        if (response == 100) throw new IOException("OpenRGBの機器構成が変わりました。再取得してください");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != device || response != command || length > MaximumPacket)
            throw new IOException("OpenRGBの応答IDまたはサイズが不正です");
        var data = new byte[(int)length]; await stream.ReadExactlyAsync(data, ct); return data;
    }
    public static RgbDevice ParseDevice(uint index, byte[] data)
    {
        if (data.Length > MaximumPacket) throw new IOException("OpenRGBの機器情報が大きすぎます");
        using var memory = new MemoryStream(data, false); using var reader = new BinaryReader(memory, new UTF8Encoding(false, true));
        string Text()
        {
            var length = reader.ReadUInt16();
            if (length is 0 or > 4096) throw new IOException("OpenRGBの文字列長が不正です");
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length || bytes[^1] != 0) throw new IOException("OpenRGBの文字列が不完全です");
            return Encoding.UTF8.GetString(bytes, 0, bytes.Length - 1);
        }
        void Skip(int bytes) { if (bytes < 0 || memory.Length - memory.Position < bytes) throw new EndOfStreamException(); memory.Position += bytes; }
        int Count(int limit) { var n = reader.ReadUInt16(); if (n > limit) throw new IOException("OpenRGBの項目数が上限を超えました"); return n; }
        if (reader.ReadUInt32() != data.Length) throw new IOException("OpenRGBの機器情報サイズが不正です");
        _ = reader.ReadUInt32(); var name = Text(); var vendor = Text(); var description = Text(); var version = Text();
        _ = Text(); // Serial is unnecessary for a read-only inventory; do not retain it.
        var location = Text(); var modes = new string[Count(256)]; var active = reader.ReadInt32();
        for (var i = 0; i < modes.Length; i++) { modes[i] = Text(); Skip(36); Skip(Count(16384) * 4); }
        var zones = new RgbZone[Count(256)];
        for (var i = 0; i < zones.Length; i++)
        {
            var zoneName = Text(); var type = reader.ReadUInt32(); var min = reader.ReadUInt32(); var max = reader.ReadUInt32(); var count = reader.ReadUInt32();
            if (count > 65535 || min > max || count > max) throw new IOException("OpenRGBのLED数が不正です");
            Skip(reader.ReadUInt16()); zones[i] = new(zoneName, type, count, min, max);
        }
        var leds = Count(16384); for (var i = 0; i < leds; i++) { _ = Text(); Skip(4); }
        Skip(Count(16384) * 4);
        if (memory.Position != memory.Length) throw new IOException("OpenRGBの機器情報に未対応のデータがあります");
        return new(index, name, vendor, description, version, location, modes, active, zones);
    }
}
