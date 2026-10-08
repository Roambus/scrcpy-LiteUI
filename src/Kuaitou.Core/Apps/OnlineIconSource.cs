using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kuaitou.Core.Apps;

/// <summary>
/// 在线图标源：应用宝 → 小米商店 → iTunes。对应 legacy/kuaitou/apps.py 的
/// <c>_yyb_icon_url</c> / <c>_xiaomi_icon_url</c> / <c>_itunes_icon_url</c> / <c>_http_get</c>
/// 与每源冷却 <c>_online_ok/_online_fail</c>。
///
/// 只做「找一个能用的图标字节并转成 PNG」，不碰本地缓存也不落盘——落盘与索引登记由
/// <see cref="IconResolver"/> 负责。三个源任意一个成功即返回。
///
/// 每源独立冷却：某个源网络故障 / 被限流时短暂静默，避免每个包都去撞一次墙；
/// 「未上架」（HTTP 404/400）不算故障，不冷却。
/// </summary>
public sealed class OnlineIconSource
{
    private static readonly string[] ItunesCountries = ["cn", "us"];

    /// <summary>iTunes 按 UA 做地区管控：US 店用浏览器 UA 一律 403，只有 curl 这类非浏览器 UA 才放行。</summary>
    private const string CurlUa = "curl/8.4.0";

    private const string BrowserUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

    private const int CooldownSeconds = 300;

    /// <summary>小米商店偶发返回的占位图 hash，命中就当没有。</summary>
    private const string XiaomiPlaceholder = "02f4849db3f7e487599e257b336d57b159d425b04";

    /// <summary>
    /// 系统通用名在 App Store 必然错配（如「设置 / 信息 / 电话」），iTunes 源直接跳过。
    /// </summary>
    private static readonly HashSet<string> ItunesGenericNames = new(StringComparer.Ordinal)
    {
        "设置", "信息", "电话", "相机", "相册", "浏览器", "音乐", "视频", "日历", "天气",
        "计算器", "录音机", "文件管理", "主题", "钱包", "互传", "邮件", "电子邮件", "联系人",
        "时钟", "闹钟时钟", "指南针", "手机管家", "扫描", "翻译机", "开关控制", "一键锁屏",
        "意见反馈", "游戏中心", "应用商店", "vivo摄影", "vivo健康", "vivo官网", "原子笔记",
        "电话与联系人", "智能遥控", "智慧生活", "系统跟踪", "Android System Angle",
    };

    private static readonly Regex YybPattern = new(
        "\"pkg_name\":\"(?<pkg>[^\"]+)\",\"app_id\":\"\\d+\",\"name\":\"[^\"]*\",\"icon\":\"(?<icon>[^\"]+)\"",
        RegexOptions.Compiled);

    private static readonly Regex XiaomiPattern = new(
        @"thumbnail/PNG/l\d+/AppStore/(?<hash>[0-9a-f]{40})", RegexOptions.Compiled);

