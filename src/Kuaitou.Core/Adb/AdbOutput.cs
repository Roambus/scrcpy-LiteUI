using Kuaitou.Core.Storage;

namespace Kuaitou.Core.Adb;

/// <summary>按 adb devices 原始状态分组的设备列表，供界面区分「已连接 / 等待授权 / 离线 / 未连接」。</summary>
public sealed record DeviceStates(
    IReadOnlyList<string> Device,
    IReadOnlyList<string> Unauthorized,
    IReadOnlyList<string> Offline,
    IReadOnlyList<string> Other);

/// <summary>设备的连接地址拆解。USB 设备没有端口，Port 为空串。</summary>
public readonly record struct DeviceAddress(string Serial, string Ip, string Port);

/// <summary>
/// adb 文本输出的纯解析函数。全部无副作用、可脱离界面单测 —— 解析错了界面看不出来，
/// 只有测试能拦。对应 legacy/kuaitou/device.py 的 get_devices / device_states / device_info /
/// _is_disconnect。
/// </summary>
public static class AdbOutput
{
    /// <summary>掉线关键词，命中即认为需要重连一次。</summary>
    private static readonly string[] DisconnectHints =
    [
        "no devices/emulators found", "device offline", "device not found",
        "couldn't read from", "closed", "eof", "broken pipe",
    ];

    /// <summary>
    /// 从 `adb devices` 输出里取「状态为 device」的序列号列表。
    /// 首行是 "List of devices attached"，跳过；每行以制表符分隔序列号与状态。
    /// </summary>
    public static List<string> ParseDevices(string stdout)
    {
        var devices = new List<string>();
        string[] lines = Normalize(stdout).Split('\n');
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }
            int tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab < 0)
            {
                continue;
            }
            string addr = line[..tab].Trim();
            string status = line[(tab + 1)..].Trim();
            if (status == "device" && addr.Length > 0)
            {
                devices.Add(addr);
            }
        }
        return devices;
    }

    /// <summary>
    /// 按原始状态分组。之前只认 status=='device'，导致两类误报：手机停在「允许 USB 调试」
    /// 弹窗时被当成「未连接」；adb 里还挂着但已掉线的设备被当成「已连接」。
    /// </summary>
    public static DeviceStates ParseDeviceStates(string stdout)
    {
        var device = new List<string>();
        var unauthorized = new List<string>();
        var offline = new List<string>();
        var other = new List<string>();

        string[] lines = Normalize(stdout).Split('\n');
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }
            int tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab < 0)
            {
                continue;
            }
            string serial = line[..tab].Trim();
            string status = line[(tab + 1)..].Trim();
            if (serial.Length == 0)
            {
                continue;
            }
            switch (status)
            {
                case "device":
                    device.Add(serial);
                    break;
                case "unauthorized":
                    unauthorized.Add(serial);
                    break;
                case "offline":
                    offline.Add(serial);
                    break;
                default:
                    other.Add(serial);
                    break;
            }
        }

        return new DeviceStates(device, unauthorized, offline, other);
    }

    /// <summary>把序列号拆成 {serial, ip, port}；USB 设备（无端口）也能安全处理。</summary>
    public static DeviceAddress SplitAddress(string serial)
    {
        int colon = serial.LastIndexOf(':');
        if (colon > 0)
        {
            string ip = serial[..colon];
            string port = serial[(colon + 1)..];
            if (ip.Length > 0 && port.Length > 0 && port.All(char.IsAsciiDigit))
            {
                return new DeviceAddress(serial, ip, port);
            }
        }
        return new DeviceAddress(serial, serial, "");
    }

    /// <summary>输出里是否含掉线关键词（大小写不敏感）。</summary>
    public static bool IsDisconnect(string? message)
    {
        string text = (message ?? "").ToLowerInvariant();
        return DisconnectHints.Any(text.Contains);
    }

    /// <summary>
    /// 排除组播/广播/回环/链路本地地址（169.254 是网卡未连通时的自动地址）。
    /// USB 直连设备的「ip」其实是序列号（如 V2324A），这里会判为不可用，调用方据此跳过。
    /// </summary>
    public static bool IsUsableIp(string? ip)
    {
        string[] parts = (ip ?? "").Split('.');
        if (parts.Length != 4 || !parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit)))
        {
            return false;
        }
        int[] octets = [.. parts.Select(int.Parse)];
        if (octets.Any(o => o > 255))
        {
            return false;
        }
        int a = octets[0];
        int b = octets[1];
        int d = octets[3];
        if (a is 0 or 127 || a >= 224)
        {
            return false;
        }
        if (a == 169 && b == 254)
        {
            return false;
        }
        return d is not (0 or 255);
    }

    /// <summary>
    /// 按 IP 数值排序用的键；USB 序列号这种不是 IP 的值排到最后。
    ///
    /// 旧版这里直接 int(ip)，USB 直连设备的「ip」是序列号（如 V2324A），一旦插着数据线
    /// 调发现接口就会抛 ValueError，整个局域网扫描直接失败。这里统一返回 long，不可用的给 long.MaxValue。
    /// </summary>
    public static long IpSortKey(string? ip)
    {
        if (!IsUsableIp(ip))
        {
            return long.MaxValue;
        }
        int[] octets = [.. ip!.Split('.').Select(int.Parse)];
        return ((long)octets[0] << 24) | ((long)octets[1] << 16) | ((long)octets[2] << 8) | (uint)octets[3];
    }

    /// <summary>
    /// 多设备时挑哪台：指定 serial 直接用；只有一台不附加；多台优先 USB（序列号不含冒号），
    /// 否则退回配置里的地址。不挑的话 adb/scrcpy 会直接报 'Multiple (2) ADB devices' 失败。
    /// 纯函数，便于单测。
    /// </summary>
    public static IReadOnlyList<string> SelectSerialArgs(string? serial, IReadOnlyList<string> devices, AppConfig cfg)
    {
        if (!string.IsNullOrEmpty(serial))
        {
            return ["-s", serial];
        }
        if (devices.Count < 2)
        {
            return [];
        }

        string? usb = devices.FirstOrDefault(d => !d.Contains(':', StringComparison.Ordinal));
        if (usb is not null)
        {
            return ["-s", usb];
        }

        string want = $"{cfg.Ip}:{cfg.Port}";
        return ["-s", devices.Contains(want) ? want : devices[0]];
    }

    private static string Normalize(string stdout)
        => (stdout ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
}
