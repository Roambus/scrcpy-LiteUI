using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Kuaitou.Core.Adb;
using Kuaitou.Core.Storage;
using Kuaitou.Interop;

namespace Kuaitou.Core.Scrcpy;

/// <summary>
/// 拉起 / 收尾 scrcpy 投屏进程。对应 legacy/kuaitou/device.py 的 _spawn / launch_app /
/// launch_desktop / _launch_result / _kill_children / cleanup_scrcpy / shutdown_all /
/// note_adb_server_before_start。
///
/// 三条与旧版一模一样的硬要求：
/// 1. 输出重定向进投屏日志：打包后没有控制台，投屏在别人电脑上起不来时只能靠这份日志定位。
/// 2. 本进程拉起的子进程要登记，退出时统一 taskkill /t（连同它拉起的 adb 一起结束），
///    否则会出现「界面关了，投屏窗口和 adb 进程还在后台跑」。
/// 3. 只关我们自己拉起来的 adb 服务，不误杀 Android Studio 等正在用的 adb。
///
/// 功能栏（投屏窗口右侧那列按钮）由界面层实现，这里只负责在窗口就绪时发
/// <see cref="WindowReady"/> 通知——Core 不认识 WPF。
/// </summary>
public sealed class ScrcpyLauncher : IDisposable
{
    private readonly AdbRunner _adb;
    private readonly ConfigStore _config;
    private readonly AdsStore _store;
    private readonly AppPaths _paths;
    private readonly VolumeService _volume;
    private readonly DeviceActionService _actions;
    private readonly ScreenPowerService _screenPower;

    // 同一台设备上的同一个应用只保留一个投屏窗口：手机端没有应用多开，重复启动要么报错
    // 要么把已有的那个顶掉。只记「镜像应用」，镜像桌面不在此列。
    private readonly object _mirrorLock = new();
    private readonly Dictionary<(string Serial, string Package), ScrcpySession> _mirror = [];

    // 本进程拉起的所有子进程与它们的日志句柄：关闭应用时要一起结束
    private readonly object _childLock = new();
    private readonly HashSet<Process> _children = [];
    private readonly Dictionary<int, FileStream> _childLogs = [];

    private readonly object _oursLock = new();
    private bool _adbServerOurs;
    private bool _adbServerNoted;
    private bool _disposed;

    public ScrcpyLauncher(
        AdbRunner adb,
        ConfigStore config,
        AdsStore store,
        AppPaths paths,
        VolumeService volume,
        DeviceActionService actions,
        ScreenPowerService screenPower)
    {
        _adb = adb;
        _config = config;
        _store = store;
        _paths = paths;
        _volume = volume;
        _actions = actions;
        _screenPower = screenPower;
    }

    /// <summary>投屏窗口出现、该挂功能栏了。参数里带上窗口句柄与设备 / 应用信息。</summary>
    public event Action<ScrcpySession>? WindowReady;

    /// <summary>某个投屏进程结束了（收尾已完成）。界面据此收掉功能栏。</summary>
    public event Action<ScrcpySession>? SessionEnded;

    /// <summary>本次 adb 服务是否由我们拉起（决定退出时要不要 kill-server）。</summary>
    public bool AdbServerOurs
    {
        get
        {
            lock (_oursLock)
            {
                return _adbServerOurs;
            }
        }
    }

    /// <summary>
    /// 首次调用 adb 之前调一次：记下 adb 服务是不是本次由我们拉起来的。
    /// 探测 127.0.0.1:5037 有没有人监听——有就是别的东西（Android Studio 等）在跑。
    /// </summary>
    public void NoteAdbServerBeforeStart()
    {
        lock (_oursLock)
        {
            if (_adbServerNoted)
            {
                return;
            }
            _adbServerNoted = true;
            _adbServerOurs = !IsAdbServerRunning();
        }
    }

