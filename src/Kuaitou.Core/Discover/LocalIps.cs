using System.Net.NetworkInformation;
using System.Net.Sockets;
using Kuaitou.Core.Adb;

namespace Kuaitou.Core.Discover;

/// <summary>
/// 本机各网卡的 IPv4 地址。对应 legacy/kuaitou/discover.py 里 <c>_arp_table()</c> 解析
/// `arp -a` 输出 "Interface: x.x.x.x ---" 行的那一半——这里换成托管接口取，格式不受系统语言影响。
///
/// 一台电脑常有多张网卡（物理网卡 + UU Lanplay / Tailscale 之类的虚拟网卡），
/// 手机连在哪个网段并不确定，所以要把它们全部纳入扫描网段。
/// </summary>
public static class LocalIps
{
    /// <summary>取本机各已启用网卡上的 IPv4 地址（去重）。取不到时返回空列表。</summary>
    public static IReadOnlyList<string> Addresses()
    {
        var list = new List<string>();
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                // 回环接口的状态也是 Up，但 127.0.0.1 不是要扫的网段，先排除
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }
                foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }
                    string ip = info.Address.ToString();
                    if (AdbOutput.IsUsableIp(ip) && !list.Contains(ip))
                    {
                        list.Add(ip);
                    }
                }
            }
        }
        catch (Exception)
        {
            // 取不到本机地址不该让设备发现整体失败，退化成「只扫 ARP 邻居所在网段」
        }
        return list;
    }
}
