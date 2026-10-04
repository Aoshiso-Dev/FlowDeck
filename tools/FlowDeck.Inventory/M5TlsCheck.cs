using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlowDeck.Infrastructure;

// Read-only TLS interoperability check. Never reads application frames or writes fan/RGB commands.
internal static class M5TlsCheck
{
    public static void CertificateInfo(string directory)
    {
        var identity = new M5WifiIdentityStore(directory).Load() ?? throw new InvalidOperationException("No paired identity");
        using var cert = identity.Certificate();
        var pem = cert.ExportCertificatePem();
        Console.WriteLine(JsonSerializer.Serialize(new { caBytes = Encoding.UTF8.GetByteCount(pem), caSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pem))).ToLowerInvariant() }));
    }
    public static async Task RunAsync(string address, string peer, string directory)
    {
        var identity = new M5WifiIdentityStore(directory).Load() ?? throw new InvalidOperationException("No paired identity");
        using var cert = identity.Certificate(forTlsServer: true);
        CertificateInfo(directory);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var listener = new TcpListener(IPAddress.Parse(address), 47631);
        listener.Start();
        Console.WriteLine("TLS diagnostic listener ready");
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                if (client.Client.RemoteEndPoint is not IPEndPoint endpoint || endpoint.Address.ToString() != peer) continue;
                using var tls = new SslStream(client.GetStream(), false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    { ServerCertificate = cert, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token);
                    Console.WriteLine(JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, success = true, protocol = tls.SslProtocol.ToString(), cipher = tls.NegotiatedCipherSuite.ToString() }));
                    return;
                }
                catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException)
                { Console.WriteLine(JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, success = false, type = e.GetType().Name, e.Message, inner = e.InnerException?.Message })); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { listener.Stop(); }
    }
}
