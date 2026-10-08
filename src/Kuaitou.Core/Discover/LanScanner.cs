using Kuaitou.Core.Adb;
using Kuaitou.Core.Scrcpy;
using Kuaitou.Core.Storage;
using Kuaitou.Interop;

namespace Kuaitou.Core.Discover;

/// <summary>
/// 第一级设备发现：局域网。对应 legacy/kuaitou/discover.py 的 <c>discover_devices</c>。
///
/// 已连接设备 + ARP 已知主机 + 同网段 /24，只探测 5555 端口；扫描阶段不做任何 adb connect，
/// 用户点选后才去连接，因此不会把第二台设备意外连上、也就不会再出现 'Multiple ADB devices'。
/// （旧版曾扫 30000-49999 随机端口段，太慢且 connect 会顺带连上多台设备，已改掉。）
/// </summary>
public sealed class LanScanner
{
    private const string KnownSource = "已知设备";
    private const string SameSubnetSource = "同网段";

    private readonly DeviceInventory _inventory;
    private readonly ConfigStore _config;

    public LanScanner(DeviceInventory inventory, ConfigStore config)
    {
        _inventory = inventory;
        _config = config;
    }

    /// <summary>扫一遍局域网，返回可连接（或已连接）的设备列表。</summary>
    public DiscoverResult Scan()
    {
        double started = PortProbe.Now();
        var entries = new Dictionary<string, DiscoverEntry>(StringComparer.Ordinal);
        var order = new List<string>();

        DiscoverEntry Add(string ip, string source, bool connected = false, string? port = null, bool usb = false)
        {
            if (!entries.TryGetValue(ip, out DiscoverEntry? entry))
            {
                string p = string.IsNullOrEmpty(port) ? WirelessService.ClassicAdbPort.ToString() : port;
                entry = new DiscoverEntry
                {
                    Ip = ip,
                    Port = p,
                    Addr = $"{ip}:{p}",
                    Source = source,
                    Connected = connected,
                    Usb = usb,
                };
                entries[ip] = entry;
                order.Add(ip);
            }
            else
            {
                entry = entry with { Connected = entry.Connected || connected, Usb = entry.Usb || usb };
                entries[ip] = entry;
            }
            return entry;
        }

        // 1) adb 已在线的设备：直接列出（USB 直连的序列号不带端口，无线的是 ip:port）
        foreach (string serial in _inventory.GetDevices())
        {
            DeviceAddress info = AdbOutput.SplitAddress(serial);
            bool usb = !serial.Contains(':', StringComparison.Ordinal);
            Add(info.Ip, usb ? "USB 有线" : "已连接", connected: true,
                port: info.Port.Length > 0 ? info.Port : WirelessService.ClassicAdbPort.ToString(), usb: usb);
            entries[info.Ip] = entries[info.Ip] with { Addr = serial };
        }

        // 2) ARP 邻居（标为「已知设备」），3) 各本机网段 /24 内的其余主机
        IReadOnlyList<string> localIps = LocalIps.Addresses();
        IReadOnlyList<string> arp = ArpTable.NeighborIps();
        var known = new HashSet<string>(localIps, StringComparer.Ordinal);
        known.UnionWith(arp);
        foreach (string ip in arp)
        {
            Add(ip, KnownSource);
        }
        foreach (string prefix in LocalPrefixes(localIps, arp))
        {
            for (int host = 1; host < 255; host++)
            {
                Add($"{prefix}.{host}", SameSubnetSource);
            }
        }

        var candidates = order.Where(ip => !entries[ip].Connected).ToList();
        HashSet<string> open5555 = PortProbe.ProbeMany(
            candidates, WirelessService.ClassicAdbPort, PortProbe.DefaultTimeout, PortProbe.DefaultWorkers);

        var found = new List<DiscoverEntry>();
        foreach (string ip in order)
        {
            DiscoverEntry entry = entries[ip];
            if (entry.Connected)
            {
                found.Add(entry);                       // 已连接的照常显示
            }
            else if (open5555.Contains(ip))
            {
                found.Add(entry with { Source = known.Contains(ip) ? KnownSource : SameSubnetSource });
            }
        }

        found.Sort(Compare);
        return new DiscoverResult(found, Math.Round(PortProbe.Now() - started, 1));
    }

    /// <summary>
    /// 要按 /24 扫一遍的网段前缀。一台电脑常有多张网卡（物理网卡 + UU Lanplay / Tailscale
    /// 之类的虚拟网卡），手机连在哪个网段上并不确定，所以把本机各网卡、ARP 邻居以及配置里
    /// 上次连过的地址所在网段全部覆盖到。
    /// </summary>
    private List<string> LocalPrefixes(IReadOnlyList<string> localIps, IReadOnlyList<string> arp)
    {
        var ips = new List<string>(localIps);
        ips.AddRange(arp);
        ips.Add(_config.Load().Ip);

        var prefixes = new List<string>();
        foreach (string ip in ips)
        {
            if (string.IsNullOrEmpty(ip) || !AdbOutput.IsUsableIp(ip))
            {
                continue;
            }
            string prefix = ip[..ip.LastIndexOf('.')];
            if (!prefixes.Contains(prefix))
            {
                prefixes.Add(prefix);
            }
        }
        return prefixes;
    }

    /// <summary>排序键：(已连接优先, USB 优先, 已知设备优先, IP 数值)。与旧版一致。</summary>
    private static int Compare(DiscoverEntry a, DiscoverEntry b)
    {
        int c = b.Connected.CompareTo(a.Connected);
        if (c != 0)
        {
            return c;
        }
        c = b.Usb.CompareTo(a.Usb);
        if (c != 0)
        {
            return c;
        }
        c = (a.Source != KnownSource).CompareTo(b.Source != KnownSource);
        if (c != 0)
        {
            return c;
        }
        return AdbOutput.IpSortKey(a.Ip).CompareTo(AdbOutput.IpSortKey(b.Ip));
    }
}
