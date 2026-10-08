using System.Security.Cryptography;
using Kuaitou.Core.Adb;
using Kuaitou.Core.Discover;
using Kuaitou.Core.Scrcpy;
using Kuaitou.Core.Storage;

namespace Kuaitou.Core.Sys;

/// <summary>扫码配对的状态快照。对应 legacy/kuaitou/system.py 的 <c>_qr_state</c>。</summary>
public sealed record QrState
{
    /// <summary>idle / starting / waiting / pairing / connecting / done / failed / timeout。</summary>
    public string State { get; init; } = "idle";

    public string Message { get; init; } = "";

    public string Name { get; init; } = "";

    public string Password { get; init; } = "";

    public string Addr { get; init; } = "";

    /// <summary>本次配对的令牌；自增后旧的 worker 会自行收工（用于「取消」）。</summary>
    public long Tok { get; init; }
}

/// <summary>
/// 扫码连接（二维码配对，Android 11+）。对应 legacy/kuaitou/system.py 的
/// <c>_qr_payload</c> / <c>_qr_worker</c> / <c>_qr_finish</c>。
///
/// 命名空间刻意叫 Sys 而不是 System：叫 Kuaitou.Core.System 会劫持所有 <c>System.*</c>
/// 的解析（测试里写 System.Text.Encoding 会被解析成 Kuaitou.Core.System.Text 而编译失败）。
///
/// 电脑显示二维码（<c>WIFI:T:ADB;S:&lt;名字&gt;;P:&lt;密码&gt;;</c>），手机在「无线调试 → 使用二维码
/// 配对设备」里扫一下：手机扫到后会按码里的名字广播 _adb-tls-pairing._tcp，我们等它出现，
/// 再用码里的密码执行 `adb pair`，最后等 _adb-tls-connect 出现并 adb connect。
/// 不用手输 IP / 端口 / 配对码；配对协议仍由 adb 客户端完成，不自己实现。
/// </summary>
public sealed class QrPairingService : IDisposable
{
    /// <summary>等手机扫码的最长时间（秒）。</summary>
    private const int WaitScanTimeoutSeconds = 120;

    private const int ConnectTimeoutSeconds = 30;
    private const string PasswordAlphabet =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private readonly AdbRunner _adb;
    private readonly MdnsScanner _mdns;
    private readonly DeviceInventory _inventory;
    private readonly ReconnectService _reconnect;
    private readonly ConfigStore _config;

    private readonly object _lock = new();
    private QrState _state = new();
    private Action<QrState>? _onUpdate;
    private long _tok;

    public QrPairingService(
        AdbRunner adb, MdnsScanner mdns, DeviceInventory inventory,
        ReconnectService reconnect, ConfigStore config)
    {
        _adb = adb;
        _mdns = mdns;
        _inventory = inventory;
        _reconnect = reconnect;
        _config = config;
    }

    /// <summary>二维码里那串文本。纯函数，便于单测。</summary>
    public static string Payload(string name, string password) => $"WIFI:T:ADB;S:{name};P:{password};;";

    public QrState Snapshot()
    {
        lock (_lock)
        {
            return _state;
        }
    }

    /// <summary>开始一次扫码配对：新生成名字与密码，显示二维码，后台等手机来扫。</summary>
    public QrState Start(Action<QrState> onUpdate)
    {
        string name = "kuaitou-" + RandomNumberGenerator.GetHexString(8);
        string password = RandomPassword(12);

        QrState next;
        lock (_lock)
        {
            _tok++;
            _onUpdate = onUpdate;
            _state = _state with
            {
                State = "starting",
                Message = "",
                Addr = "",
                Name = name,
                Password = password,
                Tok = _tok,
            };
            next = _state;
        }

        long tok = next.Tok;
        _ = Task.Run(() => Run(tok, name, password));
        return next;
    }

