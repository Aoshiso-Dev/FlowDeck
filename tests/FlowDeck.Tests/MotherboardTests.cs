using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class MotherboardTests
{
    [Fact] public void UnverifiedDefaultNamesDoNotClaimPhysicalRails()
    {
        var sensor = new BoardSensor("/lpc/nct6799d/0/voltage/0", "Vcore", "Nuvoton NCT6799D", "voltage", "V", 2.64, 2.64);
        Assert.Equal("NCT6799D 電圧0", MotherboardDisplay.DefaultName(sensor));
    }
    [Theory]
    [InlineData("fan", 0, true)] [InlineData("fan", -1, false)] [InlineData("fan", 40000, false)]
    [InlineData("temperature", 42, true)] [InlineData("temperature", 255, false)]
    [InlineData("control", 100, true)] [InlineData("control", 101, false)] [InlineData("voltage", double.NaN, false)]
    public void InvalidMeasurementsAreNotUsable(string kind, double value, bool expected)
        => Assert.Equal(expected, MotherboardDisplay.IsUsable(kind, value));

    [Fact] public void StaleAndMissingSelectionsNeverBecomeOtherSensors()
    {
        var clock = new TestClock();
        var snapshot = new BoardSnapshot("White", [new("fan0", "Fan #1", "Nuvoton", "fan", "rpm", 1500, 1500)], clock.GetTimestamp());
        var settings = new MotherboardSettings([new("missing", "ポンプ", true), new("fan0", "ファン", true)]);
        var panel = MotherboardDisplay.Panel(snapshot, settings, clock);
        Assert.Null(panel.Rows[0].Value); Assert.Equal(1500, panel.Rows[1].Value);
        clock.Advance(3); panel = MotherboardDisplay.Panel(snapshot, settings, clock);
        Assert.False(panel.Fresh); Assert.All(panel.Rows, row => Assert.Null(row.Value)); Assert.Equal(3000, panel.AgeMs);
    }
    [Fact] public async Task DisplaySettingsDoNotChangeFanPolicyOrRevision()
    {
        await using var f = new Fixture(); await f.Initialize(); await f.Manual(54);
        var before = f.Core.Snapshot; var writes = f.Hub.Writes;
        f.Core.UpdateSettings(f.Core.Settings with { Motherboard = new([new("fan0", "CPU FAN", true)]) }, false);
        Assert.Equal(before.ControlRevision, f.Core.Snapshot.ControlRevision);
        Assert.Equal(before.ConfirmedDuty, f.Core.Snapshot.ConfirmedDuty); Assert.Equal(writes, f.Hub.Writes);
    }
    [Fact] public void DisplayLimitsRejectAmbiguousOrExcessiveConfiguration()
    {
        Assert.NotNull(MotherboardDisplay.Validate(new([new("x", "Fan", true), new("x", "Pump", true)])));
        Assert.NotNull(MotherboardDisplay.Validate(new(Enumerable.Range(0, 9).Select(i => new BoardSensorSetting(i.ToString(), "Fan", true)).ToArray())));
        Assert.NotNull(MotherboardDisplay.Validate(new(OpenRgbPort: 80)));
    }
    [Fact] public async Task SeparateFrameIsCapabilityGatedBoundedAndExpires()
    {
        await using var f = new Fixture(); await f.Initialize();
        var sensors = Enumerable.Range(0, 8).Select(i => new BoardSensor(i.ToString(), "温度", "chip", "temperature", "°C", 40, 40)).ToArray();
        var snapshot = new BoardSnapshot(new string('板', 100), sensors, f.Clock.GetTimestamp());
        var settings = new MotherboardSettings(sensors.Select(s => new BoardSensorSetting(s.Id, string.Concat(Enumerable.Repeat("😀温度", 30)), true)).ToArray());
        var protocol = new M5ProtocolSession(f.Core, time: f.Clock, motherboard: () => MotherboardDisplay.Panel(snapshot, settings, f.Clock));
        var frames = new List<JsonElement>(); protocol.Outgoing += line => { Assert.True(Encoding.UTF8.GetByteCount(line) + 1 <= 4096); using var doc = JsonDocument.Parse(line); frames.Add(doc.RootElement.Clone()); };
        void Hello(bool capable) => protocol.Receive(JsonSerializer.Serialize(new { v = 1, type = "hello", deviceType = "M5GO", deviceId = "test", bootId = capable ? "new" : "old", firmwareVersion = "1.2.0", capabilities = capable ? new[] { "motherboard-v1" } : [] }));
        Hello(false); Assert.DoesNotContain(frames, p => p.GetProperty("type").GetString() == "motherboardState");
        Hello(true); var board = frames.Last(); Assert.Equal("motherboardState", board.GetProperty("type").GetString());
        Assert.Equal(8, board.GetProperty("rows").GetArrayLength()); Assert.False(protocol.Synchronized);
        f.Clock.Advance(2);
        protocol.Receive(JsonSerializer.Serialize(new { v = 1, type = "heartbeat", sessionId = protocol.SessionId, controllerEpoch = f.Core.Epoch }));
        f.Clock.Advance(1); protocol.SendState();
        Assert.False(frames.Last().GetProperty("fresh").GetBoolean());
        Assert.All(frames.Last().GetProperty("rows").EnumerateArray(), r => Assert.Equal(JsonValueKind.Null, r.GetProperty("value").ValueKind));
        Assert.Equal(0, f.Hub.Writes);
    }
    [Fact] public async Task BoardFramesUseSelectedTransportOnly()
    {
        await using var f = new Fixture(); await f.Initialize();
        var router = new M5TransportRouter(f.Core, new(Transport: "auto"), f.Clock,
            () => new("Board", true, "監視のみ", [], 0));
        var routes = new List<(string Route, string Type)>();
        router.Outgoing += (route, json) => { using var doc = JsonDocument.Parse(json); routes.Add((route, doc.RootElement.GetProperty("type").GetString()!)); };
        var hello = JsonSerializer.Serialize(new { v = 1, type = "hello", deviceType = "M5GO", deviceId = "test", bootId = "boot", firmwareVersion = "1.2.0", capabilities = new[] { "motherboard-v1" } });
        router.Receive("wifi", hello); Assert.Contains(("wifi", "motherboardState"), routes);
        routes.Clear(); router.Receive("usb", hello); router.Receive("wifi", hello);
        Assert.Contains(("usb", "motherboardState"), routes); Assert.DoesNotContain(("wifi", "motherboardState"), routes);
    }
}

