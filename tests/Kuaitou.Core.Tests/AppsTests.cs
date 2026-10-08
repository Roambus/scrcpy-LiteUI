using Kuaitou.Core.Apps;
using Kuaitou.Core.Storage;
using SkiaSharp;

namespace Kuaitou.Core.Tests;

/// <summary>
/// P4 应用列表与图标层的纯逻辑测试：<c>--list-apps</c> 输出解析、拼音 / 首字母搜索、
/// 图标多策略匹配、名称别名、difflib 相似度、图片入库管线（PNG 化 + 裁方 + 单色拒绝）、
/// 图标仓库的索引优先级与落盘。
/// </summary>
public sealed class AppsTests
{
    // ---------- 应用列表解析 ----------

    [Fact]
    public void Parse_TakesNameAndPackage_SortsByName()
    {
        const string output = "List of apps:\n"
            + " - 微信    com.tencent.mm\n"
            + " - Chrome    com.android.chrome\n"
            + " * 淘宝    com.taobao.taobao\n";

        List<AppEntry> apps = AppsOutputParser.Parse(output);

        // 按名称小写排序，即码点序：chrome < 微 < 淘
        Assert.Equal(["Chrome", "微信", "淘宝"], apps.Select(a => a.Name));
        Assert.Equal("com.android.chrome", apps[0].Package);
    }

    [Fact]
    public void Parse_RejectsBadPackage_AndDedupes()
    {
        const string output = " - 好应用    com.foo.bar\n"
            + " - 坏应用    not-a-package\n"
            + " - 重复    com.foo.bar\n"
            + " - 没包名\n";

        List<AppEntry> apps = AppsOutputParser.Parse(output);

        Assert.Single(apps);
        Assert.Equal("com.foo.bar", apps[0].Package);
    }

    [Theory]
    [InlineData("com.foo.bar", true)]
    [InlineData("com.foo", true)]
    [InlineData("nopackage", false)]
    [InlineData("com..bar", false)]
    [InlineData("1com.foo", false)]
    [InlineData("", false)]
    public void IsPackageName_ValidatesFormat(string package, bool expected)
        => Assert.Equal(expected, AppsOutputParser.IsPackageName(package));

    // ---------- 图标索引 ----------

    [Fact]
    public void IconIndex_ExactAndNormalizedMatch()
    {
        var index = new IconIndex();
        index.Add("com.tencent.mm", "p_mm");

        Assert.Equal("p_mm", index.Find("com.tencent.mm"));
        Assert.Equal("p_mm", index.Find("COM.Tencent.MM"));
        Assert.Equal("p_mm", index.Find("com.tencent.m-m"));   // 只差分隔符
    }

    [Fact]
    public void IconIndex_MatchesWhenLibraryKeyIsPrefixOrSuffix()
    {
        var index = new IconIndex();
        index.Add("cn.amazon.mshop.android", "p_amz");

        // 设备包名比库键多一段
        Assert.Equal("p_amz", index.Find("cn.amazon.mshop.android.shopping"));
        // 设备包名比库键多前缀段
        Assert.Equal("p_amz", index.Find("com.foo.cn.amazon.mshop.android"));
    }

    [Fact]
    public void IconIndex_StripsVariantSuffix()
    {
        var index = new IconIndex();
        index.Add("com.foo.bar", "p_bar");

        Assert.Equal("p_bar", index.Find("com.foo.bar.pro"));
        Assert.Equal("p_bar", index.Find("com.foo.bar.lite"));
    }

    [Fact]
    public void IconIndex_FallsBackToFuzzyMatch()
    {
        var index = new IconIndex();
        index.Add("com.foo.bar", "p_bar");
        index.Add("com.other.baz", "p_baz");

        // 归一化后只差一个字母
        Assert.Equal("p_bar", index.Find("com.foo.bars"));
        Assert.Null(index.Find("completely.different.package"));
    }

