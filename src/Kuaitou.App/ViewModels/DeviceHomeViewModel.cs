using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kuaitou.Core;
using Kuaitou.Core.Apps;
using Kuaitou.Core.Scrcpy;
using Kuaitou.Core.Storage;

namespace Kuaitou.App.ViewModels;

/// <summary>
/// 一台设备的首页。对应旧版 index.html 的 <c>.main</c>（搜索行 + 快捷启动区 + 全部应用区）
/// 加底部 <c>.statusbar</c>。
///
/// 应用列表按设备缓存（后台扫描，界面不等它）；图标来源是「手机取回 &gt; 可写目录 &gt; 内置素材库」，
/// 再补一层在线抓取，全程不阻塞。抓不到就摆首字母底色，按 3s / 6s / 12s 重试。
/// </summary>
public sealed class DeviceHomeViewModel : ObservableObject, IDisposable
{
    /// <summary>每台设备快捷启动的上限，加满后要先移出一个才能再加。</summary>
    private const int QuickMax = 8;

    /// <summary>图标解码后允许的最大物理边长（像素）。够 48 DIP 的卡片用，又不会让整图常驻内存。</summary>
    private const int MaxIconPixels = 128;

    private static readonly TimeSpan IconTickInterval = TimeSpan.FromSeconds(1);

    /// <summary>图标都拿齐之后改用的慢节奏：只用来发现「图标库换了版本」，不必每秒醒一次。</summary>
    private static readonly TimeSpan IconIdleInterval = TimeSpan.FromSeconds(5);

    private readonly DeviceServices _services;
    private readonly Dispatcher _dispatcher;

    private readonly Dictionary<string, BitmapImage> _iconCache = new(StringComparer.Ordinal);
    private readonly List<AppCardViewModel> _allCards = [];
    private readonly List<AppCardViewModel> _quickCards = [];
    private readonly Dictionary<string, AppCardViewModel> _gridCards = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _iconTimer;

    private List<AppEntry> _apps = [];
    private List<string> _quickList = [];
    private AppSearchIndex? _searchIndex;
    private int _lastRev = -1;
    private bool _loaded;
    private bool _disposed;

    private string _search = "";
    private string _hint = "";
    private bool _hintIsError;