    /// <summary>镜像一个应用。已在投屏时只把旧窗口唤到前台，不再拉新进程。</summary>
    public LaunchOutcome LaunchApp(
        string package,
        string? serial = null,
        string? title = null,
        string? iconPath = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = (serial ?? "", package);
        lock (_mirrorLock)
        {
            if (_mirror.TryGetValue(key, out ScrcpySession? existing) && !existing.HasExited)
            {
                existing.FocusWindow();
                return new LaunchOutcome(existing, AlreadyRunning: true);
            }
        }

        AppConfig cfg = _config.Load();
        string audioMode = cfg.AudioMode;
        if (audioMode == "pc")
        {
            // 抓的就是手机输出那路声音：手机音量压着，电脑这边就没声 / 很小，所以先拉满
            _volume.BoostMediaVolume(serial);
        }

        IReadOnlyList<string> cmd = ScrcpyCommandBuilder.Build(
            _paths.ScrcpyPath, cfg, _adb.SerialArgs(serial), package, title, WindowTools.SystemDpi);

        ScrcpySession session;
        try
        {
            session = SpawnSession(cmd, $"镜像应用 {package} @ {serial ?? "-"}", serial, package, title, iconPath);
        }
        catch (Exception)
        {
            // 投屏没起来（scrcpy.exe 被杀软拦了之类）：刚拉满的音量得还回去，
            // 否则用户的手机会一直停在最大音量上，且没有任何东西会再还原它
            if (audioMode == "pc")
            {
                _volume.RestoreMediaVolume(serial);
            }
            throw;
        }

        _volume.RecheckBoostVolume(serial);   // 投屏起来后回头看音量有没有被 ROM 拉回去
        lock (_mirrorLock)
        {
            _mirror[key] = session;
        }

        StartWatch(session, nudge: true, () =>
        {
            lock (_mirrorLock)
            {
                if (_mirror.TryGetValue(key, out ScrcpySession? current) && ReferenceEquals(current, session))
                {
                    _mirror.Remove(key);
                }
            }
            CloseLog(session.Pid);
            Sleep(1.5);
            try
            {
                _adb.Run(["shell", "am", "force-stop", package], timeoutSeconds: 5, serial: serial);
            }
            catch (Exception)
            {
                // 关窗口顺手停一下应用，失败无所谓
            }
            _volume.RestoreMediaVolume(serial);
        });
        return new LaunchOutcome(session, AlreadyRunning: false);
    }

    /// <summary>镜像手机桌面。</summary>
    public ScrcpySession LaunchDesktop(string? serial = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        AppConfig cfg = _config.Load();
        string audioMode = cfg.AudioMode;
        if (audioMode == "pc")
        {
            _volume.BoostMediaVolume(serial);
        }

        IReadOnlyList<string> cmd = ScrcpyCommandBuilder.BuildDesktop(
            _paths.ScrcpyPath, cfg, _adb.SerialArgs(serial));

        ScrcpySession session;
        try
        {
            session = SpawnSession(cmd, $"镜像桌面 @ {serial ?? "-"}", serial, package: null, title: null, iconPath: null);
        }
        catch (Exception)
        {
            if (audioMode == "pc")
            {
                _volume.RestoreMediaVolume(serial);
            }
            throw;
        }

        _volume.RecheckBoostVolume(serial);
        StartWatch(session, nudge: false, () =>
        {
            CloseLog(session.Pid);
            _volume.RestoreMediaVolume(serial);
        });
        return session;
    }

    /// <summary>
    /// 执行一个功能栏动作（返回 / 桌面 / 多任务 / 通知栏 / 控制中心 / 音量 / 强制横屏）。
    /// 转发给 <see cref="DeviceActionService"/>，让界面层不必再认识 adb。
    /// </summary>
    public bool RunAction(string? serial, string action) => _actions.Run(serial, action);

    /// <summary>功能栏置顶按钮：切画面窗口的 TOPMOST 并记进配置，下次投屏沿用。</summary>
    public bool SetTopmost(ScrcpySession session, bool on)
    {
        bool ok = WindowTools.SetTopmost(session.WindowHandle, on);
        _config.Update(c => c.AlwaysOnTop = on);
        return ok;
    }

