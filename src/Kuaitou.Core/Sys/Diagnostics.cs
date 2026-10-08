using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Kuaitou.Core.Adb;
using Kuaitou.Core.Storage;

namespace Kuaitou.Core.Sys;

/// <summary>
/// 一键诊断报告。对应 legacy/kuaitou/system.py 的 <c>run_diag</c> / <c>_fps_report</c> /
/// <c>_run_capture</c> / <c>_os_desc</c>。
///
/// 换台电脑后「adb 连得上、却扫不出应用 / 投不出画面」时，把每个环节的实际输出汇总成一份
/// 报告，直接指出卡在哪一步。报告只在内存里生成文本，保存位置由用户自选（走 App 层的另存为）。
///
/// 命名空间叫 Sys 而不是 System：后者会劫持所有 <c>System.*</c> 的解析。
/// </summary>
public sealed partial class Diagnostics
{
    /// <summary>深诊断里 scrcpy --list-apps 的超时，与扫应用同值。</summary>
    private const int AppsScanTimeoutSeconds = 120;

    /// <summary>命令行采集的超时（scrcpy --version / adb version / adb devices -l 都用它）。</summary>
    private const int CaptureTimeoutSeconds = 20;

    private readonly AppPaths _paths;
    private readonly ConfigStore _config;
    private readonly AdsStore _store;
    private readonly AdbRunner _adb;
    private readonly DeviceInventory _inventory;
    private readonly Func<bool> _adbServerOurs;

    public Diagnostics(
        AppPaths paths,
        ConfigStore config,
        AdsStore store,
        AdbRunner adb,
        DeviceInventory inventory,
        Func<bool> adbServerOurs)
    {
        _paths = paths;
        _config = config;
        _store = store;
        _adb = adb;
        _inventory = inventory;
        _adbServerOurs = adbServerOurs;
    }

