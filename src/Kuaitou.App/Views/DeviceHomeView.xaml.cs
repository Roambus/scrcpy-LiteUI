using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kuaitou.App.ViewModels;

namespace Kuaitou.App.Views;

/// <summary>
/// 单台设备的首页。数据由 <see cref="DeviceHomeViewModel"/> 提供，
/// 侧边栏选中设备时由主窗口换掉 <c>DataContext</c>。
/// </summary>
public partial class DeviceHomeView : UserControl
{
    public DeviceHomeView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 双击应用卡片 = 启动该应用并投屏（对应旧版卡片上的 <c>ondblclick</c>）。
    /// 角标按钮自己吃掉了单击，所以点 ＋ / × 不会连带触发这里。
    /// </summary>
    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement { DataContext: AppCardViewModel card })
        {
            return;
        }

        e.Handled = true;
        ICommand command = card.LaunchCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
