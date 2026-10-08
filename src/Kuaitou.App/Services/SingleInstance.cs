using System.Threading;
using Kuaitou.Interop;

namespace Kuaitou.App.Services;

/// <summary>
/// 单实例守门人：命名 Mutex 判重；已有实例在跑时把它唤到前台，本进程直接退出。
/// 对应 legacy/launcher_server.py 里的单实例处理。
///
/// 用 Local\ 前缀把作用域限制在当前登录会话内 —— 多用户同时登录时各跑各的互不干扰。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\KuaitouSingleInstance";
    private const string WindowTitle = "快投";

    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>抢占单实例。抢到返回实例（用完 Dispose）；已有实例在跑返回 null 并把老窗口唤到前台。</summary>
    public static SingleInstance? Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (createdNew)
        {
            return new SingleInstance(mutex);
        }

        ActivateExistingWindow();
        mutex.Dispose();
        return null;
    }

    private static void ActivateExistingWindow()
    {
        nint hwnd = NativeMethods.FindWindowW(null, WindowTitle);
        if (hwnd == nint.Zero)
        {
            // 老实例还没建出窗口（或已开始退出），没得唤，直接放行本进程退出
            return;
        }
        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        NativeMethods.SetForegroundWindow(hwnd);
    }

    public void Dispose()
    {
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 不是本线程持有的（理论上不会发生），忽略
        }
        _mutex.Dispose();
    }
}
