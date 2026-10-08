using System.Runtime.InteropServices;

namespace Kuaitou.Interop;

/// <summary>
/// Windows IPv4 邻居表（ARP 缓存）读取。对应 legacy/kuaitou/discover.py 里 <c>_arp_table()</c>
/// 的「邻居主机地址」那一半——本机各网卡地址改用托管接口取（见 Core 的 LocalIps）。
///
/// 用 GetIpNetTable 而不是解析 `arp -a` 的文本：文本格式随系统语言变，P/Invoke 拿的是
/// 结构化数据，不依赖 `arp.exe` 在不在 PATH 上。
/// </summary>
public static class ArpTable
{
    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;

    /// <summary>一条邻居记录：dwIndex(4) + dwPhysAddrLen(4) + bPhysAddr[8] + dwAddr(4) + dwType(4)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpnetRow
    {
        public uint Index;
        public uint PhysAddrLen;
        public ulong PhysAddr;
        public uint Addr;
        public uint Type;
    }

    /// <summary>
    /// 读一遍邻居表，返回 IPv4 字面量列表（去重、保持系统返回顺序）。
    /// 表里有 MAC 缺失的条目（虚拟网卡 UU Lanplay / Tailscale），同样收下——手机可能挂在那些网段上。
    /// </summary>
    public static IReadOnlyList<string> NeighborIps()
    {
        var ips = new List<string>();
        uint size = 0;
        if (NativeMethods.GetIpNetTable(nint.Zero, ref size, false) != ErrorInsufficientBuffer || size == 0)
        {
            return ips;
        }

        nint buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (NativeMethods.GetIpNetTable(buffer, ref size, false) != NoError)
            {
                return ips;
            }

            uint count = (uint)Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<MibIpnetRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (uint i = 0; i < count; i++)
            {
                // 表头是 4 字节的 dwNumEntries，随后紧排 N 条记录。
                nint row = buffer + (4 + (int)(i * (uint)rowSize));

                // dwAddr 是 in_addr 语义（网络字节序）：内存里那 4 个字节就是 IP 的自然顺序。
                // 这里必须逐字节读，不能当成 DWORD 取位——那会在小端机上把八位组读反
                // （192.168.1.5 变成 5.1.168.192），邻居全错。
                byte b0 = Marshal.ReadByte(row, 16);
                byte b1 = Marshal.ReadByte(row, 17);
                byte b2 = Marshal.ReadByte(row, 18);
                byte b3 = Marshal.ReadByte(row, 19);
                string ip = $"{b0}.{b1}.{b2}.{b3}";
                if (seen.Add(ip))
                {
                    ips.Add(ip);
                }
            }
        }
        catch (Exception)
        {
            // 读不到邻居表不该让设备发现整体失败，当作「没有邻居」继续
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return ips;
    }
}
