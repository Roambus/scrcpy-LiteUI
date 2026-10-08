using System.Diagnostics;
using System.Text;
using Kuaitou.Core.Storage;

namespace Kuaitou.Core.Adb;

/// <summary>一次 adb 调用的结果。对应旧版 run_adb() 的 (stdout, stderr, returncode) 三元组。</summary>
public sealed record AdbResult(string Stdout, string Stderr, int ExitCode)
{
    /// <summary>命令是否成功（退出码 0）。</summary>
    public bool Ok => ExitCode == 0;

    /// <summary>stdout + stderr 拼接，旧版判定掉线时就是把两者拼起来找关键词。</summary>
    public string Combined => Stdout + Stderr;
}

/// <summary>
/// adb 调用封装。对应 legacy/kuaitou/device.py 的 run_adb / run_adb_bin / _serial_args / _reconnect。
///
/// 两条硬要求，都来自旧版踩过的真坑：
/// 1. 必须显式按 UTF-8 解码 adb 输出。打包后进程没有控制台、locale 是 GBK，用默认编码去解
///    adb 的 UTF-8 输出会抛 UnicodeDecodeError，stdout 变 None，所有调用方一起失败。
/// 2. 全局并发闸限 2：并发太多会把 adb server 打挂、把无线设备挤掉线。
/// </summary>
public sealed class AdbRunner
{
    /// <summary>不针对具体设备、无需附加 -s 的子命令。</summary>
    private static readonly HashSet<string> NoDeviceArgs =
    [
        "devices", "connect", "pair", "disconnect", "start-server",
        "kill-server", "version", "help", "mdns",
    ];

    /// <summary>掉线关键词。命中就先重连一次再重试，避免一次抖动直接报错。</summary>
    private static readonly string[] DisconnectHints =
    [
        "no devices/emulators found", "device offline", "device not found",
        "couldn't read from", "closed", "eof", "broken pipe",
    ];

    private static readonly SemaphoreSlim Slots = new(2, 2);

    private readonly string _adbPath;
    private readonly Func<AppConfig> _config;

    /// <param name="adbPath">adb.exe 的完整路径。</param>
    /// <param name="configProvider">读当前配置（多设备时挑哪台、掉线重连用哪个地址）。</param>
    public AdbRunner(string adbPath, Func<AppConfig> configProvider)
    {
        _adbPath = adbPath;
        _config = configProvider;
    }

    /// <summary>按环境解析出来的默认 adb 路径与配置来源。</summary>
    public static AdbRunner Default { get; } = new(AppPaths.Resolve().AdbPath, () => ConfigStore.Default.Load());

    public string AdbPath => _adbPath;

    /// <summary>文本调用。返回 stdout / stderr（已 Trim）与退出码；超时与异常都给 -1。</summary>
    public AdbResult Run(IReadOnlyList<string> args, int timeoutSeconds = 8, string? serial = null)
    {
        IReadOnlyList<string> full = NeedsDevice(args) ? Prepend(SerialArgs(serial), args) : args;

        Slots.Wait();
        try
        {
            ExecResult first = Exec(full, timeoutSeconds);
            if (first.TimedOut)
            {
                return new AdbResult("", "timeout", -1);
            }

            if (NeedsDevice(full) && AdbOutput.IsDisconnect(first.OutText + first.ErrText))
            {
                Reconnect(serial);
                first = Exec(full, timeoutSeconds);
                if (first.TimedOut)
                {
                    return new AdbResult("", "timeout", -1);
                }
            }
            return new AdbResult(first.OutText.Trim(), first.ErrText.Trim(), first.ExitCode);
        }
        catch (Exception e)
        {
            return new AdbResult("", e.Message, -1);
        }
        finally
        {
            Slots.Release();
        }
    }

    /// <summary>二进制调用：远程按块取 APK / 图标时用，返回原始字节；失败返回空数组。</summary>
    public byte[] RunBinary(IReadOnlyList<string> args, int timeoutSeconds = 45, string? serial = null)
    {
        IReadOnlyList<string> full = NeedsDevice(args) ? Prepend(SerialArgs(serial), args) : args;

        Slots.Wait();
        try
        {
            ExecResult first = Exec(full, timeoutSeconds);
            if (!first.TimedOut
                && NeedsDevice(full)
                && AdbOutput.IsDisconnect(Encoding.UTF8.GetString(first.ErrBytes)))
            {
                Reconnect(serial);
                first = Exec(full, timeoutSeconds);
            }
            return first.TimedOut ? [] : first.OutBytes;
        }
        catch (Exception)
        {
            return [];
        }
        finally
        {
            Slots.Release();
        }
    }

    /// <summary>
    /// 要附加的设备参数。指定 serial 时直接用 -s；没指定而同时连着多台时优先 USB
    /// （序列号不含冒号，延迟低、不掉线），否则退回配置里的地址 —— 不指定的话
    /// adb/scrcpy 会直接报 'Multiple (2) ADB devices' 失败。
    /// </summary>
    public IReadOnlyList<string> SerialArgs(string? serial)
        => AdbOutput.SelectSerialArgs(serial, DeviceInventory.GetDevices(this), _config());

    /// <summary>连接掉线后重连一次：优先重连指定设备，否则用配置里的上次地址。</summary>
    private void Reconnect(string? serial)
    {
        string addr;
        if (!string.IsNullOrEmpty(serial))
        {
            addr = serial;
        }
        else
        {
            AppConfig cfg = _config();
            addr = !string.IsNullOrEmpty(cfg.Ip) && !string.IsNullOrEmpty(cfg.Port)
                ? $"{cfg.Ip}:{cfg.Port}"
                : "";
        }

        if (addr.Length == 0)
        {
            return;
        }
        // 这里已经握着槽位，用不占槽的裸调用，避免和 SerialArgs 抢闸死等
        Exec(["connect", addr], 15);
    }

    private static bool NeedsDevice(IReadOnlyList<string> args)
        => args.Count > 0 && !NoDeviceArgs.Contains(args[0]);

    private static IReadOnlyList<string> Prepend(IReadOnlyList<string> prefix, IReadOnlyList<string> args)
    {
        var list = new List<string>(prefix.Count + args.Count);
        list.AddRange(prefix);
        list.AddRange(args);
        return list;
    }

    private ExecResult Exec(IReadOnlyList<string> args, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _adbPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // UTF-8 显式解码，见类注释第 1 条
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("adb 进程启动失败");

        Task<byte[]> outTask = ReadAllAsync(process.StandardOutput.BaseStream);
        Task<byte[]> errTask = ReadAllAsync(process.StandardError.BaseStream);

        bool exited = process.WaitForExit(timeoutSeconds * 1000);
        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 进程可能刚好自己退了
            }
            process.WaitForExit();
            return new ExecResult([], [], -1, TimedOut: true);
        }

        byte[] outBytes = outTask.GetAwaiter().GetResult();
        byte[] errBytes = errTask.GetAwaiter().GetResult();
        return new ExecResult(outBytes, errBytes, process.ExitCode, TimedOut: false);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms).ConfigureAwait(false);
        return ms.ToArray();
    }

    private readonly record struct ExecResult(byte[] OutBytes, byte[] ErrBytes, int ExitCode, bool TimedOut)
    {
        public string OutText => Encoding.UTF8.GetString(OutBytes);

        public string ErrText => Encoding.UTF8.GetString(ErrBytes);
    }
}