    private static readonly Regex ItunesArtSizePattern = new(
        @"/\d+x\d+bb\.(?:jpg|png)$", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly object _cooldownLock = new();
    private readonly Dictionary<string, double> _cooldown = new(StringComparer.Ordinal);

    public OnlineIconSource(HttpClient? http = null)
    {
        _http = http ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// 依次尝试三个源，返回 256×256 PNG 字节；全都拿不到返回 null。
    /// 同步阻塞，调用方是后台 worker 线程。
    /// </summary>
    public byte[]? TryFetch(string package, string? name)
    {
        if (string.IsNullOrEmpty(package))
        {
            return null;
        }

        string?[] urls = [YybIconUrl(package), XiaomiIconUrl(package), ItunesIconUrl(name)];
        foreach (string? url in urls)
        {
            if (string.IsNullOrEmpty(url))
            {
                continue;
            }
            HttpResult download = HttpGet(url, 15, referer: "https://sj.qq.com/", ua: null);
            if (!download.Ok || download.Data is null || download.Data.Length < 1500)
            {
                continue;
            }
            byte[]? png = IconImage.ToPng(download.Data);
            if (png is not null)
            {
                return png;
            }
        }
        return null;
    }

    /// <summary>应用宝详情页 SSR 内嵌记录，按包名精确取图标（Android 原生方形，256px）。</summary>
    private string? YybIconUrl(string package)
    {
        if (!OnlineOk("yyb"))
        {
            return null;
        }

        HttpResult r = HttpGet("https://sj.qq.com/appdetail/" + Uri.EscapeDataString(package), 12, null, null);
        if (!r.Ok)
        {
            if (r.Status is not (404 or 400))       // 404=未上架；403/5xx=限流或故障，冷却
            {
                OnlineFail("yyb");
            }
            return null;
        }

        string html = Encoding.UTF8.GetString(r.Data!);
        Match m = YybPattern.Match(html);
        // 包名对不上 = 页面无该包记录（非故障，不冷却）
        if (!m.Success || m.Groups["pkg"].Value != package)
        {
            return null;
        }

        string url = m.Groups["icon"].Value;
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }
        else if (url.StartsWith("http://", StringComparison.Ordinal))
        {
            url = "https://" + url[7..];
        }
        return url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
               || url.Contains("yyb-icon", StringComparison.Ordinal)
            ? null
            : url;
    }

    /// <summary>小米应用商店详情页，取第一个 PNG 缩略图 hash 拼直链（包名必须出现在页面中防重定向占位）。</summary>
    private string? XiaomiIconUrl(string package)
    {
        if (!OnlineOk("mi"))
        {
            return null;
        }

        HttpResult r = HttpGet("https://app.mi.com/details?id=" + Uri.EscapeDataString(package), 10, null, null);
        if (!r.Ok)
        {
            if (r.Status is not (404 or 400))
            {
                OnlineFail("mi");
            }
            return null;
        }

        string html = Encoding.UTF8.GetString(r.Data!);
        if (!html.Contains(package, StringComparison.Ordinal))
        {
            return null;
        }

        Match m = XiaomiPattern.Match(html);
        if (!m.Success || m.Groups["hash"].Value == XiaomiPlaceholder)
        {
            return null;
        }
        return "https://file.market.xiaomi.com/thumbnail/PNG/l360/AppStore/" + m.Groups["hash"].Value;
    }

    /// <summary>
    /// iTunes Search API 按应用名取图（512 方形满版，无 iOS 圆角）；名称相似度校验防同名错配。
    ///
    /// 先查 CN 店（中文应用名命中率高），没有再查 US 店：海外应用根本不在 CN 店上架。
    /// 命中多条时只看第一条；limit 必须用 1：实测 limit&gt;1 会触发 403。
    /// </summary>
    private string? ItunesIconUrl(string? name)
    {
        if (string.IsNullOrEmpty(name) || ItunesGenericNames.Contains(name) || !OnlineOk("itunes"))
        {
            return null;
        }
        string normalized = IconAliases.NormalizeAppName(name);
        if (normalized.Length == 0)
        {
            return null;
        }

        foreach (string country in ItunesCountries)
        {
            string url = "https://itunes.apple.com/search?term=" + Uri.EscapeDataString(name)
                + "&country=" + country + "&entity=software&limit=1";
            HttpResult r = HttpGet(url, 10, null, country == "cn" ? null : CurlUa);
            if (!r.Ok)
            {
                if (r.Status is 404 or 400)
                {
                    continue;                       // 该店没有这条记录，换下一个地区
                }
                OnlineFail("itunes");
                return null;
            }

            JsonElement results;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(r.Data!);
                if (!doc.RootElement.TryGetProperty("results", out results)
                    || results.ValueKind != JsonValueKind.Array
                    || results.GetArrayLength() == 0)
                {
                    continue;
                }
            }
            catch (JsonException)
            {
                continue;
            }

            JsonElement item = results[0];
            string art = item.TryGetProperty("artworkUrl512", out JsonElement artElement)
                ? artElement.GetString() ?? ""
                : "";
            string trackName = item.TryGetProperty("trackName", out JsonElement nameElement)
                ? nameElement.GetString() ?? ""
                : "";
            string other = IconAliases.NormalizeAppName(trackName);
            if (art.Length == 0 || other.Length == 0)
            {
                continue;
            }
            if (normalized.Contains(other, StringComparison.Ordinal)
                || other.Contains(normalized, StringComparison.Ordinal)
                || SequenceSimilarity.Ratio(normalized, other) >= 0.72)
            {
                string sized = ItunesArtSizePattern.Replace(art, "/512x512bb.jpg");
                return sized.Length == 0 ? null : sized;
            }
        }
        return null;
    }

    private bool OnlineOk(string source)
    {
        lock (_cooldownLock)
        {
            return Now() > _cooldown.GetValueOrDefault(source, 0);
        }
    }

    private void OnlineFail(string source)
    {
        lock (_cooldownLock)
        {
            _cooldown[source] = Now() + CooldownSeconds;
        }
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>
    /// 带超时的 GET。HTTP 状态码与网络异常都不抛，统一放进 <see cref="HttpResult"/>，
    /// 由各源按状态码决定「未上架」还是「故障冷却」。
    /// </summary>
    private HttpResult HttpGet(string url, int timeoutSeconds, string? referer, string? ua)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", ua ?? BrowserUa);
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        if (!string.IsNullOrEmpty(referer))
        {
            request.Headers.TryAddWithoutValidation("Referer", referer);
        }

        try
        {
            using HttpResponseMessage response = _http
                .Send(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new HttpResult(false, (int)response.StatusCode, null);
            }
            byte[] data = response.Content.ReadAsByteArrayAsync(cts.Token).GetAwaiter().GetResult();
            return new HttpResult(true, (int)response.StatusCode, data);
        }
        catch (Exception)
        {
            return new HttpResult(false, 0, null);   // 0 = 网络层失败（超时 / DNS / 连接被拒）
        }
    }

    private readonly record struct HttpResult(bool Ok, int Status, byte[]? Data);
}
