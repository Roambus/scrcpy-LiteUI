namespace Kuaitou.Core.Apps;

/// <summary>
/// 字符串相似度。对应 Python 标准库 <c>difflib.SequenceMatcher.ratio</c> 与
/// <c>difflib.get_close_matches</c>——旧版图标匹配的末级兜底用的就是这两个。
///
/// 移植的是「最长匹配块递归」那套算法本身，去掉了 junk / autojunk 处理：
/// 只有长度超过 200 的序列才会触发 autojunk，而这里比的都是归一化后的包名与应用名
/// （几十个字符的 ASCII），两者的结果完全一致。
/// </summary>
public static class SequenceSimilarity
{
    /// <summary>相似度 0..1：<c>2 * 匹配字符数 / 总长度</c>。</summary>
    public static double Ratio(string a, string b)
    {
        int total = a.Length + b.Length;
        if (total == 0)
        {
            return 1.0;
        }
        return 2.0 * MatchingChars(a, b, 0, a.Length, 0, b.Length) / total;
    }

    /// <summary>
    /// <c>get_close_matches</c> 的 n=1 情形：返回相似度不低于 <paramref name="cutoff"/> 的最优候选，
    /// 都不达标返回 null。并列时取序更大的那个字符串（与 difflib 用 (ratio, x) 元组取最大一致）。
    /// </summary>
    public static string? GetCloseMatch(string word, IEnumerable<string> candidates, double cutoff)
    {
        string? best = null;
        double bestRatio = -1;

        foreach (string candidate in candidates)
        {
            double ratio = Ratio(word, candidate);
            if (ratio < cutoff)
            {
                continue;
            }
            if (ratio > bestRatio
                || (ratio == bestRatio && best is not null && string.CompareOrdinal(candidate, best) > 0))
            {
                best = candidate;
                bestRatio = ratio;
            }
        }
        return best;
    }

    /// <summary>递归累加各层「最长匹配块」的长度。</summary>
    private static int MatchingChars(string a, string b, int alo, int ahi, int blo, int bhi)
    {
        (int i, int j, int size) = LongestMatch(a, b, alo, ahi, blo, bhi);
        if (size == 0)
        {
            return 0;
        }
        return size
            + MatchingChars(a, b, alo, i, blo, j)
            + MatchingChars(a, b, i + size, ahi, j + size, bhi);
    }

    /// <summary>difflib.find_longest_match 的自适应版（b2j + j2len 递推）。</summary>
    private static (int Index, int Other, int Size) LongestMatch(
        string a, string b, int alo, int ahi, int blo, int bhi)
    {
        int bestI = alo, bestJ = blo, bestSize = 0;
        int width = bhi - blo;

        // b 中每个字符出现的下标表
        var positions = new Dictionary<char, List<int>>();
        for (int j = blo; j < bhi; j++)
        {
            if (!positions.TryGetValue(b[j], out List<int>? list))
            {
                list = [];
                positions[b[j]] = list;
            }
            list.Add(j);
        }

        var j2len = new int[width + 1];
        for (int i = alo; i < ahi; i++)
        {
            var next = new int[width + 1];
            if (positions.TryGetValue(a[i], out List<int>? js))
            {
                foreach (int j in js)
                {
                    int previous = j - 1 >= blo ? j2len[j - 1 - blo] : 0;
                    int k = previous + 1;
                    next[j - blo] = k;
                    if (k > bestSize)
                    {
                        bestI = i - k + 1;
                        bestJ = j - k + 1;
                        bestSize = k;
                    }
                }
            }
            j2len = next;
        }
        return (bestI, bestJ, bestSize);
    }
}
