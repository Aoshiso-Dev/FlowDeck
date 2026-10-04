using System.Security.Cryptography;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public static class ServiceSeed
{
    public static (string Sha256, AppSettings Settings) Read(string path, string? expectedHash = null)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("初期設定は絶対パスで指定してください");
        ServiceEnvironment.NoLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > SettingsTransfer.MaximumBytes) throw new IOException("初期設定ファイルが大きすぎます");
        using var memory = new MemoryStream(); file.CopyTo(memory); var bytes = memory.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (expectedHash is not null && hash != expectedHash) throw new IOException("確認した初期設定の内容と異なります");
        return (hash, SettingsTransfer.Read(bytes).Content.Settings);
    }
}
