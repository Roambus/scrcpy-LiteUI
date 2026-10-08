using System.Text.RegularExpressions;
using Kuaitou.Core.Adb;
using Kuaitou.Core.Storage;

namespace Kuaitou.Core.Scrcpy;

/// <summary>USB 转无线的结果：<see cref="Addr"/> 在已发起连接时给出，<see cref="Message"/> 供界面提示。</summary>
public sealed record WirelessResult(bool Ok, string Addr, string Message);

/// <summary>
/// USB 一键转无线。对应 legacy/kuaitou/device.py 的 usb_to_wifi / _device_wifi_ip / _usb_serial。
///
/// 手机用数据线插上（已允许 USB 调试）时点一下：记下 Wi-Fi IP → adb tcpip 5555 → adb connect，
/// 之后拔掉数据线就能无线投屏。只在用户点击时执行。
/// </summary>
public partial class WirelessService
{
    /// <summary>adb tcpip 模式的固定端口。</summary>
    public const int ClassicAdbPort = 5555;

    private const double TcpRestartWait = 1.5;   // 等 adbd 在 5555 上重新监听
    private const int ReadyRounds = 10;          // 等设备在 adb 里就绪（首次会弹「允许调试」）

    private readonly AdbRunner _adb;
    private readonly ConfigStore _config;
    private readonly DeviceInventory _inventory;
    private readonly ReconnectService _reconnect;

    public WirelessService(AdbRunner adb, ConfigStore config, DeviceInventory inventory, ReconnectService reconnect)
    {
        _adb = adb;
        _config = config;
        _inventory = inventory;
        _reconnect = reconnect;
    }

    /// <summary>USB 转无线。IP 要在切 tcpip 之前取（USB 那时一定在线）。</summary>
    public WirelessResult UsbToWifi(int port = ClassicAdbPort)
    {
        string? usb = UsbSerial();
        if (usb is null)
        {
            return new WirelessResult(false, "", "没有检测到 USB 连接的手机：请先用数据线连上电脑并允许 USB 调试");
        }

        string ip = DeviceWifiIp(usb);
        if (ip.Length == 0)
        {
            return new WirelessResult(false, "", "取不到手机的 Wi-Fi IP：请确认手机已连上 Wi-Fi 再试");
        }

        AdbResult tcpip = _adb.Run(["tcpip", port.ToString()], timeoutSeconds: 20, serial: usb);
        string text = tcpip.Combined.ToLowerInvariant();
        if (!text.Contains("restarting in tcp mode") && !text.Contains("already in tcp mode"))
        {
            string detail = tcpip.Combined.Trim();
            return new WirelessResult(false, "", detail.Length > 0 ? detail : "切换无线调试失败");
        }

        string addr = $"{ip}:{port}";
        Thread.Sleep(TimeSpan.FromSeconds(TcpRestartWait));

        AdbResult connect = _adb.Run(["connect", addr], timeoutSeconds: 15);
        string connectText = connect.Combined.ToLowerInvariant();
        if (!connectText.Contains("connected") || connectText.Contains("cannot")
            || connectText.Contains("failed") || connectText.Contains("refused"))
        {
            string detail = connect.Combined.Trim();
            return new WirelessResult(false, addr, detail.Length > 0 ? detail : "adb connect 失败");
        }

        _reconnect.Unskip(addr);

        for (int i = 0; i < ReadyRounds; i++)
        {
            bool ready = DeviceInventory.GetDevices(_adb).Any(d => AdbOutput.SplitAddress(d).Ip == ip);
            if (ready)
            {
                _inventory.RememberDevice(addr);
                _config.Update(c =>
                {
                    c.Ip = ip;
                    c.Port = port.ToString();
                });
                return new WirelessResult(true, addr, $"已连接 {addr}");
            }
            Thread.Sleep(TimeSpan.FromSeconds(1));
        }

        return new WirelessResult(true, addr, "已发起无线连接，若手机弹出「允许调试」请点允许");
    }

    /// <summary>第一台 USB 直连设备：序列号不带端口。</summary>
    public string? UsbSerial()
        => _inventory.GetStates().Device.FirstOrDefault(d => !d.Contains(':', StringComparison.Ordinal));

    /// <summary>取手机的 Wi-Fi IP：优先 `ip route` 的 src，回退 wlan0 的 inet 地址。</summary>
    public string DeviceWifiIp(string serial)
    {
        AdbResult route = _adb.Run(["shell", "ip", "route"], timeoutSeconds: 8, serial: serial);
        Match m = RouteSrcRegex().Match(route.Stdout);
        if (m.Success && AdbOutput.IsUsableIp(m.Groups[1].Value))
        {
            return m.Groups[1].Value;
        }

        AdbResult addr = _adb.Run(["shell", "ip", "-f", "inet", "addr", "show", "wlan0"], timeoutSeconds: 8, serial: serial);
        Match m2 = WlanInetRegex().Match(addr.Stdout);
        return m2.Success && AdbOutput.IsUsableIp(m2.Groups[1].Value) ? m2.Groups[1].Value : "";
    }

    [GeneratedRegex(@"\bsrc\s+(\d{1,3}(?:\.\d{1,3}){3})")]
    private static partial Regex RouteSrcRegex();

    [GeneratedRegex(@"\binet\s+(\d{1,3}(?:\.\d{1,3}){3})")]
    private static partial Regex WlanInetRegex();
}
