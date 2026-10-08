using Kuaitou.Core.Adb;
using Kuaitou.Core.Storage;

namespace Kuaitou.Core.Scrcpy;

/// <summary>按地址连接的结果。<see cref="Message"/> 供界面提示（失败原因原样回显）。</summary>
public sealed record ConnectResult(bool Ok, string Addr, string Message);

/// <summary>
/// 手动连接 / 断开。对应 legacy/kuaitou/web.py 的 /api/connect 与 /api/disconnect。
/// </summary>
public sealed class ConnectionService
{
    private readonly AdbRunner _adb;
    private readonly ConfigStore _config;
    private readonly DeviceInventory _inventory;
    private readonly ReconnectService _reconnect;

    public ConnectionService(AdbRunner adb, ConfigStore config, DeviceInventory inventory, ReconnectService reconnect)
    {
        _adb = adb;
        _config = config;
        _inventory = inventory;
        _reconnect = reconnect;
    }

    /// <summary>
    /// 按 IP + 端口连接。adb 报失败、但设备其实已在线或正等授权时也算连上：
    /// 否则界面会出现「明明连上了却显示连接失败」的误报。
    /// </summary>
    public ConnectResult Connect(string ip, string port)
    {
        ip = (ip ?? "").Trim();
        port = string.IsNullOrEmpty(port) ? WirelessService.ClassicAdbPort.ToString() : port.Trim();
        string addr = $"{ip}:{port}";

        AdbResult r = _adb.Run(["connect", addr], timeoutSeconds: 15);
        string text = r.Combined.ToLowerInvariant();
        bool ok = text.Contains("connected")
            && !text.Contains("cannot") && !text.Contains("failed") && !text.Contains("refused");

        if (!ok)
        {
            DeviceStates states = _inventory.GetStates();
            var ips = states.Device.Concat(states.Unauthorized)
                .Select(d => AdbOutput.SplitAddress(d).Ip);
            ok = ips.Contains(ip);
        }

        if (ok)
        {
            _reconnect.Unskip(addr);
            _config.Update(c =>
            {
                c.Ip = ip;
                c.Port = port;
            });
            _inventory.RememberDevice(addr);
            return new ConnectResult(true, addr, $"已连接 {addr}");
        }

        string detail = r.Combined.Trim();
        return new ConnectResult(false, addr, detail.Length > 0 ? detail : "连接失败");
    }

    /// <summary>
    /// 首次配对（手机「无线调试 → 使用配对码配对设备」里显示的端口和配对码）。
    /// 对应 legacy/kuaitou/web.py 的 /api/pair：只看 adb 有没有回 "Successfully paired"，
    /// 配对成功后手机就会出现在 adb 设备列表里，用户再点「连接」即可。
    /// </summary>
    public ActionResult Pair(string ip, string port, string code)
    {
        ip = (ip ?? "").Trim();
        port = (port ?? "").Trim();
        code = (code ?? "").Trim();
        if (ip.Length == 0 || port.Length == 0 || code.Length == 0)
        {
            return new ActionResult(false, "请输入手机 IP、配对端口和配对码");
        }

        string addr = $"{ip}:{port}";
        AdbResult r = _adb.Run(["pair", addr, code], timeoutSeconds: 15);
        string text = r.Combined.Trim();
        return text.Contains("Successfully paired", StringComparison.Ordinal)
            ? new ActionResult(true, $"配对成功：现在点上面的「连接」即可连上 {ip}")
            : new ActionResult(false, text.Length > 0 ? text : "配对失败");
    }

    /// <summary>主动断开某台设备，并把它拉进黑名单（不自动重连）。</summary>
    public void Disconnect(string device)
    {
        if (string.IsNullOrEmpty(device))
        {
            return;
        }
        _reconnect.Skip(device);
        _adb.Run(["disconnect", device], timeoutSeconds: 10);
    }
}
