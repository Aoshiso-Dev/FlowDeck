using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed class M5WifiServer : IAsyncDisposable
{
    private readonly M5TransportRouter router;
    private readonly M5WifiIdentity identity;
    private readonly M5LanAddress lan;
    private readonly int port, discoveryPort;
    private readonly IEventLog log;
    private readonly CancellationTokenSource stop = new();
    private Task? listenerTask, discoveryTask;
    private readonly object lifecycle = new();
    private readonly M5Shutdown shutdown = new();
    private bool resourcesClosed;
    private Channel<string>? outgoing;
    public string? Error { get; private set; }
    public bool Listening { get; private set; }
    public bool Authenticated { get; private set; }
    public string? RemoteAddress { get; private set; }
    public event Action<JsonElement>? StatusReceived;
    public M5WifiServer(M5TransportRouter router, M5WifiIdentity identity, M5LanAddress lan, int port, IEventLog log, int discoveryPort = M5Network.DiscoveryPort)
    { this.router = router; this.identity = identity; this.lan = lan; this.port = port; this.log = log; this.discoveryPort = discoveryPort; }
    public void Start(bool discovery = true)
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(shutdown.Requested, this);
            if (listenerTask is not null) return;
            router.Outgoing += Send;
            listenerTask = Task.Run(ListenAsync);
            if (discovery) discoveryTask = Task.Run(DiscoverAsync);
        }
    }
    private void Send(string route, string frame) { if (route == "wifi") outgoing?.Writer.TryWrite(frame); }
    private async Task ListenAsync()
    {
        // Windows Schannel requires a user key container; Dispose removes the temporary import.
        using var cert = identity.Certificate(forTlsServer: true);
        while (!stop.IsCancellationRequested)
        {
            var listener = new TcpListener(IPAddress.Parse(lan.Address), port);
            try
            {
                listener.Start(2); Listening = true; Error = null;
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    if (client.Client.RemoteEndPoint is not IPEndPoint remote || !lan.Contains(remote.Address)) continue;
                    using var connection = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    Task? reader = null, writer = null;
                    try
                    {
                        client.NoDelay = true;
                        using var tls = new SslStream(client.GetStream(), false);
                        connection.CancelAfter(TimeSpan.FromSeconds(8));
                        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        { ServerCertificate = cert, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                          ClientCertificateRequired = false }, connection.Token);
                        var queue = Channel.CreateBounded<string>(32);
                        reader = ReadAsync(tls, queue, connection.Token);
                        var authFrame = await queue.Reader.ReadAsync(connection.Token);
                        using var authDocument = JsonDocument.Parse(authFrame); var auth = authDocument.RootElement;
                        if (auth.GetProperty("v").GetInt32() != 1 || auth.GetProperty("type").GetString() != "pairAuth" ||
                            auth.GetProperty("deviceId").GetString() != identity.DeviceId || !identity.MatchesToken(auth.GetProperty("token").GetString() ?? ""))
                            throw new AuthenticationException("未登録の端末です");
                        await tls.WriteAsync(Encoding.UTF8.GetBytes("{\"v\":1,\"type\":\"pairAccepted\"}\n"), connection.Token);
                        connection.CancelAfter(Timeout.InfiniteTimeSpan);
                        Authenticated = true; Error = null; RemoteAddress = remote.Address.ToString();
                        outgoing = Channel.CreateBounded<string>(32);
                        writer = WriteAsync(tls, outgoing.Reader, connection.Token);
                        log.Write("m5wifi", new { status = "authenticated", address = RemoteAddress, identity.DeviceId });
                        string? lastSynced = null; int? freeHeap = null;
                        while (!connection.IsCancellationRequested)
                        {
                            var frame = await queue.Reader.ReadAsync(connection.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(7), connection.Token);
                            using var document = JsonDocument.Parse(frame);
                            StatusReceived?.Invoke(document.RootElement.Clone());
                            router.Receive("wifi", frame);
                            var root = document.RootElement;
                            if (root.TryGetProperty("link", out var link) && link.TryGetProperty("freeHeap", out var heap) && heap.TryGetInt32(out var heapBytes)) freeHeap = heapBytes;
                            if (root.TryGetProperty("type", out var kind) && kind.GetString() == "command")
                                log.Write("m5wifiRequest", new { id = root.GetProperty("id").GetString(), session = root.GetProperty("sessionId").GetString(), command = root.GetProperty("command").GetString() });
                            if (kind.GetString() == "stateApplied" && router.ActiveRoute == "wifi" && router.Protocol.Synchronized)
                            {
                                var synced = $"{router.Protocol.SessionId}/{root.GetProperty("controlRevision")}/{root.GetProperty("lightingRevision")}";
                                if (synced != lastSynced) { log.Write("m5wifiStateApplied", new { session = router.Protocol.SessionId, freeHeap, address = RemoteAddress }); lastSynced = synced; }
                            }
                            if (writer.IsCompleted) await writer;
                        }
                    }
                    catch (Exception e) when (e is IOException or SocketException or AuthenticationException or OperationCanceledException or
                        JsonException or InvalidOperationException or KeyNotFoundException or TimeoutException or ChannelClosedException)
                    { if (!stop.IsCancellationRequested) { Error = "Wi-Fi接続待ち（" + e.GetType().Name + "）" + (e is AuthenticationException ? ": " + e.Message + " / " + e.InnerException?.Message : ""); log.Write("m5wifi", new { status = "disconnected", error = e.GetType().Name, code = e.HResult }); } }
                    finally
                    {
                        connection.Cancel(); outgoing = null; Authenticated = false; RemoteAddress = null; router.Disconnect("wifi");
                        if (reader is not null) try { await reader; } catch (Exception) { }
                        if (writer is not null) try { await writer; } catch (Exception) { }
                    }
                }
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            { if (!stop.IsCancellationRequested) Error = "Wi-Fi待受を開始できません: " + e.Message; }
            finally { Listening = false; listener.Stop(); }
            try { await Task.Delay(2000, stop.Token); } catch (OperationCanceledException) { }
        }
    }
    private static async Task ReadAsync(Stream stream, Channel<string> frames, CancellationToken ct)
    {
        try
        {
            var framer = new JsonLineFramer(4096); var bytes = new byte[1024];
            while (true)
            {
                var count = await stream.ReadAsync(bytes, ct); if (count == 0) break;
                foreach (var frame in framer.Feed(bytes, count)) await frames.Writer.WriteAsync(frame, ct);
            }
            frames.Writer.TryComplete();
        }
        catch (Exception e) { frames.Writer.TryComplete(e); }
    }
    private static async Task WriteAsync(Stream stream, ChannelReader<string> frames, CancellationToken ct)
    { await foreach (var frame in frames.ReadAllAsync(ct)) await stream.WriteAsync(Encoding.UTF8.GetBytes(frame + "\n"), ct); }
    private async Task DiscoverAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, discoveryPort));
                while (!stop.IsCancellationRequested)
                {
                    var packet = await udp.ReceiveAsync(stop.Token);
                    if (packet.Buffer.Length > 512 || !lan.Contains(packet.RemoteEndPoint.Address)) continue;
                    try
                    {
                        using var document = JsonDocument.Parse(packet.Buffer); var r = document.RootElement;
                        if (r.GetProperty("v").GetInt32() != 1 || r.GetProperty("type").GetString() != "discover" ||
                            r.GetProperty("serverId").GetString() != identity.ServerId || r.GetProperty("nonce").GetString() is not { Length: > 0 and <= 64 } nonce) continue;
                        var response = JsonSerializer.SerializeToUtf8Bytes(new { v = 1, type = "server", serverId = identity.ServerId, nonce, port });
                        await udp.SendAsync(response, packet.RemoteEndPoint, stop.Token);
                    }
                    catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { }
                }
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            { if (!stop.IsCancellationRequested) Error = "PC自動検出を開始できません: " + e.Message; }
            try { await Task.Delay(2000, stop.Token); } catch (OperationCanceledException) { }
        }
    }
    public ValueTask DisposeAsync()
    { lock (lifecycle) return new(shutdown.Run(ShutdownAsync)); }
    private async Task ShutdownAsync()
    {
        if (resourcesClosed) return;
        router.Outgoing -= Send; stop.Cancel();
        // A listener failure must not skip waiting for discovery to release its socket.
        try { await Task.WhenAll(listenerTask ?? Task.CompletedTask, discoveryTask ?? Task.CompletedTask).ConfigureAwait(false); }
        finally { stop.Dispose(); resourcesClosed = true; }
    }
}
