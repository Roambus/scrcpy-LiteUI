using System.Runtime.InteropServices;
using System.Text;

namespace Kuaitou.Interop;

/// <summary>屏幕坐标下的矩形（Win32 RECT）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Rect32
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;

    public readonly int Height => Bottom - Top;
}

/// <summary>一个点（Win32 POINT）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Point32
{
    public int X;
    public int Y;
}

/// <summary>
/// Win32 原生调用的统一入口。
///
/// 这里用 DllImport 而不是 LibraryImport 源生成器：调用点极少、对性能不敏感，
/// 而 DllImport 对 non-ASCII 字符串参数的处理最直观（CharSet.Unicode 直接按 UTF-16 传）。
/// </summary>
public static class NativeMethods
{
    /// <summary>ShowWindow 的 SW_RESTORE：把最小化的窗口还原。</summary>
    public const int SW_RESTORE = 9;

    /// <summary>ShowWindow 的 SW_HIDE / SW_SHOWNOACTIVATE。</summary>
    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;

    /// <summary>AllowSetForegroundWindow 的 ASFW_ANY：允许任意进程抢前台。</summary>
    public const int ASFW_ANY = -1;

    public const int GWL_EXSTYLE = -20;

    /// <summary>GWLP_HWNDPARENT：给窗口指定「属主」，从而永远压在属主之上、跟着一起最小化。</summary>
    public const int GWLP_HWNDPARENT = -8;

    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_DLGMODALFRAME = 0x00000001;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;

    public static readonly nint HWND_TOPMOST = -1;
    public static readonly nint HWND_NOTOPMOST = -2;

    public const uint WM_SETICON = 0x0080;
    public const int ICON_SMALL = 0;
    public const int ICON_BIG = 1;

    /// <summary>SetWinEventHook 的事件范围与标志。</summary>
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    /// <summary>EnumWindows 的回调：返回 false 会中止枚举。</summary>
    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    /// <summary>SetWinEventHook 的回调。</summary>
    public delegate void WinEventDelegate(
        nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild,
        uint dwEventThread, uint dwmsEventTime);

    /// <summary>按窗口标题找顶层窗口。标题是「快投」。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint hWnd, out Rect32 lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(nint hWnd, out Rect32 lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(nint hWnd, ref Point32 lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point32 lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassNameW(nint hWnd, [Out] StringBuilder lpClassName, int nMaxCount);

    /// <summary>取系统 DPI（Win10 1607+）。取不到由调用方兜 96。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetDpiForSystem();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetDpiForWindow(nint hWnd);

    /// <summary>设置窗口的「属主」（hWndParent 语义），用于让叠加条跟随画面窗口。</summary>
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern nint GetWindowLongPtrW(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWinEventHook(
        uint eventMin, uint eventMax, nint hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(nint hWinEventHook);

    /// <summary>
    /// 读 IPv4 邻居表（ARP 缓存）。iphlpapi!GetIpNetTable。
    /// 首次传空缓冲会返回 ERROR_INSUFFICIENT_BUFFER(122) 并回填所需字节数，再按大小调一次。
    /// </summary>
    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetIpNetTable(
        nint pIpNetTable, ref uint pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder);

    [DllImport("kernel32.dll")]
    public static extern nint GetCurrentProcess();

    /// <summary>把进程的工作集还给系统（psapi!EmptyWorkingSet）。</summary>
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyWorkingSet(nint hProcess);
}
