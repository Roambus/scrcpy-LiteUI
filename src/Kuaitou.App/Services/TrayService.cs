using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using Kuaitou.Core;
using Kuaitou.Core.Storage;

namespace Kuaitou.App.Services;

/// <summary>
/// 系统托盘。对应 legacy/kuaitou/system.py 的 <c>_tray_start</c> / <c>_tray_stop</c> /
/// <c>_tray_sync</c> / <c>_tray_items</c>。
///
/// 「关闭时缩小到托盘」开启后，点关闭只是把主界面收起来，应用继续在托盘待命：
/// 右键托盘图标可以重新打开主界面、按设备启动镜像桌面或快捷启动的应用，或退出应用。
/// 旧版用的是 pystray（一个后台线程 + tkinter 菜单）；WPF 侧用 H.NotifyIcon，菜单每次弹出时现取。
/// </summary>
public sealed class TrayService : IDisposable
{
    private const int MaxQuickLaunchItems = 12;

    private readonly DeviceServices _services;
    private readonly Action _showWindow;
    private readonly Action _quit;
    private TaskbarIcon? _icon;
    private bool _disposed;

    public TrayService(DeviceServices services, Action showWindow, Action quit)
    {
        _services = services;
        _showWindow = showWindow;
        _quit = quit;
    }

    /// <summary>
    /// 按配置里的托盘开关启停：开着就显示托盘图标，关掉就收走。
    /// 这里每次都重新建一个 TaskbarIcon，不复用被 Dispose 过的实例
    /// （H.NotifyIcon 的 ForceCreate 在 Dispose 之后会抛，托盘开关关掉再打开就会建不出来）。
    /// </summary>
    public void Sync(bool enabled)
    {
        if (_disposed)
        {
            return;
        }

        if (enabled)
        {
            if (_icon is null)
            {
                _icon = CreateIcon();
                _icon.ForceCreate(false);
            }
        }
        else if (_icon is not null)
        {
            _icon.Dispose();
            _icon = null;
        }
    }

    /// <summary>托盘图标是否已经显示出来。开机静默启动时据此判断「托盘起不来就得把窗口露出来」。</summary>
    public bool IsCreated => !_disposed && _icon?.IsCreated == true;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _icon?.Dispose();
        }
        catch (Exception)
        {
            // 退出路径上不抛
        }
        _icon = null;
    }

    /// <summary>新建一个托盘图标（含右键菜单与左键唤出主界面）。</summary>
    private TaskbarIcon CreateIcon()
    {
        var menu = new ContextMenu();
        // 每次弹出前重建：连上 / 掉线的设备、快捷启动列表都是实时取的（与旧版一致）
        menu.Opened += (_, _) => RebuildMenu(menu);

        var icon = new TaskbarIcon
        {
            ToolTipText = "快投",
            IconSource = LoadIcon(),
            ContextMenu = menu,
            NoLeftClickDelay = true,
        };
        // 左键单击 / 双击都唤出主界面（旧版菜单里「打开主界面」是默认项）
        icon.TrayLeftMouseUp += (_, _) => _showWindow();
        icon.TrayMouseDoubleClick += (_, _) => _showWindow();
        return icon;
    }

    private void RebuildMenu(ContextMenu menu)
    {
        menu.Items.Clear();

        var open = new MenuItem { Header = "打开主界面", FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => _showWindow();
        menu.Items.Add(open);

        List<string> devices;
        try
        {
            devices = _services.Inventory.GetDevices();
        }
        catch (Exception)
        {
            devices = [];
        }

        if (devices.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (string serial in devices)
            {
                menu.Items.Add(BuildDeviceMenu(serial));
            }
        }

        menu.Items.Add(new Separator());
        var quit = new MenuItem { Header = "退出应用" };
        quit.Click += (_, _) => _quit();
        menu.Items.Add(quit);
    }

    /// <summary>一台设备的子菜单：镜像桌面 + 该设备的快捷启动应用（最多 12 个）。</summary>
    private MenuItem BuildDeviceMenu(string serial)
    {
        string label = serial;
        try
        {
            string model = _services.Inventory.GetModel(serial);
            if (model.Length > 0 && model != serial)
            {
                label = $"{model} ({serial})";
            }
        }
        catch (Exception)
        {
            // 拿不到型号就用序列号
        }

        var device = new MenuItem { Header = label };

        var desktop = new MenuItem { Header = "镜像桌面" };
        desktop.Click += (_, _) => SafeRun(() => _services.Launcher.LaunchDesktop(serial));
        device.Items.Add(desktop);

        foreach (string package in QuickPackages(serial).Take(MaxQuickLaunchItems))
        {
            // 应用列表还没加载过时拿不到中文名，退回包名，别让菜单空着
            string name = _services.Apps.GetAppName(package) ?? package;
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => SafeRun(() => _services.Launcher.LaunchApp(package, serial));
            device.Items.Add(item);
        }
        return device;
    }

    /// <summary>该设备的快捷启动包名；没有专属列表时用 "*" 兜底。</summary>
    private IReadOnlyList<string> QuickPackages(string serial)
    {
        AppConfig cfg = _services.Config.Load();
        if (cfg.QuickLaunch.TryGetValue(serial, out List<string>? own) && own.Count > 0)
        {
            return own;
        }
        return cfg.QuickLaunch.TryGetValue("*", out List<string>? fallback) ? fallback : [];
    }

    /// <summary>托盘菜单里的动作一律吞掉异常：菜单点一下没反应，也好过整个托盘崩掉。</summary>
    private static void SafeRun(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // 与旧版 _act() 包装一致：静默
        }
    }

    /// <summary>
    /// 托盘图标：优先用随包的 appicon.ico（与 exe 同一张图），取不到时画一个占位图标
    /// （对应旧版 _tray_image 的兜底）。
    /// </summary>
    private static ImageSource LoadIcon()
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri("pack://application:,,,/appicon.ico", UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return FallbackIcon();
        }
    }

    private static ImageSource FallbackIcon()
    {
        var group = new DrawingGroup();
        var accent = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
        var white = Brushes.White;
        group.Children.Add(new GeometryDrawing(
            accent, null, new RectangleGeometry(new Rect(0, 0, 64, 64), 14, 14)));
        group.Children.Add(new GeometryDrawing(
            white, null, new RectangleGeometry(new Rect(20, 12, 14, 40), 4, 4)));
        group.Children.Add(new GeometryDrawing(
            white, null, new RectangleGeometry(new Rect(38, 22, 14, 30), 3, 3)));

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
