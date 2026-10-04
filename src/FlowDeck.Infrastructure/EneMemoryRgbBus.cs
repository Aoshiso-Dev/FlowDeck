using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FlowDeck.Core;
using Microsoft.Win32.SafeHandles;

namespace FlowDeck.Infrastructure;

internal interface IEneMemoryStreamIo
{
    bool VerifyPreviouslyIdentified(byte address);
    void WritePixel(byte address,int led,byte red,byte blue,byte green);
}
internal sealed class EneMemoryRgbBus : IEneMemoryIo, IEneMemoryStreamIo, IEneMemoryIdentifyingIo, IEneMemoryGuardedIo
{
    private const string ModuleHash = "91F9B4B1C39E3D399CE48477A89D8F6BD3E58A2241064A4DAEC3A1513DFF56E5";
    private readonly SafeFileHandle? handle;
    private readonly EneMemoryBusLifetime lifetime;
    private readonly Stopwatch deadline = Stopwatch.StartNew();
    private readonly HashSet<byte> verified = [];
    private readonly Func<string, long[], int, long[]>? executeOverride;
    internal EneMemoryRgbBus(Func<string, long[], int, long[]> execute, EneMemoryBusLifetime? lifetime = null)
    {
        executeOverride = execute ?? throw new ArgumentNullException(nameof(execute));
        this.lifetime = lifetime ?? new(() => { }, () => { }, () => { });
    }
    public EneMemoryRgbBus()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string name; try { name = process.ProcessName; } catch (InvalidOperationException) { continue; }
                if (name.Equals("OpenRGB", StringComparison.OrdinalIgnoreCase) || name.Contains("Polychrome", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("ASRRGBLED", StringComparison.OrdinalIgnoreCase) || name.Contains("SignalRGB", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("LightingService", StringComparison.OrdinalIgnoreCase) || name.Contains("Trident", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("他のRGBソフトを終了してください: " + name);
            }
        }
        var module = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "native", "SmbusPIIX4.bin"));
        if (Convert.ToHexString(SHA256.HashData(module)) != ModuleHash) throw new IOException("メモリRGB用モジュールが確認済み版と一致しません");
        var mutex = new Mutex(false, @"Global\Access_SMBUS.HTP.Method");
        lifetime = new(() => handle?.Dispose(), mutex.ReleaseMutex, mutex.Dispose);
        try
        {
            bool held;
            try { held = mutex.WaitOne(1000); } catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("SMBusが使用中です。少し待って再取得してください");
            lifetime.AcquiredMutex();
            handle = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xc0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (handle.IsInvalid) throw Failure("メモリRGBには更新済みサービスまたは管理者権限が必要です");
            if (!DeviceIoControl(handle, 0xa1b22084, module, module.Length, [], 0, out _, IntPtr.Zero)) throw Failure("SMBusモジュールを読み込めません");
            var identity = Execute("ioctl_identity", [], 3);
            if (identity[1] != 0xb00 || (uint)identity[2] != 0x790b1022 || Execute("ioctl_piix4_port_sel", [-1], 1)[0] != 0)
                throw new IOException("未対応のSMBus構成です");
        }
        catch (Exception opening)
        {
            try { Dispose(); } catch (Exception closing) { throw new EneMemoryBusOpenException(opening, closing, this); }
            throw;
        }
    }
    public bool Identify(byte address) => IdentifyController(address) is not null;
    public EneMemoryIdentification? IdentifyController(byte address)
    {
        lifetime.CheckAccess();
        _ = MemoryLighting.Identity(address);
        verified.Remove(address);
        for (byte command = 0xa0; command <= 0xaf; command++)
        {
            try { if (ReadByte(address, command) != command - 0xa0) return null; }
            catch (IOException e) when (e.InnerException is Win32Exception { NativeErrorCode: 433 or 1167 }) { return null; }
        }
        var name = Encoding.ASCII.GetString(Enumerable.Range(0, 16).Select(i => ReadSelected(address, (ushort)(0x1000 + i))).ToArray()).TrimEnd('\0', ' ');
        if (!LightingDeviceDefinitionCatalog.BuiltIn.Devices.Any(d => d.Driver == "ene-dram-rgb" && d.Match.Firmware == name)) return null;
        var config = Convert.ToHexString(Enumerable.Range(0, 64).Select(i => ReadSelected(address, (ushort)(0x1c00 + i))).ToArray());
        var identity = new EneMemoryIdentification(name, config);
        if (EneMemoryDefinition.Resolve(identity) is null) return null;
        verified.Add(address); return identity;
    }
    public byte Read(byte address, ushort register) => Read(address, register, () => { });
    public byte Read(byte address, ushort register, Action beforeData)
    {
        Require(address); ValidateRead(register);
        return ReadSelected(address, register, beforeData);
    }
    internal static void ValidateRead(ushort register)
    { if (!(register is >= 0x8020 and <= 0x8023 or >= 0x8160 and <= 0x8177 or >= 0x8100 and <= 0x8117)) throw new ArgumentOutOfRangeException(nameof(register)); }
    public bool VerifyPreviouslyIdentified(byte address)
    {
        lifetime.CheckAccess();
        // Called only within a stream begun by a full firmware/configuration match. DIMMs are not hot-pluggable.
        _ = MemoryLighting.Identity(address);
        if (ReadByte(address,0xa0) != 0 || ReadByte(address,0xaf) != 15 || ReadSelected(address,0x1c02) != 8) return false;
        verified.Add(address); return true;
    }
    internal static long[] PixelTransfer(byte address,byte red,byte blue,byte green)
    {
        _ = MemoryLighting.Identity(address);
        // PawnIO block data is a byte-packed length followed by payload, padded to five 64-bit cells.
        return [address,0,3,5,3L | ((long)red<<8) | ((long)blue<<16) | ((long)green<<24),0,0,0,0];
    }
    public void WritePixel(byte address,int led,byte red,byte blue,byte green) => WritePixel(address, led, red, blue, green, () => { });
    public void WritePixel(byte address,int led,byte red,byte blue,byte green, Action beforeData)
    {
        Require(address); if(led is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(led));
        var register=(ushort)(0x8100+led*3);
        Execute("ioctl_smbus_xfer",[address,0,0,3,BinaryPrimitives.ReverseEndianness(register)],0);
        beforeData();
        Execute("ioctl_smbus_xfer",PixelTransfer(address,red,blue,green),0);
    }
    public void Write(byte address, ushort register, byte value) => Write(address, register, value, () => { });
    public void Write(byte address, ushort register, byte value, Action beforeData)
    {
        Require(address); ValidateWrite(register, value);
        Execute("ioctl_smbus_xfer", [address, 0, 0, 3, BinaryPrimitives.ReverseEndianness(register)], 0);
        beforeData();
        Execute("ioctl_smbus_xfer", [address, 0, 1, 2, value], 0);
    }
    public static void ValidateWrite(ushort register, byte value)
    {
        if (!(register is >= 0x8160 and <= 0x8177 or >= 0x8100 and <= 0x8117 || register == 0x8020 && value <= 1 || register == 0x8021 && value <= 5 ||
            register == 0x8022 && value <= 4 || register == 0x8023 && value == 0 || register == 0x80a0 && value == 1))
            throw new ArgumentOutOfRangeException(nameof(register), "メモリRGBの許可された揮発設定以外は書き込めません");
    }
    private void Require(byte address)
    { lifetime.CheckAccess(); if (!verified.Contains(address)) throw new IOException("対象のメモリRGBを確認できません"); }
    private byte ReadSelected(byte address, ushort register, Action? beforeData = null)
    {
        Execute("ioctl_smbus_xfer", [address, 0, 0, 3, BinaryPrimitives.ReverseEndianness(register)], 0);
        beforeData?.Invoke();
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
        lifetime.CheckAccess();
        if (deadline.Elapsed.TotalSeconds > 4) throw new IOException("メモリRGBの通信を時間制限で中止しました");
        if (executeOverride is not null)
        {
            var result = executeOverride(name, args.ToArray(), count);
            if (result is null || result.Length != count) throw new IOException("SMBusの応答サイズが不正です");
            return result.ToArray();
        }
        var input = new byte[32 + args.Length * 8]; Encoding.ASCII.GetBytes(name).CopyTo(input, 0);
        for (var i = 0; i < args.Length; i++) BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(32 + i * 8), args[i]);
        var output = new byte[count * 8];
        if (!DeviceIoControl(handle!, 0xa1b22104, input, input.Length, output, output.Length, out var bytes, IntPtr.Zero)) throw Failure("メモリRGB通信に失敗しました");
        if (bytes != output.Length) throw new IOException("SMBusの応答サイズが不正です");
        return Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(i * 8))).ToArray();
    }
    private static IOException Failure(string message) => new(message, new Win32Exception(Marshal.GetLastWin32Error()));
    public void Dispose() => lifetime.Dispose();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out uint bytesReturned, IntPtr overlapped);
}

