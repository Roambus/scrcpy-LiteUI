using System.Reflection;
using System.Text;

namespace Kuaitou.Core.Apps;

/// <summary>一次搜索命中的结果：应用、评分、名称里要高亮的字符区间 <c>[start, end)</c>。</summary>
public sealed record AppMatch(AppEntry App, double Score, IReadOnlyList<(int Start, int End)> Ranges);

/// <summary>
/// 应用搜索：拼音 / 首字母 / 多关键词 / 评分 / 高亮区间。对应 index.html 的
/// <c>PINYIN_GROUPS</c> / <c>buildUnits</c> / <c>alignUnits</c> / <c>mergeRanges</c> /
/// <c>matchApp</c> / <c>filterApps</c>。
///
/// 名称拆成「可匹配单元」：汉字带全拼与首字母，英数原样，空白与标点不占位。
/// 单元只在构造索引时算一次，之后每次按键搜索都复用——旧版把它缓存在 <c>app._units</c> 上。
/// </summary>
public sealed class AppSearchIndex
{
    /// <summary>汉字 → 拼音（声母韵母）。内置拼音表里第一个登记该字的拼音胜出。</summary>
    private static readonly Dictionary<char, string> PinyinMap = LoadPinyinMap();

    private readonly List<Searchable> _items;

    public AppSearchIndex(IEnumerable<AppEntry> apps)
    {
        _items = [.. apps.Select(app => new Searchable(app))];
    }

    /// <summary>
    /// 按关键词搜索。<paramref name="query"/> 为空（或全空白）时返回全部应用、评分 0、无高亮。
    /// 结果按「评分降序 → 名称升序」排列。
    /// </summary>
    public List<AppMatch> Search(string? query)
    {
        string keyword = (query ?? "").Trim().ToLowerInvariant();
        if (keyword.Length == 0)
        {
            return [.. _items.Select(item => new AppMatch(item.App, 0, []))];
        }

        string[] tokens = keyword.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var results = new List<AppMatch>();
        foreach (Searchable item in _items)
        {
            AppMatch? match = Match(item, tokens);
            if (match is not null)
            {
                results.Add(match);
            }
        }

        results.Sort((a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            return byScore != 0
                ? byScore
                : string.CompareOrdinal(a.App.Name.ToLowerInvariant(), b.App.Name.ToLowerInvariant());
        });
        return results;
    }

    /// <summary>
    /// 每个 token 都必须命中名称或包名；返回评分与名称高亮区间。任一 token 落空即整体不匹配。
    /// </summary>
    private static AppMatch? Match(Searchable item, IReadOnlyList<string> tokens)
    {
        IReadOnlyList<Unit> units = item.Units;
        double score = 0;
        var ranges = new List<(int Start, int End)>();

        foreach (string token in tokens)
        {
            double? best = null;

            (int Start, int End)? hit = AlignUnits(units, token);
            if (hit is not null)
            {
                (int start, int end) = hit.Value;
                double s = (start == 0 ? 100 : 60)
                           + (end - start)
                           + Math.Max(0, 20 - start);
                if (units[start].Ch == token[0])
                {
                    s += 8;
                }
                best = s;
                ranges.Add((units[start].Index, units[end - 1].Index + 1));
            }

            int index = item.App.Package.ToLowerInvariant().IndexOf(token, StringComparison.Ordinal);
            if (index >= 0)
            {
                double s = 15;
                if (index == 0)
                {
                    s += 15;
                }
                else if (item.App.Package[index - 1] is '.' or '_')
                {
                    s += 10;
                }
                if (best is null || s > best)
                {
                    best = s;
                }
            }

            if (best is null)
            {
                return null;
            }
            score += best.Value;
        }

        return new AppMatch(item.App, score, MergeRanges(ranges));
    }

