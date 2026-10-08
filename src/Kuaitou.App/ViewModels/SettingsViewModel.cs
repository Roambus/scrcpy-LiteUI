using System.Collections.ObjectModel;
using System.Windows.Input;
using Kuaitou.App.Services;
using Kuaitou.Core;
using Kuaitou.Core.Scrcpy;
using Kuaitou.Core.Storage;
using Kuaitou.Core.Sys;
using Microsoft.Win32;

namespace Kuaitou.App.ViewModels;

/// <summary>
/// 设置页的视图模型。对应旧版 index.html 里三块设置面板 + 那批 saveXxx 函数。
///
/// 每个属性的 setter 都是「改内存里的值 → 通过 ConfigStore 串行读-改-写落盘」，
/// 与旧版每个控件 onchange 就发一次 POST /api/config 是同一个语义。
/// 写失败会在提示条上如实说明，绝不假装保存成功（配置是覆盖写的，静默失败用户无从察觉）。
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly DeviceServices _services;
    private readonly ConfigStore _store;
    private readonly Action<bool>? _onTrayChanged;
    private readonly Action<string, bool>? _showToast;
    private readonly bool _ready;
    private readonly RelayCommand _deletePresetCommand;
    private readonly AsyncRelayCommand _runSelfCheckCommand;
    private readonly AsyncRelayCommand _runDeepDiagCommand;
    private readonly AsyncRelayCommand _saveReportCommand;
    private readonly AsyncRelayCommand _exportLogCommand;
    private bool _diagnosing;

    private string _hint = "";
    private bool _hintIsError;
    private string _diagText = "";

    private bool _reconnectEnabled;
    private bool _autostart;
    private bool _minimizeToTray;
    private bool _noControl;
    private bool _powerOffOnClose;
    private bool _mouseCapture;
    private bool _isLightTheme;
    private string _ip = "";
    private string _port = "";
    private string _fps = "";
    private string _bitrate = "";
    private string _videoCodec = "auto";
    private string _maxSize = "";
    private string _resW = "";
    private string _resH = "";
    private string _scale = "";
    private string _audioMode = "both";
    private string _selectedPreset = "";

    public SettingsViewModel(
        DeviceServices services,
        ConfigStore store,
        AppConfig cfg,
        DevicesViewModel devices,
        AddDeviceViewModel addDevice,
        Action<bool>? onTrayChanged = null,
        Action<string, bool>? showToast = null)
    {
        _services = services;
        _store = store;
        _onTrayChanged = onTrayChanged;
        _showToast = showToast;
        Devices = devices;
        AddDevice = addDevice;

        _reconnectEnabled = cfg.ReconnectEnabled;
        _autostart = cfg.Autostart;
        _minimizeToTray = cfg.MinimizeToTray;
        _noControl = cfg.NoControl;
        _powerOffOnClose = cfg.PowerOffOnClose;
        _mouseCapture = cfg.MouseCapture;
        _isLightTheme = cfg.Theme != ThemeManager.Dark;
        _ip = cfg.Ip;
        _port = cfg.Port;
        _fps = cfg.Fps;
        _bitrate = cfg.Bitrate;
        _videoCodec = cfg.VideoCodec;
        _maxSize = cfg.MaxSize;
        _resW = cfg.ResW;
        _resH = cfg.ResH;
        _scale = cfg.Scale;
        _audioMode = cfg.AudioMode;
        foreach (string preset in cfg.ResPresets)
        {
            ResPresets.Add(preset);
        }

        _deletePresetCommand = new RelayCommand(DeletePreset, () => !string.IsNullOrEmpty(SelectedPreset));

        // 诊断命令：跑自检 / 完整诊断（后台线程，跑完弹另存为对话框），以及重存 / 导出
        _runSelfCheckCommand = new AsyncRelayCommand(() => RunDiagAsync(deep: false), CanRunDiag);
        _runDeepDiagCommand = new AsyncRelayCommand(() => RunDiagAsync(deep: true), CanRunDiag);
        _saveReportCommand = new AsyncRelayCommand(SaveReportAsync, CanRunDiag);
        _exportLogCommand = new AsyncRelayCommand(ExportLogAsync, CanRunDiag);

        // 初始化赋值全部走的是字段直写、不经过 setter；从这里开始才允许落盘
        _ready = true;

        // 配置读坏了要如实告知，不能默默回落成默认值
        if (store.LastLoadError is { } error)
        {
            SetHint(error, isError: true);
        }
    }

    public string Version => AppInfo.Version;

    /// <summary>设备列表与连接操作，挂在「设备与连接」板块里；生命周期由主窗口管。</summary>
    public DevicesViewModel Devices { get; }

    /// <summary>「添加设备」板块（扫码 / 扫描 / 深度搜索 / 配对码）；生命周期同样归主窗口管。</summary>
    public AddDeviceViewModel AddDevice { get; }

    /// <summary>画面选项的可选值（供下拉框绑定）。</summary>
    public IReadOnlyList<Option> VideoCodecOptions { get; } =
    [
        new("auto", "自动（推荐）"),
        new("h264", "H.264（兼容性最好）"),
        new("h265", "H.265（同画质更省带宽）"),
    ];

    public IReadOnlyList<Option> AudioModeOptions { get; } =
    [
        new("both", "手机和电脑都播放"),
        new("phone", "仅手机播放"),
        new("pc", "仅电脑播放"),
    ];

    public ObservableCollection<string> ResPresets { get; } = [];

    public ICommand DeletePresetCommand => _deletePresetCommand;

    /// <summary>诊断板块的四个按钮：环境自检 / 完整诊断 / 保存诊断报告 / 导出日志。</summary>
    public ICommand RunSelfCheckCommand => _runSelfCheckCommand;

    public ICommand RunDeepDiagCommand => _runDeepDiagCommand;

    public ICommand SaveReportCommand => _saveReportCommand;

    public ICommand ExportLogCommand => _exportLogCommand;

    /// <summary>诊断结果文本。空串表示还没跑过，界面上那个结果框就收起来。</summary>
    public string DiagText
    {
        get => _diagText;
        private set
        {
            if (SetProperty(ref _diagText, value))
            {
                OnPropertyChanged(nameof(HasDiag));
            }
        }
    }

    public bool HasDiag => !string.IsNullOrEmpty(DiagText);

    /// <summary>底部提示条文本。空串表示不显示。</summary>
    public string Hint
    {
        get => _hint;
        private set => SetProperty(ref _hint, value);
    }

    public bool HintIsError
    {
        get => _hintIsError;
        private set => SetProperty(ref _hintIsError, value);
    }

    public bool HasHint => !string.IsNullOrEmpty(Hint);

    public bool ReconnectEnabled
    {
        get => _reconnectEnabled;
        set
        {
            if (SetProperty(ref _reconnectEnabled, value))
            {
                Persist(c => c.ReconnectEnabled = value);
            }
        }
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set
        {
            if (SetProperty(ref _minimizeToTray, value))
            {
                Persist(c => c.MinimizeToTray = value);
                // 开关一动就启停托盘图标（由主窗口持有的 TrayService 去落实）
                _onTrayChanged?.Invoke(value);
            }
        }
    }

    /// <summary>开机自启。开启时自动把「缩小到托盘」也打开（否则开机后没有出口）。</summary>
    public bool Autostart
    {
        get => _autostart;
        set
        {
            if (!SetProperty(ref _autostart, value))
            {
                return;
            }
            if (value && !MinimizeToTray)
            {
                _minimizeToTray = true;
                OnPropertyChanged(nameof(MinimizeToTray));
                _onTrayChanged?.Invoke(true);
            }
            Persist(c =>
            {
                c.Autostart = value;
                if (value)
                {
                    c.MinimizeToTray = true;
                }
            });
            // 写 / 删注册表由 AutoStartService 落实；开发态不碰注册表，会如实说明
            ActionResult result = _services.AutoStart.Apply(value);
            _showToast?.Invoke(result.Message, !result.Ok);
        }
    }

    public bool NoControl
    {
        get => _noControl;
        set
        {
            if (SetProperty(ref _noControl, value))
            {
                Persist(c => c.NoControl = value);
            }
        }
    }

    public bool PowerOffOnClose
    {
        get => _powerOffOnClose;
        set
        {
            if (SetProperty(ref _powerOffOnClose, value))
            {
                Persist(c => c.PowerOffOnClose = value);
            }
        }
    }

    public bool MouseCapture
    {
        get => _mouseCapture;
        set
        {
            if (SetProperty(ref _mouseCapture, value))
            {
                Persist(c => c.MouseCapture = value);
            }
        }
    }

    /// <summary>浅色主题开关（旧版界面上也是一个开关，不是下拉）。</summary>
    public bool IsLightTheme
    {
        get => _isLightTheme;
        set
        {
            if (!SetProperty(ref _isLightTheme, value))
            {
                return;
            }
            string theme = value ? ThemeManager.Light : ThemeManager.Dark;
            ThemeManager.Apply(theme);
            Persist(c => c.Theme = theme);
        }
    }

    public string Ip
    {
        get => _ip;
        set
        {
            if (SetProperty(ref _ip, value))
            {
                Persist(c => c.Ip = value);
            }
        }
    }

    public string Port
    {
        get => _port;
        set
        {
            if (SetProperty(ref _port, value))
            {
                Persist(c => c.Port = value);
            }
        }
    }

    public string Fps
    {
        get => _fps;
        set
        {
            if (SetProperty(ref _fps, value))
            {
                Persist(c => c.Fps = value);
            }
        }
    }

    public string Bitrate
    {
        get => _bitrate;
        set
        {
            if (SetProperty(ref _bitrate, value))
            {
                Persist(c => c.Bitrate = value);
            }
        }
    }

    public string VideoCodec
    {
        get => _videoCodec;
        set
        {
            if (SetProperty(ref _videoCodec, value))
            {
                Persist(c => c.VideoCodec = value);
            }
        }
    }

    public string MaxSize
    {
        get => _maxSize;
        set
        {
            if (SetProperty(ref _maxSize, value))
            {
                Persist(c => c.MaxSize = value);
            }
        }
    }

    public string ResW
    {
        get => _resW;
        set
        {
            if (SetProperty(ref _resW, value))
            {
                Persist(c => c.ResW = value);
            }
        }
    }

    public string ResH
    {
        get => _resH;
        set
        {
            if (SetProperty(ref _resH, value))
            {
                Persist(c => c.ResH = value);
            }
        }
    }

    public string Scale
    {
        get => _scale;
        set
        {
            if (SetProperty(ref _scale, value))
            {
                Persist(c => c.Scale = value);
            }
        }
    }

    public string AudioMode
    {
        get => _audioMode;
        set
        {
            if (SetProperty(ref _audioMode, value))
            {
                Persist(c => c.AudioMode = value);
            }
        }
    }

    public string SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetProperty(ref _selectedPreset, value))
            {
                _deletePresetCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>套用选中的预设到宽高。</summary>
    public void ApplySelectedPreset()
    {
        if (string.IsNullOrEmpty(SelectedPreset))
        {
            return;
        }
        string[] parts = SelectedPreset.Split('x');
        if (parts.Length != 2)
        {
            return;
        }
        ResW = parts[0];
        ResH = parts[1];
    }

    /// <summary>把当前宽高存成一个预设。</summary>
    public void SavePreset()
    {
        string w = ResW.Trim();
        string h = ResH.Trim();
        if (w.Length == 0 || h.Length == 0)
        {
            SetHint("请先输入宽高，再保存为预设", isError: true);
            return;
        }

        string preset = $"{w}x{h}";
        if (ResPresets.Contains(preset))
        {
            SetHint($"预设「{preset}」已存在", isError: false);
            return;
        }

        if (!_store.Update(c =>
        {
            if (!c.ResPresets.Contains(preset))
            {
                c.ResPresets.Add(preset);
            }
        }))
        {
            SetHint("配置没有写入成功（存储位置不可写），这次改动不会保留", isError: true);
            return;
        }

        ResPresets.Add(preset);
        SetHint($"已保存预设 {preset}", isError: false);
    }

    /// <summary>删除选中的预设，删前二次确认。</summary>
    private void DeletePreset()
    {
        string preset = SelectedPreset;
        if (string.IsNullOrEmpty(preset))
        {
            return;
        }

        bool ok = Views.ConfirmDialog.Ask(
            System.Windows.Application.Current?.MainWindow,
            "删除这个分辨率预设？",
            $"预设「{preset}」会被移除。删掉只能重新加回来。",
            "删除",
            danger: true);
        if (!ok)
        {
            return;
        }

        if (!_store.Update(c => c.ResPresets.Remove(preset)))
        {
            SetHint("配置没有写入成功（存储位置不可写），这次改动不会保留", isError: true);
            return;
        }

        ResPresets.Remove(preset);
        SelectedPreset = "";
        SetHint($"已删除预设 {preset}", isError: false);
    }

    // ---------- 诊断 ----------

    private bool CanRunDiag() => !_diagnosing;

    /// <summary>跑一次诊断（自检 / 完整诊断），跑完把报告交给用户另存为。对应旧版 runDiag()。</summary>
    private async Task RunDiagAsync(bool deep)
    {
        _diagnosing = true;
        RaiseDiagCanExecute();
        DiagText = deep
            ? "正在做完整诊断（会实际拉起一次 scrcpy 扫应用，约需 10-60 秒）…"
            : "正在自检…";
        try
        {
            DiagText = await Task.Run(() => _services.Diagnose.Run(deep)).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            DiagText = "诊断失败: " + e.Message;
        }
        finally
        {
            _diagnosing = false;
            RaiseDiagCanExecute();
        }

        // 诊断完成后弹「另存为」由用户自己挑保存位置（取消则只在这里显示结果）
        SaveText(LogCollector.SuggestedReportName(DateTime.Now), DiagText, "诊断报告");
    }

    /// <summary>重存上一次的诊断报告。对应旧版 saveReport()。</summary>
    private Task SaveReportAsync()
    {
        if (!HasDiag)
        {
            Toast("请先运行「环境自检」或「完整诊断」", false);
            return Task.CompletedTask;
        }
        SaveText(LogCollector.SuggestedReportName(DateTime.Now), DiagText, "诊断报告");
        return Task.CompletedTask;
    }

    /// <summary>汇总投屏 / 扫描 / 错误日志，同样由用户选保存位置。对应旧版 exportLog()。</summary>
    private Task ExportLogAsync()
    {
        SaveText(LogCollector.SuggestedFileName(DateTime.Now), LogCollector.Collect(_services.Store), "日志");
        return Task.CompletedTask;
    }

    /// <summary>弹系统「另存为」，写入结果如实反馈（保存位置一律由用户决定）。</summary>
    private void SaveText(string defaultName, string text, string label)
    {
        var dialog = new SaveFileDialog
        {
            FileName = defaultName,
            DefaultExt = ".txt",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        if (LogCollector.SaveText(dialog.FileName, text, out string error) is not null)
        {
            Toast($"{label}已保存到：{dialog.FileName}", false);
        }
        else
        {
            Toast($"保存失败：{error}", true);
        }
    }

    private void RaiseDiagCanExecute()
    {
        _runSelfCheckCommand.RaiseCanExecuteChanged();
        _runDeepDiagCommand.RaiseCanExecuteChanged();
        _saveReportCommand.RaiseCanExecuteChanged();
        _exportLogCommand.RaiseCanExecuteChanged();
    }

    /// <summary>优先走窗口里的 Toast；没有接线的场合退回底部提示条。</summary>
    private void Toast(string text, bool isError)
    {
        if (_showToast is not null)
        {
            _showToast(text, isError);
        }
        else
        {
            SetHint(text, isError);
        }
    }

    private void Persist(Action<AppConfig> mutate)
    {
        if (!_ready)
        {
            return;
        }

        if (_store.Update(mutate))
        {
            SetHint("");
            return;
        }
        SetHint("配置没有写入成功（存储位置不可写），这次改动不会保留", isError: true);
    }

    private void SetHint(string text, bool isError = false)
    {
        HintIsError = isError;
        Hint = text;
        OnPropertyChanged(nameof(HasHint));
    }

    /// <summary>下拉框一个选项：值是 config 里存的那个，文字是界面上显示的。</summary>
    public sealed record Option(string Value, string Label);
}
