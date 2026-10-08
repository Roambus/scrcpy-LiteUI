using Kuaitou.Core.Adb;
using Kuaitou.Core.Storage;

namespace Kuaitou.Core.Scrcpy;

/// <summary>
/// 投屏窗口功能栏上那些按钮对应的手机侧动作。对应 legacy/kuaitou/device.py 的
/// winbar_action / _KEYEVENT / _VOLUME_KEY / _PANEL / toggle_rotate_lock / release_rotate_lock。
///
/// 这些命令走 adb，与 scrcpy 的控制通道无关 —— 所以「只看不控」开着的时候它们照样生效
/// （按钮是用户明确点的，不是误触）。
/// </summary>
public sealed class DeviceActionService
{
    private const string KeyBack = "4";
    private const string KeyHome = "3";
    private const string KeyAppSwitch = "187";
    private const string KeyVolumeDown = "25";
    private const string KeyVolumeUp = "24";

    private readonly AdbRunner _adb;
    private readonly AdsStore _store;

    // 「强制横屏」要临时关掉系统自动旋转（不然设了 user_rotation 也会被传感器顶回去）。
    // 改的是用户手机上的全局设置，所以先记原值，再点一次或退出程序时都必须还回去。
    private readonly object _rotateLock = new();
    private readonly Dictionary<string, (string Auto, string Rotation)> _rotateSaved = [];

    public DeviceActionService(AdbRunner adb, AdsStore store)
    {
        _adb = adb;
        _store = store;
    }

    /// <summary>功能栏动作名，与旧版 winbar 传下来的那套字符串一致。</summary>
    public const string ActionBack = "back";
    public const string ActionHome = "home";
    public const string ActionAppSwitch = "app_switch";
    public const string ActionVolumeUp = "volume_up";
    public const string ActionVolumeDown = "volume_down";
    public const string ActionNotifications = "notifications";
    public const string ActionControlCenter = "control_center";
    public const string ActionRotateLock = "rotate_lock";

    /// <summary>执行一个功能栏动作。返回是否认得这个动作。</summary>
    public bool Run(string? serial, string action)
    {
        try
        {
            switch (action)
            {
                case ActionVolumeUp:
                    return Key(serial, KeyVolumeUp);
                case ActionVolumeDown:
                    return Key(serial, KeyVolumeDown);
                case ActionBack:
                    return Key(serial, KeyBack);
                case ActionHome:
                    return Key(serial, KeyHome);
                case ActionAppSwitch:
                    return Key(serial, KeyAppSwitch);
                case ActionNotifications:
                    // Android 7+ 的 statusbar 服务：展开通知栏
                    return Panel(serial, "expand-notifications");
                case ActionControlCenter:
                    // 展开快捷设置面板
                    return Panel(serial, "expand-settings");
                case ActionRotateLock:
                    ToggleRotateLock(serial);
                    return true;
                default:
                    LogAction($"未知动作：{action}");
                    return false;
            }
        }
        catch (Exception e)
        {
            LogAction($"动作 {action} 执行失败：{e.Message}");
            return false;
        }
    }

    /// <summary>
    /// 强制横屏的开关。返回切换后是否处于「锁定横屏」。
    /// </summary>
    public bool ToggleRotateLock(string? serial)
    {
        string key = serial ?? "";
        lock (_rotateLock)
        {
            if (_rotateSaved.Remove(key, out (string Auto, string Rotation) saved))
            {
                SetRotate(serial, saved.Auto, saved.Rotation);
                LogAction($"强制横屏 -> 关，已还原 {saved.Auto}/{saved.Rotation}");
                return false;
            }

            (string auto, string rotation) = RotationMode(serial);
            _rotateSaved[key] = (auto, rotation);
            SetRotate(serial, "0", "1");   // 关掉自动旋转 + 转成横屏
            LogAction($"强制横屏 -> 开（原值 {auto}/{rotation}）");
            return true;
        }
    }

    /// <summary>这把设备当前是否处于强制横屏。</summary>
    public bool IsRotateLocked(string? serial)
    {
        lock (_rotateLock)
        {
            return _rotateSaved.ContainsKey(serial ?? "");
        }
    }

    /// <summary>把强制横屏还回去。serial 传 null 表示所有设备都还（退出时用）。</summary>
    public void ReleaseRotateLock(string? serial)
    {
        List<string> keys;
        lock (_rotateLock)
        {
            keys = serial is null
                ? _rotateSaved.Keys.ToList()
                : ((serial) is { Length: > 0 } s && _rotateSaved.ContainsKey(s) ? [s] : []);
        }
        foreach (string key in keys)
        {
            try
            {
                ToggleRotateLock(key.Length == 0 ? null : key);
            }
            catch (Exception)
            {
                // 还原失败也不能在退出路径上抛
            }
        }
    }

    /// <summary>读这台设备当前的 (自动旋转, 用户旋转) 原值；读不到就用系统默认的 1 / 0。</summary>
    private (string Auto, string Rotation) RotationMode(string? serial)
    {
        var values = new List<string>();
        foreach (string key in new[] { "accelerometer_rotation", "user_rotation" })
        {
            AdbResult r = _adb.Run(["shell", "settings", "get", "system", key], timeoutSeconds: 6, serial: serial);
            string value = r.Stdout.Trim();
            values.Add(r.ExitCode == 0 && value.Length > 0 && value.All(char.IsDigit) ? value : "");
        }
        return (values[0].Length > 0 ? values[0] : "1", values[1].Length > 0 ? values[1] : "0");
    }

    private void SetRotate(string? serial, string auto, string rotation)
    {
        _adb.Run(["shell", "settings", "put", "system", "accelerometer_rotation", auto], timeoutSeconds: 6, serial: serial);
        _adb.Run(["shell", "settings", "put", "system", "user_rotation", rotation], timeoutSeconds: 6, serial: serial);
    }

    private bool Key(string? serial, string code)
        => _adb.Run(["shell", "input", "keyevent", code], timeoutSeconds: 6, serial: serial).ExitCode == 0;

    private bool Panel(string? serial, string command)
        => _adb.Run(["shell", "cmd", "statusbar", command], timeoutSeconds: 6, serial: serial).ExitCode == 0;

    /// <summary>功能栏动作的结果写进投屏日志：按钮「点了没反应」时只能靠它复盘。</summary>
    private void LogAction(string message)
        => _store.WriteText(
            AdsStore.LaunchLogStream,
            $"\n[winbar {DateTime.Now:HH:mm:ss}] {message}\n",
            append: true);
}