    [Fact]
    public void IconIndex_KeepsFirstRegistration()
    {
        var index = new IconIndex();
        index.Add("com.foo.bar", "first");
        index.Add("com.foo.bar", "second");

        Assert.Equal("first", index.Find("com.foo.bar"));
    }

    // ---------- 名称别名 ----------

    [Theory]
    [InlineData("微信", "com.tencent.mm")]
    [InlineData("WPS Office", "cn.wps.moffice_eng")]
    [InlineData("哔哩哔哩HD", "tv.danmaku.bili")]
    [InlineData("抖音极速版", "com.ss.android.ugc.aweme.lite")]
    [InlineData("chrome", "com.android.chrome")]
    public void IconAliases_FindsKnownNames(string name, string expected)
        => Assert.Equal(expected, IconAliases.Find(name));

    [Fact]
    public void IconAliases_ReturnsNullForUnknown() => Assert.Null(IconAliases.Find("绝不存在这个应用名"));

    // ---------- difflib 相似度 ----------

    [Fact]
    public void SequenceSimilarity_Basics()
    {
        Assert.Equal(1.0, SequenceSimilarity.Ratio("abc", "abc"));
        Assert.Equal(1.0, SequenceSimilarity.Ratio("", ""));
        Assert.Equal(0.0, SequenceSimilarity.Ratio("abc", "xyz"));
        Assert.True(SequenceSimilarity.Ratio("comfoobars", "comfoobar") > 0.9);
    }

    [Fact]
    public void GetCloseMatch_RespectsCutoff()
    {
        Assert.Equal("comfoobar",
            SequenceSimilarity.GetCloseMatch("comfoobars", ["comfoobar"], 0.9));
        Assert.Null(SequenceSimilarity.GetCloseMatch("abc", ["xyz"], 0.9));
    }

    // ---------- 图片入库管线 ----------

    [Fact]
    public void IconImage_RejectsTooSmallSource()
        => Assert.Null(IconImage.ToPng(NoisePng(64)));

    [Fact]
    public void IconImage_RejectsPlainColor()
        => Assert.Null(IconImage.ToPng(SolidPng(256, new SKColor(128, 128, 128))));