    /// <summary>强制横屏是否开着（供功能栏按钮决定高亮）。</summary>
    public bool IsRotateLocked(string? serial) => _actions.IsRotateLocked(serial);

    /// <summary>
    /// 上次投屏时是不是开着「置顶」。这个键不写进启动参数（旧版会写，但参数化之后
    /// 一律由功能栏的 pin 按钮控制），只用来记住状态：挂功能栏时若为真就把画面窗口
    /// 设为 TOPMOST 并让 pin 按钮高亮。
    /// </summary>
    public bool AlwaysOnTopConfigured => _config.Load().AlwaysOnTop;

    /// <summary>
    /// 启动后等几秒：进程若很快退出，说明它没起来，把日志尾部返回给界面，
    /// 让用户直接看到 scrcpy 的真实报错，而不是「点了没反应」。
    /// </summary>
    public LaunchProbe Probe(ScrcpySession session, double seconds = 3.0)
    {
        long deadline = Environment.TickCount64 + (long)(seconds * 1000);
        while (Environment.TickCount64 < deadline)
        {
            if (session.HasExited)
            {
                string tail = _store.ReadLogTail(AdsStore.LaunchLogStream).Trim();
                return new LaunchProbe(false, tail.Length > 0
                    ? tail
                    : $"scrcpy 启动后立即退出（返回码 {ExitCodeOf(session)}）");
            }
            Thread.Sleep(100);
        }
        return new LaunchProbe(true);
    }

