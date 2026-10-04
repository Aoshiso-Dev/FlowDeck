using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace FlowDeck.Infrastructure;

internal static class WindowsTcpPeer
{
    // Match the complete reverse TCP tuple, not a listening port that might belong to another process.
    internal static void Check(uint expectedProcess, IPEndPoint client, IPEndPoint server)
    {
        if (expectedProcess == 0 || !client.Address.Equals(IPAddress.Loopback) || !server.Address.Equals(IPAddress.Loopback))
            throw new IOException("SDKの接続先がローカルの所有プロセスではありません");
        uint size = 0;
        var code = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (code != 122 || size is < 4 or > 8 * 1024 * 1024) throw new IOException("SDK接続先のプロセス情報を取得できません");
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                var capacity = size;
                code = GetExtendedTcpTable(buffer, ref size, false, 2, 5, 0);
                if (code == 122) continue;
                if (code != 0) throw new Win32Exception((int)code, "SDK接続先の照合に失敗しました");
                var count = Marshal.ReadInt32(buffer);
                if (count < 0 || 4L + count * 24L > capacity) throw new IOException("TCP所有者情報のサイズが不正です");
                var matches = 0;
                for (var i = 0; i < count; i++)
                {
                    var bytes = new byte[24]; Marshal.Copy(buffer + 4 + i * 24, bytes, 0, 24);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 5) continue; // ESTABLISHED
                    if (new IPAddress(bytes.AsSpan(4, 4)).Equals(server.Address) && BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(8, 2)) == server.Port &&
                        new IPAddress(bytes.AsSpan(12, 4)).Equals(client.Address) && BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(16, 2)) == client.Port)
                    {
                        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20, 4)) != expectedProcess)
                            throw new IOException("SDK接続先は所有しているOpenRGBではありません");
                        matches++;
                    }
                }
                if (matches != 1) throw new IOException("SDK接続先と所有プロセスの対応を確認できません");
                return;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("SDK接続先の一覧が更新中です");
    }
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint GetExtendedTcpTable(IntPtr table,
        ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);
}
