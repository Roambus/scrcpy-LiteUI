using System.Text;

namespace Kuaitou.Interop;

/// <summary>
/// 围绕「别人的窗口」的一组高层操作：按进程找窗口、等窗口出现、唤到前台、戳一下强制重排、
/// 量客户区、设属主、切置顶。对应 legacy/kuaitou/device.py 的 _find_window_by_pid /
/// _wait_window / focus_proc_window / nudge_scrcpy_window 与 winbar.py 里的窗口几何那几个函数。
///
/// 只做 Win32 调用，不掺业务：谁用谁负责决定超时与失败语义。
/// </summary>
public static class WindowTools
{
    /// <summary>DPI 取不到时的兜底值（100% 缩放）。</summary>
    public const int DefaultDpi = 96;

    /// <summary>
    /// 屏幕坐标下的客户区矩形。用客户区而不是整窗矩形：功能栏要贴着画面右边缘，
    /// 标题栏和边框都不算数；顺带全屏（没有标题栏）时照样算得出来。
    /// </summary>
    public static Rect32? ClientRectOnScreen(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
        {
            return null;
        }
        if (!NativeMethods.GetClientRect(hwnd, out Rect32 rc))
        {
            return null;
        }
        var origin = new Point32();
        if (!NativeMethods.ClientToScreen(hwnd, ref origin))
        {
            return null;
        }
        if (rc.Width <= 0 || rc.Height <= 0)
        {
            return null;
        }
        return new Rect32
        {
            Left = origin.X,
            Top = origin.Y,
            Right = origin.X + rc.Width,
            Bottom = origin.Y + rc.Height,
        };
    }

    /// <summary>整窗矩形（含标题栏与边框）。</summary>
    public static Rect32? WindowRect(nint hwnd)
        => hwnd != 0 && NativeMethods.GetWindowRect(hwnd, out Rect32 rc) ? rc : null;

    /// <summary>按 PID 找该进程的第一个可见顶层窗口（scrcpy 只开一个主窗口）。</summary>
    public static nint FindVisibleWindowByPid(int pid, Func<nint, bool>? skip = null)
    {
        nint found = 0;
        try
        {
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint windowPid);
                if (windowPid != (uint)pid || !NativeMethods.IsWindowVisible(hwnd))
                {
                    return true;
                }
                if (skip is not null && skip(hwnd))
                {
                    return true;
                }
                found = hwnd;
                return false;
            }, 0);
        }
        catch (Exception)
        {
            return 0;
        }
        return found;
    }

    /// <summary>
    /// 等目标进程的主窗口出现；进程先退出（启动失败）或超时都返回 0。
    /// <paramref name="processGone"/> 用来判断进程是否还活着。
    /// </summary>
    public static nint WaitForWindow(int pid, TimeSpan timeout, Func<bool>? processGone = null)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            nint hwnd = FindVisibleWindowByPid(pid);
            if (hwnd != 0)
            {
                return hwnd;
            }
            if (processGone?.Invoke() == true)
            {
                return 0;
            }
            Thread.Sleep(300);
        }
        return 0;
    }

    /// <summary>把窗口从最小化 / 别的窗口后面唤到前台。</summary>
    public static bool Focus(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }
        try
        {
            // ASFW_ANY：不给这个许可的话跨进程只能抢到任务栏闪烁
            NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            return NativeMethods.SetForegroundWindow(hwnd);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 把窗口宽高各 +1 像素再放回去，强制窗口 / 渲染重新布局。
    /// scrcpy 首次显示时画面偶尔会糊着第一帧，戳一下就好了。
    /// </summary>
    public static bool Nudge(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }
        try
        {
            if (!NativeMethods.GetWindowRect(hwnd, out Rect32 rect))
            {
                return false;
            }
            return NativeMethods.SetWindowPos(
                hwnd, 0, rect.Left, rect.Top, rect.Width + 1, rect.Height + 1,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 把窗口标成「叠加层」：不进 Alt+Tab（TOOLWINDOW）、点它不抢焦点（NOACTIVATE）。
    /// 配合 WPF 的 AllowsTransparency 窗口，透明像素既不显示也收不到鼠标，直接穿到下面去。
    /// </summary>
    public static void MakeOverlay(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
        {
            return;
        }
        try
        {
            nint current = NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE);
            long value = current.ToInt64()
                         | NativeMethods.WS_EX_TOOLWINDOW
                         | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE, new nint(value));
        }
        catch (Exception)
        {
            // 设不上只是多了个任务栏条目 / 点一下会抢焦点，窗口本身还能用
        }
    }

    /// <summary>把 <paramref name="hwnd"/> 的属主设成 <paramref name="owner"/>：永远压在属主之上、跟着一起最小化。</summary>
    public static void SetOwner(nint hwnd, nint owner)
    {
        try
        {
            NativeMethods.SetWindowLongPtrW(hwnd, NativeMethods.GWLP_HWNDPARENT, owner);
        }
        catch (Exception)
        {
            // 属性设不上只是叠不住的层级关系，窗口本身还能用
        }
    }

    /// <summary>切换窗口置顶。</summary>
    public static bool SetTopmost(nint hwnd, bool on)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }
        try
        {
            return NativeMethods.SetWindowPos(
                hwnd, on ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>把 <paramref name="hwnd"/> 移到 <paramref name="after"/> 之后（不改大小与激活状态）。</summary>
    public static bool SetZOrder(nint hwnd, nint after)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }
        try
        {
            return NativeMethods.SetWindowPos(
                hwnd, after, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>取系统 DPI（含显示缩放，125% → 120）；取不到回退 <see cref="DefaultDpi"/>。</summary>
    public static int SystemDpi
    {
        get
        {
            try
            {
                uint dpi = NativeMethods.GetDpiForSystem();
                return dpi > 0 ? (int)dpi : DefaultDpi;
            }
            catch (Exception)
            {
                return DefaultDpi;
            }
        }
    }

    /// <summary>取某个窗口的 DPI；窗口无效或取不到回退 <see cref="SystemDpi"/>。</summary>
    public static int DpiForWindow(nint hwnd)
    {
        try
        {
            if (hwnd != 0)
            {
                uint dpi = NativeMethods.GetDpiForWindow(hwnd);
                if (dpi > 0)
                {
                    return (int)dpi;
                }
            }
        }
        catch (Exception)
        {
            // 落到下面的系统 DPI
        }
        return SystemDpi;
    }

    /// <summary>当前鼠标位置（屏幕坐标，物理像素）；取不到返回 null。</summary>
    public static Point32? CursorPosition
        => NativeMethods.GetCursorPos(out Point32 pt) ? pt : null;

    /// <summary>窗口是否还存在。</summary>
    public static bool Exists(nint hwnd) => hwnd != 0 && NativeMethods.IsWindow(hwnd);

    /// <summary>窗口是否最小化了。</summary>
    public static bool IsMinimized(nint hwnd) => hwnd != 0 && NativeMethods.IsIconic(hwnd);

    /// <summary>读窗口类名，用于识别/跳过自己造的窗口。</summary>
    public static string ClassNameOf(nint hwnd)
    {
        var buffer = new StringBuilder(256);
        int len = NativeMethods.GetClassNameW(hwnd, buffer, buffer.Capacity);
        return len > 0 ? buffer.ToString() : "";
    }
}
