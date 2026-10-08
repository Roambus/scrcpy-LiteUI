using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kuaitou.Core;
using Kuaitou.Core.Discover;
using Kuaitou.Core.Scrcpy;
using Kuaitou.Core.Sys;
using Net.Codecrete.QrCodeGenerator;

namespace Kuaitou.App.ViewModels;

/// <summary>
/// 扫描结果里的一行。局域网扫描与深度搜索共用同一个列表（对应 legacy 的 renderScanList）。
/// </summary>
public sealed class ScanItem
{
    public ScanItem(DiscoverEntry entry)
    {
        Entry = entry;
        Tag = entry.Usb ? "USB 有线"
            : entry.Connected ? "已连接"
            : entry.Pending ? "待授权 · 手机上点「允许」"
            : entry.Pairing ? "待配对 · 点一下填地址"
            : entry.PortScan ? "端口扫描 · 点一下连接"
            : "点一下连接";
    }

    public DiscoverEntry Entry { get; }

    public string Addr => Entry.Addr;

    public string Ip => Entry.Ip;

    public string Port => Entry.Port;

    /// <summary>来源说明（如「已知设备」「深度搜索（mDNS）」），界面上当提示文字。</summary>
    public string Source => Entry.Source;

    /// <summary>行尾那枚状态标签。</summary>
    public string Tag { get; }

    public bool Connected => Entry.Connected;

    public bool Pairing => Entry.Pairing;

    /// <summary>online=已连接（绿）/ wait=待配对或待授权（黄）/ 其它为普通。</summary>
    public string StateKind => Entry.Connected ? "online" : (Entry.Pairing || Entry.Pending) ? "wait" : "";
}

/// <summary>
/// 设置页「添加设备」板块。对应 legacy/kuaitou/index.html 的扫码连接（<c>startQr</c> / <c>pollQr</c>）、
/// 局域网扫描（<c>discover</c> / <c>renderScanList</c> / <c>onScanClick</c>）与深度搜索
/// （<c>deepDiscover</c> 的 SSE + 轮询两条进度通道）。
///
/// 进度不走轮询也不走回调事件排队：<see cref="QrPairingService"/> 与 <see cref="DeepScanJob"/>
/// 都在后台线程里跑，状态一变就回调一次（「状态没变不发」由 Core 层保证），这里只负责
/// 把回调切回界面线程刷新。
/// </summary>
public sealed class AddDeviceViewModel : ObservableObject, IDisposable
{
    private const string QrButtonIdle = "扫码连接（推荐）";
    private const string DeepButtonIdle = "深度搜索";

    private const string QrHintDefault =
        "手机（Android 11+）：设置 → 开发者选项 → 无线调试 → 使用二维码配对设备，扫描这里出现的二维码。";

    private const string ScanHintDefault =
        "「扫描」查同网段 5555 端口；「深度搜索」= 局域网 + mDNS + 端口扫描（30000~50000，多线程），"
        + "能发现跨网段 / 随机端口的设备，较慢但最全（搜索中再点一下可随时停止）。";

    private readonly DeviceServices _services;
    private readonly DevicesViewModel _devices;
    private readonly Dispatcher _dispatcher;

    private bool _qrActive;
    private bool _qrVisible;
    private ImageSource? _qrImage;
    private string _qrSub = "";
    private string _qrHint = QrHintDefault;
    private string _qrButtonText = QrButtonIdle;

    private string _scanHint = ScanHintDefault;
    private bool _deepActive;
    private bool _deepVisible;
    private double _deepPercent;
    private string _deepButtonText = DeepButtonIdle;

    private string _pairPort = "";
    private string _pairCode = "";

    public AddDeviceViewModel(DeviceServices services, DevicesViewModel devices, Dispatcher dispatcher)
    {
        _services = services;
        _devices = devices;
        _dispatcher = dispatcher;

        QrToggleCommand = new RelayCommand(ToggleQr);
        DeepToggleCommand = new RelayCommand(ToggleDeep);
        ScanCommand = new AsyncRelayCommand(ScanAsync, onError: OnError);
        SelectCommand = new AsyncRelayCommand<ScanItem>(SelectAsync, onError: OnError);
        PairCommand = new AsyncRelayCommand(PairAsync, CanPair, OnError);
    }

    /// <summary>列表里点中「待配对」那一行后，界面上要把焦点移到配对码输入框。</summary>
    public event Action? PairFocusRequested;

    public ObservableCollection<ScanItem> Results { get; } = [];

    public ICommand QrToggleCommand { get; }

    public ICommand DeepToggleCommand { get; }

    public ICommand ScanCommand { get; }

    public ICommand SelectCommand { get; }

    public ICommand PairCommand { get; }

    /// <summary>扫描 / 深度搜索 / 配对结果共用的提示行。</summary>
    public string ScanHint
    {
        get => _scanHint;
        private set => SetProperty(ref _scanHint, value);
    }

