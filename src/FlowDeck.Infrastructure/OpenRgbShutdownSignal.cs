using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace FlowDeck.Infrastructure;

internal interface IOpenRgbShutdownSignal : IDisposable
{
    void Prepare(ProcessStartInfo start);
    void CheckStartInfo(ProcessStartInfo start);
    void Request();
}

// One private manual-reset event per owned child. The child opens it for SYNCHRONIZE only,
// after authenticating its actual parent. No window messages, console control or public SDK command.
internal sealed class OpenRgbShutdownSignal : IOpenRgbShutdownSignal
{
    internal const string EnvironmentName = "FLOWDECK_SHUTDOWN_V1";
    internal const string Prefix = @"Local\FlowDeck.OpenRgb.Shutdown.";
    private readonly SafeWaitHandle handle;
    private readonly string name = Prefix + Guid.NewGuid().ToString("N");
    private bool prepared, disposed;

    public OpenRgbShutdownSignal()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new UnauthorizedAccessException("補助プロセス終了通知の所有者がありません");
        var sddl = "D:P(A;;0x1F0003;;;SY)(A;;0x1F0003;;;BA)";
        if (sid.IsAccountSid()) sddl += "(A;;0x1F0003;;;" + sid.Value + ")";
        var descriptor = new RawSecurityDescriptor(sddl);
        var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pinned.AddrOfPinnedObject() };
            handle = CreateEvent(ref attributes, true, false, name);
            var error = Marshal.GetLastWin32Error();
            if (handle.IsInvalid || error == 183)
            {
                handle.Dispose();
                throw new Win32Exception(error, "補助プロセスの専用終了通知を作成できません");
            }
        }
        finally { pinned.Free(); }
    }
    public void Prepare(ProcessStartInfo start)
    {
        if (disposed || prepared || start.Environment.ContainsKey(EnvironmentName))
            throw new IOException("OpenRGBの終了通知を再利用・上書きできません");
        start.Environment.Add(EnvironmentName, name); prepared = true;
    }
    public void CheckStartInfo(ProcessStartInfo start)
    {
        if (disposed || !prepared || !start.Environment.TryGetValue(EnvironmentName, out var value) || value != name)
            throw new IOException("OpenRGBの終了通知が起動前に変わりました");
    }
    public void Request()
    {
        if (disposed || !prepared) throw new IOException("OpenRGBの終了通知は利用できません");
        if (!SetEvent(handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenRGBへ終了を通知できません");
    }
    public void Dispose() { if (disposed) return; handle.Dispose(); disposed = true; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
    { public int Length; public IntPtr Descriptor; public int InheritHandle; }
    [DllImport("kernel32.dll", EntryPoint = "CreateEventW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateEvent(ref SecurityAttributes security, [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState, string name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(SafeWaitHandle handle);
}
