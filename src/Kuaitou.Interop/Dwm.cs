using System.Runtime.InteropServices;

namespace Kuaitou.Interop;

/// <summary>
/// 桌面窗口管理器（DWM）的外观设置。主窗口是自绘标题栏，主题配色由我们自己的 XAML 负责，
/// 这里只补两件 XAML 做不到的事：Win11 的圆角、以及让系统按深色渲染窗口边框与阴影。
/// 调用在老系统上会返回错误码（属性不存在），静默忽略即可，不影响功能。
/// </summary>
public static class Dwm
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    private const int DwmwcpDoNotRound = 1;
    private const int DwmwcpRound = 2;

    /// <summary>交回系统默认颜色（<c>DWMWA_COLOR_DEFAULT</c>）。</summary>
    public const int ColorDefault = -1;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    /// <summary>深色主题下把窗口边框 / 阴影切成深色版本。</summary>
    public static void SetDarkMode(nint hwnd, bool dark)
    {
        int value = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
    }

    /// <summary>Win11 圆角。自绘窗口（WindowStyle=None）默认是不圆角的，得显式要。</summary>
    public static void SetRoundedCorners(nint hwnd, bool rounded)
    {
        int value = rounded ? DwmwcpRound : DwmwcpDoNotRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref value, sizeof(int));
    }

    /// <summary>
    /// 给仍带系统标题栏的窗口（投屏窗口就是）刷上主题色：标题栏底色 / 标题文字色 / 边框色。
    /// 三个值都是 0x00BBGGRR 的 COLORREF，传 <see cref="ColorDefault"/> 交回系统默认。
    /// </summary>
    public static void SetCaptionColors(nint hwnd, int caption, int text, int border)
    {
        _ = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
    }
}