    /// <summary>生成诊断报告文本。<paramref name="deep"/> 为真时额外实际跑一次 scrcpy --list-apps。</summary>
    public string Run(bool deep)
    {
        var lines = new List<string>();
        void Add(string key, string value = "") => lines.Add(value.Length > 0 ? $"{key}: {value}" : key);
        void Block(string title, int rc, string output)
        {
            Add("");
            Add($"== {title}  rc={rc} ==");
            Add(Indent(output));
        }

        Add("== 环境 ==");
        Add("时间", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        Add("快投版本", AppInfo.Version);
        Add("系统", RuntimeInformation.OSDescription);
        Add("打包运行", AppPaths.IsBundledApp ? "是" : "否");
        Add(".NET", RuntimeInformation.FrameworkDescription);
        Add("默认编码", Encoding.Default.WebName);
        Add("只读资源目录", _paths.ResDir);
        Add("可写数据目录", _paths.DataDir);
        Add("ANDROID_ADB_SERVER_PORT", Environment.GetEnvironmentVariable("ANDROID_ADB_SERVER_PORT") ?? "(未设置)");
        Add("adb 服务是本次启动的", _adbServerOurs().ToString());
        Add("scrcpy.exe 存在", File.Exists(_paths.ScrcpyPath).ToString());
        Add("scrcpy-server 存在", File.Exists(Path.Combine(Path.GetDirectoryName(_paths.ScrcpyPath) ?? ".", "scrcpy-server")).ToString());

        string devices;
        try
        {
            devices = string.Join(", ", _inventory.GetDevices());
        }
        catch (Exception e)
        {
            devices = $"(查询失败：{e.Message})";
        }
        Add("已连接设备", devices.Length > 0 ? devices : "(无)");

        Add("");
        Add("== 存储位置 ==");
        foreach (string line in _store.LocationReport().Split('\n'))
        {
            if (line.TrimEnd('\r').Length > 0)
            {
                Add(line.TrimEnd('\r'));
            }
        }

        (int rc, string output) = Capture([_paths.ScrcpyPath, "--version"], CaptureTimeoutSeconds);
        Block("scrcpy --version", rc, output);

        (rc, output) = Capture([_paths.AdbPath, "version"], CaptureTimeoutSeconds);
        Block("adb version", rc, output);

        (rc, output) = Capture([_paths.AdbPath, "devices", "-l"], CaptureTimeoutSeconds);
        Block("adb devices -l", rc, output);

        (rc, output) = Capture([_paths.AdbPath, .. _adb.SerialArgs(null), "shell", "getprop", "ro.product.model"], CaptureTimeoutSeconds);
        Block("adb shell getprop", rc, output);

        AppConfig cfg = _config.Load();
        Add("");
        Add("== 画面设置 ==");
        Add("视频编码", cfg.VideoCodec);
        Add("最大尺寸", cfg.MaxSize);
        Add("目的帧率上限", cfg.Fps);
        Add("码率(Mbps)", cfg.Bitrate);
        Add("掉线自动重连", cfg.ReconnectEnabled.ToString());
        Add("实测帧率", FpsReport(_store.ReadText(AdsStore.LaunchLogStream) ?? ""));

        if (deep)
        {
            Add("");
            Add("== scrcpy --list-apps（扫应用用的就是这一步）==");
            (rc, output) = Capture([_paths.ScrcpyPath, .. _adb.SerialArgs(null), "--list-apps"], AppsScanTimeoutSeconds);
            Add($"rc={rc}");
            string tail = output.Length > 1500 ? output[^1500..] : output;
            Add(Indent(tail));
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// 从投屏日志里提取 scrcpy --print-fps 的输出（形如 <c>60 fps</c> / <c>58 fps (+2 frames skipped)</c>）。
    /// 投屏命令都带 --print-fps，所以最近一次投屏的实测帧率就躺在日志里。
    /// </summary>
    public static IReadOnlyList<int> ParseFpsSamples(string log)
    {
        var values = new List<int>();
        foreach (Match m in FpsRegex().Matches(log))
        {
            if (int.TryParse(m.Groups[1].Value, out int v))
            {
                values.Add(v);
            }
        }
        return values.Count <= 15 ? values : values[^15..];
    }

    /// <summary>把帧率采样拼成一句给人看的话；日志里还没有记录时如实说明怎么产生。</summary>
    public static string FpsReport(string log)
    {
        IReadOnlyList<int> values = ParseFpsSamples(log);
        if (values.Count == 0)
        {
            return "（日志里还没有帧率记录：先用「镜像桌面 / 镜像应用」投一次屏再看）";
        }
        int min = values.Min();
        int max = values.Max();
        double avg = values.Average();
        return $"最近 {values.Count} 次采样：平均 {avg:F0} FPS，最低 {min}，最高 {max}，末次 {values[^1]}";
    }

    private static string Indent(string output)
        => output.Length > 0 ? output.Replace("\r\n", "\n").Replace("\n", "\n  ") : "(无输出)";

    /// <summary>
    /// 跑一条命令并采集它的合并输出（stdout + stderr）。对应旧版 <c>_run_capture</c>：
    /// 工作目录固定到 scrcpy/ 下（scrcpy-server 等相对路径要靠它），显式 UTF-8 解码，
    /// 无窗口，超时就杀进程树。
    /// </summary>
    private (int Rc, string Output) Capture(IReadOnlyList<string> cmd, int timeoutSeconds)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cmd[0],
                WorkingDirectory = Path.GetDirectoryName(_paths.ScrcpyPath) ?? ".",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            for (int i = 1; i < cmd.Count; i++)
            {
                psi.ArgumentList.Add(cmd[i]);
            }

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                return (-1, "异常：进程启动失败");
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(timeoutSeconds * 1000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // 已经退了
                }
                return (-1, "异常：命令超时");
            }

            return (process.ExitCode, (stdout + "\n" + stderr).Trim());
        }
        catch (Exception e)
        {
            return (-1, $"异常：{e.Message}");
        }
    }

    [GeneratedRegex(@"(\d+)\s*fps\b", RegexOptions.IgnoreCase)]
    private static partial Regex FpsRegex();
}
