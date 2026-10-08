using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kuaitou.Core.Adb;
using Kuaitou.Core.Storage;

namespace Kuaitou.Core.Scrcpy;

/// <summary>
/// 锁屏解锁。对应 legacy/kuaitou/device.py 的 _pins_map / set_unlock_pin / screen_locked /
/// wake_screen / _swipe_and_type_pin / unlock_now。
///
/// 密码只做了一次异或 + base64，藏在数据流里，**不是加密**，也不假装是——只是别让明文
/// 直接躺在数据流里被人一眼看到。
/// </summary>
public partial class UnlockService
{
    private static readonly byte[] PinXorKey = "kuaitou"u8.ToArray();

    private const double UnlockSettle = 0.8;       // 上滑动画 / 输入法弹出的等待时间
    private const double UnlockWakeSettle = 0.6;   // 点亮屏幕后等锁屏界面画出来，再去上滑
    private static readonly (int W, int H) DefaultScreen = (1080, 2400);   // 取不到 wm size 时的兜底，够 swipe 用
    private const double LockCacheTtl = 5.0;       // 界面 3 秒一轮询，dumpsys window 输出很大
    private const int UnlockVerifyRounds = 5;      // 解锁后复查锁屏标志的轮数
    private const double UnlockVerifyWait = 0.8;   // 每轮复查间隔：vivo 等 ROM 标志翻转会晚一拍

    private readonly AdbRunner _adb;
    private readonly AdsStore _store;
    private readonly DeviceInventory _inventory;
    private readonly ScreenPowerService _screenPower;

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (double At, bool Locked)> _lockCache = [];

    public UnlockService(AdbRunner adb, AdsStore store, DeviceInventory inventory, ScreenPowerService screenPower)
    {
        _adb = adb;
        _store = store;
        _inventory = inventory;
        _screenPower = screenPower;
    }

    /// <summary>取这台设备已保存的解锁密码；没设过或数据损坏都返回空串（= 不启用解锁）。</summary>
    public string GetPin(string? serial)
    {
        string key = _inventory.GetDeviceKey(serial);
        return key.Length > 0 && ReadPinsMap().TryGetValue(key, out string? pin) ? pin : "";
    }

    /// <summary>保存某台设备的解锁密码；传空串只关掉这台设备，其它设备的密码保留。</summary>
    public bool SetPin(string serial, string pin)
    {
        string key = _inventory.GetDeviceKey(serial);
        if (key.Length == 0)
        {
            return false;
        }

        Dictionary<string, string> pins = ReadPinsMap();
        pin = (pin ?? "").Trim();
        if (pin.Length > 0)
        {
            pins[key] = pin;
        }
        else
        {
            pins.Remove(key);
        }
        return WritePinsMap(pins);
    }

    /// <summary>
    /// 屏幕是否锁着（按 dumpsys window 的锁屏标志判断）。取不到标志时一律当作未锁。
    /// ROM 差异（标志名不存在）和命令失败都归到这条路径：宁可不解锁，也不能在不确定
    /// 的状态下往手机上乱敲密码——那可能把密码打进某个聊天窗口里。
    /// </summary>
    public bool ScreenLocked(string? serial, bool force = false)
    {
        string cacheKey = serial ?? "";
        double now = Now();
        lock (_cacheLock)
        {
            if (!force && _lockCache.TryGetValue(cacheKey, out (double At, bool Locked) hit) && now - hit.At < LockCacheTtl)
            {
                return hit.Locked;
            }
        }

        AdbResult r = _adb.Run(["shell", "dumpsys", "window"], timeoutSeconds: 10, serial: serial);
        bool locked = false;
        if (r.Ok && r.Stdout.Length > 0)
        {
            foreach (string hint in new[] { "mDreamingLockscreen", "mShowingLockscreen" })
            {
                Match m = Regex.Match(r.Stdout, hint + @"=(\w+)");
                if (m.Success)
                {
                    locked = m.Groups[1].Value == "true";
                    break;
                }
            }
        }

        lock (_cacheLock)
        {
            _lockCache[cacheKey] = (now, locked);
        }
        return locked;
    }

    /// <summary>
    /// 点亮手机屏幕，并把之前临时顶住的「熄屏后自动锁定」还回去。
    /// 熄屏状态下锁屏标志还在，但上滑和输入都没有落点，必须先叫醒屏幕再动手。
    /// </summary>
    public void WakeScreen(string? serial)
    {
        _screenPower.ReleaseLockTimeout(serial);
        // Android 15+ 关屏时是直接关的显示电源，得用对应的 power-on 叫回来；
        // 老系统没这条命令（会报错，忽略即可），补一发 KEYCODE_WAKEUP 就够。
        _adb.Run(["shell", "cmd", "display", "power-on", "0"], timeoutSeconds: 8, serial: serial);
        _adb.Run(["shell", "input", "keyevent", "224"], timeoutSeconds: 8, serial: serial);
    }

