using System.Windows;
using System.Windows.Input;

namespace Kuaitou.App.Views;

/// <summary>
/// 自绘确认弹窗。破坏性操作（断开设备 / 删除分辨率预设）在动手前弹它做二次确认。
/// </summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog()
    {
        InitializeComponent();
    }

    /// <summary>弹窗并等待用户选择。返回 true 表示点了确认。</summary>
    public static bool Ask(Window? owner, string title, string desc, string okText, bool danger = false)
    {
        var dialog = new ConfirmDialog();
        if (owner is not null && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }
        dialog.TitleText.Text = title;
        dialog.DescText.Text = desc;
        dialog.OkButton.Content = okText;
        if (danger)
        {
            dialog.OkButton.Style = (Style)dialog.FindResource("BtnDanger");
        }
        return dialog.ShowDialog() == true;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Esc 关闭（默认按钮的键盘行为之外再补一条）
        PreviewKeyDown += OnKeyDown;
    }
}
