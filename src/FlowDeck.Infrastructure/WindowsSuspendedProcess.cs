using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FlowDeck.Infrastructure;

// CreateNoWindow and no shell/UAC. Creation is suspended so job ownership and the startup
// configuration check happen before native DLL initialization or a hardware detector can run.
internal sealed class WindowsSuspendedProcess : IOpenRgbSuspendedProcess
{
    private readonly SafeProcessHandle process;
    private readonly SafeWaitHandle thread;
    private readonly OwnedProcessJob job;
    private bool jobAttached;
    public uint ProcessId { get; }
    public bool HasExited => Wait(0);
    public int? ExitCode
    {
        get
        {
            if (!HasExited) return null;
            if (!GetExitCodeProcess(process, out var code))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "補助プロセスの終了コードを確認できません");
            return unchecked((int)code);
        }
    }
    private WindowsSuspendedProcess(ProcessInformation info, OwnedProcessJob job)
    { process = new(info.Process, true); thread = new(info.Thread, true); ProcessId = info.ProcessId; this.job = job; }
    public static IOpenRgbSuspendedProcess Create(ProcessStartInfo start)
    {
        if (start.UseShellExecute || !start.CreateNoWindow || !Path.IsPathFullyQualified(start.FileName) ||
            !Path.IsPathFullyQualified(start.WorkingDirectory) || start.Arguments.Length != 0 ||
            start.RedirectStandardInput || start.RedirectStandardOutput || start.RedirectStandardError || start.Verb.Length != 0)
            throw new ArgumentException("補助プロセスの起動条件が不正です");
        var command = string.Join(' ', new[] { start.FileName }.Concat(start.ArgumentList).Select(Quote));
        var environment = string.Join('\0', start.Environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).Select(e =>
        {
            if (e.Key.Length == 0 || e.Key.Contains('=') || e.Key.Contains('\0') || e.Value is null || e.Value.Contains('\0'))
                throw new ArgumentException("補助プロセスの環境変数が不正です");
            return e.Key + "=" + e.Value;
        })) + "\0\0";
        var nativeEnvironment = Marshal.StringToHGlobalUni(environment);
        var attributes = IntPtr.Zero; var jobList = IntPtr.Zero;
        OwnedProcessJob? job = null; var initialized = false; var transferred = false;
        try
        {
            job = OwnedProcessJob.CreateUnassigned();
            nuint size = 0; _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size is < 1 or > 65536) throw new Win32Exception(Marshal.GetLastWin32Error(), "補助プロセスの起動属性を作成できません");
            attributes = Marshal.AllocHGlobal((int)size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "補助プロセスの起動属性を初期化できません");
            initialized = true;
            jobList = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(jobList, job.Handle.DangerousGetHandle());
            // Assign the kill-on-close job as part of CreateProcess itself. Even a parent failure
            // between creation and Resume cannot leave an unowned suspended child behind.
            if (!UpdateProcThreadAttribute(attributes, 0, 0x0002000D, jobList, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "補助プロセスのJob起動属性を設定できません");
            var info = new StartupInfoEx { Info = new() { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 1, ShowWindow = 0 }, Attributes = attributes };
            if (!CreateProcess(start.FileName, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false,
                0x00000004 | 0x00000400 | 0x08000000 | 0x00080000, nativeEnvironment, start.WorkingDirectory, ref info, out var child))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "補助プロセスを一時停止状態で作成できません");
            var result = new WindowsSuspendedProcess(child, job); transferred = true; return result;
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (jobList != IntPtr.Zero) Marshal.FreeHGlobal(jobList);
            if (!transferred) job?.Dispose();
            Marshal.FreeHGlobal(nativeEnvironment);
        }
    }
    internal static string Quote(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("起動引数が不正です");
        var text = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            text.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); text.Append(c); slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }
    public IDisposable AttachJob()
    {
        if (jobAttached) throw new InvalidOperationException("補助プロセスのJobは既に設定されています");
        jobAttached = true; return job;
    }
    public void Resume()
    {
        if (!jobAttached) throw new IOException("補助プロセスの終了管理が未設定です");
        if (ResumeThread(thread) != 1) throw new Win32Exception(Marshal.GetLastWin32Error(), "補助プロセスを開始できません");
    }
    public Task CloseInputAsync() => Task.CompletedTask;
    public Task<bool> WaitForExitAsync(TimeSpan timeout) => Task.Run(() => Wait((uint)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue)));
    private bool Wait(uint milliseconds) => WaitForSingleObject(process, milliseconds) switch
    {
        0 => true, 258 => false, _ => throw new Win32Exception(Marshal.GetLastWin32Error(), "補助プロセスの終了を確認できません")
    };
    public void Kill()
    {
        if (!HasExited && !TerminateProcess(process, 1) && !HasExited)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "所有している補助プロセスを終了できません");
    }
    public void Dispose() { if (!jobAttached) job.Dispose(); thread.Dispose(); process.Dispose(); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute;
        public uint Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation
    { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder command,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle handle, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle handle, out uint exitCode);
}