    /// <summary>
    /// 关闭应用时统一收尾。顺序与旧版一致：先把改过的手机设置还回去，再杀进程，
    /// 最后（且仅当）adb 服务是我们拉起的才 kill-server。
    /// 功能栏窗口由界面层先收（Core 不认识 WPF）。
    /// </summary>
    public void ShutdownAll()
    {
        try
        {
            // 老系统关屏时顶住过「熄屏后自动锁定」，退出前必须还给用户
            _screenPower.ReleaseLockTimeout(null);
        }
        catch (Exception)
        {
            // 退出路径上不抛
        }
        try
        {
            // 拉满的手机音量同理：关窗会直接退出，等投屏线程收尾来不及，这里先还
            _volume.RestoreAll();
        }
        catch (Exception)
        {
            // 同上
        }
        try
        {
            // 功能栏的「强制横屏」关掉过系统的自动旋转，同样必须还
            _actions.ReleaseRotateLock(null);
        }
        catch (Exception)
        {
            // 同上
        }

        CleanupScrcpy();
        KillChildren();

        if (AdbServerOurs)
        {
            try
            {
                // 只关我们自己拉起来的 adb 服务，避免误杀 Android Studio 等正在用的 adb
                RunBare(_adb.AdbPath, ["kill-server"], 5);
            }
            catch (Exception)
            {
                // 同上
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        ShutdownAll();
    }

    /// <summary>本进程当前登记的投屏进程数（诊断用）。</summary>
    public int ChildCount
    {
        get
        {
            lock (_childLock)
            {
                return _children.Count;
            }
        }
    }

    // ---------- 子进程 ----------

    private ScrcpySession SpawnSession(
        IReadOnlyList<string> cmd, string tag, string? serial, string? package, string? title, string? iconPath)
    {
        Process process = Spawn(cmd, tag, out _, iconPath);
        return new ScrcpySession(process, serial, package, title, iconPath);
    }

    /// <summary>
    /// 拉起 scrcpy，并把它的标准输出 / 错误重定向到投屏日志。
    ///
    /// 与旧版的一处差异：Python 可以把已打开的文件对象直接当子进程 stdout（句柄继承），
    /// .NET 的 ProcessStartInfo 没这个能力，所以这里改成「重定向 + 后台把两个流照抄进日志」。
    /// 结果对用户是一样的：日志里既能看到 --print-fps 的输出，也能看到启动失败的报错。
    /// </summary>
    private Process Spawn(IReadOnlyList<string> cmd, string tag, out FileStream? log, string? iconPath = null)
    {
        log = _store.OpenAppend(AdsStore.LaunchLogStream);
        if (log is not null)
        {
            WriteLogHeader(log, tag, cmd);
        }

        var psi = new ProcessStartInfo
        {
            FileName = cmd[0],
            WorkingDirectory = Path.GetDirectoryName(_paths.ScrcpyPath) ?? ".",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = log is not null,
            RedirectStandardError = log is not null,
        };

        // 投屏窗口的标题栏 / 任务栏图标：Windows 版 scrcpy 会从 SCRCPY_ICON_DIR 指向的目录里
        // 读 scrcpy.png 当窗口图标，把目标应用的图标摆成这个名字再指过去即可。
        if (PrepareIconDir(iconPath) is { } iconDir)
        {
            psi.Environment["SCRCPY_ICON_DIR"] = iconDir;
        }

        for (int i = 1; i < cmd.Count; i++)
        {
            psi.ArgumentList.Add(cmd[i]);
        }

        Process process = Process.Start(psi) ?? throw new InvalidOperationException("scrcpy 进程启动失败");

        lock (_childLock)
        {
            _children.Add(process);
            if (log is not null)
            {
                _childLogs[process.Id] = log;
            }
        }

        if (log is not null)
        {
            var gate = new object();
            _ = PumpAsync(process.StandardOutput.BaseStream, log, gate);
            _ = PumpAsync(process.StandardError.BaseStream, log, gate);
        }
        return process;
    }

    /// <summary>
    /// 把应用图标摆成 scrcpy 认的样子：Windows 版 scrcpy 从环境变量 SCRCPY_ICON_DIR 指向的
    /// 目录里读 <c>scrcpy.png</c> 当投屏窗口的标题栏 / 任务栏图标。图标本体落在数据流里
    /// （不是普通文件），所以复制一份到那个目录。任何一步失败都返回 null —— 少个图标而已，
    /// 绝不能影响投屏本身。
    /// </summary>
    private string? PrepareIconDir(string? iconPath)
    {
        if (string.IsNullOrEmpty(iconPath))
        {
            return null;
        }
        try
        {
            byte[] data = File.ReadAllBytes(iconPath);
            if (data.Length == 0)
            {
                return null;
            }

            // 一个图标一个目录（按图标路径取哈希），同一个应用重复投屏时直接覆盖。
            // 放系统临时目录：这只是给 scrcpy 读的一个中转副本，不该混进仓库或用户的数据流旁。
            string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(iconPath)))[..16];
            string dir = Path.Combine(Path.GetTempPath(), "Kuaitou-icons", name);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "scrcpy.png"), data);
            return dir;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void WriteLogHeader(FileStream log, string tag, IReadOnlyList<string> cmd)
    {
        try
        {
            byte[] header = Encoding.UTF8.GetBytes(
                $"\n===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} | {tag} =====\n{List2CmdLine(cmd)}\n");
            log.Write(header, 0, header.Length);
            log.Flush();
        }
        catch (Exception)
        {
            // 日志头写不进去不影响投屏
        }
    }

    private static async Task PumpAsync(Stream source, FileStream log, object gate)
    {
        var buffer = new byte[4096];
        while (true)
        {
            int read;
            try
            {
                read = await source.ReadAsync(buffer).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }
            if (read <= 0)
            {
                return;
            }
            lock (gate)
            {
                try
                {
                    log.Write(buffer, 0, read);
                    log.Flush();
                }
                catch (Exception)
                {
                    return;
                }
            }
        }
    }

    private void CloseLog(int pid)
    {
        FileStream? log;
        lock (_childLock)
        {
            _childLogs.Remove(pid, out log);
        }
        if (log is not null)
        {
            try
            {
                log.Dispose();
            }
            catch (Exception)
            {
                // 句柄已经没了也算收工
            }
        }
    }

