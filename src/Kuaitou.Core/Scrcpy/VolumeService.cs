using System.Globalization;
using System.Text.RegularExpressions;
using Kuaitou.Core.Adb;
using Kuaitou.Core.Storage;

namespace Kuaitou.Core.Scrcpy;

/// <summary>
/// 手机媒体音量的读 / 设 / 投屏前拉满 / 关窗还原。对应 legacy/kuaitou/device.py 的
/// _parse_volume / _parse_dumpsys_volume / _media_volume / set_media_volume /
/// boost_media_volume / recheck_boost_volume / restore_media_volume / restore_all_media_volume。
///
/// 「仅电脑播放」时 scrcpy 抓的就是手机输出那一路声音，手机音量压着，电脑这边就没声 / 很小，
/// 所以投屏前拉满、关窗时还回去。改了用户手机上的设置就必须记着还，跟「顶长熄屏超时」
/// 「强制横屏」是同一个道理。
/// </summary>
public sealed partial class VolumeService
{
    /// <summary>STREAM_MUSIC：投屏抓的就是这一路。</summary>
    private const string VolumeStream = "3";

    private const int StepKeys = 30;        // 一轮最多按这么多次，按不动就收手
    private const double Settle = 0.4;      // 按完等一下再读：音频服务记下新值有一点点延迟

    /// <summary>
    /// 投屏起来后复查音量的时间点（秒，相对投屏启动）。真机实测：镜像一开音频通路切换，
    /// ROM 会按「输出设备的记忆值」把媒体音量拉回去（70 → 40 都见过），不能只拉一次。
    /// </summary>
    private static readonly double[] BoostRechecks = [3.0, 5.0, 7.0];

    private readonly AdbRunner _adb;
    private readonly AdsStore _store;

    // 拉满登记表：序列号 -> 拉满之前用户自己的音量
    private readonly object _boostedLock = new();
    private readonly Dictionary<string, int> _boosted = [];

    // 序列号 -> 锁：拉满 / 投屏后复查 / 还原 三件事不许互相插队
    private readonly object _opsLock = new();
    private readonly Dictionary<string, SemaphoreSlim> _ops = [];

    public VolumeService(AdbRunner adb, AdsStore store)
    {
        _adb = adb;
        _store = store;
    }