    public string QrHint
    {
        get => _qrHint;
        private set => SetProperty(ref _qrHint, value);
    }

    public string QrButtonText
    {
        get => _qrButtonText;
        private set => SetProperty(ref _qrButtonText, value);
    }

    /// <summary>二维码下方那行「名称 … · 密码 …」。</summary>
    public string QrSub
    {
        get => _qrSub;
        private set => SetProperty(ref _qrSub, value);
    }

    public ImageSource? QrImage
    {
        get => _qrImage;
        private set => SetProperty(ref _qrImage, value);
    }

    /// <summary>只有正在等手机扫码时才显示二维码面板。</summary>
    public bool QrVisible
    {
        get => _qrVisible;
        private set => SetProperty(ref _qrVisible, value);
    }

    public string DeepButtonText
    {
        get => _deepButtonText;
        private set => SetProperty(ref _deepButtonText, value);
    }

    /// <summary>深度搜索进行中才显示进度条。</summary>
    public bool DeepVisible
    {
        get => _deepVisible;
        private set => SetProperty(ref _deepVisible, value);
    }

    /// <summary>深度搜索进度（0~100）。</summary>
    public double DeepPercent
    {
        get => _deepPercent;
        private set => SetProperty(ref _deepPercent, value);
    }

    public string PairPort
    {
        get => _pairPort;
        set
        {
            if (SetProperty(ref _pairPort, value))
            {
                ((AsyncRelayCommand)PairCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public string PairCode
    {
        get => _pairCode;
        set
        {
            if (SetProperty(ref _pairCode, value))
            {
                ((AsyncRelayCommand)PairCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public void Dispose()
    {
        _services.QrPairing.Cancel();
        _services.DeepScan.Dispose();
    }

    // ---------- 扫码连接 ----------

    private void ToggleQr()
    {
        if (_qrActive)
        {
            _services.QrPairing.Cancel();      // 回调会把按钮与面板复位
            QrHint = "已取消扫码配对";
            return;
        }

        QrState state = _services.QrPairing.Start(OnQrUpdate);
        QrSub = $"名称 {state.Name} · 密码 {state.Password}";
        QrImage = BuildQr(QrPairingService.Payload(state.Name, state.Password));
        QrHint = QrImage is null
            ? "二维码生成失败，请在手机上选「使用配对码配对设备」，用下面的「首次配对」输入配对码和上方的密码。"
            : "已生成二维码，等待手机扫描…";
        QrVisible = true;
        SetQrActive(true);
    }

    private void OnQrUpdate(QrState state)
    {
        if (_dispatcher.HasShutdownStarted)
        {
            return;                        // 窗口已经关了，回调没人接，安静收工
        }

        _dispatcher.Invoke(() =>
        {
            if (state.Message.Length > 0)
            {
                QrHint = state.Message;
            }

            switch (state.State)
            {
                case "done":
                    QrVisible = false;
                    SetQrActive(false);
                    QrHint = $"扫码连接成功：{state.Addr}";
                    _ = _devices.RefreshOnceAsync();
                    break;
                case "failed":
                case "timeout":
                case "idle":
                    QrVisible = false;
                    SetQrActive(false);
                    break;
            }
        });
    }

    private void SetQrActive(bool active)
    {
        _qrActive = active;
        QrButtonText = active ? "停止扫码配对" : QrButtonIdle;
    }

    /// <summary>
    /// 把配对文本画成二维码位图。按模块坐标建几何、按固定倍率栅格化到白底黑块：
    /// 二维码必须是不透明的黑白两色，直接摊成矢量在深色主题下会没有静区。
    /// 画不出来时返回 null，由调用方退回「配对码」提示。
    /// </summary>
    private static ImageSource? BuildQr(string payload)
    {
        try
        {
            const int quietZone = 2;      // 静区（模块数）：扫码器要靠它把码从背景里分出来
            const int scale = 8;          // 每模块 8px，够清晰也不会太大
            QrCode qr = QrCode.EncodeText(payload, QrCode.Ecc.Medium);
            int dim = (qr.Size + (quietZone * 2)) * scale;

            var modules = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (QrRectangle rect in qr.ToRectangles())
            {
                modules.Children.Add(new RectangleGeometry(
                    new Rect(
                        (rect.X + quietZone) * scale,
                        (rect.Y + quietZone) * scale,
                        rect.Width * scale,
                        rect.Height * scale)));
            }

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, dim, dim));
                dc.DrawGeometry(Brushes.Black, null, modules);
            }

            var bitmap = new RenderTargetBitmap(dim, dim, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------- 局域网扫描 ----------

    private async Task ScanAsync()
    {
        ScanHint = "正在扫描局域网设备（只探测 5555 端口）…";
        Results.Clear();
        DiscoverResult result = await Task.Run(_services.Lan.Scan).ConfigureAwait(true);
        ReplaceResults(result.Found);
        ScanHint = Results.Count == 0
            ? "未发现可用设备：请确认手机已开启「无线调试」且与电脑在同一网络，然后重试"
            : $"发现 {Results.Count} 个可用设备（耗时 {result.Elapsed}s），点一下即可直接连接";
    }

    // ---------- 深度搜索 ----------

    private void ToggleDeep()
    {
        if (_deepActive)
        {
            DeepButtonText = "正在停止…";
            _services.DeepScan.Cancel();       // 收尾仍由回调那一路负责
            return;
        }

        Results.Clear();
        DeepPercent = 0;
        DeepVisible = true;
        SetDeepActive(true);
        ScanHint = "正在搜索局域网设备 + mDNS（约 6 秒先出结果）…";
        _services.DeepScan.Start(OnDeepUpdate);
    }

    private void OnDeepUpdate(DeepState state)
    {
        if (_dispatcher.HasShutdownStarted)
        {
            return;                        // 窗口已经关了，回调没人接，安静收工
        }

        _dispatcher.Invoke(() =>
        {
            ReplaceResults(state.Found);
            DeepPercent = Math.Round(state.Progress * 1000) / 10.0;
            DeepVisible = state.Running;
            if (state.Text.Length > 0)
            {
                ScanHint = state.Text;
            }

            if (!state.Running)
            {
                SetDeepActive(false);
                ScanHint = SummarizeDeep(state);
            }
        });
    }

    private void SetDeepActive(bool active)
    {
        _deepActive = active;
        DeepButtonText = active ? "停止搜索" : DeepButtonIdle;
    }

    private static string SummarizeDeep(DeepState state)
    {
        string parts = $"mDNS {state.MdnsCount} / 局域网 {state.LanCount}"
            + (state.PortCount > 0 ? $" / 端口扫描 {state.PortCount}" : "");
        if (state.Found.Count == 0)
        {
            if (state.Error.Length > 0)
            {
                return state.Error;
            }
            return state.Canceled
                ? "已停止搜索"
                : "没发现目标：请确认手机已打开「无线调试」或与电脑在同一局域网，然后重试";
        }

        bool hasPairing = state.Found.Any(x => x.Pairing);
        return (state.Canceled ? "已停止搜索：" : "")
            + $"发现 {state.Found.Count} 个目标（{parts}，耗时 {state.Elapsed}s）"
            + (hasPairing ? "；「待配对」的点一下会把地址填进下面的「首次配对」" : "，点一下即可直接连接");
    }

    private void ReplaceResults(IReadOnlyList<DiscoverEntry> found)
    {
        Results.Clear();
        foreach (DiscoverEntry entry in found)
        {
            Results.Add(new ScanItem(entry));
        }
    }

    // ---------- 结果列表点击 ----------

    private async Task SelectAsync(ScanItem item)
    {
        if (item.Connected)
        {
            DeviceItem? device = _devices.Devices.FirstOrDefault(d => d.Serial == item.Addr);
            if (device is not null)
            {
                _devices.Selected = device;
            }
            ScanHint = $"已切换到 {item.Addr}";
            return;
        }

        if (item.Pairing)
        {
            // 手机正停在「使用配对码配对设备」界面：把地址填进首次配对的输入框，
            // 配对码还得看手机（配对协议由 adb 完成，免配对码那条路走「扫码连接」）。
            _devices.ConnectIp = item.Ip;
            PairPort = item.Port;
            PairFocusRequested?.Invoke();
            ScanHint = $"已填入配对地址 {item.Addr}：请在手机上点「使用配对码配对设备」并输入手机显示的配对码";
            return;
        }

        ScanHint = $"正在连接 {item.Addr}…";
        ConnectResult result = await Task.Run(
            () => _services.Connection.Connect(item.Ip, item.Port)).ConfigureAwait(true);
        if (result.Ok)
        {
            await _devices.RefreshOnceAsync().ConfigureAwait(true);
        }
        ScanHint = result.Message;
    }

    // ---------- 配对码配对 ----------

    private bool CanPair() => PairPort.Trim().Length > 0 && PairCode.Trim().Length > 0;

    private async Task PairAsync()
    {
        string ip = _devices.ConnectIp.Trim();
        if (ip.Length == 0)
        {
            ScanHint = "请先在上面的「按地址连接」里填手机 IP";
            return;
        }

        ScanHint = $"正在与 {ip}:{PairPort.Trim()} 配对…";
        ActionResult result = await Task.Run(
            () => _services.Connection.Pair(ip, PairPort.Trim(), PairCode.Trim())).ConfigureAwait(true);
        ScanHint = result.Message;
        if (result.Ok)
        {
            await _devices.RefreshOnceAsync().ConfigureAwait(true);
        }
    }

    private void OnError(Exception e) => ScanHint = $"操作失败：{e.Message}";
}
