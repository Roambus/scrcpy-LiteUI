using System.Windows.Input;
using System.Windows.Media.Imaging;
using Kuaitou.Core.Apps;

namespace Kuaitou.App.ViewModels;

/// <summary>应用名里的一段文本；<see cref="IsMark"/> 为 true 时界面上加高亮底色。</summary>
public sealed record NameSegment(string Text, bool IsMark);

/// <summary>
/// 应用网格 / 快捷启动区里的一张应用卡片。对应 index.html 的 <c>makeAppItem</c>，
/// 以及它那套图标重试（<c>iconImgError</c>）与角标三态（<c>updateAddBtn</c>）。
///
/// 图标先查本地素材库：命中立刻出图；命不中就先摆首字母底色，由父页面按 3s / 6s / 12s
/// 重试——在线补抓是后台做的，过几秒才可能落盘。三次都拿不到就保持首字母底色。
/// </summary>
public sealed class AppCardViewModel : ObservableObject
{
    /// <summary>图标重试间隔（秒），与旧版 <c>iconImgError</c> 的 3s/6s/12s 逐字一致。</summary>
    private static readonly int[] RetryDelays = [3, 6, 12];

    private readonly IconResolver _resolver;
    private readonly Func<string, BitmapImage?> _loadIcon;
    private readonly Action<AppCardViewModel> _launch;
    private readonly Action<AppCardViewModel> _toggleQuick;

    private IReadOnlyList<NameSegment> _segments;
    private BitmapImage? _icon;
    private string? _iconPath;
    private int _retries;
    private long _nextRetryAt;
    private bool _isInQuick;

    public AppCardViewModel(
        AppEntry app,
        bool inQuickArea,
        IconResolver resolver,
        Func<string, BitmapImage?> loadIcon,
        Action<AppCardViewModel> launch,
        Action<AppCardViewModel> toggleQuick)
    {
        Package = app.Package;
        Name = string.IsNullOrEmpty(app.Name) ? app.Package : app.Name;
        Initial = Name.Length > 0 ? Name[..1] : "?";
        InQuickArea = inQuickArea;
        _resolver = resolver;
        _loadIcon = loadIcon;
        _launch = launch;
        _toggleQuick = toggleQuick;
        _segments = [new NameSegment(Name, false)];

        if (inQuickArea)
        {
            CardWidth = 96;
            CardPadding = new System.Windows.Thickness(8, 12, 8, 12);
            IconSize = 40;
            IconRadius = 10;
            InitialFontSize = 17;
            NameFontSize = 11;
        }
        else
        {
            CardWidth = 118;
            CardPadding = new System.Windows.Thickness(10, 18, 10, 18);
            IconSize = 48;
            IconRadius = 12;
            InitialFontSize = 20;
            NameFontSize = 12;
        }

        LaunchCommand = new RelayCommand(() => _launch(this));
        ToggleQuickCommand = new RelayCommand(() => _toggleQuick(this));

        // WPF 的 CornerRadius / ClipToBounds 只裁矩形、不会裁圆角，真正圆角得挂带 RadiusX/Y 的 Clip
        IconClip = new System.Windows.Media.RectangleGeometry(
            new System.Windows.Rect(0, 0, IconSize, IconSize),
            IconRadius,
            IconRadius);
    }

    public string Package { get; }

    /// <summary>应用名；设备上取不到名字时退回包名（与旧版 <c>findApp</c> 的兜底一致）。</summary>
    public string Name { get; }

    /// <summary>没有图标时垫在底下的首字母。</summary>
    public string Initial { get; }

    /// <summary>true 表示这张卡画在「快捷启动」区里（角标是移除用的 ×）。</summary>
    public bool InQuickArea { get; }

    // ---------- 卡片尺寸（快捷区与应用区两套，避免写两份模板） ----------

    public double CardWidth { get; }

    public System.Windows.Thickness CardPadding { get; }

    public double IconSize { get; }

    public double IconRadius { get; }

    /// <summary>图标的圆角裁剪几何，供 XAML 绑到元素的 <c>Clip</c> 上。</summary>
    public System.Windows.Media.RectangleGeometry IconClip { get; }

    public double InitialFontSize { get; }

    public double NameFontSize { get; }

    // ---------- 图标 ----------

