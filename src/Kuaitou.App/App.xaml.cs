using System.Windows;
using Kuaitou.App.Services;
using Kuaitou.Core.Storage;

namespace Kuaitou.App;

/// <summary>
/// 应用入口。对应旧版 launcher_server.py 承担的启动职责：单实例判定 → 装主题 → 开主窗口。
/// </summary>
public partial class App : Application
{
    private SingleInstance? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 退出时机由我们自己掌握：静默启动时主窗口根本没显示过，不能靠「最后一个窗口关闭」来退；
        // 真正的退出统一走主窗口 Closed 里的 Application.Shutdown()（旧版是 on_closed 里 os._exit）。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 已有实例在跑：SingleInstance 会把老窗口唤到前台，本进程立刻退出。
        _singleInstance = SingleInstance.Acquire();
        if (_singleInstance is null)
        {
            Shutdown();
            return;
        }

        // 主题在开窗之前就定下来：窗口还没画就装好令牌字典，首屏直接是对的配色，
        // 不会先亮一帧再翻成深色（旧版 index.html 里那套 no-anim 冻结过渡在 WPF 里用不着）。
        ConfigStore configStore = ConfigStore.Default;
        AppConfig config = configStore.Load();
        ThemeManager.Apply(config.Theme);

        // 开机自启会带 --silent：只驻托盘，不弹主界面
        bool silent = Array.Exists(e.Args, arg => string.Equals(arg, "--silent", StringComparison.OrdinalIgnoreCase));

        var shell = new MainWindow(configStore, config, silent);
        this.MainWindow = shell;

        // 先把托盘亮出来（配置开着「缩小到托盘」，或本次是静默启动）
        shell.StartTray(silent);
        if (!silent || !shell.Tray.IsCreated)
        {
            // 静默启动时若托盘没起来（图标建不出来），窗口必须露出来，否则应用就「消失」了
            shell.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