public class OpenRgbTests
{
    [Fact] public void AnnotationsFollowConnectionAndZoneRatherThanDiscoveryIndex()
    {
        var device = new RgbDevice(1, "ASRock", "ASRock", "test", "1", "USB-A", [], -1, []);
        var id = device.ZoneRegistrationId("ARGB 1", 0);
        Assert.Equal(id, (device with { Index = 7 }).ZoneRegistrationId("ARGB 1", 0));
        Assert.NotEqual(id, (device with { Location = "USB-B" }).ZoneRegistrationId("ARGB 1", 0));
        Assert.NotEqual(id, device.ZoneRegistrationId("ARGB 2", 1));
        var settings = new AppSettings { Motherboard = new(RgbZones: [new(id, "接続ファン", 24)]) };
        var restored = Contract.Copy(settings);
        Assert.Null(MotherboardDisplay.Validate(restored.Motherboard));
        Assert.Equal(24, restored.Motherboard.RgbZones![0].ConfirmedLedCount);
    }
    private static byte[] Device()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        void Text(string text) { var bytes = Encoding.UTF8.GetBytes(text + "\0"); writer.Write((ushort)bytes.Length); writer.Write(bytes); }
        writer.Write(0u); writer.Write(0u); Text("ASRock Test"); Text("ASRock"); Text("Polychrome"); Text("1"); Text("ignored"); Text("USB");
        writer.Write((ushort)1); writer.Write(0); Text("Static"); for (var i = 0; i < 9; i++) writer.Write(0u); writer.Write((ushort)0);
        writer.Write((ushort)1); Text("ARGB 1"); writer.Write(1u); writer.Write(0u); writer.Write(120u); writer.Write(24u); writer.Write((ushort)0);
        writer.Write((ushort)0); writer.Write((ushort)0);
        var data = stream.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(data, data.Length); return data;
    }
    [Fact] public void DecodeInventoryAndRejectTruncatedData()
    {
        var data = Device(); var device = OpenRgbMonitor.ParseDevice(0, data);
        Assert.True(device.IsAsrock); Assert.Equal("Static", device.Mode); Assert.Equal(24u, device.Zones[0].LedCount);
        Assert.ThrowsAny<IOException>(() => OpenRgbMonitor.ParseDevice(0, data[..^1]));
        var malformed = (byte[])data.Clone(); malformed[8] = 0xff; malformed[9] = 0xff;
        Assert.ThrowsAny<IOException>(() => OpenRgbMonitor.ParseDevice(0, malformed));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InventorySendsOnlyReadCommandsAndRejectsListChanges(bool listChanged)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var commands = new List<uint>();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token); using var stream = client.GetStream();
            for (var i = 0; i < 4; i++)
            {
                var header = new byte[16]; await stream.ReadExactlyAsync(header, timeout.Token);
                Assert.True(header.AsSpan(0, 4).SequenceEqual("ORGB"u8));
                var id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)); commands.Add(id);
                var payload = new byte[BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12))]; await stream.ReadExactlyAsync(payload, timeout.Token);
                if (id == 50) continue;
                if (id is 40 or 1) Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(payload));
                var reply = id switch { 40 => BitConverter.GetBytes(6u), 0 => BitConverter.GetBytes(1u), _ => Device() };
                if (listChanged && id == 1) { BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 100); reply = []; }
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), reply.Length);
                // Deliberately fragment reads to exercise TCP framing rather than relying on packet boundaries.
                await stream.WriteAsync(header.AsMemory(0, 7), timeout.Token); await stream.WriteAsync(header.AsMemory(7), timeout.Token);
                await stream.WriteAsync(reply, timeout.Token);
            }
        }, timeout.Token);
        var read = new OpenRgbMonitor().ReadAsync(port, timeout.Token);
        if (listChanged) await Assert.ThrowsAsync<IOException>(() => read);
        else Assert.Single((await read).Devices);
        await server; Assert.Equal(new uint[] { 40, 50, 0, 1 }, commands);
    }
}

