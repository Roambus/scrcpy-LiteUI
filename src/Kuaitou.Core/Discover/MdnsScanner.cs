using Kuaitou.Core.Adb;

namespace Kuaitou.Core.Discover;

/// <summary>
/// mDNS 设备发现。对应 legacy/kuaitou/discover.py 的 <c>mdns_available</c> / <c>mdns_services</c> /
/// <c>collect_mdns</c>。
///
/// Android 11+ 打开「无线调试」后，手机会广播 _adb-tls-connect（可直连）和 _adb-tls-pairing
/// （等配对）两种服务。mDNS 走组播，能发现和电脑不同 /24 网段、但在同一广播域里的设备。
/// </summary>
public sealed class MdnsScanner
{
    private static readonly string[] PairingPrefixes = ["_adb-tls-"];

    private readonly AdbRunner _adb;

    public MdnsScanner(AdbRunner adb)
    {
        _adb = adb;
    }

    /// <summary>adb 的 mDNS 后端是否可用。打包内置的 adb 37 默认可用，老版本可能不行。</summary>
    public (bool Ok, string Message) Available()
    {
        AdbResult r = _adb.Run(["mdns", "check"], timeoutSeconds: 8);
        string message = r.Combined.Trim();
        return (message.Contains("daemon version", StringComparison.Ordinal), message);
    }

    /// <summary>查一次 `adb mdns services`。</summary>
    public List<MdnsService> Services(int timeoutSeconds = 8)
    {
        AdbResult r = _adb.Run(["mdns", "services"], timeoutSeconds: timeoutSeconds);
        return ParseServices(r.Stdout);
    }

    /// <summary>
    /// 解析 `adb mdns services` 的输出，返回 (实例名, 服务类型, IP, 端口)。
    /// 输出形如 <c>adb-XXXX-HgzRvA\t_adb-tls-connect._tcp\t192.168.1.39:42865</c>，也容错空白分隔；
    /// 只收 `_adb-tls-*` 服务。纯函数，便于单测。
    /// </summary>
    public static List<MdnsService> ParseServices(string? text)
    {
        var rows = new List<MdnsService>();
        string normalized = (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (string raw in normalized.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string[] parts = line.Contains('\t', StringComparison.Ordinal)
                ? line.Split('\t')
                : line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !parts[1].Trim().StartsWith(PairingPrefixes[0], StringComparison.Ordinal))
            {
                continue;
            }

            string instance = parts[0].Trim();
            string service = parts[1].Trim();
            string endpoint = parts[2].Trim();
            int colon = endpoint.LastIndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            string ip = endpoint[..colon];
            string port = endpoint[(colon + 1)..];
            if (ip.Length > 0 && port.Length > 0 && port.All(char.IsAsciiDigit))
            {
                rows.Add(new MdnsService(instance, service, ip, port));
            }
        }
        return rows;
    }

    /// <summary>
    /// 在时间窗内反复查 `adb mdns services`，把中途出现的服务合并起来。
    ///
    /// mDNS 是异步发现的：adb 服务刚起来时列表常是空的，手机广播也要几秒才会进缓存，
    /// 所以单次查询很容易「一个都搜不到」。按 interval 轮询整个 window，同一地址只留一条
    /// （优先可直连的 _adb-tls-connect）。
    /// </summary>
    public List<MdnsService> Collect(double windowSeconds = 5.0, double intervalSeconds = 1.2, Func<bool>? stop = null)
    {
        var box = new Dictionary<string, MdnsService>(StringComparer.Ordinal);
        double deadline = PortProbe.Now() + windowSeconds;
        while (true)
        {
            foreach (MdnsService svc in Services())
            {
                string addr = $"{svc.Ip}:{svc.Port}";
                if (!box.TryGetValue(addr, out MdnsService? old)
                    || (old.Service.StartsWith("_adb-tls-pairing", StringComparison.Ordinal)
                        && svc.Service.StartsWith("_adb-tls-connect", StringComparison.Ordinal)))
                {
                    box[addr] = svc;
                }
            }

            double left = deadline - PortProbe.Now();
            if (left <= 0 || (stop?.Invoke() ?? false))
            {
                break;
            }
            Thread.Sleep((int)(Math.Min(intervalSeconds, left) * 1000));
        }
        return [.. box.Values];
    }
}