    public DeviceHomeViewModel(DeviceServices services, Dispatcher dispatcher, DeviceItem device)
    {
        _services = services;
        _dispatcher = dispatcher;
        Device = device;

        LaunchDesktopCommand = new AsyncRelayCommand(LaunchDesktopAsync, onError: OnError);
        UnlockCommand = new AsyncRelayCommand(UnlockAsync, onError: OnError);
        ScreenOffCommand = new AsyncRelayCommand(ScreenOffAsync, onError: OnError);
        RefreshCommand = new AsyncRelayCommand(() => LoadAppsAsync(force: true), onError: OnError);
        ClearSearchCommand = new RelayCommand(() => Search = "");

        _iconTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = IconTickInterval };
        _iconTimer.Tick += OnIconTick;
    }

    /// <summary>这台主页对应的设备。轮询合并就地更新同一个对象，绑定不会断。</summary>
    public DeviceItem Device { get; }

    /// <summary>「全部应用」区当前显示的应用（搜索时就是搜索结果）。</summary>
    public ObservableCollection<AppCardViewModel> Apps { get; } = [];

    /// <summary>快捷启动区的应用卡片。</summary>
    public ObservableCollection<AppCardViewModel> QuickApps { get; } = [];

    public ICommand LaunchDesktopCommand { get; }

    public ICommand UnlockCommand { get; }

    public ICommand ScreenOffCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand ClearSearchCommand { get; }

    /// <summary>应用搜索框内容（拼音 / 首字母匹配，每次改动立刻过滤）。</summary>
    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value))
            {
                OnPropertyChanged(nameof(HasSearch));
                ApplyFilter();
            }
        }
    }

    /// <summary>搜索框里有内容时才显示那个清空按钮。</summary>
    public bool HasSearch => _search.Length > 0;

    /// <summary>状态栏上的提示文本。空串表示不显示。</summary>
    public string Hint
    {
        get => _hint;
        private set
        {
            if (SetProperty(ref _hint, value))
            {
                OnPropertyChanged(nameof(HasHint));
            }
        }
    }

    public bool HintIsError
    {
        get => _hintIsError;
        private set => SetProperty(ref _hintIsError, value);
    }

    public bool HasHint => !string.IsNullOrEmpty(Hint);

    /// <summary>右下角的应用计数：有搜索时显示命中数，没搜索时显示总数。</summary>
    public string AppCountText => $"{Apps.Count} 个应用";

    /// <summary>快捷启动区标题右侧的计数：一个都没有时留空（跟旧版一致）。</summary>
    public string QuickCountText => _quickList.Count > 0 ? $"{_quickList.Count} / {QuickMax} 个" : "";

    public bool HasQuick => QuickApps.Count > 0;

    public bool HasApps => Apps.Count > 0;

    /// <summary>搜了但一条没命中。</summary>
    public bool HasNoResults => _loaded && HasSearch && Apps.Count == 0;

    /// <summary>没搜索、列表也是空的（扫描失败或设备上真没有可启动应用）。</summary>
    public bool HasNoApps => _loaded && !HasSearch && Apps.Count == 0;

    /// <summary>进页面时调一次：起图标重试定时器、后台读应用列表、按需从手机取一次图标。</summary>
    public void Start()
    {
        _iconTimer.Start();
        _ = LoadAppsAsync(force: false);
        _services.IconSync.AutoSync(Device.Serial);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _iconTimer.Stop();
        _iconTimer.Tick -= OnIconTick;
        _allCards.Clear();
        _quickCards.Clear();
        _gridCards.Clear();
        Apps.Clear();
        QuickApps.Clear();
        _iconCache.Clear();
    }

    // ---------- 应用列表 ----------

    /// <summary>
    /// 读应用列表（<paramref name="force"/> 为 true 时忽略缓存重扫）。扫描放后台线程，
    /// 界面先出缓存里的旧列表，扫完再换新的。手动刷新时顺手让手机侧重取一遍图标。
    /// </summary>
    private async Task LoadAppsAsync(bool force)
    {
        if (_disposed)
        {
            return;
        }

        string serial = Device.Serial;
        SetHint(force ? "正在刷新应用列表与图标…" : "正在读取应用列表…", isError: false);
        if (force)
        {
            _services.IconSync.Resync(serial);
        }

        List<AppEntry> apps;
        try
        {
            apps = await Task.Run(() => _services.Apps.ListApps(serial, force)).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            SetHint($"读取应用列表失败：{e.Message}", isError: true);
            _loaded = true;
            OnPropertyChanged(nameof(HasNoApps));
            OnPropertyChanged(nameof(HasNoResults));
            return;
        }

        if (_disposed)
        {
            return;
        }

        BuildApps(apps);
        _loaded = true;
        if (apps.Count == 0)
        {
            SetHint(Device.IsOnline ? "没能读到应用列表，点「刷新」可以重试" : "设备不在线，应用列表来自上次缓存", isError: true);
        }
        else
        {
            SetHint("", isError: false);
        }
    }

    private void BuildApps(List<AppEntry> apps)
    {
        _apps = apps;
        _searchIndex = new AppSearchIndex(apps);

        _allCards.Clear();
        _gridCards.Clear();
        foreach (AppEntry app in apps)
        {
            var card = new AppCardViewModel(app, inQuickArea: false, _services.IconFetch, LoadIcon, LaunchCard, ToggleQuick);
            _allCards.Add(card);
            _gridCards[app.Package] = card;
        }

        _quickList = LoadQuickList();
        foreach (AppCardViewModel card in _allCards)
        {
            card.SetInQuick(_quickList.Contains(card.Package));
        }

        RebuildQuickCards();
        ApplyFilter();
        _lastRev = _services.Icons.Rev;

        foreach (AppCardViewModel card in _allCards)
        {
            card.RequestIcon();
        }
    }

    /// <summary>
    /// 这台设备的快捷启动包名：优先专属列表，没有就用配置里的 "*" 默认值
    /// （旧版 <c>_quick_list</c> 的取值顺序）。
    /// </summary>
    private List<string> LoadQuickList()
    {
        Dictionary<string, List<string>> map = _services.Config.Load().QuickLaunch;
        if (map.TryGetValue(Device.Serial, out List<string>? own))
        {
            return [.. own];
        }
        if (map.TryGetValue("*", out List<string>? fallback))
        {
            return [.. fallback];
        }
        return [];
    }

    private void RebuildQuickCards()
    {
        QuickApps.Clear();
        _quickCards.Clear();
        foreach (string package in _quickList)
        {
            AppEntry app = _apps.FirstOrDefault(a => a.Package == package)
                ?? new AppEntry { Name = package, Package = package };
            var card = new AppCardViewModel(app, inQuickArea: true, _services.IconFetch, LoadIcon, LaunchCard, ToggleQuick);
            card.SetInQuick(true);
            _quickCards.Add(card);
            QuickApps.Add(card);
            card.RequestIcon();
        }
        OnPropertyChanged(nameof(HasQuick));
        OnPropertyChanged(nameof(QuickCountText));
    }

    private void ApplyFilter()
    {
        Apps.Clear();
        if (_searchIndex is not null)
        {
            foreach (AppMatch match in _searchIndex.Search(_search))
            {
                if (!_gridCards.TryGetValue(match.App.Package, out AppCardViewModel? card))
                {
                    continue;
                }
                card.ApplyHighlight(match.Ranges);
                Apps.Add(card);
            }
        }
        OnPropertyChanged(nameof(AppCountText));
        OnPropertyChanged(nameof(HasApps));
        OnPropertyChanged(nameof(HasNoResults));
        OnPropertyChanged(nameof(HasNoApps));
    }

    private void ToggleQuick(AppCardViewModel card)
    {
        if (_disposed)
        {
            return;
        }

        int index = _quickList.IndexOf(card.Package);
        if (index < 0 && _quickList.Count >= QuickMax)
        {
            SetHint($"快捷启动最多 {QuickMax} 个，先移出一个再加", isError: true);
            return;
        }
        if (index >= 0)
        {
            _quickList.RemoveAt(index);
        }
        else
        {
            _quickList.Add(card.Package);
        }

        // 快捷启动按设备各存一份
        List<string> snapshot = [.. _quickList];
        _services.Config.Update(c => c.QuickLaunch[Device.Serial] = snapshot);

        foreach (AppCardViewModel grid in _allCards)
        {
            grid.SetInQuick(_quickList.Contains(grid.Package));
        }
        RebuildQuickCards();
    }

    // ---------- 图标 ----------

    private void OnIconTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        int rev = _services.Icons.Rev;
        bool changed = rev != _lastRev;
        if (changed)
        {
            _lastRev = rev;
            _iconCache.Clear();     // 图标被覆写了，解码缓存必须跟着作废
        }

        foreach (AppCardViewModel card in _allCards)
        {
            card.TickIcon(changed);
        }
        foreach (AppCardViewModel card in _quickCards)
        {
            card.TickIcon(changed);
        }

        // 还有卡片在等图标就保持 1 秒一眼（重试节奏是 3s / 6s / 12s），
        // 全都拿齐了就退到 5 秒的慢节奏——后台白白每秒醒一次没有意义。
        _iconTimer.Interval = WaitingForIcons() ? IconTickInterval : IconIdleInterval;
    }

    /// <summary>是否还有卡片在等图标（没拿到、也还没用完重试次数）。</summary>
    private bool WaitingForIcons()
        => _allCards.Any(card => card.NeedsIconRetry) || _quickCards.Any(card => card.NeedsIconRetry);

    /// <summary>按路径取一张冻结的位图。图标是数据流 / 普通文件，WPF 认不了这种路径，只能自己读字节。</summary>
    private BitmapImage? LoadIcon(string path)
    {
        if (_iconCache.TryGetValue(path, out BitmapImage? cached))
        {
            return cached;
        }

        BitmapImage? image = Decode(path);
        if (image is not null)
        {
            _iconCache[path] = image;
        }
        return image;
    }

    private static BitmapImage? Decode(string path)
    {
        try
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length == 0)
            {
                return null;
            }

            // 按显示尺寸解码，而不是整张原图：安卓应用图标动辄 512×512，几十上百个一起
            // 以全分辨率常驻，光位图就是几十上百 MB。卡片最大只画到 48 DIP，这里限制到
            // 128 物理像素，清晰度够用，单张内存从约 1MB 降到约 64KB。
            using var stream = new MemoryStream(data);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = MaxIconPixels;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------- 动作 ----------

    private async Task LaunchDesktopAsync()
    {
        SetHint("正在启动投屏…", isError: false);
        try
        {
            ScrcpySession session = await Task.Run(() => _services.Launcher.LaunchDesktop(Device.Serial))
                .ConfigureAwait(true);
            // 探活等 3 秒：进程若秒退，把 scrcpy 的真实报错顶到状态栏上，
            // 而不是让用户对着「点了没反应」发愣
            LaunchProbe probe = await Task.Run(() => _services.Launcher.Probe(session)).ConfigureAwait(true);
            SetHint(probe.Ok ? "" : probe.Error, isError: !probe.Ok);
        }
        catch (Exception e)
        {
            SetHint($"投屏启动失败：{e.Message}", isError: true);
        }
    }

    private async Task UnlockAsync()
    {
        SetHint("正在解锁…", isError: false);
        ActionResult result = await Task.Run(() => _services.Unlock.UnlockNow(Device.Serial)).ConfigureAwait(true);
        SetHint(result.Message, isError: !result.Ok);
    }

    private async Task ScreenOffAsync()
    {
        SetHint("正在关闭物理屏幕…", isError: false);
        ActionResult result = await Task.Run(() => _services.ScreenPower.ScreenOff(Device.Serial)).ConfigureAwait(true);
        SetHint(result.Message, isError: !result.Ok);
    }

    private void LaunchCard(AppCardViewModel card) => _ = LaunchCardAsync(card);

    private async Task LaunchCardAsync(AppCardViewModel card)
    {
        if (!Device.IsOnline)
        {
            SetHint("设备不在线，无法投屏", isError: true);
            return;
        }

        SetHint($"正在打开 {card.Name}…", isError: false);
        try
        {
            string? iconPath = RoundedIcon(card.IconPath);
            LaunchOutcome outcome = await Task.Run(
                () => _services.Launcher.LaunchApp(card.Package, Device.Serial, card.Name, iconPath))
                .ConfigureAwait(true);

            if (outcome.AlreadyRunning)
            {
                // 同一个应用已经在投屏：没有重复拉起，只把已有窗口唤到了前台
                SetHint($"「{card.Name}」已在投屏中，已切到该窗口", isError: false);
                return;
            }

            LaunchProbe probe = await Task.Run(() => _services.Launcher.Probe(outcome.Session)).ConfigureAwait(true);
            SetHint(probe.Ok ? "" : probe.Error, isError: !probe.Ok);
        }
        catch (Exception e)
        {
            SetHint($"投屏启动失败：{e.Message}", isError: true);
        }
    }

    private void SetHint(string text, bool isError)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(() => SetHint(text, isError));
            return;
        }
        HintIsError = isError;
        Hint = text;
    }

    private void OnError(Exception e) => SetHint($"操作失败：{e.Message}", isError: true);

    /// <summary>
    /// 给投屏窗口的图标做圆角。scrcpy 只认一张方形 PNG（SCRCPY_ICON_DIR 里的 scrcpy.png），
    /// 圆角只能提前烘进图片：这里按界面同款比例渲染一张带透明角的 PNG，按源路径缓存到临时目录，
    /// 同一个应用重复投屏不再重渲染。任何一步失败都退回原图 —— 少个圆角，别影响投屏。
    /// </summary>
    private static string? RoundedIcon(string? iconPath)
    {
        if (string.IsNullOrEmpty(iconPath) || !File.Exists(iconPath))
        {
            return iconPath;
        }
        try
        {
            string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(iconPath)))[..16];
            string dir = Path.Combine(Path.GetTempPath(), "Kuaitou-icons-rounded");
            Directory.CreateDirectory(dir);
            string outPath = Path.Combine(dir, name + ".png");
            if (File.Exists(outPath))
            {
                return outPath;
            }

            var source = new BitmapImage();
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.UriSource = new Uri(iconPath, UriKind.Absolute);
            source.EndInit();

            int width = source.PixelWidth;
            int height = source.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                return iconPath;
            }

            // 与界面里 48px / 圆角 12px 的比例对齐（约 22%）
            double radius = Math.Min(width, height) * 0.22;
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height), radius, radius));
                dc.DrawImage(source, new Rect(0, 0, width, height));
                dc.Pop();
            }

            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using FileStream stream = File.Create(outPath);
            encoder.Save(stream);
            return outPath;
        }
        catch (Exception)
        {
            return iconPath;
        }
    }
}