    public BitmapImage? Icon
    {
        get => _icon;
        private set
        {
            if (SetProperty(ref _icon, value))
            {
                OnPropertyChanged(nameof(HasIcon));
            }
        }
    }

    public bool HasIcon => _icon is not null;

    /// <summary>图标文件路径，投屏时拿它当窗口图标（没图标就留空）。</summary>
    public string? IconPath => _iconPath;

    /// <summary>还在等图标：没拿到，而且重试次数还没用尽（重试节奏 3s / 6s / 12s）。</summary>
    public bool NeedsIconRetry => _icon is null && _retries < RetryDelays.Length;

    // ---------- 名称高亮 ----------

    public IReadOnlyList<NameSegment> Segments
    {
        get => _segments;
        private set
        {
            if (SetProperty(ref _segments, value))
            {
                OnPropertyChanged(nameof(HasHighlight));
            }
        }
    }

    /// <summary>搜索命中名称时为 true，界面上换成带高亮的分段渲染。</summary>
    public bool HasHighlight => _segments.Count != 1 || _segments[0].IsMark;

    // ---------- 快捷启动角标 ----------

    /// <summary>这个包名在不在当前设备的快捷启动列表里。</summary>
    public bool IsInQuick
    {
        get => _isInQuick;
        private set
        {
            if (SetProperty(ref _isInQuick, value))
            {
                OnPropertyChanged(nameof(ShowCheckBadge));
                OnPropertyChanged(nameof(ShowPlusBadge));
                OnPropertyChanged(nameof(AddButtonHint));
            }
        }
    }

    public bool ShowRemoveBadge => InQuickArea;

    public bool ShowCheckBadge => !InQuickArea && _isInQuick;

    public bool ShowPlusBadge => !InQuickArea && !_isInQuick;

    public string AddButtonHint => InQuickArea
        ? "移出快捷启动"
        : _isInQuick ? "已在快捷启动，点击移除" : "添加到快捷启动";

    public ICommand LaunchCommand { get; }

    public ICommand ToggleQuickCommand { get; }

    // ---------- 由父页面驱动 ----------

    /// <summary>按命中区间把名称拆成分段（未命中就整段不标记）。</summary>
    public void ApplyHighlight(IReadOnlyList<(int Start, int End)> ranges)
    {
        if (ranges.Count == 0)
        {
            Segments = [new NameSegment(Name, false)];
            return;
        }

        var segments = new List<NameSegment>();
        int cursor = 0;
        foreach ((int start, int end) in ranges)
        {
            if (start > cursor)
            {
                segments.Add(new NameSegment(Name[cursor..start], false));
            }
            segments.Add(new NameSegment(Name[start..end], true));
            cursor = end;
        }
        if (cursor < Name.Length)
        {
            segments.Add(new NameSegment(Name[cursor..], false));
        }
        Segments = segments.Count > 0 ? segments : [new NameSegment(Name, false)];
    }

    public void SetInQuick(bool value) => IsInQuick = value;

    /// <summary>立刻查一次素材库；没命中就排下一次重试。</summary>
    public void RequestIcon()
    {
        string? path = _resolver.RequestIcon(Package, Name);
        if (path is not null && SetIcon(path))
        {
            return;
        }
        ScheduleRetry();
    }

    /// <summary>父页面每秒调一次。图标库换了版本时重来一轮（<paramref name="revChanged"/>）。</summary>
    public void TickIcon(bool revChanged)
    {
        if (revChanged)
        {
            _retries = 0;
            _nextRetryAt = 0;
        }
        if (_icon is not null || _retries >= RetryDelays.Length)
        {
            return;
        }
        if (_nextRetryAt != 0 && Environment.TickCount64 < _nextRetryAt)
        {
            return;
        }
        RequestIcon();
    }

    private bool SetIcon(string path)
    {
        BitmapImage? image = _loadIcon(path);
        if (image is null)
        {
            return false;
        }
        _iconPath = path;
        Icon = image;
        return true;
    }

    private void ScheduleRetry()
    {
        if (_retries >= RetryDelays.Length)
        {
            return;
        }
        _nextRetryAt = Environment.TickCount64 + (RetryDelays[_retries] * 1000L);
        _retries++;
    }
}
