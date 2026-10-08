using System.Text.RegularExpressions;

namespace Kuaitou.Core.Apps;

/// <summary>
/// 图标素材库的多策略查找表。对应 legacy/kuaitou/apps.py 的 <c>_IconIndex</c>。
///
/// 素材库（icons/）的文件名就是包名，但换手机后包名常与库键有细微差异（大小写、分隔符，
/// 或多了 / 少了末段，如库键 <c>cn.amazon.mShop.android</c> 对设备的
/// <c>cn.amazon.mShop.android.shopping</c>），所以用多级容错匹配代替精确命中。
///
/// 匹配跑在每一次图标请求上，而库有两千多个键，逐键扫描太亏，所以启动时就把
/// 「包名 → 图标路径」拆成几张查找表：精确 / 归一化 / 前后缀 / 末两段重合都退化成
/// 几次字典查找，只有模糊兜底才需要遍历，且带廉价预筛。
/// </summary>
public sealed class IconIndex
{
    /// <summary>包名末尾的「版本变体」段：剥掉后再试一轮。</summary>
    private static readonly HashSet<string> VariantSegments = new(StringComparer.Ordinal)
    {
        "pro", "lite", "plus", "premium", "hd", "pad", "free", "paid",
        "global", "international", "intl", "overseas", "oversea", "beta",
        "demo", "app", "apk",
    };

    private readonly Dictionary<string, string> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _norm = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _ahead = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _behind = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _tail2 = new(StringComparer.Ordinal);
    private List<string>? _normKeys;

    /// <summary>库里已登记的键数（诊断 / 单测用）。</summary>
    public int Count => _exact.Count;

    /// <summary>去掉非字母数字并转小写：只差分隔符 / 大小写的换皮包名靠它对上。</summary>
    public static string NormalizeKey(string? value)
        => Regex.Replace((value ?? "").ToLowerInvariant(), "[^a-z0-9]", "");

    /// <summary>
    /// 登记一个库键。同一键重复登记时保留先到的（手机传回 / 在线抓的图标优先于内置素材库）。
    /// </summary>
    public void Add(string? key, string? path)
    {
        string normalized = (key ?? "").Trim().ToLowerInvariant();
        if (normalized.Length == 0 || string.IsNullOrEmpty(path))
        {
            return;
        }

        if (!_exact.ContainsKey(normalized))
        {
            _exact[normalized] = path;
        }

        string compact = NormalizeKey(normalized);
        if (compact.Length > 0 && !_norm.ContainsKey(compact))
        {
            _norm[compact] = path;
        }

        string[] segments = normalized.Split('.');
        for (int i = 1; i < segments.Length; i++)
        {
            // 各级前缀 / 后缀 → 库键：同一片段留最短的库键（最贴近用户实际看到的包名）
            KeepShortest(_ahead, string.Join('.', segments[..i]), normalized);
            KeepShortest(_behind, string.Join('.', segments[i..]), normalized);
        }

        if (segments.Length >= 2)
        {
            string tail = segments[^2] + "." + segments[^1];
            if (!_tail2.TryGetValue(tail, out List<string>? bucket))
            {
                bucket = [];
                _tail2[tail] = bucket;
            }
            bucket.Add(normalized);
        }
        _normKeys = null;
    }

    /// <summary>
    /// 多级匹配：常规链 → 剥掉变体后缀再来一轮 → 模糊兜底。全程只查表，不做全表扫描
    /// （只有最后那步模糊比对会遍历归一化键表）。
    /// </summary>
    public string? Find(string? package)
    {
        string key = (package ?? "").Trim().ToLowerInvariant();
        if (key.Length == 0)
        {
            return null;
        }

        string? hit = Core(key);
        if (hit is not null)
        {
            return hit;
        }

        string[] segments = key.Split('.');
        while (segments.Length > 2 && VariantSegments.Contains(segments[^1]))
        {
            segments = segments[..^1];       // com.foo.bar.pro → com.foo.bar
            hit = Core(string.Join('.', segments));
            if (hit is not null)
            {
                return hit;
            }
        }
        return Fuzzy(NormalizeKey(key));
    }

    /// <summary>精确 → 归一化 → 前后缀段（互为前缀 / 后缀）→ 末两段重合。</summary>
    private string? Core(string key)
    {
        if (Lookup(_exact, key, out string? hit))
        {
            return hit;
        }

        string compact = NormalizeKey(key);
        if (compact.Length > 0 && Lookup(_norm, compact, out string? normalized))
        {
            return normalized;
        }

        string[] segments = key.Split('.');
        if (segments.Length < 2)
        {
            return null;
        }

        for (int i = segments.Length - 1; i > 0; i--)     // 库键是设备包名的前缀：取最长（最具体）
        {
            if (Lookup(_exact, string.Join('.', segments[..i]), out string? prefix))
            {
                return prefix;
            }
        }
        for (int i = 1; i < segments.Length; i++)         // 库键是设备包名的后缀：同样取最长
        {
            if (Lookup(_exact, string.Join('.', segments[i..]), out string? suffix))
            {
                return suffix;
            }
        }
        if (Lookup(_ahead, key, out string? longer) && Lookup(_exact, longer, out string? longerPath))
        {
            return longerPath;                            // 库键比设备包名多一段
        }
        if (Lookup(_behind, key, out string? prefixed) && Lookup(_exact, prefixed, out string? prefixedPath))
        {
            return prefixedPath;                          // 库键比设备包名多了前缀段
        }

        if (_tail2.TryGetValue(segments[^2] + "." + segments[^1], out List<string>? candidates))
        {
            string? best = null;
            int bestCommon = 0;
            foreach (string candidate in candidates)       // 末两段重合，再比公共后缀长度定优劣
            {
                string[] other = candidate.Split('.');
                int common = 0;
                while (common < Math.Min(segments.Length, other.Length)
                       && segments[segments.Length - 1 - common] == other[other.Length - 1 - common])
                {
                    common++;
                }
                if (common > bestCommon && _exact.TryGetValue(candidate, out string? path))
                {
                    best = path;
                    bestCommon = common;
                }
            }
            return best;
        }
        return null;
    }

    /// <summary>
    /// 末级兜底：归一化后高度相似（换皮包名只差个别字母，com.foo.bar 对 com.foo.bars）。
    /// 库太小时（<c>&lt;2</c> 个键）直接跳过，没有比对的意义。
    /// </summary>
    private string? Fuzzy(string compact, double cutoff = 0.9)
    {
        if (compact.Length == 0 || _norm.Count < 2)
        {
            return null;
        }
        _normKeys ??= [.. _norm.Keys.OrderBy(k => k, StringComparer.Ordinal)];
        string? match = SequenceSimilarity.GetCloseMatch(compact, _normKeys, cutoff);
        return match is not null && _norm.TryGetValue(match, out string? path) ? path : null;
    }

    private static bool Lookup(Dictionary<string, string> table, string? key, out string? path)
    {
        path = null;
        return !string.IsNullOrEmpty(key) && table.TryGetValue(key, out path) && path.Length > 0;
    }

    private static void KeepShortest(Dictionary<string, string> table, string fragment, string key)
    {
        if (!table.TryGetValue(fragment, out string? current) || key.Length < current.Length)
        {
            table[fragment] = key;
        }
    }
}
