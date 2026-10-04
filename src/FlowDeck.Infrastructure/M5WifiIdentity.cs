using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record M5WifiIdentity(string ServerId, string DeviceId, string Token, string Pfx)
{
    public string ServerName => "nzxt-" + ServerId;
    public X509Certificate2 Certificate(bool forTlsServer = false) => X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(Pfx), null,
        forTlsServer ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
    public static M5WifiIdentity Create(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 64) throw new ArgumentException("M5GOの機器IDを確認してください");
        var id = Guid.NewGuid().ToString("N");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=nzxt-" + id, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign, true));
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("nzxt-" + id);
        request.CertificateExtensions.Add(names.Build());
        // A private USB-paired trust anchor, valid even before an ESP32 has a wall clock.
        // It is not installed into the OS trust store. Re-pairing replaces the anchor and token.
        using var cert = request.CreateSelfSigned(DateTimeOffset.FromUnixTimeSeconds(0), new DateTimeOffset(2099, 12, 31, 0, 0, 0, TimeSpan.Zero));
        return new(id, deviceId, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), Convert.ToBase64String(cert.Export(X509ContentType.Pfx)));
    }
    public bool MatchesToken(string candidate)
    {
        if (candidate.Length != 64) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Token), Encoding.ASCII.GetBytes(candidate));
    }
}

public sealed class M5WifiIdentityStore(string directory)
{
    private string FilePath => Path.Combine(directory, "m5-wifi.protected");
    public M5WifiIdentity? Load()
    {
        if (!File.Exists(FilePath)) return null;
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<M5WifiIdentity>(bytes, Contract.Json) ?? throw new InvalidDataException("Wi-Fi登録情報が不正です"); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Save(M5WifiIdentity identity)
    {
        Directory.CreateDirectory(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(identity, Contract.Json);
        try
        {
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            var temporary = FilePath + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(protectedBytes); stream.Flush(true); }
            File.Move(temporary, FilePath, true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
