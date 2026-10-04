using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class M5WifiTests
{
    private static string Hello(string device = "panel") => JsonSerializer.Serialize(new
    { v = 1, type = "hello", deviceType = "M5GO", deviceId = device, bootId = "boot", firmwareVersion = "1.1.0" });
    [Fact] public async Task UsbPreemptsWifiAndLateWifiCommandCannotRun()
    {
        await using var f = new Fixture(); await f.Initialize();
        var r = new M5TransportRouter(f.Core, new(DeviceId: "panel", Transport: "auto"), f.Clock);
        r.Receive("wifi", Hello()); var old = r.Protocol.SessionId;
        r.Receive("usb", Hello()); Assert.Equal("usb", r.ActiveRoute); Assert.NotEqual(old, r.Protocol.SessionId);
        r.Receive("wifi", JsonSerializer.Serialize(new { v = 1, type = "command", sessionId = old, controllerEpoch = f.Core.Epoch,
            id = "old", commandSequence = 1, expectedRevision = f.Core.Snapshot.ControlRevision, command = "SetManualDuty", payload = new { duty = 60 } }));
        Assert.Equal(0, f.Hub.Writes);
        r.Receive("wifi", Hello()); Assert.Equal("usb", r.ActiveRoute);
        r.Disconnect("usb"); r.Receive("wifi", Hello()); Assert.Equal("wifi", r.ActiveRoute); Assert.NotEqual(old, r.Protocol.SessionId);
        Assert.Equal(0, f.Hub.Writes);
    }
    [Fact] public async Task WrongDeviceAndMalformedHelloCannotDisplaceCurrentConnection()
    {
        await using var f = new Fixture(); await f.Initialize();
        var r = new M5TransportRouter(f.Core, new(DeviceId: "panel", Transport: "auto"), f.Clock);
        r.Receive("wifi", Hello()); var session = r.Protocol.SessionId;
        r.Receive("usb", Hello("stranger")); r.Receive("usb", "{}");
        r.Receive("usb", Hello().Replace("boot\"", " \""));
        r.Receive("usb", Hello().Replace("\"v\":1", "\"v\":9999999999999999"));
        Assert.Equal("wifi", r.ActiveRoute); Assert.Equal(session, r.Protocol.SessionId);
        f.Clock.Advance(3); r.Receive("usb", Hello()); Assert.Equal("usb", r.ActiveRoute);
    }
    [Theory][InlineData("wifi", "usb")][InlineData("usb", "wifi")]
    public async Task DisabledTransportCannotReceiveCommands(string mode, string excluded)
    {
        await using var f = new Fixture(); await f.Initialize();
        var r = new M5TransportRouter(f.Core, new(DeviceId: "panel", Transport: mode), f.Clock);
        r.Receive(excluded, Hello()); Assert.False(r.Protocol.Connected); Assert.Null(r.ActiveRoute);
    }
    [Fact] public async Task WifiLossPreservesPcFanControl()
    {
        await using var f = new Fixture(); await f.Initialize(); await f.Manual(54);
        var r = new M5TransportRouter(f.Core, new(DeviceId: "panel", Transport: "auto"), f.Clock);
        r.Receive("wifi", Hello()); r.Disconnect("wifi");
        Assert.True(f.Core.Snapshot.Managing); Assert.Equal(54, f.Core.Snapshot.ConfirmedDuty);
        Assert.Equal(1, f.Hub.Writes);
    }
    [Fact] public async Task SilentUsbExpiresBeforeWifiTakesOwnership()
    {
        await using var f = new Fixture(); await f.Initialize();
        var r = new M5TransportRouter(f.Core, new(DeviceId: "panel", Transport: "auto"), f.Clock);
        r.Receive("usb", Hello()); var usb = r.Protocol.SessionId;
        f.Clock.Advance(2); r.Receive("wifi", Hello()); Assert.Equal("usb", r.ActiveRoute);
        f.Clock.Advance(1); r.Receive("wifi", Hello()); Assert.Equal("wifi", r.ActiveRoute);
        Assert.NotEqual(usb, r.Protocol.SessionId); Assert.Equal(0, f.Hub.Writes);
    }
    [Fact] public void PairingSecretsRoundTripInProtectedFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nzxt-wifi-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var id = M5WifiIdentity.Create("panel"); var store = new M5WifiIdentityStore(directory); store.Save(id);
            Assert.Equal(id, store.Load());
            var file = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "m5-wifi.protected")));
            Assert.DoesNotContain(id.Token, file); Assert.DoesNotContain(id.Pfx, file);
            Assert.True(id.MatchesToken(id.Token)); Assert.False(id.MatchesToken(new string('0', 64)));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Theory][InlineData("10.0.0.2", true)][InlineData("172.16.1.2", true)][InlineData("192.168.1.2", true)]
    [InlineData("100.102.1.2", false)][InlineData("8.8.8.8", false)][InlineData("127.0.0.1", false)]
    public void OnlyPrivateLanAddressesAreOffered(string address, bool allowed) => Assert.Equal(allowed, M5Network.PrivateAddress(IPAddress.Parse(address)));

    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return port; }
    private static async Task Wait(Func<bool> condition)
    { for (var n = 0; n < 100; n++) { if (condition()) return; await Task.Delay(20); } throw new TimeoutException(); }
    private static async Task<SslStream> Connect(int port, M5WifiIdentity id, TcpClient tcp, bool trust = true)
    {
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        using var expected = id.Certificate(); var expectedBytes = expected.RawData;
        var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => trust && cert is not null && cert.GetRawCertData().SequenceEqual(expectedBytes));
        try { await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = id.ServerName, EnabledSslProtocols = SslProtocols.Tls12 }, CancellationToken.None); }
        catch { ssl.Dispose(); throw; }
        return ssl;
    }
    private static Task Send(Stream stream, object value) => stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n")).AsTask();
    private static async Task<JsonElement> ReadType(StreamReader reader, string type)
    {
        for (var i = 0; i < 12; ++i)
        {
            var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(4)); Assert.NotNull(line);
            using var d = JsonDocument.Parse(line); if (d.RootElement.GetProperty("type").GetString() == type) return d.RootElement.Clone();
        }
        throw new InvalidDataException(type);
    }
    [Fact] public async Task ActualTlsSessionRequiresPairingAndCanConfirmFanCommand()
    {
        await using var f = new Fixture(); await f.Initialize();
        var id = M5WifiIdentity.Create("panel"); var r = new M5TransportRouter(f.Core, new(DeviceId: "panel", Transport: "wifi"), f.Clock);
        var port = FreePort(); await using var server = new M5WifiServer(r, id, new("test", "127.0.0.1", "255.0.0.0", "test"), port, new TestLog());
        server.Start(false); await Wait(() => server.Listening);
        using var tcp = new TcpClient();
        SslStream sslConnection;
        try { sslConnection = await Connect(port, id, tcp); }
        catch (Exception e) { throw new InvalidOperationException("Server: " + server.Error, e); }
        using var ssl = sslConnection; using var reader = new StreamReader(ssl, leaveOpen: true);
        Assert.False(r.Protocol.Connected);
        await Send(ssl, new { v = 1, type = "pairAuth", deviceId = "panel", token = id.Token });
        await ReadType(reader, "pairAccepted");
        await ssl.WriteAsync(Encoding.UTF8.GetBytes(Hello() + "\n"));
        var welcome = await ReadType(reader, "welcome"); var state = await ReadType(reader, "state");
        Assert.True(server.Authenticated); Assert.False(r.Protocol.Synchronized);
        var session = welcome.GetProperty("sessionId").GetString();
        await Send(ssl, new { v = 1, type = "stateApplied", sessionId = session, controllerEpoch = f.Core.Epoch,
            snapshotSequence = state.GetProperty("snapshotSequence").GetInt64(), controlRevision = state.GetProperty("controlRevision").GetInt64(), lightingRevision = state.GetProperty("lightingRevision").GetInt64() });
        await Wait(() => r.Protocol.Synchronized);
        await Send(ssl, new { v = 1, type = "command", sessionId = session, controllerEpoch = f.Core.Epoch,
            id = "wifi-command", commandSequence = 1, expectedRevision = f.Core.Snapshot.ControlRevision, command = "SetManualDuty", payload = new { duty = 54 } });
        var result = await ReadType(reader, "result"); Assert.Equal("confirmed", result.GetProperty("outcome").GetString());
        Assert.Equal(54, f.Core.Snapshot.ConfirmedDuty); Assert.Equal(1, f.Hub.Writes);
    }
    [Theory][InlineData("panel", false)][InlineData("other", true)]
    public async Task WrongTokenOrDeviceNeverReachesProtocol(string device, bool correctToken)
    {
        await using var f = new Fixture(); await f.Initialize();
        var id = M5WifiIdentity.Create("panel"); var r = new M5TransportRouter(f.Core, new(DeviceId: "panel", Transport: "wifi"));
        var port = FreePort(); var log = new TestLog(); await using var server = new M5WifiServer(r, id, new("test", "127.0.0.1", "255.0.0.0", "test"), port, log);
        server.Start(false); await Wait(() => server.Listening);
        using var tcp = new TcpClient(); using var ssl = await Connect(port, id, tcp);
        await Send(ssl, new { v = 1, type = "pairAuth", deviceId = device, token = correctToken ? id.Token : new string('0', 64) });
        var bytes = new byte[512]; var count = await ssl.ReadAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, count); Assert.False(r.Protocol.Connected); Assert.Equal(0, f.Hub.Writes);
        Assert.DoesNotContain(id.Token, JsonSerializer.Serialize(log.Entries));
    }
    [Fact] public async Task UntrustedTlsCertificateIsRejected()
    {
        await using var f = new Fixture(); await f.Initialize(); var id = M5WifiIdentity.Create("panel");
        var port = FreePort(); var r = new M5TransportRouter(f.Core, new(DeviceId: "panel", Transport: "wifi"));
        await using var server = new M5WifiServer(r, id, new("test", "127.0.0.1", "255.0.0.0", "test"), port, new TestLog());
        server.Start(false); await Wait(() => server.Listening); using var tcp = new TcpClient();
        await Assert.ThrowsAsync<AuthenticationException>(() => Connect(port, id, tcp, false)); Assert.False(r.Protocol.Connected);
    }
    [Fact] public async Task RepeatedStartAndConcurrentDisposeReleaseTheActualLoopbackListener()
    {
        await using var f = new Fixture(); await f.Initialize();
        var id = M5WifiIdentity.Create("panel"); var router = new M5TransportRouter(f.Core, new(DeviceId: "panel", Transport: "wifi"));
        var port = FreePort(); await using var server = new M5WifiServer(router, id, new("test", "127.0.0.1", "255.0.0.0", "test"), port, new TestLog());
        server.Start(false); server.Start(false); await Wait(() => server.Listening);
        var first = server.DisposeAsync().AsTask(); var second = server.DisposeAsync().AsTask();
        Assert.Same(first, second); await first.WaitAsync(TimeSpan.FromSeconds(3)); await server.DisposeAsync();
        Assert.False(server.Listening); Assert.False(server.Authenticated);
        Assert.Throws<ObjectDisposedException>(() => server.Start(false));
        var probe = new TcpListener(IPAddress.Loopback, port);
        try { probe.Start(); } finally { probe.Stop(); }
        Assert.Equal(0, f.Hub.Writes);
    }
    [Fact] public async Task DiscoveryReturnsOnlyMatchingServerAndNonceWithoutSecrets()
    {
        await using var f = new Fixture(); await f.Initialize(); var id = M5WifiIdentity.Create("panel");
        int discoveryPort;
        using (var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) discoveryPort = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        var port = FreePort(); var r = new M5TransportRouter(f.Core, new(DeviceId: "panel", Transport: "wifi"));
        await using var server = new M5WifiServer(r, id, new("test", "127.0.0.1", "255.0.0.0", "test"), port, new TestLog(), discoveryPort);
        server.Start(); await Wait(() => server.Listening); await Task.Delay(50);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var request = JsonSerializer.SerializeToUtf8Bytes(new { v = 1, type = "discover", serverId = id.ServerId, nonce = "nonce" });
        await udp.SendAsync(request, new IPEndPoint(IPAddress.Loopback, discoveryPort));
        var reply = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2)); var json = Encoding.UTF8.GetString(reply.Buffer);
        using var doc = JsonDocument.Parse(json); Assert.Equal("nonce", doc.RootElement.GetProperty("nonce").GetString());
        Assert.Equal(id.ServerId, doc.RootElement.GetProperty("serverId").GetString()); Assert.Equal(port, doc.RootElement.GetProperty("port").GetInt32());
        Assert.DoesNotContain(id.Token, json); Assert.DoesNotContain(id.Pfx, json); Assert.False(r.Protocol.Connected);
    }
}