    /// <summary>停止等待（再次点按钮 = 取消）。</summary>
    public QrState Cancel()
    {
        QrState next;
        Action<QrState>? callback;
        lock (_lock)
        {
            _tok++;                     // 令牌一变，旧的 worker 下一轮就自行收工
            _state = _state with { State = "idle", Message = "" };
            next = _state;
            callback = _onUpdate;
        }
        callback?.Invoke(next);
        return next;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _tok++;
            _state = _state with { State = "idle" };
        }
    }

    private static string RandomPassword(int length)
    {
        var chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            chars[i] = PasswordAlphabet[RandomNumberGenerator.GetInt32(PasswordAlphabet.Length)];
        }
        return new string(chars);
    }

    private bool Alive(long tok)
    {
        lock (_lock)
        {
            return _tok == tok;
        }
    }

    /// <summary>令牌没变的才更新并通知（变了说明用户已取消，这一轮作废）。</summary>
    private void Update(long tok, Func<QrState, QrState> mutate)
    {
        QrState next;
        Action<QrState>? callback;
        lock (_lock)
        {
            if (_tok != tok)
            {
                return;
            }
            _state = mutate(_state);
            next = _state;
            callback = _onUpdate;
        }
        callback?.Invoke(next);
    }

    /// <summary>等手机广播配对服务 → adb pair → 等连接服务出现 → adb connect。</summary>
    private void Run(long tok, string name, string password)
    {
        (bool available, string checkMessage) = _mdns.Available();
        if (!available)
        {
            string detail = checkMessage.Length > 0 ? checkMessage : "请更新 platform-tools";
            Update(tok, s => s with
            {
                State = "failed",
                Message = $"当前 adb 的 mDNS 不可用，无法扫码配对：{detail}",
            });
            return;
        }

        Update(tok, s => s with { State = "waiting", Message = "等待手机扫描二维码…" });
        double deadline = PortProbe.Now() + WaitScanTimeoutSeconds;
        MdnsService? target = null;
        while (PortProbe.Now() < deadline && Alive(tok))
        {
            List<MdnsService> pairing = _mdns.Services()
                .Where(r => r.Service.StartsWith("_adb-tls-pairing", StringComparison.Ordinal))
                .ToList();
            // 优先按码里的名字匹配；个别机型不回显名字时，只有一个待配对服务也认
            List<MdnsService> match = pairing.Where(r => r.Instance == name).ToList();
            if (match.Count == 0 && pairing.Count == 1)
            {
                match = pairing;
            }
            if (match.Count > 0)
            {
                target = match[0];
                break;
            }
            Thread.Sleep(1500);
        }

        if (!Alive(tok))
        {
            return;
        }
        if (target is null)
        {
            Update(tok, s => s with
            {
                State = "timeout",
                Message = "等待超时：没等到手机扫描。请确认手机与电脑在同一 Wi-Fi（mDNS 不跨网段），再重新扫一次。",
            });
            return;
        }

        string addr = $"{target.Ip}:{target.Port}";
        Update(tok, s => s with { State = "pairing", Addr = addr, Message = $"已发现手机 {addr}，正在配对…" });
        AdbResult pair = _adb.Run(["pair", addr, password], timeoutSeconds: 20);
        string text = pair.Combined.Trim();
        if (!text.Contains("Successfully paired", StringComparison.Ordinal))
        {
            Update(tok, s => s with
            {
                State = "failed",
                Message = "配对失败：" + (text.Length > 0 ? text : "adb pair 没有返回结果"),
            });
            return;
        }

        Update(tok, s => s with { State = "connecting", Message = "配对成功，正在建立连接…" });
        double connectDeadline = PortProbe.Now() + ConnectTimeoutSeconds;
        while (PortProbe.Now() < connectDeadline && Alive(tok))
        {
            foreach (string serial in _inventory.GetDevices())
            {
                if (AdbOutput.SplitAddress(serial).Ip == target.Ip)
                {
                    Finish(tok, target.Ip, serial);
                    return;
                }
            }

            foreach (MdnsService svc in _mdns.Services())
            {
                if (svc.Service.StartsWith("_adb-tls-connect", StringComparison.Ordinal) && svc.Ip == target.Ip)
                {
                    _adb.Run(["connect", $"{svc.Ip}:{svc.Port}"], timeoutSeconds: 15);
                    break;
                }
            }
            Thread.Sleep(1500);
        }

        if (Alive(tok))
        {
            Update(tok, s => s with
            {
                State = "failed",
                Message = $"已配对成功，但自动连接超时：请在手机保持「无线调试」开着，再用「按地址连接」连 {target.Ip}。",
            });
        }
    }

    private void Finish(long tok, string ip, string serial)
    {
        DeviceAddress info = serial.Length > 0 ? AdbOutput.SplitAddress(serial) : default;
        string port = info.Port.Length > 0 ? info.Port : WirelessService.ClassicAdbPort.ToString();
        string addr = serial.Length > 0 ? serial : $"{ip}:{port}";

        _reconnect.Unskip(addr);
        _inventory.RememberDevice(addr);
        _config.Update(c =>
        {
            c.Ip = ip;
            c.Port = port;
        });
        Update(tok, s => s with { State = "done", Addr = addr, Message = $"已连接 {addr}" });
    }
}