    /// <summary>
    /// 从 <c>volume is 7 in range [0..15]</c> 里抠出 (当前值, 上限)。
    ///
    /// 必须认这句而不是「把输出里的数字都抓出来」：<c>cmd media_session</c> 会先回显
    /// <c>[V] will control stream=3 (STREAM_MUSIC)</c>，里面的 3 会被误当成音量。
    /// </summary>
    public static (int Cur, int Top) ParseVolumeOutput(string? output)
    {
        Match m = RangeRegex().Match(output ?? "");
        if (m.Success)
        {
            return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
        }

        // 有的 ROM 只回一个裸数字（拿不到上限，用 -1 表示）
        m = BareNumberRegex().Match(output ?? "");
        return m.Success ? (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), -1) : (-1, -1);
    }

    /// <summary>
    /// 从 <c>dumpsys audio</c> 里抠 STREAM_MUSIC 的 (当前值, 上限)。
    ///
    /// 兜底用：<c>cmd media_session volume --get</c> 在某些 ROM 上会漏报或报旧值，
    /// dumpsys 里那份是音频服务自己的状态。
    /// </summary>
    public static (int Cur, int Top) ParseDumpsysVolume(string? output)
    {
        foreach (Match block in DumpsysBlockRegex().Matches(output ?? ""))
        {
            Match value = StreamVolumeRegex().Match(block.Groups[1].Value);
            if (!value.Success)
            {
                continue;
            }
            Match top = MaxRegex().Match(block.Groups[1].Value);
            return (int.Parse(value.Groups[1].Value, CultureInfo.InvariantCulture),
                    top.Success ? int.Parse(top.Groups[1].Value, CultureInfo.InvariantCulture) : -1);
        }
        return (-1, -1);
    }

    /// <summary>
    /// 读媒体音量 → (当前值, 上限)；读不到给 (-1, -1)。
    ///
    /// Android 16 起 <c>media</c> 这条 shell 命令已经没了（真机报
    /// <c>media: inaccessible or not found</c>），得改用 <c>cmd media_session</c>。
    /// 这里再拿 <c>dumpsys audio</c> 兜一层：读数被 ROM 漏报时，后面按音量键的逻辑
    /// 不会误以为「按不动了」而提前收手。
    /// </summary>
    public (int Cur, int Top) MediaVolume(string? serial)
    {
        (int Cur, int Top) fallback = (-1, -1);   // 读到了值但没读到上限：先留着，能读到更全的就用更全的
        string[][] probes =
        [
            ["shell", "cmd", "media_session", "volume", "--stream", VolumeStream, "--get"],
            ["shell", "media", "volume", "--stream", VolumeStream, "--get"],
        ];
        foreach (string[] args in probes)
        {
            AdbResult r = _adb.Run(args, timeoutSeconds: 6, serial: serial);
            if (r.ExitCode != 0)
            {
                continue;
            }
            (int Cur, int Top) parsed = ParseVolumeOutput(r.Stdout);
            if (parsed.Cur >= 0 && parsed.Top > 0)
            {
                return parsed;
            }
            if (parsed.Cur >= 0 && fallback.Cur < 0)
            {
                fallback = parsed;
            }
        }

        AdbResult dump = _adb.Run(["shell", "dumpsys", "audio"], timeoutSeconds: 15, serial: serial);
        if (dump.ExitCode == 0)
        {
            (int Cur, int Top) parsed = ParseDumpsysVolume(dump.Stdout);
            if (parsed.Cur >= 0 && parsed.Top > 0)
            {
                return parsed;
            }
        }
        return fallback;
    }

    /// <summary>当前媒体音量，读不到给 -1。</summary>
    public int GetMediaVolume(string? serial) => MediaVolume(serial).Cur;

    /// <summary>
    /// 把媒体音量设成 vol，返回是否真的设成了。
    ///
    /// 真机实测（Android 16 / vivo）：<c>cmd media_session volume --set</c> 会在日志里留下
    /// <c>setStreamVolume(index:...)</c> 却立刻被 ROM 回滚，只有音量键是真的生效。所以先试
    /// 命令，读回来核对；没变就退回音量键，一步步按过去。
    /// </summary>
    public bool SetMediaVolume(int vol, string? serial)
    {
        try
        {
            _adb.Run(
                ["shell", "cmd", "media_session", "volume", "--stream", VolumeStream, "--set",
                 vol.ToString(CultureInfo.InvariantCulture)],
                timeoutSeconds: 6,
                serial: serial);
            Thread.Sleep(TimeSpan.FromSeconds(0.2));
            int cur = MediaVolume(serial).Cur;
            if (cur == vol)
            {
                return true;
            }

            bool ok = PressVolumeTo(vol, cur, serial);
            if (!ok)
            {
                // 读数被 ROM 拖着不更新时，上面会误判成没到位。以最终读回值再确认一次，
                // 免得明明拉满了还去还原。
                ok = MediaVolume(serial).Cur == vol;
            }
            return ok;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 把手机媒体音量拉到最大，返回原值；不需要改（已最大 / 读不到 / 改不动）时返回 null。
    /// </summary>
    public int? BoostMediaVolume(string? serial)
    {
        string key = serial ?? "";
        SemaphoreSlim op = OpLock(key);
        op.Wait();
        try
        {
            (int cur, int top) = MediaVolume(serial);
            if (cur < 0 || top <= 0)
            {
                Log($"读不到媒体音量，跳过拉满（serial={key}）");
                return null;
            }
            if (cur >= top)
            {
                Log($"媒体音量本来就在最大（{cur}/{top}），不用动");
                return null;
            }

            lock (_boostedLock)
            {
                if (_boosted.TryGetValue(key, out int remembered))
                {
                    return remembered;   // 这台已经拉满过，别把拉满后的值当成原值记第二次
                }
            }

            if (!SetMediaVolume(top, serial))
            {
                Log($"拉满失败：{cur} → {MediaVolume(serial).Cur}，目标是 {top}（serial={key}）");
                return null;             // 没真拉上去就别登记，免得还原时去动用户的音量
            }

            lock (_boostedLock)
            {
                _boosted[key] = cur;
            }
            Log($"已拉满：{cur} → {top}（关窗口后还原成 {cur}；serial={key}）");
            return cur;
        }
        catch (Exception e)
        {
            Log($"拉满出错：{e.Message}");
            return null;
        }
        finally
        {
            op.Release();
        }
    }

    /// <summary>
    /// 投屏起来之后按 3 / 5 / 7 秒复查几遍：有的 ROM 会在音频通路切换时把音量拉回去。
    /// 跑在后台线程上，调用方不用等。每遍都先确认「这台还在拉满名单里」才动手，
    /// 免得窗口已经关了还去改用户自己的音量。
    /// </summary>
    public void RecheckBoostVolume(string? serial)
    {
        string key = serial ?? "";
        _ = Task.Run(() =>
        {
            SemaphoreSlim op = OpLock(key);
            long started = Environment.TickCount64;
            foreach (double at in BoostRechecks)
            {
                double remain = at - (Environment.TickCount64 - started) / 1000.0;
                if (remain > 0)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(remain));
                }
                lock (_boostedLock)
                {
                    if (!_boosted.ContainsKey(key))
                    {
                        return;              // 已经还原过（窗口关了），别再往回拉
                    }
                }

                if (!op.Wait(0))
                {
                    continue;                // 正有拉满 / 还原在跑，这一轮别插队
                }
                try
                {
                    lock (_boostedLock)
                    {
                        if (!_boosted.ContainsKey(key))
                        {
                            return;
                        }
                    }
                    (int cur, int top) = MediaVolume(serial);
                    if (cur >= 0 && cur < top)
                    {
                        Log(SetMediaVolume(top, serial)
                            ? $"投屏后复查：音量被拉回到 {cur}，已重新拉满到 {top}（serial={key}）"
                            : $"投屏后复查：音量是 {cur}（未满 {top}），再拉也没拉动（serial={key}）");
                    }
                }
                catch (Exception)
                {
                    // 复查失败无所谓，用户手上的音量才是本体
                }
                finally
                {
                    op.Release();
                }
            }
        });
    }

    /// <summary>
    /// 把拉满的音量还给用户：投屏窗口关闭时调。认的是启动时那台设备，所以 key 用
    /// <c>serial ?? ""</c> —— 与「传 None 表示全部还原」区分开。
    /// </summary>
    public void RestoreMediaVolume(string? serial)
    {
        string key = serial ?? "";
        SemaphoreSlim op = OpLock(key);
        op.Wait();
        try
        {
            int? orig;
            lock (_boostedLock)
            {
                orig = _boosted.TryGetValue(key, out int remembered) ? remembered : null;
                _boosted.Remove(key);
            }
            if (orig is { } value)
            {
                SetMediaVolume(value, serial);
                // 记下实际读回值：音量档位多半只能按步进走，原值又未必是步进的整数倍，可能差一格
                Log($"已还原成 {value}（实际 {MediaVolume(serial).Cur}；serial={key}）");
            }
        }
        finally
        {
            op.Release();
        }
    }

    /// <summary>全部还原：退出应用时调（关窗直接退出，等投屏线程收尾来不及）。</summary>
    public void RestoreAll()
    {
        List<(string Key, int Orig)> targets;
        lock (_boostedLock)
        {
            targets = _boosted.Select(kv => (kv.Key, kv.Value)).ToList();
            _boosted.Clear();
        }
        foreach ((string key, int orig) in targets)
        {
            SemaphoreSlim op = OpLock(key);
            op.Wait();
            try
            {
                SetMediaVolume(orig, key.Length == 0 ? null : key);
            }
            finally
            {
                op.Release();
            }
        }
    }

    /// <summary>
    /// 按一下音量键并读回新值；读到没变或读不到就再等等重读一次。
    ///
    /// 读数滞后会让人误判成「按不动了」，从而在离目标还很远时就收手 —— 这正是
    /// 「选了仅电脑播放，音量却没调上去」的常见由来，所以这里宁可多读一次。
    /// </summary>
    private int VolumeAfterKey(string key, string? serial, int previous)
    {
        _adb.Run(["shell", "input", "keyevent", key], timeoutSeconds: 6, serial: serial);
        int cur = -1;
        for (int i = 0; i < 2; i++)
        {
            Thread.Sleep(TimeSpan.FromSeconds(Settle));
            cur = MediaVolume(serial).Cur;
            if (cur >= 0 && cur != previous)
            {
                return cur;
            }
        }
        return cur;
    }

    /// <summary>
    /// 把媒体音量挪到 vol，返回是否真的到位。
    ///
    /// 一下一下按太慢：真机一次按键往返约半秒。改成先按一下量出步进（各家 ROM 是 10 还是 15
    /// 不一样），再一口气把剩下的按完，最后读回核对；核不上就按量出的步进再补一轮。
    /// </summary>
    private bool PressVolumeTo(int vol, int cur, string? serial)
    {
        if (cur < 0)
        {
            return false;
        }

        for (int round = 0; round < 3; round++)
        {
            if (cur == vol)
            {
                return true;
            }

            string key = vol > cur ? "24" : "25";   // 24 = 音量加，25 = 音量减
            int before = cur;
            cur = VolumeAfterKey(key, serial, before);
            int step = cur - before;
            if (cur < 0 || step == 0 || (step > 0) != (key == "24"))
            {
                return false;      // 到头了，或者被 ROM / 前台的播放器顶回来了
            }

            step = Math.Abs(step);
            // 向下取整：宁可最后差一格，也别按过头。原值不是步进整数倍时（真机 150 档、步进 10，
            // 用户原值可能是 36 这种）目标本身就按不到，向上取整会直接冲过去（36 → 0 见过）。
            int need = Math.Min(Math.Abs(vol - cur) / step, StepKeys);
            if (need > 0)
            {
                var args = new List<string> { "shell", "input", "keyevent" };
                for (int i = 0; i < need; i++)
                {
                    args.Add(key);
                }
                _adb.Run(args, timeoutSeconds: 10 + need, serial: serial);
            }
            Thread.Sleep(TimeSpan.FromSeconds(Settle));
            cur = MediaVolume(serial).Cur;
        }
        return cur == vol;
    }

    private SemaphoreSlim OpLock(string key)
    {
        lock (_opsLock)
        {
            if (!_ops.TryGetValue(key, out SemaphoreSlim? gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _ops[key] = gate;
            }
            return gate;
        }
    }

    /// <summary>音量处理结果写进投屏日志：窗口程序没有控制台，用户报「没调到最大」时只能靠它复盘。</summary>
    private void Log(string message)
        => _store.WriteText(
            AdsStore.LaunchLogStream,
            $"\n[volume {DateTime.Now:HH:mm:ss}] {message}\n",
            append: true);

    [GeneratedRegex(@"volume is (\d+)\s+in range \[(\d+)\.\.(\d+)\]")]
    private static partial Regex RangeRegex();

    [GeneratedRegex(@"^\s*(\d+)\s*$")]
    private static partial Regex BareNumberRegex();

    [GeneratedRegex(@"STREAM_MUSIC:(.{0,400}?)(?=STREAM_[A-Z]|$)", RegexOptions.Singleline)]
    private static partial Regex DumpsysBlockRegex();

    [GeneratedRegex(@"streamVolume:(\d+)")]
    private static partial Regex StreamVolumeRegex();

    [GeneratedRegex(@"Max:\s*(\d+)")]
    private static partial Regex MaxRegex();
}
