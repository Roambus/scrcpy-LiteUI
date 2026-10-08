using System.Globalization;
using Kuaitou.Core.Storage;

namespace Kuaitou.Core.Scrcpy;

/// <summary>
/// 拼 scrcpy 命令行。对照 legacy/kuaitou/device.py 的 build_scrcpy_cmd / _stream_args /
/// _video_args / _mode_args，参数顺序一字不差。
///
/// 刻意做成纯函数：不碰 adb、不读文件、电脑 DPI 从外面传进来。这是整个重写里最该被测死的
/// 一段逻辑，所以一点副作用都不要有。
///
/// 唯一的例外是「投屏窗口置顶」：旧版是启动期的 <c>--always-on-top</c>，新版挪进投屏窗口
/// 右侧功能栏、运行中随时可切，由窗口层用 SetWindowPos 处理，这条命令里不再出现。
/// </summary>
public static class ScrcpyCommandBuilder
{
    /// <summary>虚拟屏密度（UI 放大倍数 × 电脑 DPI）的下限；再小画面就糊了。</summary>
    private const int MinDpi = 72;

    /// <summary>电脑 DPI 取不到时的兜底值（100% 缩放）。</summary>
    public const int DefaultPcDpi = 96;

    /// <summary>
    /// 拼一条投屏命令。镜像桌面时 <paramref name="pkg"/> 留空；镜像应用时传包名与标题。
    /// </summary>
    public static IReadOnlyList<string> Build(
        string scrcpyPath,
        AppConfig cfg,
        IReadOnlyList<string> serialArgs,
        string? pkg = null,
        string? title = null,
        int pcDpi = DefaultPcDpi)
    {
        var cmd = new List<string> { scrcpyPath };
        cmd.AddRange(serialArgs);
        cmd.Add("--flex-display");
        cmd.Add("--stay-awake");
        cmd.AddRange(StreamArgs(cfg));
        cmd.AddRange(VideoArgs(cfg));
        cmd.AddRange(ModeArgs(cfg));

        if (cfg.AudioMode == "phone")
        {
            // 仅手机播放：scrcpy 默认把声音也抓到电脑，这条把它关掉
            cmd.Add("--no-audio");
        }

        if (!string.IsNullOrEmpty(pkg))
        {
            if (!string.IsNullOrEmpty(title))
            {
                // 标题栏写目标应用的名字：默认那个 "scrcpy" 看不出投的是哪个应用
                cmd.Add("--window-title=" + title);
            }
            cmd.Add($"--new-display={cfg.ResW}x{cfg.ResH}/{Dpi(cfg, pcDpi)}");
            cmd.Add("--no-vd-system-decorations");   // 虚拟屏里不画状态栏 / 导航栏
            cmd.Add("--start-app=" + pkg);
        }

        return cmd;
    }

    /// <summary>
    /// 拼「镜像桌面」的命令。与镜像应用是两条独立的命令（旧版如此，不能合并）：
    /// 桌面走的是手机真实屏幕，所以**没有** <c>--flex-display</c>，也不需要虚拟屏那一组参数；
    /// 标题固定写「镜像桌面」，免得窗口只显示默认的 "scrcpy"。
    /// </summary>
    public static IReadOnlyList<string> BuildDesktop(
        string scrcpyPath,
        AppConfig cfg,
        IReadOnlyList<string> serialArgs)
    {
        var cmd = new List<string> { scrcpyPath };
        cmd.AddRange(serialArgs);
        cmd.Add("--stay-awake");
        cmd.Add("--window-title=镜像桌面");
        cmd.AddRange(StreamArgs(cfg));
        cmd.AddRange(VideoArgs(cfg));
        cmd.AddRange(ModeArgs(cfg));

        if (cfg.AudioMode == "phone")
        {
            cmd.Add("--no-audio");
        }
        return cmd;
    }

    /// <summary>
    /// 虚拟屏密度：1 倍放大 = 电脑 DPI（新虚拟显示器的密度与电脑一致，观感最接近原生）。
    /// 取整按银行家舍入，跟 Python 的 <c>round()</c> 一致。
    /// </summary>
    public static int Dpi(AppConfig cfg, int pcDpi)
        => Math.Max(MinDpi, (int)Math.Round(pcDpi * Scale(cfg)));

    /// <summary>UI 放大倍数；填了非数字（界面上是个自由输入框）就按 1.0 算。</summary>
    private static double Scale(AppConfig cfg)
        => double.TryParse(
            (cfg.Scale ?? "").Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double value)
            ? value
            : 1.0;

    /// <summary>码率 / 帧率：镜像应用和镜像桌面共用，避免两边设置不一致。</summary>
    private static IEnumerable<string> StreamArgs(AppConfig cfg)
    {
        yield return "--video-bit-rate=" + cfg.Bitrate + "M";
        yield return "--max-fps=" + cfg.Fps;
    }

    /// <summary>画面公共参数：实测帧率输出（诊断报告用）、视频编码、最大尺寸。</summary>
    private static IEnumerable<string> VideoArgs(AppConfig cfg)
    {
        yield return "--print-fps";   // 每秒把实测帧率写进投屏日志，供诊断报告提取

        string codec = (cfg.VideoCodec ?? "auto").Trim().ToLowerInvariant();
        if (codec is "h264" or "h265")
        {
            yield return "--video-codec=" + codec;
        }
        // auto 不传：交给 scrcpy 自己挑，免得锁死编码器

        if (int.TryParse((cfg.MaxSize ?? "").Trim(), out int maxSize) && maxSize > 0)
        {
            yield return "--max-size=" + maxSize.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>「怎么投、能不能控」这一类开关：镜像应用与镜像桌面共用。</summary>
    private static IEnumerable<string> ModeArgs(AppConfig cfg)
    {
        // 键盘固定走 UHID：把电脑键盘直接当成手机的外设，打字就落在手机上，不依赖手机输入法。
        // 按用户要求常开、界面上不给开关，所以不放进配置。
        yield return "--keyboard=uhid";

        if (cfg.NoControl)
        {
            // 只看不控：scrcpy 不再注入任何输入。功能栏上的按钮走的是 adb，跟这条无关。
            yield return "--no-control";
        }
        if (cfg.PowerOffOnClose)
        {
            // 关窗熄屏：关掉投屏窗口时顺手关掉手机屏幕（不锁屏）
            yield return "--power-off-on-close";
        }
        if (cfg.MouseCapture)
        {
            // 鼠标捕获：鼠标变相对模式、光标锁在窗口里，玩游戏那类场景才要
            yield return "--mouse=uhid";
        }
    }
}
