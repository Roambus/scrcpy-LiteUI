using System.Collections.ObjectModel;
using System.Windows;

namespace Kuaitou.App.Services;

/// <summary>
/// 主题切换。做法是整体替换应用级 MergedDictionaries 里的令牌字典
/// （Themes/Tokens.Light.xaml ↔ Themes/Tokens.Dark.xaml）。
///
/// 因此界面里引用令牌的地方必须一律用 DynamicResource：用 StaticResource 的话，
/// 字典被换掉了引用还指着旧对象，界面上不会有任何变化。
/// 首屏不闪靠「窗口还没画就先装好字典」天然成立，不需要旧版那套 no-anim 冻结过渡。
/// </summary>
public static class ThemeManager
{
    public const string Light = "light";
    public const string Dark = "dark";

    private const string LightTokens = "pack://application:,,,/Themes/Tokens.Light.xaml";
    private const string DarkTokens = "pack://application:,,,/Themes/Tokens.Dark.xaml";

    /// <summary>当前主题标识，与配置键 theme 的取值一致（light / dark）。</summary>
    public static string Current { get; private set; } = Light;

    /// <summary>主题切换后触发。需要跟着换色的非 XAML 资源（如 DWM 深色标题栏）订阅它。</summary>
    public static event Action<string>? ThemeChanged;

    public static void Apply(string theme)
    {
        Current = theme == Dark ? Dark : Light;

        var dict = new ResourceDictionary
        {
            Source = new Uri(Current == Dark ? DarkTokens : LightTokens, UriKind.Absolute),
        };

        ReplaceTokens(dict);
        ThemeChanged?.Invoke(Current);
    }

    /// <summary>找到现有令牌字典并原地替换，其余全局资源（样式、画笔等）保持不动。</summary>
    private static void ReplaceTokens(ResourceDictionary dict)
    {
        Collection<ResourceDictionary> merged = Application.Current.Resources.MergedDictionaries;
        for (int i = 0; i < merged.Count; i++)
        {
            string? source = merged[i].Source?.OriginalString;
            if (source is not null && source.Contains("Tokens.", StringComparison.Ordinal))
            {
                merged[i] = dict;
                return;
            }
        }
        merged.Add(dict);
    }
}
