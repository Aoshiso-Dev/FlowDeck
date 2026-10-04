using System.Management;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public record ServiceInstallation(int Version, string[] AllowedUsers);

public static class ServiceEnvironment
{
    public static string InstallationDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FlowDeck", "Service");
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FlowDeck", "Service");
    private static readonly string[] TrustedWriters = ["S-1-5-18", "S-1-5-32-544",
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"]; // SYSTEM, Administrators, TrustedInstaller
    private const FileSystemRights Writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
    public static void ValidateSecurity(FileSystemSecurity security)
    {
        if (new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl is null)
            throw new UnauthorizedAccessException("サービスのアクセス制御がありません");
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !TrustedWriters.Contains(owner.Value))
            throw new UnauthorizedAccessException("サービスのファイル所有者が管理者またはSYSTEMではありません");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                (rule.FileSystemRights & Writes) != 0 && !TrustedWriters.Contains(rule.IdentityReference.Value))
                throw new UnauthorizedAccessException("通常ユーザーがサービスのファイルを変更できる配置です");
    }
    public static void NoLinks(string path)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full) && new FileInfo(full).LinkTarget is not null) throw new IOException("サービスのパスにリンクは使用できません");
        for (var folder = new DirectoryInfo(Directory.Exists(full) ? full : Path.GetDirectoryName(full)!); folder is not null; folder = folder.Parent)
            if (folder.LinkTarget is not null) throw new IOException("サービスのパスにリンクは使用できません");
    }
    public static void ValidateTree(string root)
    {
        NoLinks(root); var folder = new DirectoryInfo(root);
        if (!folder.Exists) throw new DirectoryNotFoundException("サービスの保護された配置がありません: " + root);
        ValidateSecurity(folder.GetAccessControl());
        foreach (var file in folder.EnumerateFiles()) { NoLinks(file.FullName); ValidateSecurity(file.GetAccessControl()); }
        foreach (var child in folder.EnumerateDirectories()) ValidateTree(child.FullName);
    }
    public static ServiceInstallation ValidateInstallation(string executableDirectory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true) throw new UnauthorizedAccessException("実機のサービスはSCMからSYSTEMとして起動してください");
        if (!Path.TrimEndingDirectorySeparator(Path.GetFullPath(executableDirectory)).Equals(InstallationDirectory, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("実機のサービスはProgram Filesの専用配置からのみ実行できます");
        ValidateTree(InstallationDirectory); ValidateTree(DataDirectory);
        // The immediate parents must not permit replacing either protected service directory.
        foreach (var path in new[] { Path.GetDirectoryName(InstallationDirectory)!, Path.GetDirectoryName(DataDirectory)! })
            ValidateSecurity(new DirectoryInfo(path).GetAccessControl());
        var installation = Read<ServiceInstallation>(Path.Combine(DataDirectory, "service.json"));
        if (installation.Version != 1 || installation.AllowedUsers is not { Length: >= 1 and <= 8 } ||
            installation.AllowedUsers.Distinct(StringComparer.Ordinal).Count() != installation.AllowedUsers.Length)
            throw new IOException("サービスの操作ユーザー設定が不正です");
        foreach (var sid in installation.AllowedUsers)
            if (!new SecurityIdentifier(sid).IsAccountSid()) throw new IOException("サービスの操作ユーザーSIDが不正です");
        foreach (var file in new[] { "runtime/python/python.exe", "worker/hub_worker.py" })
            if (!File.Exists(Path.Combine(InstallationDirectory, file))) throw new FileNotFoundException("サービスの依存ファイルがありません", file);
        return installation;
    }
    public static T Read<T>(string path)
    {
        NoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 1024 * 1024) throw new IOException("サービス設定が大きすぎます");
        using var doc = JsonDocument.Parse(stream); SettingsTransfer.NoDuplicates(doc.RootElement);
        return doc.RootElement.Deserialize<T>(Contract.Json) ?? throw new IOException("サービス設定が空です");
    }
    public static void Write<T>(string path, T value)
    {
        NoLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, value, Contract.Json); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string BootIdentity()
    {
        using var query = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\cimv2"),
            new ObjectQuery("SELECT LastBootUpTime FROM Win32_OperatingSystem"), new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10) });
        using var values = query.Get(); var times = new List<string>();
        foreach (ManagementObject value in values)
            using (value) if (value["LastBootUpTime"] is string time && time.Length >= 14)
                times.Add(ManagementDateTimeConverter.ToDateTime(time).ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        return times.Count == 1 ? times[0] : throw new IOException("Windowsの起動識別を取得できません。古いファン設定を適用せず停止します");
    }
}

/// <summary>Mutex ownership stays on one dedicated thread across async start/stop callbacks.</summary>
public sealed class ServiceHardwareLease : IAsyncDisposable
{
    private readonly ManualResetEventSlim release = new();
    private readonly TaskCompletionSource acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;
    private ServiceHardwareLease(string? testPrefix)
    {
        new Thread(() =>
        {
            var held = new List<Mutex>();
            try
            {
                foreach (var name in new[] { @"Global\NzxtControlHub-1E71-2022", @"Global\FlowDeck-1E71-2022" })
                {
                    var mutex = new Mutex(true, testPrefix is null ? name : testPrefix + name.Replace('\\', '-'), out var created);
                    if (!created) { mutex.Dispose(); throw new IOException("FlowDeckまたは旧版が機器を使用しています"); }
                    held.Add(mutex);
                }
                acquired.TrySetResult(); release.Wait();
            }
            catch (Exception e) { acquired.TrySetException(e); }
            finally
            {
                foreach (var mutex in held.AsEnumerable().Reverse()) { mutex.ReleaseMutex(); mutex.Dispose(); }
                finished.TrySetResult();
            }
        }) { IsBackground = true, Name = "FlowDeck hardware ownership" }.Start();
    }
    public static async Task<ServiceHardwareLease> AcquireAsync(string? testPrefix = null)
    {
        var lease = new ServiceHardwareLease(testPrefix);
        try { await lease.acquired.Task.ConfigureAwait(false); return lease; }
        catch { await lease.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { await finished.Task.ConfigureAwait(false); return; }
        release.Set(); await finished.Task.ConfigureAwait(false); release.Dispose();
    }
}