    /// <summary>
    /// 在后台静默解锁这台设备的屏幕，返回结果供界面提示。
    /// 没设密码就不碰屏幕，只回一条说明；先点亮屏幕再复查锁屏状态 —— 熄屏时上滑 /
    /// 输密码都落不到锁屏界面上（这是真机反馈过的失败原因之一）。
    /// </summary>
    public ActionResult UnlockNow(string? serial)
    {
        try
        {
            string pin = GetPin(serial);
            if (pin.Length == 0)
            {
                return ActionResult.Failure("这台设备还没设置锁屏密码：请到「设置 → 设备 → 锁屏解锁」里填写");
            }

            WakeScreen(serial);
            Sleep(UnlockWakeSettle);
            if (!ScreenLocked(serial, force: true))
            {
                return ActionResult.Success("屏幕已点亮，当前没有锁屏");
            }

            SwipeAndTypePin(pin, serial);
            for (int i = 0; i < UnlockVerifyRounds; i++)
            {
                if (!ScreenLocked(serial, force: true))
                {
                    return ActionResult.Success("已解锁手机屏幕");
                }
                Sleep(UnlockVerifyWait);
            }

            return ActionResult.Failure(
                "已发送解锁指令，但手机仍报告锁屏：请核对密码是否与手机一致（图案锁 / 指纹 / 人脸无法自动解锁）");
        }
        catch (Exception e)
        {
            return ActionResult.Failure($"解锁出错：{e.Message}");
        }
    }

    /// <summary>上滑叫出密码输入框，把密码打进去再回车。</summary>
    private void SwipeAndTypePin(string pin, string? serial)
    {
        (int w, int h) = ScreenSize(serial);
        int x = w / 2;
        _adb.Run(
            ["shell", "input", "swipe", x.ToString(), ((int)(h * 0.8)).ToString(),
             x.ToString(), ((int)(h * 0.25)).ToString(), "200"],
            timeoutSeconds: 8, serial: serial);
        Sleep(UnlockSettle);
        _adb.Run(["shell", "input", "text", pin], timeoutSeconds: 8, serial: serial);
        Sleep(0.3);
        _adb.Run(["shell", "input", "keyevent", "66"], timeoutSeconds: 8, serial: serial);   // 66 = 回车
        Sleep(UnlockSettle);
    }

    private (int W, int H) ScreenSize(string? serial)
    {
        AdbResult r = _adb.Run(["shell", "wm", "size"], timeoutSeconds: 8, serial: serial);
        Match m = ScreenSizeRegex().Match(r.Stdout);
        return m.Success
            ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value))
            : DefaultScreen;
    }

    /// <summary>读回 {设备键: 密码}。没设置过或数据损坏都返回空表。</summary>
    private Dictionary<string, string> ReadPinsMap()
    {
        var pins = new Dictionary<string, string>();
        string? raw = _store.ReadText(AdsStore.UnlockPinsStream);
        if (string.IsNullOrEmpty(raw))
        {
            return pins;
        }

        Dictionary<string, string>? saved;
        try
        {
            saved = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
        }
        catch (JsonException)
        {
            return pins;
        }
        if (saved is null)
        {
            return pins;
        }

        foreach ((string key, string blob) in saved)
        {
            if (string.IsNullOrEmpty(blob))
            {
                continue;
            }
            try
            {
                pins[key] = Encoding.UTF8.GetString(Scramble(Convert.FromBase64String(blob.Trim())));
            }
            catch (Exception)
            {
                // 单条坏掉就跳过，不影响其它设备
            }
        }
        return pins;
    }

    /// <summary>整表回写；一台设备都没有密码时把数据流删掉，不留空壳。</summary>
    private bool WritePinsMap(Dictionary<string, string> pins)
    {
        if (pins.Count == 0)
        {
            _store.Delete(AdsStore.UnlockPinsStream);
            return true;
        }

        var data = pins.ToDictionary(
            kv => kv.Key,
            kv => Convert.ToBase64String(Scramble(Encoding.UTF8.GetBytes(kv.Value))));
        (string path, _) = _store.WriteText(AdsStore.UnlockPinsStream, JsonSerializer.Serialize(data));
        return !string.IsNullOrEmpty(path);
    }

    /// <summary>异或「伪装」：只求密码别明文躺在数据流里，不是加密，也不假装是。</summary>
    private static byte[] Scramble(byte[] raw)
    {
        var output = new byte[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            output[i] = (byte)(raw[i] ^ PinXorKey[i % PinXorKey.Length]);
        }
        return output;
    }

    private static double Now() => Environment.TickCount64 / 1000.0;

    private static void Sleep(double seconds) => Thread.Sleep(TimeSpan.FromSeconds(seconds));

    [GeneratedRegex(@"(\d+)x(\d+)")]
    private static partial Regex ScreenSizeRegex();
}
