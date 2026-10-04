using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FlowDeck.Tests;

// The fake server builds SDK packets independently of the production codec. It never opens a hardware backend.
internal sealed class OpenRgbSdkTestServer : IAsyncDisposable
{
    internal sealed record Packet(uint Device, uint Command, byte[] Data);
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly TaskCompletionSource<NetworkStream> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task server;
    private TcpClient? client;
    public readonly ConcurrentQueue<Packet> Requests = new();
    public readonly Dictionary<uint, byte[]> Devices = new() { [41] = Description(), [700] = Description("Second device") };
    public uint Version = 6, Flags = 1;
    public bool AckFirst, Fragment;
    public Func<Packet, Task<bool>>? Intercept;
    public Exception? Failure;
    public CancellationToken Stopping => stop.Token;
    public int Port { get; }
    public ushort PeerPort => checked((ushort)((IPEndPoint)client!.Client.RemoteEndPoint!).Port);
    public OpenRgbSdkTestServer()
    {
        listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port; server = RunAsync();
    }
    private async Task RunAsync()
    {
        try
        {
            client = await listener.AcceptTcpClientAsync(stop.Token); var stream = client.GetStream(); connected.SetResult(stream);
            while (!stop.IsCancellationRequested)
            {
                var header = new byte[16]; await stream.ReadExactlyAsync(header, stop.Token);
                Assert.Equal("ORGB", Encoding.ASCII.GetString(header, 0, 4));
                var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)); Assert.True(length <= 1024 * 1024);
                var data = new byte[(int)length]; await stream.ReadExactlyAsync(data, stop.Token);
                var packet = new Packet(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)), BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)), data);
                Requests.Enqueue(packet);
                if (Intercept is { } intercept && await intercept(packet)) continue;
                switch (packet.Command)
                {
                    case 40:
                        Assert.Equal(new byte[] { 6, 0, 0, 0 }, data);
                        if (Version < 6) await SendAsync(0, 40, Numbers(Version)); else await ReplyAsync(packet, 40, Numbers(Version));
                        await SendAsync(0, 51, "Test server\0"u8.ToArray()); break;
                    case 50: Assert.Equal("FlowDeck\0", Encoding.UTF8.GetString(data)); await AckAsync(packet); break;
                    case 52: Assert.Equal(new byte[] { 1, 0, 0, 0 }, data); await ReplyAsync(packet, 53, Numbers(Flags)); break;
                    case 0: await ReplyAsync(packet, 0, Numbers([(uint)Devices.Count, .. Devices.Keys])); break;
                    case 1:
                        Assert.Equal(new byte[] { 6, 0, 0, 0 }, data);
                        await ReplyAsync(packet, 1, Devices[packet.Device]); break;
                    case 1050: case 1051: case 1101: case 1103: await AckAsync(packet); break;
                    default: throw new InvalidOperationException($"Unexpected command {packet.Command}");
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
        catch (Exception e) { Failure = e; client?.Dispose(); connected.TrySetException(e); }
    }
    public Task AckAsync(Packet packet, uint status = 0) => SendAsync(packet.Device, 10, Numbers(packet.Command, status));
    public async Task ReplyAsync(Packet packet, uint response, byte[] data)
    {
        if (AckFirst) await AckAsync(packet);
        await SendAsync(packet.Device, response, data);
        if (!AckFirst) await AckAsync(packet);
    }
    public Task SendAsync(uint device, uint command, byte[] data)
    {
        var packet = new byte[16 + data.Length]; Encoding.ASCII.GetBytes("ORGB").CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), device); BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), command);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12), data.Length); data.CopyTo(packet, 16); return SendRawAsync(packet);
    }
    public async Task SendRawAsync(byte[] packet)
    {
        var stream = await connected.Task.WaitAsync(stop.Token); await writes.WaitAsync(stop.Token);
        try
        {
            if (Fragment) { for (var i = 0; i < packet.Length; i += 7) await stream.WriteAsync(packet.AsMemory(i, Math.Min(7, packet.Length - i)), stop.Token); }
            else await stream.WriteAsync(packet, stop.Token);
        }
        finally { writes.Release(); }
    }
    public void Disconnect() => client?.Dispose();
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); client?.Dispose(); listener.Stop(); await server;
        Assert.Null(Failure);
    }
    public static byte[] Numbers(params uint[] numbers)
    {
        var bytes = new byte[numbers.Length * 4];
        for (var i = 0; i < numbers.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 * i), numbers[i]); return bytes;
    }
    public static byte[] Sized(Action<BinaryWriter> content)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream); writer.Write(0); content(writer);
        var data = stream.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(data, data.Length); return data;
    }
    public static void Text(BinaryWriter writer, string text, bool wide = false)
    {
        var bytes = Encoding.UTF8.GetBytes(text + '\0'); if (wide) writer.Write((uint)bytes.Length); else writer.Write((ushort)bytes.Length); writer.Write(bytes);
    }
    public static void Mode(BinaryWriter w, bool direct)
    {
        Text(w, direct ? "Direct" : "Native"); w.Write(direct ? 32u : 2115u);
        w.Write(1u); w.Write(100u); w.Write(0u); w.Write(100u); w.Write(direct ? 0u : 1u); w.Write(4u);
        w.Write(25u); w.Write(80u); w.Write(0u); w.Write(direct ? 1u : 2u);
        w.Write((ushort)(direct ? 0 : 1)); if (!direct) w.Write(0x00332211u);
    }
    public static byte[] Description(string name = "Device", int count = 2, uint? matrixIndex = null) => Sized(w =>
    {
        w.Write(1u); Text(w, name); Text(w, "Vendor"); Text(w, "Description"); Text(w, "Firmware"); Text(w, "SERIAL"); Text(w, "USB location");
        w.Write((ushort)2); w.Write(0); Mode(w, true); Mode(w, false);
        w.Write((ushort)1); Text(w, "Zone"); w.Write(2u); w.Write(2u); w.Write(8u); w.Write((uint)count);
        w.Write((ushort)(8 + count * 4)); w.Write(1u); w.Write((uint)count);
        for (var i = 0; i < count; i++) w.Write(i == 0 && matrixIndex is { } index ? index : (uint)i);
        w.Write((ushort)1); Text(w, "Segment"); w.Write(1u); w.Write(1u); w.Write((uint)(count - 1));
        w.Write((ushort)12); w.Write(1u); w.Write(1u); w.Write(0u); w.Write(0u);
        w.Write(0u); w.Write(-1); w.Write((ushort)2); Mode(w, true); Mode(w, false); Text(w, "Zone label");
        w.Write((ushort)count); for (var i = 0; i < count; i++) Text(w, $"LED {i}");
        w.Write((ushort)count); for (var i = 0; i < count; i++) w.Write(0x00332211u);
        w.Write((ushort)1); Text(w, "First LED"); w.Write(0u); Text(w, "Device label"); Text(w, "{}", true);
    });
    public static byte[] SignalColors(params uint[] colors) => Sized(w => { w.Write(0u); w.Write((ushort)colors.Length); foreach (var color in colors) w.Write(color); });
    public static byte[] SignalController(uint reason, byte[] description) => Sized(w => { w.Write(reason); w.Write(description, 4, description.Length - 4); });
}
