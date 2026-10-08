using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kuaitou.App.Services;
using Kuaitou.App.ViewModels;
using Kuaitou.Core;
using Kuaitou.Core.Storage;
using Kuaitou.Interop;

namespace Kuaitou.App;

/// <summary>
/// 主窗口。左侧边栏分「设备」（每台设备一个主页入口）与贴底的「设置」两组，
/// 右侧内容区在设备主页与设置页之间切换。标题栏是自绘的（WindowStyle=None + WindowChrome），
/// 配色跟着主题走。
/// </summary>
public partial class MainWindow : Window
{
    private const double SidebarWidth = 232;
    private const double SidebarCollapsedWidth = 70;

    /// <summary>侧边栏是否处于折叠态。设备入口的模板靠它决定要不要把名字 / 地址收起来。</summary>
    public static readonly DependencyProperty IsSidebarCollapsedProperty =
        DependencyProperty.Register(nameof(IsSidebarCollapsed), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    private readonly DevicesViewModel _devices;
    private readonly AddDeviceViewModel _addDevice;
    private readonly SettingsViewModel _settings;
    private readonly MirrorBarManager _barManager;
    private readonly TrayService _tray;
    private readonly bool _silent;
    private readonly DispatcherTimer _toastTimer;
    private DeviceHomeViewModel? _home;
    private bool _sidebarCollapsed;
    private bool _reallyExiting;

    public MainWindow(ConfigStore configStore, AppConfig config, bool silent = false)
    {
        InitializeComponent();

        _silent = silent;

        _devices = new DevicesViewModel(DeviceServices.Default, Dispatcher);
        _addDevice = new AddDeviceViewModel(DeviceServices.Default, _devices, Dispatcher);
        // 托盘开关一变就启停托盘图标；托盘由本窗口持有（退出时一起收掉）
        _settings = new SettingsViewModel(
            DeviceServices.Default, configStore, config, _devices, _addDevice, SyncTray, ShowToast);
        DataContext = _settings;
        SettingsPage.DataContext = _settings;

        // 托盘：左键唤出主界面，右键菜单里可以按设备投屏 / 退出（见 TrayService）
        _tray = new TrayService(DeviceServices.Default, ShowFromTray, QuitFromTray);

        // Toast 的自动收起定时器：显示时重启，到点动画淡出
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, _) => HideToast();

        // 必须在第一次调用 adb 之前记下 adb 服务是不是我们拉起来的（决定退出时要不要 kill-server）
        DeviceServices.Default.Launcher.NoteAdbServerBeforeStart();
        _devices.Start();
        DeviceServices.Default.Reconnect.Start();

        // 投屏窗口右侧功能栏的挂 / 收调度
        _barManager = new MirrorBarManager(DeviceServices.Default.Launcher, Dispatcher);

        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) =>
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            _toastTimer.Stop();
            // 功能栏是 scrcpy 窗口的属主窗口：必须先收功能栏再杀 scrcpy，顺序反了会留下悬空的属主关系。
            // 两个后台轮询（设备列表 / 自动重连）也要先停，别在 adb 服务被关掉之后又把它拉起来。
            _barManager.Dispose();
            _devices.Dispose();
            // 扫码配对和深度搜索都在后台跑，退出前要先让它们收工
            _addDevice.Dispose();
            // 设备主页自己起了一个图标重试定时器，换页 / 退出都要收掉
            _home?.Dispose();
            DeviceServices.Default.Reconnect.Dispose();
            DeviceServices.Default.IconFetch.Dispose();
            _tray.Dispose();
            DeviceServices.Default.Launcher.ShutdownAll();
            // 退出时机握在自己手里（ShutdownMode.OnExplicitShutdown），收尾完毕就退
            Application.Current?.Shutdown();
        };
    }

    /// <summary>托盘服务。App 层据此判断「静默启动时托盘到底起没起来」。</summary>
    public TrayService Tray => _tray;

    /// <summary>
    /// 启动时按配置（或本次的 --silent）先把托盘亮出来。对应旧版 launcher_server.py
    /// 里 <c>if _tray_enabled() or silent: _tray_start()</c> 那一段。
    /// </summary>
    public void StartTray(bool silent)
    {
        if (_settings.MinimizeToTray || silent)
        {
            _tray.Sync(true);
        }
    }

    /// <summary>配置里的托盘开关一变就启停托盘图标（由设置页回调过来）。</summary>
    private void SyncTray(bool enabled) => _tray.Sync(enabled);

    /// <summary>托盘左键 / 「打开主界面」：把窗口唤回前台。</summary>
    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        ExitBackground();
    }

    /// <summary>
    /// 界面真正进后台（收进托盘 / 最小化）时把能放掉的都放掉：设备主页的图标解码缓存与
    /// 秒级定时器，以及已经用不到的物理内存页。回到前台再按当前设备重建——图标有磁盘素材库，
    /// 重建只是重新解码，不必一直挂在内存里。
    /// </summary>
    private void EnterBackground()
    {
        bool onHomePage = HomePage.Visibility == Visibility.Visible;
        RecycleHome();
        if (onHomePage)
        {
            // 正开在设备主页上：内容切回设置页，免得还原时对着一张空白页
            HomePage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Visible;
            DeviceList.SelectedItem = null;
            if (NavDevice.IsChecked != true)
            {
                NavDevice.IsChecked = true;
            }
        }
        _devices.SetBackground(true);
        // 刚把整份图标缓存与卡片视觉树放掉，这里收一次内存才真正还给系统（只在这一刻收，平时不碰 GC）
        MemoryTools.ReclaimBackgroundMemory();
    }

    /// <summary>回到前台：轮询恢复 3 秒节奏，并立刻刷一次设备列表（后台那段是慢节奏）。</summary>
    private void ExitBackground()
    {
        _devices.SetBackground(false);
        _ = _devices.RefreshOnceAsync();
    }

    /// <summary>收掉设备主页：Dispose 会停掉它的图标重试定时器并清空整份解码缓存。</summary>
    private void RecycleHome()
    {
        if (_home is null)
        {
            return;
        }
        _home.Dispose();
        _home = null;
        HomePage.DataContext = null;
    }

    /// <summary>托盘「退出应用」：真的要退了，越过缩小到托盘那一段。</summary>
    private void QuitFromTray()
    {
        _reallyExiting = true;
        Close();
    }

    /// <summary>
    /// 关窗语义照旧版 on_closing：开了「缩小到托盘」（或本次是静默自启）且托盘确实起着，
    /// 点关闭只把窗口藏起来，应用继续在托盘待命；从托盘选的「退出应用」才真的退出。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyExiting && (_settings.MinimizeToTray || _silent) && _tray.IsCreated)
        {
            e.Cancel = true;
            Hide();
            EnterBackground();
        }
        base.OnClosing(e);
    }

    /// <summary>非阻塞的临时提示：窗口底部浮一条，几秒后淡出。失败用红字。</summary>
    private void ShowToast(string text, bool isError)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        ToastText.Text = text;
        ToastText.Foreground = (Brush)FindResource(isError ? "Brush.Red" : "Brush.Text");
        ToastBar.BorderBrush = (Brush)FindResource(isError ? "Brush.Red" : "Brush.BorderStrong");
        ToastBar.Visibility = Visibility.Visible;
        ToastBar.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));

        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => ToastBar.Visibility = Visibility.Collapsed;
        ToastBar.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    public bool IsSidebarCollapsed
    {
        get => (bool)GetValue(IsSidebarCollapsedProperty);
        set => SetValue(IsSidebarCollapsedProperty, value);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        nint handle = new WindowInteropHelper(this).Handle;
        Dwm.SetRoundedCorners(handle, rounded: true);
        Dwm.SetDarkMode(handle, ThemeManager.Current == ThemeManager.Dark);
    }

    private void OnThemeChanged(string theme)
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
        {
            Dwm.SetDarkMode(handle, theme == ThemeManager.Dark);
        }
    }

    // ---------- 标题栏 ----------
    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            return;
        }
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已经松开（拖拽期间发生状态切换）时会抛，忽略即可
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e) => ToggleMaximizeRestore();

    private void ToggleMaximizeRestore()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        bool maximized = WindowState == WindowState.Maximized;
        MaxIcon.Data = Geometry.Parse(maximized ? "M1,3 H9 V11 H1 Z M3,3 V1 H11 V9 H9" : "M1,1 H11 V11 H1 Z");
        MaxButton.ToolTip = maximized ? "还原" : "最大化";
        AutomationProperties.SetName(MaxButton, maximized ? "还原" : "最大化");

        // 最小化也算进后台：轮询放慢，顺手把工作集还给系统（页面留着，还原时还在原处）
        if (WindowState == WindowState.Minimized)
        {
            _devices.SetBackground(true);
            MemoryTools.TrimWorkingSet();
        }
        else
        {
            _devices.SetBackground(false);
        }
    }

    // ---------- 侧边栏 ----------
    private void OnToggleSidebar(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        IsSidebarCollapsed = _sidebarCollapsed;

        AnimateSidebarWidth(_sidebarCollapsed ? SidebarCollapsedWidth : SidebarWidth);
        Visibility textVisibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        AppTitle.Visibility = textVisibility;
        DeviceGroupTitle.Visibility = textVisibility;
        NavGroupTitle.Visibility = textVisibility;
        foreach (TextBlock text in new[] { NavDeviceText, NavScreenText, NavGeneralText, NavDiagText })
        {
            text.Visibility = textVisibility;
        }

        CollapseIcon.LayoutTransform = _sidebarCollapsed
            ? new RotateTransform(180)
            : new RotateTransform(0);
        AutomationProperties.SetName(CollapseButton, _sidebarCollapsed ? "展开侧边栏" : "折叠侧边栏");
    }

    /// <summary>
    /// 侧边栏宽度做一段缓动动画：折叠 / 展开顺滑收放，而不是硬切一格。
    /// FillBehavior=Stop，动画结束把值落回 Width，之后布局拿到的就是最终宽度。
    /// </summary>
    private void AnimateSidebarWidth(double target)
    {
        double from = SidebarPanel.ActualWidth;
        SidebarPanel.Width = target;
        if (from <= 0 || Math.Abs(from - target) < 0.5)
        {
            return;
        }

        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        SidebarPanel.BeginAnimation(FrameworkElement.WidthProperty, animation);
    }

    // ---------- 内容区切换 ----------

    /// <summary>点侧边栏里的一台设备 → 切到那台设备的主页。</summary>
    private void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceItem device)
        {
            return;
        }

        // 设备主页与设置页互斥：进来先把设置分组的高亮撤掉
        NavDevice.IsChecked = false;
        NavScreen.IsChecked = false;
        NavGeneral.IsChecked = false;
        NavDiag.IsChecked = false;

        // 换一台设备就把上一页收掉：它的图标重试定时器还在跑，不收会一直空转
        RecycleHome();
        _home = new DeviceHomeViewModel(DeviceServices.Default, Dispatcher, device);
        HomePage.DataContext = _home;
        _home.Start();
        HomePage.Visibility = Visibility.Visible;
        SettingsPage.Visibility = Visibility.Collapsed;
    }

    /// <summary>点设置分组里的一项 → 切回设置页。</summary>
    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        // InitializeComponent 期间 Tag 还没解析完 / 内容区还没建好，先放过
        if (sender is not RadioButton { IsChecked: true, Tag: string index }
            || !int.TryParse(index, out int panel)
            || SettingsPage is null)
        {
            return;
        }

        DeviceList.SelectedItem = null;   // 触发 SelectionChanged，但那条路径对 null 直接返回

        // 进设置页就把设备主页收掉：它的图标秒级定时器与整份图标解码缓存不该在后台继续占着，
        // 等回到设备页再按当前设备重建（图标有磁盘素材库，重建只是重新解码，不必常驻内存）。
        RecycleHome();

        SettingsPage.PanelIndex = panel;
        SettingsPage.Visibility = Visibility.Visible;
        HomePage.Visibility = Visibility.Collapsed;
    }
}
