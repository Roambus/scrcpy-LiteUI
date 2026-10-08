using System.Text.RegularExpressions;
using Kuaitou.Core.Adb;

namespace Kuaitou.Core.Scrcpy;

/// <summary>
/// 关闭手机物理屏幕（不顺手锁屏）。对应 legacy/kuaitou/device.py 的 screen_off /
/// _display_power / _hold_lock_timeout / release_lock_timeout / _sdk_level。
///
/// 优选 Android 15+ 的 `cmd display power-off`：只关显示、设备不进睡眠，锁屏定时器
/// 不会启动。老系统没有这条命令，退回「顶住『熄屏后自动锁定』+ 电源键」，那条路不保证
/// 不锁，提示里会说清楚走的是哪条。
/// </summary>
public partial class ScreenPowerService
{
    /// <summary>Android 15 = API 35：从这版起才有 cmd display power-off。</summary>
    private const int SdkAndroid15 = 35;

    /// <summary>熄屏后多久自动锁定（毫秒）对应的 secure 设置键。</summary>
    public const string LockTimeoutKey = "lock_screen_lock_after_timeout";

    /// <summary>老系统的退路：熄屏前把上面那条临时顶成一天。</summary>
    private const int ScreenOffKeepMs = 86_400_000;

    private const double ScreenOffSettle = 0.9;   // 发完熄屏指令后等系统状态更新
    private const int ScreenOffRounds = 3;        // 复查熄屏是否生效的轮数（vivo 等 ROM 状态更新晚一拍）
    private const double ScreenOffWait = 0.6;     // 每轮复查的间隔

    private readonly AdbRunner _adb;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, int> _sdkCache = [];

    /// <summary>序列号 → 顶住之前用户自己的「熄屏后自动锁定」值。</summary>
    private readonly Dictionary<string, string> _heldLockTimeout = [];

    public ScreenPowerService(AdbRunner adb)
    {
        _adb = adb;
    }

    /// <summary>关闭手机物理屏幕但不锁屏。失败时如实说明走的是哪条路、为什么不保证。</summary>
    public ActionResult ScreenOff(string? serial = null)
    {
        try
        {
            if (SdkLevel(serial) >= SdkAndroid15 && DisplayPower("power-off", serial))
            {
                Sleep(ScreenOffSettle);
                if (DisplayScreenState(serial) is true)
                {
                    return ActionResult.Failure("已发送熄屏指令，但手机仍报告屏幕亮着：请看一眼手机是否停在需要操作的界面");
                }
                return ActionResult.Success("已关闭手机物理屏幕（只关显示，不会锁定手机）");
            }
            return ScreenOffByPowerKey(serial);
        }
        catch (Exception e)
        {
            return ActionResult.Failure($"关闭屏幕出错：{e.Message}");
        }
    }

    /// <summary>
    /// 把顶住的「熄屏后自动锁定」还回原值：点亮屏幕时、退出应用时都要调。
    /// serial 为 null 时还原全部。
    /// </summary>
    public void ReleaseLockTimeout(string? serial = null)
    {
        List<KeyValuePair<string, string>> targets;
        lock (_cacheLock)
        {
            if (serial is null)
            {
                targets = [.. _heldLockTimeout];
                _heldLockTimeout.Clear();
            }
            else if (_heldLockTimeout.TryGetValue(serial, out string? original))
            {
                _heldLockTimeout.Remove(serial);
                targets = [new KeyValuePair<string, string>(serial, original)];
            }
            else
            {
                targets = [];
            }
        }

        foreach ((string device, string original) in targets)
        {
            RestoreLockTimeout(device, original);
        }
    }

    /// <summary>设备 API 级别（35 = Android 15）；取不到返回 0，一律按老系统处理。</summary>
    public int SdkLevel(string? serial = null)
    {
        string key = serial ?? "";
        lock (_cacheLock)
        {
            if (_sdkCache.TryGetValue(key, out int cached))
            {
                return cached;
            }
        }

        AdbResult r = _adb.Run(["shell", "getprop", "ro.build.version.sdk"], timeoutSeconds: 8, serial: serial);
        int level = int.TryParse(r.Stdout.Split('\n').Select(s => s.Trim()).FirstOrDefault() ?? "", out int parsed)
            ? parsed
            : 0;

        lock (_cacheLock)
        {
            _sdkCache[key] = level;
        }
        return level;
    }

    /// <summary>
    /// Android 15+ 的 `cmd display power-off|power-on &lt;id&gt;`，成功返回 True。
    /// 这是「关掉显示但不锁定」的正路：显示电源被直接关掉，设备并没进入睡眠，
    /// 锁屏那套定时器压根不会启动。老系统没有这条命令，会报错，据此回退。
    /// </summary>
    private bool DisplayPower(string action, string? serial)
    {
        AdbResult r = _adb.Run(["shell", "cmd", "display", action, "0"], timeoutSeconds: 8, serial: serial);
        string text = r.Combined.ToLowerInvariant();
        return r.Ok && !new[] { "error", "exception", "unknown", "not found" }.Any(text.Contains);
    }

