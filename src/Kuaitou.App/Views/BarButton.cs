using System.Windows.Media;

namespace Kuaitou.App.Views;

/// <summary>
/// 投屏功能栏上的一个按钮：动作名、提示文字、图标几何，以及两个可变状态
/// （<see cref="IsOn"/> 已开高亮、<see cref="Size"/> 边长）。
/// </summary>
public sealed class BarButton : ViewModels.ObservableObject
{
    private bool _isOn;
    private double _size = 30;

    public BarButton(string action, string label)
    {
        Action = action;
        Label = label;
        Glyph = MirrorGlyphs.For(action);
    }

    /// <summary>动作名，与 <see cref="Kuaitou.Core.Scrcpy.DeviceActionService"/> 里的常量一致。</summary>
    public string Action { get; }

    /// <summary>鼠标悬停提示，也当作无障碍名称。</summary>
    public string Label { get; }

    private MirrorGlyph? Glyph { get; }

    /// <summary>描边几何（Path 只设 Stroke）。</summary>
    public Geometry? Outline => Glyph?.Outline;

    /// <summary>实心几何（Path 只设 Fill），没有实心部分的图标为 null。</summary>
    public Geometry? Solid => Glyph?.Solid;

    /// <summary>置顶 / 强制横屏开着时为 true，界面上换成强调色。</summary>
    public bool IsOn
    {
        get => _isOn;
        set => SetProperty(ref _isOn, value);
    }

    /// <summary>按钮边长（逻辑像素），由功能栏按画面高度均分算出来。</summary>
    public double Size
    {
        get => _size;
        set => SetProperty(ref _size, value);
    }

    /// <summary>
    /// 两种模式共用「置顶 / 音量 + / 音量 − / 强制横屏」，仅镜像桌面追加
    /// 「返回 / 桌面 / 多任务 / 通知栏 / 控制中心」（顺序即自上而下的排列顺序）。
    /// </summary>
    public static List<BarButton> CreateAll(bool desktop)
    {
        var buttons = new List<BarButton>
        {
            new("pin", "置顶"),
            new("volume_up", "音量 +"),
            new("volume_down", "音量 −"),
            new("rotate_lock", "强制横屏"),
        };
        if (desktop)
        {
            buttons.Add(new BarButton("back", "返回"));
            buttons.Add(new BarButton("home", "桌面"));
            buttons.Add(new BarButton("app_switch", "多任务"));
            buttons.Add(new BarButton("notifications", "通知栏"));
            buttons.Add(new BarButton("control_center", "控制中心"));
        }
        return buttons;
    }
}
