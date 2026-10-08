using System.Diagnostics;
using Kuaitou.Interop;

namespace Kuaitou.Core.Scrcpy;

/// <summary>
/// 一次投屏会话。对应旧版 device.py 里 <c>_spawn</c> 出来的 Popen 加上它那几条附属信息
/// （serial / pkg / title / 图标），功能栏（投屏窗口右侧那一列按钮）要靠它们知道
/// 「按钮该发给哪台设备」「只镜像桌面才摆返回那一组」。
/// </summary>
public sealed class ScrcpySession
{
    private nint _windowHandle;

    internal ScrcpySession(
        Process process,
        string? serial,
        string? package,
        string? title,
        string? iconPath)
    {
        Process = process;
        Serial = serial;
        Package = package;
        Title = title;
        IconPath = iconPath;
    }

    internal Process Process { get; }

    public int Pid => Process.Id;

    public string? Serial { get; }

    /// <summary>被镜像的包名；null 表示这条会话是「镜像桌面」。</summary>
    public string? Package { get; }

    public string? Title { get; }

    /// <summary>目标应用图标的本地路径（用来换掉投屏窗口的标题栏 / 任务栏图标）；没有就留空。</summary>
    public string? IconPath { get; }

    /// <summary>镜像桌面走的是手机真实屏幕，只有它才摆「返回 / 桌面 / 多任务 / 通知栏 / 控制中心」。</summary>
    public bool IsDesktop => Package is null;

    /// <summary>投屏窗口句柄；窗口还没出现时为 0。由启动器的守候线程填入。</summary>
    public nint WindowHandle
    {
        get => _windowHandle;
        internal set => _windowHandle = value;
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return Process.HasExited;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    /// <summary>把投屏窗口唤到前台（再次点同一个应用时用）。</summary>
    public bool FocusWindow() => WindowTools.Focus(WindowHandle);
}

/// <summary>一次启动尝试的结果。<see cref="AlreadyRunning"/> 为 true 时表示没拉起新进程，只是把旧窗口唤到了前台。</summary>
public sealed record LaunchOutcome(ScrcpySession Session, bool AlreadyRunning);

/// <summary>启动后等几秒的探活结果：进程几秒内就退出说明没起来，<see cref="Error"/> 里带上日志尾部。</summary>
public sealed record LaunchProbe(bool Ok, string Error = "");