public class AsrockRgbTests
{
    [Theory] [InlineData(0x10)] [InlineData(0x15)] [InlineData(0xe3)] [InlineData(0xe4)] [InlineData(0x12)] [InlineData(0xa4)]
    public void SetterAndInitializationOpcodesAreRejected(int opcode)
        => Assert.Throws<ArgumentOutOfRangeException>(() => AsrockRgbMonitor.Query((byte)opcode, 0));
    [Fact] public void QueryFormatAndCapturedHeaderResponse()
    {
        var query = AsrockRgbMonitor.Query(0x14, 2); Assert.Equal(65, query.Length); Assert.Equal(0, query[0]); Assert.Equal(0x14, query[1]); Assert.Equal(2, query[3]);
        var report = new byte[65]; report[4] = 2; new byte[] { 1, 1, 80, 80, 6, 15, 9, 80 }.CopyTo(report, 5);
        var data = AsrockRgbMonitor.Response(report, 65, 2); Assert.Equal(80, data[6]);
        Assert.Throws<IOException>(() => AsrockRgbMonitor.Response(report, 65, 1));
        Assert.Throws<IOException>(() => AsrockRgbMonitor.Response(report, 20, 2));
        Assert.Equal("Rainbow", AsrockRgbMonitor.ModeName(14));
    }
}
