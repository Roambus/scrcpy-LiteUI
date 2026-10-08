namespace Kuaitou.Core.Discover;

/// <summary>
/// 发现结果里的一台候选设备。对应 legacy/kuaitou/discover.py 里那个 entries / found 的 dict
/// （ip / port / addr / source / connected / usb / instance / pairing / port_scan / pending）。
///
/// 用 record 是为了拿到值相等：深度搜索每轮都要把结果跟上一轮比对（「状态没变不发」），
/// 引用相等会让每一轮都被当成「变了」而刷一次界面。
/// </summary>
public sealed record DiscoverEntry
{
    public string Ip { get; init; } = "";

    public string Port { get; init; } = "5555";

    /// <summary>连接地址：无线是 ip:port，USB 有线是序列号本身。</summary>
    public string Addr { get; init; } = "";

    /// <summary>来源说明，界面上当 tooltip：「已连接 / USB 有线 / 已知设备 / 同网段 / 深度搜索（mDNS）…」。</summary>
    public string Source { get; init; } = "";

    /// <summary>adb 里已是 device 状态（能直接用）。</summary>
    public bool Connected { get; init; }

    /// <summary>USB 有线直连（序列号不含端口）。</summary>
    public bool Usb { get; init; }

    /// <summary>mDNS 报的是等待配对的 _adb-tls-pairing 服务。</summary>
    public bool Pairing { get; init; }

    /// <summary>端口核复核时手机返回 unauthorized：真端口，但手机上还没点「允许调试」。</summary>
    public bool Pending { get; init; }

    /// <summary>这一条是端口扫描复核通过的（深度搜索第三级）。</summary>
    public bool PortScan { get; init; }

    /// <summary>mDNS 实例名（如 adb-XXXX-HgzRvA）；没有则为空。</summary>
    public string Instance { get; init; } = "";
}

/// <summary>一次局域网扫描的结果。对应 legacy 的 <c>{"found": [...], "elapsed": x}</c>。</summary>
public sealed record DiscoverResult(IReadOnlyList<DiscoverEntry> Found, double Elapsed);

/// <summary>一条 mDNS 服务记录：<c>(实例名, 服务类型, IP, 端口)</c>。</summary>
public sealed record MdnsService(string Instance, string Service, string Ip, string Port);

/// <summary>
/// 深度搜索的进度快照。对应 legacy/kuaitou/discover.py 的 <c>_deep_state</c> 那个 dict。
/// 界面上「状态没变不发」的比较也靠它（record 值相等）。
/// </summary>
public sealed record DeepState
{
    public bool Running { get; init; }

    /// <summary>idle / lan / ports / done。</summary>
    public string Phase { get; init; } = "idle";

    public string Text { get; init; } = "";

    public double Progress { get; init; }

    public IReadOnlyList<DiscoverEntry> Found { get; init; } = [];

    public double Elapsed { get; init; }

    public string Error { get; init; } = "";

    public int Hosts { get; init; }

    public int Scanned { get; init; }

    public int MdnsCount { get; init; }

    public int LanCount { get; init; }

    public int PortCount { get; init; }

    public bool Canceled { get; init; }
}
