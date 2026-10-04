using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FlowDeck.Infrastructure;

internal interface IAsrockIdentitySession : IAsrockRgbSession
{
    string? VerifiedSerial { get; }
    int? DeviceRevision { get; }
}

internal static class AsrockHidIdentity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Attributes { public uint Size; public ushort Vendor, Product, Version; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle Open(string path, uint access, uint sharing, nint security, uint disposition, uint flags, nint template);
    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, [Out] byte[] buffer, uint length);
    // Called only while the RGB handle is retained. A fresh attribute-only handle avoids an enumerator's cached
    // serial. If a replacement races this read, the old retained RGB handle cannot silently address it.
    public static (string? Serial, int? Revision) Read(string path)
    {
        using var handle = OpenMetadata(path);
        return Read(handle);
    }
    internal static SafeFileHandle OpenMetadata(string path) => Open(path, 0, 3, 0, 3, 0, 0); // No RGB I/O access; OPEN_EXISTING.
    internal static (string? Serial, int? Revision) Read(SafeFileHandle handle)
    {
        if (handle.IsInvalid) return (null, null);
        var attributes = new Attributes { Size = (uint)Marshal.SizeOf<Attributes>() };
        if (!HidD_GetAttributes(handle, ref attributes)) return (null, null);
        if (attributes.Vendor != AsrockRgbMonitor.VendorId || attributes.Product != AsrockRgbMonitor.ProductId)
            throw new IOException("開いたHIDの製品識別がASRock照明と一致しません");
        var buffer = new byte[512];
        return (HidD_GetSerialNumberString(handle, buffer, (uint)buffer.Length) ? DecodeSerial(buffer) : null, attributes.Version);
    }
    internal static string? DecodeSerial(byte[] buffer)
    {
        if (buffer.Length is < 2 or > 4092 || buffer.Length % 2 != 0) return null;
        string value;
        try { value = new UnicodeEncoding(false, false, true).GetString(buffer); }
        catch (DecoderFallbackException) { return null; }
        var end = value.IndexOf('\0');
        if (end < 0) return null;
        value = value[..end].Trim();
        return HardwareSensorCapture.UsefulSerial(value) ? value : null;
    }
}
