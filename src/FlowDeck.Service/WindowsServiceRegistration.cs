using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Service;

// Preserve SCM recovery policy and service DACL across a failed update, including user customizations.
internal static class WindowsServiceRegistration
{
    private static T WithService<T>(uint rights, Func<IntPtr, T> action)
    {
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new IOException("SCMを開けません: " + Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(manager, CoolingServiceProtocol.ServiceName, rights);
            if (service == IntPtr.Zero) throw new IOException("サービス設定を開けません: " + Marshal.GetLastWin32Error());
            try { return action(service); } finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    private static T Query<T>(IntPtr service, uint level, Func<IntPtr, T> read)
    {
        QueryServiceConfig2(service, level, IntPtr.Zero, 0, out var needed);
        if (needed == 0 || needed > 65536) throw new IOException("サービスの登録情報サイズが不正です");
        var buffer = Marshal.AllocHGlobal(checked((int)needed));
        try
        {
            if (!QueryServiceConfig2(service, level, buffer, needed, out _)) throw new IOException("サービスの登録情報を取得できません: " + Marshal.GetLastWin32Error());
            return read(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public static ServiceRegistrationSettings Capture() => WithService<ServiceRegistrationSettings>(1 | 0x20000, service =>
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + CoolingServiceProtocol.ServiceName)
            ?? throw new IOException("サービス登録がありません");
        var start = key.GetValue("Start") is int value && value is >= 2 and <= 4 ? value : throw new IOException("未対応のサービス起動方式です");
        var delayed = Query(service, 3, p => Marshal.ReadInt32(p) != 0);
        var nonCrash = Query(service, 4, p => Marshal.ReadInt32(p) != 0);
        var recovery = Query(service, 2, p =>
        {
            var values = Marshal.PtrToStructure<FailureActions>(p);
            if (values.Count > 1024 || values.Count > 0 && values.Actions == IntPtr.Zero) throw new IOException("サービスの障害時処理が不正です");
            var actions = Enumerable.Range(0, (int)values.Count).Select(i =>
            {
                var a = Marshal.PtrToStructure<NativeAction>(values.Actions + i * Marshal.SizeOf<NativeAction>());
                return new ServiceRecoveryAction(a.Type, a.Delay);
            }).ToArray();
            return (values.Reset, Actions: actions, Reboot: Marshal.PtrToStringUni(values.Reboot) ?? "", Command: Marshal.PtrToStringUni(values.Command) ?? "");
        });
        QueryServiceObjectSecurity(service, 4, null, 0, out var length);
        if (length == 0 || length > 65536) throw new IOException("サービス権限のサイズが不正です");
        var bytes = new byte[length];
        if (!QueryServiceObjectSecurity(service, 4, bytes, length, out _)) throw new IOException("サービス権限を取得できません: " + Marshal.GetLastWin32Error());
        var sddl = new RawSecurityDescriptor(bytes, 0).GetSddlForm(AccessControlSections.Access);
        var dependencies = (key.GetValue("DependOnService") as string[] ?? []).Concat((key.GetValue("DependOnGroup") as string[] ?? []).Select(group => "+" + group)).ToArray();
        return new(start, delayed, recovery.Reset, recovery.Actions, recovery.Reboot, recovery.Command, nonCrash, sddl,
            key.GetValue("DisplayName") as string ?? CoolingServiceProtocol.ServiceName, key.GetValue("Description") as string ?? "",
            key.GetValue("ErrorControl") is int error ? error : 1, dependencies);
    });
    public static void Restore(ServiceRegistrationSettings saved) => WithService(2 | 0x10 | 0x40000, service =>
    {
        using var privilege = saved.Actions.Any(a => a.Type == 2) ? new ShutdownPrivilege() : null;
        var reboot = Marshal.StringToHGlobalUni(saved.RebootMessage); var command = Marshal.StringToHGlobalUni(saved.Command);
        var actions = Marshal.AllocHGlobal(Math.Max(1, saved.Actions.Length * Marshal.SizeOf<NativeAction>()));
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<FailureActions>());
        try
        {
            for (var i = 0; i < saved.Actions.Length; i++) Marshal.StructureToPtr(new NativeAction { Type = saved.Actions[i].Type, Delay = saved.Actions[i].Delay }, actions + i * Marshal.SizeOf<NativeAction>(), false);
            Marshal.StructureToPtr(new FailureActions { Reset = saved.ResetPeriod, Reboot = reboot, Command = command, Count = (uint)saved.Actions.Length, Actions = actions }, buffer, false);
            Change(service, 2, buffer); Marshal.WriteInt32(buffer, saved.FailureOnNonCrash ? 1 : 0); Change(service, 4, buffer);
            var security = new RawSecurityDescriptor(saved.SecurityDescriptor); var bytes = new byte[security.BinaryLength]; security.GetBinaryForm(bytes, 0);
            if (!SetServiceObjectSecurity(service, 4, bytes)) throw new IOException("サービス権限の復元に失敗しました: " + Marshal.GetLastWin32Error());
            return true;
        }
        finally { Marshal.FreeHGlobal(buffer); Marshal.FreeHGlobal(actions); Marshal.FreeHGlobal(command); Marshal.FreeHGlobal(reboot); }
    });
    private static void Change(IntPtr service, uint level, IntPtr values)
    { if (!ChangeServiceConfig2(service, level, values)) throw new IOException("サービス障害時設定の復元に失敗しました: " + Marshal.GetLastWin32Error()); }
    private sealed class ShutdownPrivilege : IDisposable
    {
        private readonly WindowsIdentity identity;
        private TokenPrivileges previous;
        public ShutdownPrivilege()
        {
            identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.AdjustPrivileges);
            try
            {
                if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid)) throw new IOException("既存の障害時再起動設定を復元する権限を取得できません");
                var next = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
                if (!AdjustTokenPrivileges(identity.AccessToken, false, ref next, Marshal.SizeOf<TokenPrivileges>(), out previous, out _) || Marshal.GetLastWin32Error() != 0)
                    throw new IOException("既存のWindows再起動設定を復元する権限がありません");
            }
            catch { identity.Dispose(); throw; }
        }
        public void Dispose()
        {
            try { if (!AdjustTokenPrivileges(identity.AccessToken, false, ref previous, Marshal.SizeOf<TokenPrivileges>(), out _, out _)) throw new IOException("一時権限を元へ戻せません"); }
            finally { identity.Dispose(); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeAction { public int Type; public uint Delay; }
    [StructLayout(LayoutKind.Sequential)] private struct FailureActions { public uint Reset; public IntPtr Reboot, Command; public uint Count; public IntPtr Actions; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint rights);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint rights);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfig2(IntPtr service, uint level, IntPtr buffer, uint length, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig2(IntPtr service, uint level, IntPtr values);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceObjectSecurity(IntPtr service, uint information, [Out] byte[]? security, uint length, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetServiceObjectSecurity(IntPtr service, uint information, byte[] security);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges next, int size, out TokenPrivileges previous, out int needed);
}
