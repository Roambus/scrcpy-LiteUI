using System.Windows.Threading;
using Kuaitou.App.Views;
using Kuaitou.Core.Scrcpy;

namespace Kuaitou.App.Services;

/// <summary>
/// 投屏功能栏的挂 / 收调度：订阅 <see cref="ScrcpyLauncher"/> 的窗口就绪与进程结束通知，
/// 给每个投屏窗口挂一条功能栏，进程结束时收掉。
///
/// 功能栏窗口必须在 <see cref="ScrcpyLauncher.ShutdownAll"/> 之前收掉——它是 scrcpy 窗口的
/// 属主子窗口，先把主子（scrcpy）杀掉会留下悬空的属主关系。
/// </summary>
public sealed class MirrorBarManager : IDisposable
{
    private readonly ScrcpyLauncher _launcher;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<ScrcpySession, MirrorBarWindow> _bars = [];
    private bool _disposed;

    public MirrorBarManager(ScrcpyLauncher launcher, Dispatcher dispatcher)
    {
        _launcher = launcher;
        _dispatcher = dispatcher;
        _launcher.WindowReady += OnWindowReady;
        _launcher.SessionEnded += OnSessionEnded;
    }

    // 两个通知都在守候线程上发出来，必须切回 UI 线程再动窗口
    private void OnWindowReady(ScrcpySession session) => Post(() => Attach(session));

    private void OnSessionEnded(ScrcpySession session) => Post(() => Detach(session));

    private void Post(Action action)
    {
        if (_disposed)
        {
            return;
        }
        _dispatcher.BeginInvoke(action);
    }

    private void Attach(ScrcpySession session)
    {
        if (_disposed || session.WindowHandle == 0 || _bars.ContainsKey(session))
        {
            return;
        }
        try
        {
            var bar = new MirrorBarWindow(session, _launcher, _launcher.AlwaysOnTopConfigured);
            bar.Closed += (_, _) => _bars.Remove(session);
            _bars[session] = bar;
        }
        catch (Exception)
        {
            // 挂功能栏失败不该影响投屏本身
        }
    }

    private void Detach(ScrcpySession session)
    {
        if (_bars.Remove(session, out MirrorBarWindow? bar))
        {
            bar.Close();
        }
    }

    /// <summary>退出前先收掉所有功能栏。</summary>
    public void CloseAll()
    {
        foreach (MirrorBarWindow bar in _bars.Values.ToList())
        {
            bar.Close();
        }
        _bars.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _launcher.WindowReady -= OnWindowReady;
        _launcher.SessionEnded -= OnSessionEnded;
        CloseAll();
    }
}
