namespace Kuaitou.Interop;

/// <summary>
/// 盯住某个窗口：它显示 / 移动 / 改变大小 / 消失时立刻回调一次。
///
/// 功能栏跟随画面窗口主要靠 40ms 定时器兜底，但「窗口刚出来」「被拖走」这类时刻
/// 定时器要多等一拍才反应过来，肉眼能看出功能栏慢半拍；用事件钩子补上这一拍。
///
/// 注意 <c>_callback</c> 必须被字段一直持有：SetWinEventHook 把委托地址交给了系统，
/// 只传局部变量的话它随时可能被 GC 收掉，之后系统回调到已释放的内存直接崩进程。
/// </summary>
public sealed class WindowWatcher : IDisposable
{
    private readonly NativeMethods.WinEventDelegate _callback;
    private readonly Action _onChanged;
    private readonly nint _target;
    private nint _hook;
    private bool _disposed;

    /// <param name="targetHwnd">要盯住的窗口。</param>
    /// <param name="onChanged">窗口显示 / 移动 / 消失时回调（可能在任意线程上）。</param>
    public WindowWatcher(nint targetHwnd, Action onChanged)
    {
        _target = targetHwnd;
        _onChanged = onChanged;
        _callback = OnEvent;
        try
        {
            _hook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_OBJECT_DESTROY,
                NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
                0,
                _callback,
                0,
                0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        }
        catch (Exception)
        {
            _hook = 0;   // 装不上钩子也不影响：定时器那条路照常工作
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_hook != 0)
        {
            try
            {
                NativeMethods.UnhookWinEvent(_hook);
            }
            catch (Exception)
            {
                // 退出路径上不抛
            }
            _hook = 0;
        }
    }

    private void OnEvent(
        nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild,
        uint dwEventThread, uint dwmsEventTime)
    {
        // 只要目标窗口自身（idObject == OBJID_WINDOW == 0）的事件，子元素的一律忽略
        if (hwnd != _target || idObject != 0 || _disposed)
        {
            return;
        }
        try
        {
            _onChanged();
        }
        catch (Exception)
        {
            // 回调方自己出错不该把系统的钩子链带崩
        }
    }
}
