using System.Text.RegularExpressions;

namespace Kuaitou.Core.Apps;

/// <summary>
/// 解析 <c>scrcpy --list-apps</c> 的输出。对应 legacy/kuaitou/apps.py 的 <c>_parse_apps_output</c>。
///
/// 输出形如 <c> - 应用名    com.foo.bar</c>：按行取「首个字段=应用名、末个字段=包名」，
/// 包名做一次格式校验防脏数据；重名包只留一条。返回按名称排序的列表。
/// 纯函数，不碰 adb 也不碰文件，直接单测盖住。
/// </summary>
public static class AppsOutputParser
{
    /// <summary>合法包名：字母开头，至少两段点分标识符。</summary>
    private static readonly Regex PackagePattern =
        new(@"^[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+$", RegexOptions.Compiled);

    /// <summary>两个以上空白当分隔符：应用名里的单个空格不会被切开。</summary>
    private static readonly Regex Separator = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>包名格式校验：字母开头，至少两段点分标识符。</summary>
    public static bool IsPackageName(string? package)
        => !string.IsNullOrEmpty(package) && PackagePattern.IsMatch(package);

    public static List<AppEntry> Parse(string? output)
    {
        var apps = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string rawLine in (output ?? "").Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || (line[0] != '-' && line[0] != '*'))
            {
                continue;
            }

            line = line.TrimStart('-', '*').Trim();
            string[] parts = Separator.Split(line);
            if (parts.Length < 2)
            {
                continue;
            }

            string label = parts[0].Trim();
            string package = parts[^1].Trim();
            if (label.Length == 0 || !IsPackageName(package) || !seen.Add(package))
            {
                continue;
            }
            apps.Add(new AppEntry { Name = label, Package = package });
        }

        // 按名称排序，忽略大小写（对应旧版 key=lambda x: x["name"].lower()）
        apps.Sort((a, b) => string.Compare(a.Name.ToLowerInvariant(), b.Name.ToLowerInvariant(),
            StringComparison.Ordinal));
        return apps;
    }
}
