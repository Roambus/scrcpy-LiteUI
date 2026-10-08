using System.Reflection;

namespace Kuaitou.Core;

/// <summary>
/// 程序自身的元信息。版本号显示在设置页底部和诊断报告里——exe 被拷到多台电脑排查问题时，
/// 靠它一眼就能确认两边跑的是不是同一个版本。
///
/// 版本号唯一来源是仓库根目录的 Directory.Build.props（发版脚本与 CI 都读那一份），
/// 这里只是把它读出来，不另存一份数字，免得两处不一致。
/// </summary>
public static class AppInfo
{
    /// <summary>三段式版本号，如 <c>2.0.0</c>。</summary>
    public static string Version
    {
        get
        {
            Assembly asm = typeof(AppInfo).Assembly;
            string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(info))
            {
                // InformationalVersion 可能带 "+<commit sha>" 后缀（SourceLink 加的），去掉
                int plus = info.IndexOf('+', StringComparison.Ordinal);
                return plus >= 0 ? info[..plus] : info;
            }
            Version? v = asm.GetName().Version;
            return v is null ? "0.0.0" : v.ToString(3);
        }
    }
}
