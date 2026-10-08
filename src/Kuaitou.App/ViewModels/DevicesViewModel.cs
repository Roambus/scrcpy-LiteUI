using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using Kuaitou.Core;
using Kuaitou.Core.Adb;
using Kuaitou.Core.Scrcpy;
using Kuaitou.Core.Storage;

namespace Kuaitou.App.ViewModels;

/// <summary>列表里的一台设备（在线 / 等待授权 / 离线三态之一）。</summary>
public sealed class DeviceItem : ObservableObject
{
    private string _name;
    private string _address;
    private string _stateKind;
    private string _stateText;
    private string _pin = "";
    private bool _hasSavedPin;

    public DeviceItem(string serial, string name, string address, string stateKind, string stateText)
    {
        Serial = serial;
        _name = name;
        _address = address;
        _stateKind = stateKind;
        _stateText = stateText;
    }

    /// <summary>adb 里的设备标识；USB 是序列号，无线是 ip:port。</summary>
    public string Serial { get; }

    /// <summary>设备型号；取不到时等于序列号。</summary>
    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    /// <summary>界面上显示的连接地址。</summary>
    public string Address
    {
        get => _address;
        private set => SetProperty(ref _address, value);
    }

    /// <summary>online / auth / offline。</summary>
    public string StateKind
    {
        get => _stateKind;
        private set
        {
            if (SetProperty(ref _stateKind, value))
            {
                OnPropertyChanged(nameof(IsOnline));
            }
        }
    }

    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    /// <summary>是否在线可控（只有在线才能投屏/解锁/关屏）。</summary>
    public bool IsOnline => _stateKind == "online";

    /// <summary>
    /// 这台设备的锁屏数字密码。每台设备各自一份（存的时候按设备键归档），
    /// 所以密码框直接长在设备行里，接几台都不会互相串。
    /// </summary>
    public string Pin
    {
        get => _pin;
        set => SetProperty(ref _pin, value);
    }

    /// <summary>
    /// 本地是否已经给这台设备存过密码，只用来在行里显示「已设置 / 未设置」。
    /// 密码本身不回填进输入框：既没必要，也会在后台读完的瞬间把用户刚打的字冲掉。
    /// </summary>
    public bool HasSavedPin
    {
        get => _hasSavedPin;
        set => SetProperty(ref _hasSavedPin, value);
    }

    /// <summary>
    /// 就地更新内容。轮询合并时刻意复用同一个行对象而不是重建：ListBox 靠对象引用记选中项，
    /// 每 3 秒换一个对象会让选中的设备闪断、密码框跟着清空重填。
    /// </summary>
    public void UpdateFrom(DeviceItem other)
    {
        Name = other.Name;
        Address = other.Address;
        StateKind = other.StateKind;
        StateText = other.StateText;
    }
}

