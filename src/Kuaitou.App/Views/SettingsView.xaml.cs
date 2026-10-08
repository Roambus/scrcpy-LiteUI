using System.Windows;
using System.Windows.Controls;
using Kuaitou.App.ViewModels;

namespace Kuaitou.App.Views;

/// <summary>
/// 设置页四大板块（设备与连接 / 画面与声音 / 常规 / 诊断）。
/// 板块切换由侧边栏驱动 <see cref="PanelIndex"/>，不用 TabControl 的标签头。
/// </summary>
public partial class SettingsView : UserControl
{
    public static readonly DependencyProperty PanelIndexProperty = DependencyProperty.Register(
        nameof(PanelIndex),
        typeof(int),
        typeof(SettingsView),
        new PropertyMetadata(0, OnPanelIndexChanged));

    private AddDeviceViewModel? _addDevice;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachDevices();
    }

    /// <summary>0=设备与连接 / 1=画面与声音 / 2=常规 / 3=诊断。</summary>
    public int PanelIndex
    {
        get => (int)GetValue(PanelIndexProperty);
        set => SetValue(PanelIndexProperty, value);
    }

    private static void OnPanelIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SettingsView)d).ApplyPanel();

    private void ApplyPanel()
    {
        // 「设备与连接」是两块：设备行（PanelDevice）+ 添加设备（PanelConnect），同进同出
        PanelDevice.Visibility = PanelIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelConnect.Visibility = PanelIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelScreen.Visibility = PanelIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanelGeneral.Visibility = PanelIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        PanelDiag.Visibility = PanelIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    /// <summary>订阅「待配对」请求：扫描结果里点了那一行，就把光标送进配对码输入框。</summary>
    private void AttachDevices()
    {
        if (_addDevice is not null)
        {
            _addDevice.PairFocusRequested -= OnPairFocusRequested;
        }
        _addDevice = ViewModel?.AddDevice;
        if (_addDevice is not null)
        {
            _addDevice.PairFocusRequested += OnPairFocusRequested;
        }
    }

    /// <summary>扫描结果里点了「待配对」那一行：地址已填好，把光标送进配对码输入框。</summary>
    private void OnPairFocusRequested() => PairCodeBox.Focus();

    /// <summary>
    /// 设备行里的密码框改动时同步进那一行的设备对象。
    /// PasswordBox.Password 不是依赖属性（WPF 有意不让明文密码进绑定系统），所以只能这么接。
    /// </summary>
    private void OnDevicePinChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && box.DataContext is DeviceItem device)
        {
            device.Pin = box.Password;
        }
    }

    /// <summary>
    /// 密码框换设备（模板复用）时，把它显示的内容换成新设备那份，
    /// 免得上一台打的字留在框里、被当成这一台的密码存下去。
    /// </summary>
    private void OnDevicePinBoxDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is PasswordBox box)
        {
            box.Password = (e.NewValue as DeviceItem)?.Pin ?? "";
        }
    }

    private void OnSavePreset(object sender, RoutedEventArgs e) => ViewModel?.SavePreset();

    private void OnPresetSelected(object sender, SelectionChangedEventArgs e) => ViewModel?.ApplySelectedPreset();
}
