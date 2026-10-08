using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Kuaitou.App.Services;
using Kuaitou.App.ViewModels;
using Kuaitou.Core.Scrcpy;
using Kuaitou.Interop;

namespace Kuaitou.App.Views;

/// <summary>
/// 投屏窗口右侧的功能栏。挂在一个 <see cref="ScrcpySession"/> 上，位置与大小跟着 scrcpy 的
/// 客户区走：40ms 定时器兜底，外加 SetWinEventHook 补上「窗口刚出现 / 被拖走」的那一拍。
///
/// 与旧版（legacy/kuaitou/winbar.py）的一处做法差异：旧版整窗是色键透明，只有图标那几个像素
/// 收得到鼠标，所以「扫到边缘就展开」只能靠定时器轮询 GetCursorPos 自己判。这里那条不透明的
/// 按钮条本身收得到鼠标，展开 / 收起直接用 MouseEnter / MouseLeave，只在收起时保留 0.6s
/// 的离开延迟（老用户的操作手感）。透明像素依旧天然穿透到下面的投屏画面上。
/// </summary>
public sealed partial class MirrorBarWindow : Window
{
    private const double StripWidth = 34;    // 展开后的宽度（逻辑像素）
    private const double StripEdge = 6;      // 收起时贴边留的细条
    private const double StripPad = 4;       // 条内上下留白
    private const double ButtonMin = 22;
    private const double ButtonMax = 30;

    private static readonly TimeSpan CollapseDelay = TimeSpan.FromSeconds(0.6);
    private static readonly TimeSpan FollowInterval = TimeSpan.FromMilliseconds(40);

    private readonly ScrcpySession _session;
    private readonly ScrcpyLauncher _launcher;
    private readonly DispatcherTimer _followTimer;

    private DispatcherTimer? _collapseTimer;
    private WindowWatcher? _watcher;
    private bool _expanded;
    private bool _pinned;
    private bool _shown;
    private bool _captionApplied;
    private double _buttonSize;

    public MirrorBarWindow(ScrcpySession session, ScrcpyLauncher launcher, bool pinned)
    {
        _session = session;
        _launcher = launcher;
        _pinned = pinned;

        InitializeComponent();
        DataContext = this;

        Buttons = new ObservableCollection<BarButton>(BarButton.CreateAll(session.IsDesktop));
        InvokeCommand = new RelayCommand<BarButton>(Invoke);

        if (pinned)
        {
            SetPinHighlight(true);
        }

        // 先把叠加窗口「生」出来（不显示），好在 Show 之前就把属主与扩展样式设好，避免闪一下
        nint handle = new WindowInteropHelper(this).EnsureHandle();
        WindowTools.MakeOverlay(handle);
        WindowTools.SetOwner(handle, session.WindowHandle);
        if (pinned)
        {
            // 上次投屏是置顶的：这次直接把画面窗口也顶上（配置键只用来记住上次状态）
            WindowTools.SetTopmost(session.WindowHandle, true);
        }

        _watcher = new WindowWatcher(session.WindowHandle, () => Dispatcher.BeginInvoke(Follow));

        _followTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = FollowInterval };
        _followTimer.Tick += (_, _) => Follow();
        _followTimer.Start();

        // 主题切换时把画面窗口的标题栏也重新刷一遍色（它不在我们的 XAML 里，DynamicResource 管不到）
        ThemeManager.ThemeChanged += OnThemeChanged;

        if (PositionToTarget())
        {
            ShowBar();
        }
        else
        {
            // 画面窗口还没量出来 / 太小：先藏着，Follow 会在合适的时候把它露出来
            HideBar();
        }