/// <summary>
/// 设备列表与连接操作。对应旧版 index.html 里顶栏设备下拉 +「添加设备」面板 + 设置页
/// 「已连接设备」那一块。状态每 3 秒轮询一次，与旧版的轮询节奏一致。
/// </summary>
public sealed class DevicesViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>界面收进托盘 / 最小化时用的慢节奏轮询：后台不需要 3 秒一眼。</summary>
    private static readonly TimeSpan BackgroundPollInterval = TimeSpan.FromSeconds(15);

    private readonly DeviceServices _services;
    private readonly Dispatcher _dispatcher;

    private CancellationTokenSource? _cts;
    private Task? _poll;
    private volatile bool _background;

    private string _connectIp = "";
    private string _connectPort = "5555";
    private readonly HashSet<string> _pinLoaded = [];   // 已经读过锁屏密码的设备序列号（每台只读一次）
    private DeviceItem? _selected;
    private string _hint = "";
    private bool _hintIsError;

    public DevicesViewModel(DeviceServices services, Dispatcher dispatcher)
    {
        _services = services;
        _dispatcher = dispatcher;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => _connectIp.Trim().Length > 0, OnCommandError);
        UsbToWifiCommand = new AsyncRelayCommand(UsbToWifiAsync, onError: OnCommandError);
        RefreshCommand = new AsyncRelayCommand(RefreshOnceAsync, onError: OnCommandError);
        UnlockCommand = new AsyncRelayCommand<DeviceItem>(UnlockAsync, onError: OnCommandError);
        ScreenOffCommand = new AsyncRelayCommand<DeviceItem>(ScreenOffAsync, onError: OnCommandError);
        DisconnectCommand = new AsyncRelayCommand<DeviceItem>(DisconnectAsync, onError: OnCommandError);
        SavePinCommand = new AsyncRelayCommand<DeviceItem>(SavePinAsync, onError: OnCommandError);
    }

    public ObservableCollection<DeviceItem> Devices { get; } = [];

    /// <summary>「最近连接」：连上一次就记一笔，之后不用再输 IP / 端口。</summary>
    public ObservableCollection<RecentDevice> RecentDevices { get; } = [];

    public ICommand ConnectCommand { get; }

    public ICommand UsbToWifiCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand UnlockCommand { get; }

    public ICommand ScreenOffCommand { get; }

    public ICommand DisconnectCommand { get; }

    public ICommand SavePinCommand { get; }

    /// <summary>底部提示条文本。空串表示不显示。</summary>
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

    /// <summary>列表里是否有设备；没有时界面上显示「还没有设备连接」那行说明。</summary>
    public bool HasDevices => Devices.Count > 0;

    public string ConnectIp
    {
        get => _connectIp;
        set
        {
            if (SetProperty(ref _connectIp, value))
            {
                ((RelayCommand)ConnectCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public string ConnectPort
    {
        get => _connectPort;
        set => SetProperty(ref _connectPort, value);
    }

    public DeviceItem? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    /// <summary>
    /// 界面是否已收进后台（托盘 / 最小化）。真进去的时候把轮询放慢，回到前台再恢复 3 秒节奏。
    /// </summary>
    public void SetBackground(bool background) => _background = background;

    /// <summary>启动后台轮询。界面关闭时调用 <see cref="Dispose"/> 停掉。</summary>
    public void Start()
    {
        if (_poll is not null)
        {
            return;
        }
        LoadRecent();
        _cts = new CancellationTokenSource();
        _poll = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    /// <summary>把配置里的「最近连接」灌进列表；连接成功后再调一次。</summary>
    private void LoadRecent()
    {
        RecentDevices.Clear();
        foreach (RecentDevice device in _services.Config.Load().RecentDevices)
        {
            RecentDevices.Add(device);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try
        {
            _poll?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // 退出时不等也无所谓
        }
        _cts?.Dispose();
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await RefreshOnceAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 单轮失败不影响继续轮询（无设备、adb 缺失等都不该让界面崩）
            }

            try
            {
                await Task.Delay(_background ? BackgroundPollInterval : PollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>立刻刷新一次设备列表（后台线程算，回到 UI 线程更新集合）。</summary>
    public async Task RefreshOnceAsync()
    {
        DeviceStates states = await Task.Run(() => _services.Inventory.GetStates()).ConfigureAwait(false);

        var items = new List<DeviceItem>();
        foreach (string serial in states.Device)
        {
            items.Add(Build(serial, "online", "已连接"));
        }
        foreach (string serial in states.Unauthorized)
        {
            items.Add(Build(serial, "auth", "等待授权"));
        }
        foreach (string serial in states.Offline)
        {
            items.Add(Build(serial, "offline", "离线"));
        }

        _dispatcher.Invoke(() => Sync(items));
    }

    private DeviceItem Build(string serial, string kind, string stateText)
    {
        DeviceAddress addr = AdbOutput.SplitAddress(serial);
        string name = kind == "offline" ? addr.Ip : _services.Inventory.GetModel(serial);
        string address = addr.Port.Length > 0 ? $"{addr.Ip}:{addr.Port}" : addr.Ip;
        return new DeviceItem(serial, name, address, kind, stateText);
    }

    private void Sync(List<DeviceItem> items)
    {
        // 就地合并：序号没变的行只更新内容，位置变了的用 Move 挪过去，绝不重建行对象
        // —— 重建会让 ListBox 的选中项闪断，3 秒一次的轮询就成了每 3 秒闪一次。
        for (int i = 0; i < items.Count; i++)
        {
            int existing = IndexOfSerial(items[i].Serial, i);
            if (existing < 0)
            {
                Devices.Insert(i, items[i]);
                continue;
            }
            if (existing != i)
            {
                Devices.Move(existing, i);
            }
            Devices[i].UpdateFrom(items[i]);
        }
        while (Devices.Count > items.Count)
        {
            Devices.RemoveAt(Devices.Count - 1);
        }

        // 选中的那台不在了（拔线、断开、转成离线后被清掉）才重新解析选中项。
        if (_selected is not null && !Devices.Contains(_selected))
        {
            string serial = _selected.Serial;
            Selected = Devices.FirstOrDefault(d => d.Serial == serial);
        }

        LoadPins();
        OnPropertyChanged(nameof(HasDevices));
    }

    private int IndexOfSerial(string serial, int start)
    {
        for (int i = start; i < Devices.Count; i++)
        {
            if (Devices[i].Serial == serial)
            {
                return i;
            }
        }
        return -1;
    }

    private async Task ConnectAsync()
    {
        string ip = ConnectIp.Trim();
        string port = ConnectPort.Trim();
        SetStatus($"正在连接 {ip}…", isError: false);

        ConnectResult result = await Task.Run(() => _services.Connection.Connect(ip, port)).ConfigureAwait(true);
        await RefreshOnceAsync().ConfigureAwait(true);
        if (result.Ok)
        {
            LoadRecent();
        }
        SetStatus(result.Ok ? result.Message : $"连接失败：{result.Message}", isError: !result.Ok);
    }

    private async Task UsbToWifiAsync()
    {
        SetStatus("正在把 USB 连接转成无线…", isError: false);
        WirelessResult result = await Task.Run(() => _services.Wireless.UsbToWifi()).ConfigureAwait(true);
        await RefreshOnceAsync().ConfigureAwait(true);
        if (result.Ok && result.Addr.Length > 0)
        {
            ConnectIp = AdbOutput.SplitAddress(result.Addr).Ip;
            LoadRecent();
        }
        SetStatus(result.Message, isError: !result.Ok);
    }

    private async Task UnlockAsync(DeviceItem device)
    {
        SetStatus($"正在解锁 {device.Name}…", isError: false);
        ActionResult result = await Task.Run(() => _services.Unlock.UnlockNow(device.Serial)).ConfigureAwait(true);
        SetStatus(result.Message, isError: !result.Ok);
    }

    private async Task ScreenOffAsync(DeviceItem device)
    {
        SetStatus($"正在关闭 {device.Name} 的物理屏幕…", isError: false);
        ActionResult result = await Task.Run(() => _services.ScreenPower.ScreenOff(device.Serial)).ConfigureAwait(true);
        SetStatus(result.Message, isError: !result.Ok);
    }

    private async Task DisconnectAsync(DeviceItem device)
    {
        await Task.Run(() => _services.Connection.Disconnect(device.Serial)).ConfigureAwait(true);
        await RefreshOnceAsync().ConfigureAwait(true);
        SetStatus($"已断开 {device.Address}", isError: false);
    }

    /// <summary>
    /// 点「保存密码」：把这台设备行里填的密码按设备归档。
    /// 每台设备一份，互不影响，也不再依赖「先在上面选中一台」。
    /// </summary>
    private async Task SavePinAsync(DeviceItem device)
    {
        string pin = device.Pin ?? "";
        bool ok = await Task.Run(() => _services.Unlock.SetPin(device.Serial, pin)).ConfigureAwait(true);
        if (ok)
        {
            device.HasSavedPin = pin.Length > 0;
        }
        SetStatus(
            ok
                ? pin.Length == 0
                    ? $"已清除「{device.Name}」的解锁密码"
                    : $"「{device.Name}」的解锁密码已保存（只存在本机数据流里，按设备归档）"
                : "解锁密码保存失败",
            isError: !ok);
    }

    /// <summary>
    /// 读出每台在线设备「有没有存过密码」（每台只读一次），只更新那个「已设置 / 未设置」的标记。
    /// 刻意不回填密码本身：读的动作在后台线程上，回来时用户可能已经在输入框里打字了，
    /// 一回填就会把刚打的字冲成空串——表现就是「点了保存却说已清除」。
    /// </summary>
    private void LoadPins()
    {
        foreach (DeviceItem device in Devices)
        {
            if (!device.IsOnline || !_pinLoaded.Add(device.Serial))
            {
                continue;
            }
            string serial = device.Serial;
            _ = Task.Run(() =>
            {
                bool has = _services.Unlock.GetPin(serial).Length > 0;
                _dispatcher.Invoke(() =>
                {
                    DeviceItem? current = Devices.FirstOrDefault(d => d.Serial == serial);
                    if (current is not null)
                    {
                        current.HasSavedPin = has;
                    }
                });
            });
        }
    }

    private void SetStatus(string text, bool isError)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(() => SetStatus(text, isError));
            return;
        }
        HintIsError = isError;
        Hint = text;
    }

    private void OnCommandError(Exception e) => SetStatus($"操作失败：{e.Message}", isError: true);
}
