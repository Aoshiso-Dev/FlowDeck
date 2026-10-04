using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FlowDeck.Infrastructure;

/// <summary>Service-owned helpers cannot retain device handles after the service process dies.</summary>
public sealed class OwnedProcessJob : IDisposable
{
    private readonly SafeFileHandle job;
    public OwnedProcessJob(Process child) : this(child.SafeHandle) { }
    internal OwnedProcessJob(SafeProcessHandle child) : this()
    {
        if (!AssignProcessToJobObject(job, child))
        { var error = Marshal.GetLastWin32Error(); job.Dispose(); throw new Win32Exception(error, "補助プロセスの終了管理を設定できません"); }
    }
    internal static OwnedProcessJob CreateUnassigned() => new();
    internal SafeFileHandle Handle => job;
    private OwnedProcessJob()
    {
        job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) { job.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "補助プロセスのJobを作成できません"); }
        try
        {
            var limits = new ExtendedLimits { Basic = new() { Flags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "補助プロセスの終了管理を設定できません");
        }
        catch { job.Dispose(); throw; }
    }
    public void Dispose() => job.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits limits, int length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