        Closed += (_, _) => ReleaseResources();
    }

    public ObservableCollection<BarButton> Buttons { get; }

    public ICommand InvokeCommand { get; }

    // ---------- 跟随画面窗口 ----------

    private static double Scale => Math.Max(1.0, WindowTools.SystemDpi / 96.0);

    private void Follow()
    {
        nint target = _session.WindowHandle;
        if (target == 0 || !WindowTools.Exists(target))
        {
            Close();   // scrcpy 已经没了，功能栏跟着收工
            return;
        }
        if (WindowTools.IsMinimized(target))
        {
            HideBar();  // 最小化时不留几个孤零零的按钮在桌面上
            return;
        }
        if (!PositionToTarget())
        {
            HideBar();
            return;
        }
        if (!_captionApplied)
        {
            ApplyCaptionTheme(target);
        }
        ShowBar();

        // 收起态是「透明热区」，而透明窗口的透明像素收不到鼠标事件（鼠标会直接穿到下面的画面），
        // 所以展开不能只靠 MouseEnter，得自己拿光标位置判一下。
        if (!_expanded && IsCursorOnStrip())
        {
            SetExpanded(true);
        }
    }

    /// <summary>光标是不是扫到了贴边的收起条上（屏幕物理像素 → 窗口逻辑像素后再比）。</summary>
    private bool IsCursorOnStrip()
    {
        if (WindowTools.CursorPosition is not { } pt)
        {
            return false;
        }
        const double Slack = 4;   // 往里多算几像素：扫边缘时不必分毫不差
        double scale = Scale;
        double x = pt.X / scale;
        double y = pt.Y / scale;
        return x >= Left - Slack
            && x <= Left + Width + Slack
            && y >= Top
            && y <= Top + Height;
    }

    // ---------- 画面窗口的系统标题栏配色 ----------

    private void OnThemeChanged(string _)
    {
        // 主题变了：清掉标记，下一拍 Follow（40ms 内）会用新主题色重刷一次
        _captionApplied = false;
    }

    /// <summary>
    /// 把主题色刷到 scrcpy 画面窗口的原生标题栏上：标题栏底色取卡片色、文字取正文色、
    /// 边框取描边色，跟主界面看起来是同一套。窗口句柄还没出来时静默跳过，下次跟随再试。
    /// </summary>
    private void ApplyCaptionTheme(nint target)
    {
        if (target == 0 || !WindowTools.Exists(target))
        {
            return;
        }
        Dwm.SetDarkMode(target, ThemeManager.Current == ThemeManager.Dark);
        Dwm.SetCaptionColors(target, ColorRef("Brush.Card"), ColorRef("Brush.Text"), ColorRef("Brush.Border"));
        _captionApplied = true;
    }

    /// <summary>把主题令牌里的颜色转成 DWM 要的 0x00BBGGRR；取不到就交回系统默认。</summary>
    private static int ColorRef(string key)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
        {
            Color c = brush.Color;
            return c.R | (c.G << 8) | (c.B << 16);
        }
        return Dwm.ColorDefault;
    }

    /// <summary>
    /// 按画面客户区算出功能栏的位置与大小，并顺带把按钮边长调好。
    /// 量不出来 / 放不下所有按钮时返回 false（宁可不给，也别让按钮溢出条带）。
    /// </summary>
    private bool PositionToTarget()
    {
        nint target = _session.WindowHandle;
        if (target == 0 || !WindowTools.Exists(target) || WindowTools.IsMinimized(target))
        {
            return false;
        }
        if (WindowTools.ClientRectOnScreen(target) is not { } rect || rect.Width <= 0 || rect.Height <= 0)
        {
            return false;
        }

        double scale = Scale;
        double available = rect.Height / scale - StripPad * 2;
        if (Buttons.Count == 0
            || rect.Width < StripWidth * 3 * scale
            || available < ButtonMin * Buttons.Count)
        {
            return false;
        }

        UpdateButtonSize(available);

        double strip = _expanded ? StripWidth : StripEdge;
        Left = rect.Right / scale - strip;
        Top = rect.Top / scale;
        Width = strip;
        Height = rect.Height / scale;
        return true;
    }

    private void UpdateButtonSize(double available)
    {
        double size = Math.Max(ButtonMin, Math.Min(ButtonMax, Math.Floor(available / Buttons.Count)));
        if (Math.Abs(size - _buttonSize) < 0.5)
        {
            return;
        }
        _buttonSize = size;
        foreach (BarButton button in Buttons)
        {
            button.Size = size;
        }
    }

    private void ShowBar()
    {
        if (_shown && Visibility == Visibility.Visible)
        {
            return;
        }
        _shown = true;
        Show();
    }

    private void HideBar()
    {
        if (!_shown || Visibility != Visibility.Visible)
        {
            return;
        }
        Hide();
    }

    // ---------- 悬停展开 / 收起 ----------

    private void OnExpandRequested(object sender, MouseEventArgs e) => SetExpanded(true);

    private void OnCollapseRequested(object sender, MouseEventArgs e)
    {
        _collapseTimer ??= CreateCollapseTimer();
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private DispatcherTimer CreateCollapseTimer()
    {
        var timer = new DispatcherTimer { Interval = CollapseDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // 0.6s 内又回到条上就不收
            if (!ExpandedBar.IsMouseOver)
            {
                SetExpanded(false);
            }
        };
        return timer;
    }

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded)
        {
            return;
        }
        _expanded = expanded;
        if (expanded)
        {
            _collapseTimer?.Stop();
        }
        CollapsedBar.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        ExpandedBar.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        Follow();   // 立刻把宽度切过去，不等到下一拍定时器
    }

    // ---------- 按钮 ----------

    private void SetPinHighlight(bool on)
    {
        foreach (BarButton button in Buttons)
        {
            if (button.Action == "pin")
            {
                button.IsOn = on;
            }
        }
    }

    private void Invoke(BarButton button)
    {
        switch (button.Action)
        {
            case "pin":
                _pinned = !_pinned;
                button.IsOn = _pinned;
                _launcher.SetTopmost(_session, _pinned);
                break;

            case "rotate_lock":
                // 乐观翻一下高亮：真实状态在手机上，这里只负责按钮看起来对不对
                button.IsOn = !button.IsOn;
                RunActionAsync(button.Action);
                break;

            default:
                RunActionAsync(button.Action);
                break;
        }
    }

    /// <summary>
    /// adb 一次往返几百毫秒，放在 UI 线程上会把整条功能栏（连跟随画面的定时器）一起卡住，
    /// 所以丢到后台线程去发。
    /// </summary>
    private void RunActionAsync(string action)
        => _ = Task.Run(() =>
        {
            try
            {
                _launcher.RunAction(_session.Serial, action);
            }
            catch (Exception)
            {
                // 单个动作失败不影响功能栏本身
            }
        });

    private void ReleaseResources()
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
        _followTimer.Stop();
        _collapseTimer?.Stop();
        _watcher?.Dispose();
        _watcher = null;
    }
}