    /// <summary>
    /// 在单元序列上连续对齐一个 token（原字符 &gt; 首字母 &gt; 全拼），返回命中的单元区间。
    /// </summary>
    private static (int Start, int End)? AlignUnits(IReadOnlyList<Unit> units, string token)
    {
        int n = units.Count;
        int m = token.Length;
        if (m == 0)
        {
            return null;
        }

        var memo = new Dictionary<int, int>();

        int Can(int i, int j)
        {
            if (j == m)
            {
                return i;
            }
            if (i == n)
            {
                return -1;
            }
            int key = (i * (m + 1)) + j;
            if (memo.TryGetValue(key, out int cached))
            {
                return cached;
            }

            Unit u = units[i];
            int res = -1;
            if (u.Ch == token[j])
            {
                res = Can(i + 1, j + 1);                                // 原字符直接匹配
            }
            if (res < 0 && u.Py.Length > 1 && u.Ini == token[j])
            {
                res = Can(i + 1, j + 1);                                // 拼音首字母
            }
            if (res < 0 && token.AsSpan(j).StartsWith(u.Py))
            {
                res = Can(i + 1, j + u.Py.Length);                      // 全拼
            }
            memo[key] = res;
            return res;
        }

        for (int s = 0; s < n; s++)
        {
            int e = Can(s, 0);
            if (e > s)
            {
                return (s, e);
            }
        }
        return null;
    }

    /// <summary>合并重叠 / 相接的字符区间，供高亮用。</summary>
    private static List<(int Start, int End)> MergeRanges(List<(int Start, int End)> ranges)
    {
        if (ranges.Count <= 1)
        {
            return ranges;
        }

        ranges.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        var merged = new List<(int Start, int End)> { ranges[0] };
        for (int i = 1; i < ranges.Count; i++)
        {
            (int start, int end) = ranges[i];
            (int lastStart, int lastEnd) = merged[^1];
            if (start <= lastEnd)
            {
                merged[^1] = (lastStart, Math.Max(lastEnd, end));
            }
            else
            {
                merged.Add((start, end));
            }
        }
        return merged;
    }

    /// <summary>把一个应用名拆成可匹配单元。</summary>
    private static List<Unit> BuildUnits(string text)
    {
        var units = new List<Unit>();
        for (int i = 0; i < text.Length; i++)
        {
            char raw = text[i];
            if (PinyinMap.TryGetValue(raw, out string? py))
            {
                units.Add(new Unit(raw, py, py[0], i));
                continue;
            }

            char lower = char.ToLowerInvariant(raw);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                units.Add(new Unit(lower, lower.ToString(), lower, i));
            }
        }
        return units;
    }

    private static Dictionary<char, string> LoadPinyinMap()
    {
        var map = new Dictionary<char, string>();
        Assembly assembly = typeof(AppSearchIndex).Assembly;

        string? resource = Array.Find(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith("PinyinData.txt", StringComparison.Ordinal));
        if (resource is null)
        {
            return map;
        }

        using Stream? stream = assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return map;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            int tab = line.IndexOf('\t');
            if (tab <= 0)
            {
                continue;
            }
            string pinyin = line[..tab];
            foreach (char ch in line[(tab + 1)..])
            {
                map.TryAdd(ch, pinyin);         // 第一个登记该字的拼音胜出，与旧版一致
            }
        }
        return map;
    }

    /// <summary>预处理过的应用：名称单元只算一次。</summary>
    private sealed class Searchable
    {
        public Searchable(AppEntry app)
        {
            App = app;
            Units = BuildUnits(app.Name);
        }

        public AppEntry App { get; }

        public IReadOnlyList<Unit> Units { get; }
    }

    /// <summary>名称里的一个可匹配单元。<paramref name="Ch"/> 是原字符（英数已转小写），
    /// <paramref name="Py"/> 是全拼，<paramref name="Index"/> 是在原名里的字符下标。</summary>
    private readonly record struct Unit(char Ch, string Py, char Ini, int Index);
}
