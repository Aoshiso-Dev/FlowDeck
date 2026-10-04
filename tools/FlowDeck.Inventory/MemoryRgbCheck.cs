using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FlowDeck.Infrastructure;
using Microsoft.Win32.SafeHandles;

internal static class MemoryRgbCheck
{
    public const string ModuleHash = "91F9B4B1C39E3D399CE48477A89D8F6BD3E58A2241064A4DAEC3A1513DFF56E5";
    public static void Run(string modulePath, string outputPath)
    {
        var output = Path.GetFullPath(outputPath);
        if (File.Exists(output)) throw new IOException("既存の診断記録は上書きしません");
        var started = DateTimeOffset.Now;
        try
        {
            using var user = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("メモリRGBの照会にはWindowsの管理者権限が必要です");
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    var name = process.ProcessName;
                    if (name.Equals("OpenRGB", StringComparison.OrdinalIgnoreCase) || name.Contains("Polychrome", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("ASRRGBLED", StringComparison.OrdinalIgnoreCase) || name.Contains("SignalRGB", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("LightingService", StringComparison.OrdinalIgnoreCase) || name.Contains("Trident", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("他のRGBソフトが起動中です: " + name);
                }
            }
            var module = File.ReadAllBytes(Path.GetFullPath(modulePath));
            if (Convert.ToHexString(SHA256.HashData(module)) != ModuleHash) throw new IOException("診断用の署名済みモジュールが確認済み版と一致しません");
            using var mutex = new Mutex(false, @"Global\Access_SMBUS.HTP.Method");
            var held = false;
            try
            {
                try { held = mutex.WaitOne(1000); } catch (AbandonedMutexException) { held = true; }
                if (!held) throw new TimeoutException("SMBusが使用中です");
                using var bus = new ProbeBus(module);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var modules = EneMemoryRgbProbe.Read(bus, deadline.Token);
                File.WriteAllText(output, JsonSerializer.Serialize(new { StartedAt = started, FinishedAt = DateTimeOffset.Now,
                    Completed = true, Controller = bus.Identity, ModuleSha256 = ModuleHash, Modules = modules,
                    ColorWrites = 0, PersistentWrites = 0, AddressRemaps = 0, SpdAccesses = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { if (held) mutex.ReleaseMutex(); }
        }
        catch (Exception e)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { StartedAt = started, FinishedAt = DateTimeOffset.Now, Completed = false,
                Error = e.Message, ColorWrites = 0, PersistentWrites = 0, AddressRemaps = 0, SpdAccesses = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
    }

    private sealed class ProbeBus : IMemoryRgbProbeBus, IDisposable
    {
        private readonly SafeFileHandle handle;
        private readonly HashSet<byte> signatures = [];
        private readonly Dictionary<byte, byte> progress = [];
        public long[] Identity { get; }
        public ProbeBus(byte[] module)
        {
            handle = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xc0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            try
            {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!DeviceIoControl(handle, 0xa1b22084, module, module.Length, [], 0, out _, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SMBusモジュールを読み込めません");
                Identity = Execute("ioctl_identity", [], 3);
                if (Identity[1] != 0xb00 || (uint)Identity[2] != 0x790b1022)
                    throw new IOException("確認対象のAMD SMBusコントローラーと一致しません");
                // -1 reads the existing port without changing it. Never route to an auxiliary bus.
                if (Execute("ioctl_piix4_port_sel", [-1], 1)[0] != 0)
                    throw new IOException("SMBusがメモリ用ポート0を選択していないため照会を中止しました");
            }
            catch { handle.Dispose(); throw; }
        }
        public byte? ReadSignature(byte address, byte command)
        {
            EneMemoryRgbProbe.CheckSignatureRequest(address, command);
            try
            {
                var value = ReadByte(address, command);
                var previous = progress.GetValueOrDefault(address, (byte)0xa0);
                if (command == previous && value == command - 0xa0)
                {
                    progress[address] = (byte)(command + 1);
                    if (command == 0xaf) signatures.Add(address);
                }
                else { progress.Remove(address); signatures.Remove(address); }
                return value;
            }
            catch (Win32Exception e) when (e.NativeErrorCode is 433 or 1167)
            { progress.Remove(address); signatures.Remove(address); return null; }
        }
        public byte ReadRegister(byte address, ushort register)
        {
            EneMemoryRgbProbe.CheckRegisterRequest(address, register);
            if (!signatures.Contains(address)) throw new IOException("ENEの識別が完了していません");
            // Select the volatile read pointer only. No data-write, apply, save or remapping command exists here.
            Execute("ioctl_smbus_xfer", [address, 0, 0, 3, BinaryPrimitives.ReverseEndianness(register)], 0);
            return ReadByte(address, 0x81);
        }
        private byte ReadByte(byte address, byte command)
        {
            var value = Execute("ioctl_smbus_xfer", [address, 1, command, 2], 1)[0];
            if (value is < 0 or > 255) throw new IOException("SMBusの応答値が不正です");
            return (byte)value;
        }
        private long[] Execute(string name, long[] args, int count)
        {
            var input = new byte[32 + args.Length * 8];
            Encoding.ASCII.GetBytes(name).CopyTo(input, 0);
            for (var i = 0; i < args.Length; i++) BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(32 + i * 8), args[i]);
            var output = new byte[count * 8];
            if (!DeviceIoControl(handle, 0xa1b22104, input, input.Length, output, output.Length, out var bytes, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (bytes != output.Length) throw new IOException("PawnIOの応答サイズが一致しません");
            return Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(i * 8))).ToArray();
        }
        public void Dispose() => handle.Dispose();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize,
            byte[] output, int outputSize, out uint bytesReturned, IntPtr overlapped);
    }
}