    /// <summary>把所有登记的子进程连同它们拉起的进程一起结束。</summary>
    private void KillChildren()
    {
        List<Process> processes;
        lock (_childLock)
        {
            processes = [.. _children];
            _children.Clear();
        }
        lock (_mirrorLock)
        {
            _mirror.Clear();   // 进程随下面一起结束，登记表一并清空
        }

        foreach (Process process in processes)
        {
            bool alive;
            try
            {
                alive = !process.HasExited;
            }
            catch (Exception)
            {
                alive = false;
            }
            if (alive)
            {
                try
                {
                    // /t：连同它拉起的 adb 等子进程一起结束
                    RunBare("taskkill", ["/f", "/t", "/pid", process.Id.ToString()], 5);
                }
                catch (Exception)
                {
                    // 已经自己退了
                }
            }
            CloseLog(process.Id);
        }
    }

    /// <summary>兜底：把系统里所有 scrcpy.exe 结束掉（用户手动开的也会被收走，与旧版一致）。</summary>
    private static void CleanupScrcpy()
    {
        try
        {
            RunBare("taskkill", ["/f", "/im", "scrcpy.exe"], 5);
        }
        catch (Exception)
        {
            // 没有在跑的 scrcpy 时 taskkill 会返回非 0，不是错误
        }
    }

    // ---------- 守候线程 ----------

    /// <summary>
    /// 后台等待投屏窗口出现 → 戳一下强制重排 → 通知界面挂功能栏 → 等进程退出 → 收尾。
    /// </summary>
    private void StartWatch(ScrcpySession session, bool nudge, Action cleanup)
    {
        var thread = new Thread(() =>
        {
            try
            {
                nint hwnd = WindowTools.WaitForWindow(
                    session.Pid, TimeSpan.FromSeconds(15), () => session.HasExited);
                if (hwnd != 0)
                {
                    if (nudge)
                    {
                        WindowTools.Nudge(hwnd);
                    }
                    session.WindowHandle = hwnd;
                    try
                    {
                        WindowReady?.Invoke(session);
                    }
                    catch (Exception)
                    {
                        // 挂功能栏失败不该影响投屏本身
                    }
                }

                session.Process.WaitForExit();
            }
            catch (Exception)
            {
                // 守候线程出错就走到下面的收尾
            }
            finally
            {
                try
                {
                    cleanup();
                }
                catch (Exception)
                {
                    // 收尾失败不抛
                }
                try
                {
                    SessionEnded?.Invoke(session);
                }
                catch (Exception)
                {
                    // 通知界面失败不抛
                }
            }
        })
        {
            IsBackground = true,
            Name = "scrcpy-watch",
        };
        thread.Start();
    }

    // ---------- 杂项 ----------

    private static bool IsAdbServerRunning()
    {
        using var client = new TcpClient();
        try
        {
            client.Connect("127.0.0.1", 5037);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>不占 adb 并发闸、不走 -s 的裸调用（kill-server 这类根本不针对设备）。</summary>
    private static void RunBare(string fileName, IReadOnlyList<string> args, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process? process = Process.Start(psi);
        if (process is null)
        {
            return;
        }
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
        }
    }

    private static int ExitCodeOf(ScrcpySession session)
    {
        try
        {
            return session.Process.ExitCode;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static void Sleep(double seconds) => Thread.Sleep(TimeSpan.FromSeconds(seconds));

    /// <summary>按 Windows 的引用规则把参数拼回一行，只用于日志展示（方便用户复制到命令行复现）。</summary>
    private static string List2CmdLine(IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        foreach (string arg in args)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }
            sb.Append(Quote(arg));
        }
        return sb.ToString();
    }

    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return arg;
        }

        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                sb.Append(c);
                continue;
            }
            if (c == '"')
            {
                // 引号前面的反斜杠要翻倍，再转义这个引号本身
                sb.Append('\\', backslashes + 1).Append('"');
                backslashes = 0;
                continue;
            }
            backslashes = 0;
            sb.Append(c);
        }
        // 结尾的反斜杠也要翻倍，否则会把收尾的引号转义掉
        return sb.Append('\\', backslashes).Append('"').ToString();
    }
}
