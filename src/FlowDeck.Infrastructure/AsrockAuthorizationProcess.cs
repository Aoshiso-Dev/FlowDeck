using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FlowDeck.Infrastructure;

// Retain the kernel process object, not only a reusable PID. Does not start/stop a process.
internal sealed class AsrockAuthorizationProcess : IDisposable
{
    private readonly SafeProcessHandle handle;
    public uint Id { get; }
    public ulong CreatedFileTime { get; }
    public AsrockAuthorizationProcess(uint id, ulong? expectedCreation = null)
    {
        if (id == 0) throw new ArgumentOutOfRangeException(nameof(id));
        handle = OpenProcess(0x00101000, false, id); // SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION
        try
        {
            if (handle.IsInvalid || !GetProcessTimes(handle, out var created, out _, out _, out _) || GetProcessId(handle) != id)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenRGB通信相手のプロセスを確認できません");
            Id = id; CreatedFileTime = created;
            if (created == 0 || expectedCreation is { } expected && created != expected)
                throw new UnauthorizedAccessException("OpenRGB通信相手の起動時刻が一致しません");
            Check();
        }
        catch { handle.Dispose(); throw; }
    }
    public void Check()
    {
        if (handle.IsClosed || WaitForSingleObject(handle, 0) != 258)
            throw new IOException("OpenRGBの認可通信相手は終了しています");
    }
    public void Dispose() => handle.Dispose();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out ulong created, out ulong exited, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