    /// <summary>
    /// dumpsys display 里的屏幕状态：True=亮着、False=已熄、null=读不到就不判断。
    /// 用 cmd display 关屏时设备并没睡（mWakefulness 仍是 Awake），所以不能拿 dumpsys power 判断。
    /// </summary>
    private bool? DisplayScreenState(string? serial)
    {
        AdbResult r = _adb.Run(["shell", "dumpsys", "display"], timeoutSeconds: 10, serial: serial);
        if (!r.Ok || r.Stdout.Length == 0)
        {
            return null;
        }
        Match m = ScreenStateRegex().Match(r.Stdout);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() == "ON" : null;
    }

    /// <summary>手机是否亮着屏（dumpsys power 的 mWakefulness）；取不到返回 null，不做判断。</summary>
    private bool? ScreenAwake(string? serial)
    {
        AdbResult r = _adb.Run(["shell", "dumpsys", "power"], timeoutSeconds: 10, serial: serial);
        if (!r.Ok || r.Stdout.Length == 0)
        {
            return null;
        }
        Match m = WakefulnessRegex().Match(r.Stdout);
        return m.Success ? m.Groups[1].Value == "Awake" : null;
    }

    /// <summary>
    /// 老系统的退路：顶住「自动锁定」→ 按电源键 → 复查是否真的熄屏。
    /// 这条路成不成取决于 ROM 认不认那条设置（vivo 就未必认），所以失败要照实说。
    /// </summary>
    private ActionResult ScreenOffByPowerKey(string? serial)
    {
        bool held = HoldLockTimeout(serial) is not null;
        _adb.Run(["shell", "input", "keyevent", "26"], timeoutSeconds: 8, serial: serial);   // 26 = 电源键
        Sleep(ScreenOffSettle);

        bool? awake = null;
        for (int i = 0; i < ScreenOffRounds; i++)
        {
            awake = ScreenAwake(serial);
            if (awake is not true)
            {
                break;
            }
            Sleep(ScreenOffWait);
        }

        if (awake is true)
        {
            return ActionResult.Failure("已按电源键，但手机仍报告屏幕亮着：请看一眼手机是否停在需要操作的界面");
        }
        return held
            ? ActionResult.Success("已关闭手机物理屏幕（这台手机走的是「顶住自动锁定」的办法，不会立刻锁屏）")
            : ActionResult.Success("已关闭手机物理屏幕；没能顶住系统的「自动锁定」，如果被锁上请点「解锁」");
    }

    /// <summary>
    /// 把「熄屏后自动锁定」临时顶成一天，成功返回原值，失败返回 null。
    /// 顶完不能马上还原——有些 ROM 会持续读这条设置，提前还原等于没顶。所以记在表里，
    /// 等下次点亮屏幕或退出应用时再还回去。
    /// </summary>
    private string? HoldLockTimeout(string? serial)
    {
        string key = serial ?? "";
        lock (_cacheLock)
        {
            if (_heldLockTimeout.TryGetValue(key, out string? already))
            {
                return already;
            }
        }

        AdbResult orig = _adb.Run(["shell", "settings", "get", "secure", LockTimeoutKey], timeoutSeconds: 8, serial: serial);
        if (!orig.Ok)
        {
            return null;
        }

        AdbResult put = _adb.Run(
            ["shell", "settings", "put", "secure", LockTimeoutKey, ScreenOffKeepMs.ToString()],
            timeoutSeconds: 8, serial: serial);
        if (!put.Ok)
        {
            return null;
        }

        string saved = orig.Stdout.Trim();
        lock (_cacheLock)
        {
            _heldLockTimeout[key] = saved;
        }
        return saved;
    }

    /// <summary>把「熄屏后自动锁定」恢复成用户原来的值；原来没有这条设置就删掉。</summary>
    private void RestoreLockTimeout(string serial, string original)
    {
        string value = (original ?? "").Trim();
        if (value.Length > 0 && !value.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            _adb.Run(["shell", "settings", "put", "secure", LockTimeoutKey, value], timeoutSeconds: 8, serial: serial);
        }
        else
        {
            _adb.Run(["shell", "settings", "delete", "secure", LockTimeoutKey], timeoutSeconds: 8, serial: serial);
        }
    }

    private static void Sleep(double seconds) => Thread.Sleep(TimeSpan.FromSeconds(seconds));

    [GeneratedRegex(@"mScreenState=(\w+)")]
    private static partial Regex ScreenStateRegex();

    [GeneratedRegex(@"mWakefulness=(\w+)")]
    private static partial Regex WakefulnessRegex();
}
