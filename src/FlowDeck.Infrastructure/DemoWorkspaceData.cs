using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DemoWorkspaceIdentity(int Version, string Installation, string Fixture);

/// <summary>Persistent demonstration data can only be initialized in an empty directory bearing our own marker.</summary>
internal sealed class DemoWorkspaceData(FileStream lease, string installation) : IDisposable
{
    public string Installation { get; } = installation;
    public static DemoWorkspaceData Open(string directory)
    {
        ServiceEnvironment.NoLinks(directory); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "demo-host.json"); var lockPath = Path.Combine(directory, "demo-host.lock");
        ServiceEnvironment.NoLinks(lockPath);
        var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            DemoWorkspaceIdentity identity;
            if (File.Exists(path)) identity = ServiceEnvironment.Read<DemoWorkspaceIdentity>(path);
            else
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any(p => !Path.GetFileName(p).Equals("demo-host.lock", StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("デモには空の専用データフォルダーを指定してください");
                identity = new(1, Guid.NewGuid().ToString("N"), "dual-hub-v1"); ServiceEnvironment.Write(path, identity);
            }
            if (identity is not { Version: 1, Fixture: "dual-hub-v1" } || !DeviceServiceProtocol.Id(identity.Installation))
                throw new IOException("デモ用データの識別情報が不正です");
            return new(lease, identity.Installation);
        }
        catch { lease.Dispose(); throw; }
    }
    public void Dispose() => lease.Dispose();
}
