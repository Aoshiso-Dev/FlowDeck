using System.Diagnostics;
using System.Security;
using FlowDeck.Core;
using Microsoft.Win32;

namespace FlowDeck.Infrastructure;

/// <summary>Reads installation metadata without loading PawnIO or opening its device. One bounded observation per process.</summary>
public sealed class WindowsDeviceDiagnosticSource : IDeviceDiagnosticSystemSource
{
    public static WindowsDeviceDiagnosticSource Shared { get; } = new(() => [new PawnIoDiagnosticReader(new WindowsDiagnosticMetadata()).Read()]);
    private readonly object gate = new();
    private readonly Func<DeviceDiagnosticKernelDriver[]> read;
    private readonly TimeSpan timeout;
    private Task<DeviceDiagnosticKernelDriver[]>? pending;
    internal WindowsDeviceDiagnosticSource(Func<DeviceDiagnosticKernelDriver[]> read, TimeSpan? timeout = null)
    { this.read = read; this.timeout = timeout ?? TimeSpan.FromSeconds(2); }

    public async Task<DeviceDiagnosticKernelDriver[]> ReadKernelDriversAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Task<DeviceDiagnosticKernelDriver[]> work;
        lock (gate) work = pending ??= Task.Run(() =>
        {
            try { return read(); }
            catch (Exception e) { return new[] { Unavailable(PawnIoDiagnosticReader.Failure(e)) }; }
        });
        try { return await work.WaitAsync(timeout, ct).ConfigureAwait(false); }
        catch (TimeoutException) { return [Unavailable(DeviceDiagnosticReadState.TimedOut)]; }
        finally { lock (gate) if (work.IsCompleted && ReferenceEquals(pending, work)) pending = null; }
    }
    private static DeviceDiagnosticKernelDriver Unavailable(DeviceDiagnosticReadState state) =>
        new("PawnIO", state, state, null, state, null, null);
}

internal sealed record DiagnosticDriverRegistration(int? Type, string? ImagePath);
internal sealed record DiagnosticPackageRegistration(string? Version);
internal interface IWindowsDiagnosticMetadata
{
    string WindowsDirectory { get; }
    DiagnosticDriverRegistration? ReadDriver();
    DiagnosticPackageRegistration? ReadPackage();
    string? ReadFileVersion(string path);
}

internal sealed class PawnIoDiagnosticReader(IWindowsDiagnosticMetadata metadata)
{
    internal static DeviceDiagnosticReadState Failure(Exception error) => error switch
    {
        UnauthorizedAccessException or SecurityException => DeviceDiagnosticReadState.AccessDenied,
        FileNotFoundException or DirectoryNotFoundException => DeviceDiagnosticReadState.Missing,
        PlatformNotSupportedException => DeviceDiagnosticReadState.Unsupported,
        ArgumentException or InvalidDataException => DeviceDiagnosticReadState.Invalid,
        _ => DeviceDiagnosticReadState.Failed
    };
    public DeviceDiagnosticKernelDriver Read()
    {
        var registration = DeviceDiagnosticReadState.NotObserved; var file = DeviceDiagnosticReadState.NotObserved;
        var package = DeviceDiagnosticReadState.NotObserved; string? fileVersion = null, packageVersion = null;
        try
        {
            var driver = metadata.ReadDriver();
            registration = driver is null ? DeviceDiagnosticReadState.NotInstalled : driver.Type == 1 ?
                DeviceDiagnosticReadState.Available : DeviceDiagnosticReadState.Invalid;
            if (registration == DeviceDiagnosticReadState.Available)
            {
                try
                {
                    var path = ResolveImagePath(driver!.ImagePath, metadata.WindowsDirectory);
                    if (path is null) file = DeviceDiagnosticReadState.Invalid;
                    else
                    {
                        fileVersion = DeviceDiagnostics.NumericVersion(metadata.ReadFileVersion(path));
                        file = fileVersion is null ? DeviceDiagnosticReadState.Invalid : DeviceDiagnosticReadState.Available;
                    }
                }
                catch (Exception e) { file = Failure(e); }
            }
        }
        catch (Exception e) { registration = Failure(e); }
        try
        {
            var value = metadata.ReadPackage(); packageVersion = DeviceDiagnostics.NumericVersion(value?.Version);
            package = value is null ? DeviceDiagnosticReadState.NotInstalled : packageVersion is null ?
                DeviceDiagnosticReadState.Invalid : DeviceDiagnosticReadState.Available;
        }
        catch (Exception e) { package = Failure(e); }
        return new("PawnIO", registration, file, fileVersion, package, packageVersion, DateTimeOffset.UtcNow);
    }
    internal static string? ResolveImagePath(string? raw, string windows)
    {
        if (raw is not { Length: > 0 and <= 2048 } || raw.Any(char.IsControl)) return null;
        var path = raw.Trim().Replace('/', '\\');
        if (path.StartsWith('"') && path.EndsWith('"')) path = path[1..^1];
        if (path.Contains('"')) return null;
        foreach (var prefix in new[] { @"\SystemRoot\", @"%SystemRoot%\", @"%windir%\" })
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { path = Path.Combine(windows, path[prefix.Length..]); break; }
        if (path.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase)) path = Path.Combine(windows, path);
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
        // Never contact a UNC server, resolve arbitrary environment variables, or interpret command-line arguments.
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            path.AsSpan(2).Contains(':') || path.IndexOfAny(['%', '*', '?', '|', '<', '>']) >= 0) return null;
        path = Path.GetFullPath(path);
        return Path.GetFileName(path).Equals("PawnIO.sys", StringComparison.OrdinalIgnoreCase) ? path : null;
    }
}

internal sealed class WindowsDiagnosticMetadata : IWindowsDiagnosticMetadata
{
    public string WindowsDirectory => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static RegistryKey? Open(string path)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        return root.OpenSubKey(path, writable: false);
    }
    public DiagnosticDriverRegistration? ReadDriver()
    {
        using var key = Open(@"SYSTEM\CurrentControlSet\Services\PawnIO");
        return key is null ? null : new(key.GetValue("Type") as int?,
            key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
    }
    public DiagnosticPackageRegistration? ReadPackage()
    {
        using var key = Open(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
        return key is null ? null : new(key.GetValue("DisplayVersion", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
    }
    public string? ReadFileVersion(string path)
    {
        var root = Path.GetPathRoot(path)!;
        if (new DriveInfo(root).DriveType != DriveType.Fixed) throw new InvalidDataException("Not a local fixed volume");
        // Check ancestors from the root before following any child. Reparse points could lead to a remote target.
        var current = root;
        foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse point");
        }
        var info = FileVersionInfo.GetVersionInfo(path);
        return info.FileVersion is null ? null : new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart).ToString();
    }
}
