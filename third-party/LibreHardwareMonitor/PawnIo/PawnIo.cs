using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using PInvoke = Windows.Win32.PInvoke;

namespace LibreHardwareMonitor.PawnIo;

public class PawnIo
{
    private const uint DEVICE_TYPE = 41394u << 16;
    private const int FN_NAME_LENGTH = 32;
    private const uint IOCTL_PIO_EXECUTE_FN = 0x841 << 2;
    private const uint IOCTL_PIO_LOAD_BINARY = 0x821 << 2;

    private readonly SafeFileHandle _handle;
    private readonly Func<SafeFileHandle, byte[], byte[], (bool Success, uint BytesRead, int Error)> _execute;

    static PawnIo()
    {
        using RegistryKey subKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");

        if (Version.TryParse(subKey?.GetValue("DisplayVersion") as string, out Version version))
        {
            Version = version;
        }
        else
        {
            using RegistryKey registryKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            using RegistryKey subKeyWow64 = registryKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");

            if (Version.TryParse(subKeyWow64?.GetValue("DisplayVersion") as string, out version))
            {
                Version = version;
            }
        }
    }

    private PawnIo(SafeFileHandle handle) : this(handle, ExecuteNative) { }

    internal PawnIo(SafeFileHandle handle, Func<SafeFileHandle, byte[], byte[], (bool Success, uint BytesRead, int Error)> execute)
    {
        _handle = handle;
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    /// <summary>
    /// Gets a value indicating whether PawnIO is installed on the system.
    /// </summary>
    public static bool IsInstalled => Version is not null;

    /// <summary>
    /// Retrieves the version information for the installed PawnIO.
    /// </summary>
    public static Version Version { get; }

    /// <summary>
    /// Gets a value indicating whether the underlying handle is currently valid and open.
    /// </summary>
    public bool IsLoaded => _handle is
    {
        IsInvalid: false,
        IsClosed: false
    };

    internal static PawnIo LoadModuleFromResource(Assembly assembly, string resourceName) =>
        LoadModule(OpenDevice, () =>
        {
            using Stream stream = assembly.GetManifestResourceStream(resourceName) ?? throw new IOException("PawnIO module resource is missing.");
            using MemoryStream memory = new();
            stream.CopyTo(memory);
            return memory.ToArray();
        }, LoadBinary);

    // Ownership transfers only after a successful load. Every failed/throwing path disposes the opened handle.
    internal static PawnIo LoadModule(Func<SafeFileHandle> open, Func<byte[]> readModule, Func<SafeFileHandle, byte[], bool> load)
    {
        SafeFileHandle handle = open();
        bool adopted = false;
        try
        {
            if (handle == null || handle.IsInvalid || handle.IsClosed || !load(handle, readModule()))
                return new PawnIo(null);
            var module = new PawnIo(handle);
            adopted = true;
            return module;
        }
        finally { if (!adopted) handle?.Dispose(); }
    }

    private static unsafe SafeFileHandle OpenDevice() => PInvoke.CreateFile(@"\\?\GLOBALROOT\Device\PawnIO",
                                                   (uint)FileAccess.ReadWrite,
                                                   FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
                                                   null,
                                                   FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                                                   FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
                                                   null);

    private static unsafe bool LoadBinary(SafeFileHandle handle, byte[] binary)
    {
        bool referenced = false;
        try
        {
            handle.DangerousAddRef(ref referenced);
            fixed (byte* pIn = binary)
                return PInvoke.DeviceIoControl((HANDLE)handle.DangerousGetHandle(), (uint)ControlCode.LoadBinary, pIn, (uint)binary.Length, null, 0u, null, null);
        }
        finally { if (referenced) handle.DangerousRelease(); }
    }

    public void Close()
    {
        // SafeFileHandle also owns its invalid/unloaded wrapper's lifetime.
        _handle?.Dispose();
    }

    public unsafe long[] Execute(string name, long[] input, int outLength)
    {
        if (IsLoaded)
        {
            byte[] output = new byte[outLength * sizeof(long)];
            byte[] totalInput = new byte[(input.Length * sizeof(long)) + FN_NAME_LENGTH];
            Buffer.BlockCopy(Encoding.ASCII.GetBytes(name), 0, totalInput, 0, Math.Min(FN_NAME_LENGTH - 1, name.Length));
            Buffer.BlockCopy(input, 0, totalInput, FN_NAME_LENGTH, input.Length * sizeof(long));

            uint read = 0;

            fixed (byte* pIn = totalInput, pOut = output)
            {
                if (PInvoke.DeviceIoControl((HANDLE)_handle.DangerousGetHandle(), (uint)ControlCode.Execute, pIn, (uint)totalInput.Length, pOut, (uint)output.Length, &read, null))
                {
                    long[] outp = new long[read / sizeof(long)];
                    Buffer.BlockCopy(output, 0, outp, 0, (int)read);
                    return outp;
                }
            }
        }

        return new long[outLength];
    }

    public long[] ExecuteChecked(string name, long[] input, int outLength)
    {
        if (outLength < 0) throw new ArgumentOutOfRangeException(nameof(outLength));
        var output = new long[outLength];
        int result = ExecuteHr(name, input, (uint)input.Length, output, (uint)output.Length, out uint count);
        Marshal.ThrowExceptionForHR(result);
        if (count != output.Length) throw new IOException("PawnIO returned an incomplete response.");
        return output;
    }

    public int ExecuteHr(string name, long[] inBuffer, uint inSize, long[] outBuffer, uint outSize, out uint returnSize)
    {
        if (inBuffer.Length < inSize)
            throw new ArgumentOutOfRangeException(nameof(inSize));

        if (outBuffer.Length < outSize)
            throw new ArgumentOutOfRangeException(nameof(outSize));

        if (!IsLoaded)
        {
            returnSize = 0;
            return unchecked((int)0x80070006); // E_HANDLE: absence is not a successful zero-output operation.
        }

        byte[] output = new byte[checked((int)outSize * sizeof(long))];
        byte[] totalInput = new byte[checked(((int)inSize * sizeof(long)) + FN_NAME_LENGTH)];
        Buffer.BlockCopy(Encoding.ASCII.GetBytes(name), 0, totalInput, 0, Math.Min(FN_NAME_LENGTH - 1, name.Length));
        Buffer.BlockCopy(inBuffer, 0, totalInput, FN_NAME_LENGTH, checked((int)inSize * sizeof(long)));

        var result = _execute(_handle, totalInput, output);
        if (result.Success)
        {
            if (result.BytesRead > output.Length || result.BytesRead % sizeof(long) != 0)
            {
                returnSize = 0;
                return unchecked((int)0x80004005); // E_FAIL: do not truncate a malformed response into success.
            }
            Buffer.BlockCopy(output, 0, outBuffer, 0, (int)result.BytesRead);
            returnSize = result.BytesRead / sizeof(long);
            return 0;
        }

        returnSize = 0;
        int failure = PInvoke.HRESULT_FROM_WIN32((WIN32_ERROR)result.Error);
        return failure < 0 ? failure : unchecked((int)0x80004005);
    }

    private static unsafe (bool Success, uint BytesRead, int Error) ExecuteNative(SafeFileHandle handle, byte[] input, byte[] output)
    {
        bool referenced = false;
        try
        {
            handle.DangerousAddRef(ref referenced);
            uint read = 0;
            fixed (byte* pIn = input, pOut = output)
            {
                bool success = PInvoke.DeviceIoControl((HANDLE)handle.DangerousGetHandle(), (uint)ControlCode.Execute,
                    pIn, (uint)input.Length, pOut, (uint)output.Length, &read, null);
                return (success, read, success ? 0 : Marshal.GetLastWin32Error());
            }
        }
        finally { if (referenced) handle.DangerousRelease(); }
    }

    private enum ControlCode : uint
    {
        LoadBinary = DEVICE_TYPE | IOCTL_PIO_LOAD_BINARY,
        Execute = DEVICE_TYPE | IOCTL_PIO_EXECUTE_FN
    }
}