    [Fact]
    public void IconImage_ProducesSquare256Png()
    {
        byte[]? png = IconImage.ToPng(NoisePng(300, height: 200));

        Assert.NotNull(png);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png![..4]);   // PNG 签名
        using SKBitmap decoded = SKBitmap.Decode(png);
        Assert.Equal(IconImage.Size, decoded.Width);
        Assert.Equal(IconImage.Size, decoded.Height);
    }

    // ---------- 拼音 / 首字母搜索 ----------

    [Fact]
    public void Search_EmptyQueryReturnsAll()
    {
        AppSearchIndex index = BuildSearchIndex();

        List<AppMatch> results = index.Search("");

        Assert.Equal(4, results.Count);
        Assert.All(results, m => Assert.Equal(0, m.Score));
        Assert.All(results, m => Assert.Empty(m.Ranges));
    }

    [Theory]
    [InlineData("tb", "淘宝")]          // 首字母
    [InlineData("taobao", "淘宝")]      // 全拼
    [InlineData("wx", "微信")]          // 首字母
    [InlineData("weixin", "微信")]      // 全拼
    [InlineData("bili", "哔哩哔哩")]     // 多字全拼
    [InlineData("淘宝", "淘宝")]        // 直接打汉字
    [InlineData("chrome", "Chrome")]    // 英文名
    public void Search_MatchesByNameOrPinyin(string query, string expectedTop)
        => Assert.Equal(expectedTop, BuildSearchIndex().Search(query)[0].App.Name);

    [Fact]
    public void Search_MatchesPackageNameToo()
    {
        List<AppMatch> results = BuildSearchIndex().Search("com.tencent");

        AppMatch match = Assert.Single(results);
        Assert.Equal("微信", match.App.Name);
    }

    [Fact]
    public void Search_ReturnsHighlightRanges()
    {
        AppMatch match = BuildSearchIndex().Search("tb")[0];

        (int start, int end) = Assert.Single(match.Ranges);
        Assert.Equal(0, start);
        Assert.Equal(2, end);
    }

    [Fact]
    public void Search_NoMatchReturnsEmpty()
        => Assert.Empty(BuildSearchIndex().Search("zzzzz"));

    // ---------- 图标仓库 ----------

    [Fact]
    public void IconStore_PrefersWritableDirOverPresetLibrary()
    {
        WithTempIcons((store, icons, writable, preset) =>
        {
            File.WriteAllBytes(Path.Combine(preset, "com.foo.bar.png"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(writable, "com.foo.bar.png"), [4, 5, 6]);

            icons.RebuildIndex();

            Assert.Equal(Path.Combine(writable, "com.foo.bar.png"), icons.FindCached("com.foo.bar"));
            Assert.Null(icons.FindCached("com.not.there"));
        });
    }

    [Fact]
    public void IconStore_FindCachedOrAlias_ResolvesByAppName()
    {
        WithTempIcons((store, icons, writable, preset) =>
        {
            File.WriteAllBytes(Path.Combine(preset, "com.tencent.mm.png"), [1, 2, 3]);

            icons.RebuildIndex();

            Assert.Equal(Path.Combine(preset, "com.tencent.mm.png"),
                icons.FindCachedOrAlias("微信", "com.tencent.mm.wx"));
        });
    }

    [Fact]
    public void IconStore_WriteIcon_LandsAndIndexes()
    {
        WithTempIcons((store, icons, writable, preset) =>
        {
            string path = icons.WriteIcon("com.demo.app", [9, 8, 7]);

            Assert.NotEqual("", path);
            Assert.True(AdsStore.FileLength(path) > 0);

            icons.AddToIndex("com.demo.app", path);
            Assert.Equal(path, icons.FindCached("com.demo.app"));
        });
    }

    [Fact]
    public void IconStore_BumpRev_Increments()
    {
        WithTempIcons((store, icons, writable, preset) =>
        {
            int before = icons.Rev;
            Assert.Equal(before + 1, icons.BumpRev());
            Assert.Equal(before + 1, icons.Rev);
        });
    }

    // ---------- 辅助 ----------

    private static AppSearchIndex BuildSearchIndex() => new(
    [
        new AppEntry { Name = "微信", Package = "com.tencent.mm" },
        new AppEntry { Name = "淘宝", Package = "com.taobao.taobao" },
        new AppEntry { Name = "哔哩哔哩", Package = "tv.danmaku.bili" },
        new AppEntry { Name = "Chrome", Package = "com.android.chrome" },
    ]);

    private static byte[] SolidPng(int size, SKColor color)
        => EncodePng(size, size, (_, _) => color);

    private static byte[] NoisePng(int width, int? height = null)
        => EncodePng(width, height ?? width, (x, y) => new SKColor(
            (byte)(x * 255 / Math.Max(1, width - 1)),
            (byte)(y * 255 / Math.Max(1, (height ?? width) - 1)),
            (byte)((x * 7 + y * 13) % 256)));

    private static byte[] EncodePng(int width, int height, Func<int, int, SKColor> paint)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, paint(x, y));
            }
        }
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>在临时目录里搭一套图标环境：宿主文件（数据流用）+ 可写目录 + 内置素材库。</summary>
    private static void WithTempIcons(Action<AdsStore, IconStore, string, string> body)
    {
        string root = Path.Combine(Path.GetTempPath(), "kuaitou_icons_test_" + Guid.NewGuid().ToString("N"));
        string writable = Path.Combine(root, "writable");
        string preset = Path.Combine(root, "preset");
        Directory.CreateDirectory(writable);
        Directory.CreateDirectory(preset);

        try
        {
            string host = Path.Combine(root, "host.bin");
            File.WriteAllBytes(host, [0]);
            var store = new AdsStore(host, Path.Combine(root, "data"));
            var icons = new IconStore(store, [writable, preset], writable);
            body(store, icons, writable, preset);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception)
            {
                // 临时目录删不掉不影响断言
            }
        }
    }
}
