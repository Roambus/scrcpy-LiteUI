using System.Text.RegularExpressions;

namespace Kuaitou.Core.Apps;

/// <summary>
/// 应用名 → 别名包名。对应 legacy/kuaitou/apps.py 的 <c>_ICON_NAME_ALIASES</c> /
/// <c>_norm_app_name</c> / <c>_name_variants</c> / <c>_alias_icon</c>。
///
/// 同名不同包名（厂商换皮 / 应用改名）时，按应用名兜底命中素材库。只收录素材库里确实有、
/// 且名称足够独特的常用应用；匹配不上就自然跳过，不会误配。
///
/// 这里只负责「名字 → 别名包名」，拿到包名后要不要用它的图标由 <see cref="IconStore"/>
/// 去查——素材库缺这个图标时不能死在这里，得让它返回 null 去尝试在线抓取。
/// </summary>
public static class IconAliases
{
    private static readonly Dictionary<string, string> NameToPackage = new(StringComparer.Ordinal)
    {
        ["微信"] = "com.tencent.mm",
        ["QQ"] = "com.tencent.mobileqq",
        ["学习强国"] = "cn.xuexi.android",
        ["支付宝"] = "com.eg.android.AlipayGphone",
        ["淘宝"] = "com.taobao.taobao",
        ["京东"] = "com.jingdong.app.mall",
        ["拼多多"] = "com.xunmeng.pinduoduo",
        ["美团"] = "com.sankuai.meituan",
        ["饿了么"] = "me.ele",
        ["大众点评"] = "com.dianping.v1",
        ["抖音"] = "com.ss.android.ugc.aweme",
        ["快手"] = "com.smile.gifmaker",
        ["哔哩哔哩"] = "tv.danmaku.bili",
        ["微博"] = "com.sina.weibo",
        ["小红书"] = "com.xingin.xhs",
        ["知乎"] = "com.zhihu.android",
        ["豆瓣"] = "com.douban.frodo",
        ["贴吧"] = "com.baidu.tieba",
        ["百度"] = "com.baidu.searchbox",
        ["高德地图"] = "com.autonavi.minimap",
        ["百度地图"] = "com.baidu.BaiduMap",
        ["腾讯视频"] = "com.tencent.qqlive",
        ["爱奇艺"] = "com.qiyi.video",
        ["优酷视频"] = "com.youku.phone",
        ["芒果TV"] = "com.hunantv.imgo.activity",
        ["网易云音乐"] = "com.netease.cloudmusic",
        ["QQ音乐"] = "com.tencent.qqmusic",
        ["酷狗音乐"] = "com.kugou.android",
        ["喜马拉雅"] = "com.ximalaya.ting.android",
        ["微信读书"] = "com.tencent.weread",
        ["掌阅"] = "com.zhangyue.read",
        ["番茄小说"] = "com.dragon.read",
        ["今日头条"] = "com.ss.android.article.news",
        ["钉钉"] = "com.alibaba.android.rimet",
        ["企业微信"] = "com.tencent.wework",
        ["飞书"] = "com.ss.android.lark",
        ["腾讯会议"] = "com.tencent.wemeet.app",
        ["WPS Office"] = "cn.wps.moffice_eng",
        ["百度网盘"] = "com.baidu.netdisk",
        ["迅雷"] = "com.xunlei.downloadprovider",
        ["剪映"] = "com.lemon.lv",
        ["美图秀秀"] = "com.mt.mtxx.mtxx",
        ["滴滴出行"] = "com.sdu.didi.psnger",
        ["12306"] = "com.MobileTicket",
        ["携程旅行"] = "ctrip.android.view",
        ["去哪儿旅行"] = "com.Qunar",
        ["铁路12306"] = "com.MobileTicket",
        ["唯品会"] = "com.achievo.vipshop",
        ["闲鱼"] = "com.taobao.idlefish",
        ["得物"] = "com.shizhuang.duapp",
        ["懂车帝"] = "com.ss.android.auto",
        ["汽车之家"] = "com.cubic.autohome",
        ["58同城"] = "com.wuba",
        ["BOSS直聘"] = "com.hpbr.bosszhipin",
        ["同花顺"] = "com.hexin.plat.android",
        ["东方财富"] = "com.eastmoney.android.berlin",
        ["雪球"] = "com.xueqiu.android",
        ["云闪付"] = "com.unionpay",
        ["中国移动"] = "com.greenpoint.android.mc10086.activity",
        ["中国联通"] = "com.sinovatech.unicom.ui",
        ["中国电信"] = "com.ct.client",
        ["招商银行"] = "cmb.pb",
        ["中国银行"] = "com.chinamworld.bocmbci",
        ["中国建设银行"] = "com.chinamworld.main",
        ["QQ邮箱"] = "com.tencent.androidqqmail",
        ["网易邮箱大师"] = "com.netease.mail",
        ["Keep"] = "com.gotokeep.keep",
        ["学习通"] = "com.chaoxing.mobile",
        ["作业帮"] = "com.baidu.homework",
        ["网易有道词典"] = "com.youdao.dict",
        ["墨墨背单词"] = "com.maimemo.android.momo",
        ["扫描全能王"] = "com.intsig.camscanner",
        ["夸克"] = "com.quark.browser",
        ["UC浏览器"] = "com.UCMobile",
        ["QQ浏览器"] = "com.tencent.mtt",
        ["火狐浏览器"] = "org.mozilla.firefox",
        ["Chrome"] = "com.android.chrome",
        ["酷安"] = "com.coolapk.market",
        ["TapTap"] = "com.taptap",
        ["4399游戏盒"] = "com.m4399.gamecenter",
        ["萤石云视频"] = "com.videogo",
        ["米家"] = "com.xiaomi.smarthome",
        ["智慧生活"] = "com.huawei.smarthome",
        ["vivo官网"] = "com.vivo.space",
        ["抖音极速版"] = "com.ss.android.ugc.aweme.lite",
        ["快手极速版"] = "com.kuaishou.nebula",
        ["今日头条极速版"] = "com.ss.android.article.lite",
        ["全民K歌"] = "com.tencent.karaoke",
        ["唱吧"] = "com.changba",
        ["虎牙直播"] = "com.duowan.kiwi",
        ["斗鱼直播"] = "air.tv.douyu.android",
        ["和平精英"] = "com.tencent.tmgp.pubgmhd",
        ["王者荣耀"] = "com.tencent.tmgp.sgame",
        ["原神"] = "com.miHoYo.Yuanshen",
        ["第五人格"] = "com.netease.dwrg",
        ["开心消消乐"] = "com.happyelements.AndroidAnimal",
        ["百度贴吧"] = "com.baidu.tieba",
        ["孔夫子旧书网"] = "com.kongfz.app",
        ["豆包"] = "com.larus.nova",
        ["通义"] = "com.aliyun.tongyi",
        ["文心一言"] = "com.baidu.newapp",
        // 海外应用：Android 上的应用名多是英文原名，按名字兜底即可命中（国内商店没有这类包）
        ["Telegram"] = "org.telegram.messenger",
        ["ChatGPT"] = "com.openai.chatgpt",
        ["Claude"] = "com.anthropic.claude",
        ["Discord"] = "com.discord",
        ["TikTok"] = "com.zhiliaoapp.musically",
        ["Slack"] = "com.Slack",
        ["Reddit"] = "com.reddit.frontpage",
        ["Figma"] = "com.figma.mirror",
        ["GitHub"] = "com.github.android",
        ["LinkedIn"] = "com.linkedin.android",
        ["Gmail"] = "com.google.android.gm",
        ["Google Drive"] = "com.google.android.apps.docs",
        ["Google Photos"] = "com.google.android.apps.photos",
        ["Google Maps"] = "com.google.android.apps.maps",
        ["Gboard"] = "com.google.android.inputmethod.latin",
        ["YouTube"] = "com.google.android.youtube",
        ["WhatsApp"] = "com.whatsapp",
        ["Instagram"] = "com.instagram.android",
        ["Facebook"] = "com.facebook.katana",
        ["Spotify"] = "com.spotify.music",
        ["Netflix"] = "com.netflix.mediaclient",
        ["Uber"] = "com.ubercab",
        ["eBay"] = "com.ebay.mobile",
        ["Amazon"] = "com.amazon.mShop.android.shopping",
        ["Shopee"] = "com.shopee.app",
        ["Lazada"] = "com.lazada.android",
        ["AliExpress"] = "com.alibaba.aliexpresshd",
        ["CapCut"] = "com.lemon.lvoverseas",
        ["Binance"] = "com.binance.dev",
        ["OKX"] = "com.okinc.okex.gp",
        ["Coinbase"] = "com.coinbase.android",
        ["MetaMask"] = "io.metamask",
        ["Trust Wallet"] = "com.wallet.crypto.trustapp",
        ["PayPal"] = "com.paypal.android.p2pmobile",
        ["Zoom"] = "us.zoom.videomeetings",
        ["Microsoft Teams"] = "com.microsoft.teams",
        ["Outlook"] = "com.microsoft.office.outlook",
        ["OneDrive"] = "com.microsoft.skydrive",
        ["Notion"] = "notion.id",
        ["Duolingo"] = "com.duolingo",
        ["Steam"] = "com.valvesoftware.android.steam.community",
        ["V2EX"] = "com.v2ex.v2ex",
        // 国内应用补充：名字独特且素材库大概率已有，兜底命中率比精确包名匹配高
        ["番茄免费小说"] = "com.dragon.read",
        ["腾讯地图"] = "com.tencent.map",
        ["夸克浏览器"] = "com.quark.browser",
        ["网易云"] = "com.netease.cloudmusic",
        ["哔哩哔哩动画"] = "tv.danmaku.bili",
        ["滴答清单"] = "cn.ticktick.task",
        ["薄荷健康"] = "com.boohee.one",
        ["扇贝单词"] = "com.shanbay.words",
        ["小猿搜题"] = "com.fenbi.android.solar",
        ["猿辅导"] = "com.fenbi.android.leo",
        ["美团外卖"] = "com.sankuai.meituan.takeoutnew",
        ["饿了么外卖"] = "me.ele",
        ["闲鱼二手"] = "com.taobao.idlefish",
        ["迅雷看看"] = "com.xunlei.downloadprovider",
        ["知乎日报"] = "com.zhihu.android.app.night",
        ["航班管家"] = "com.flightmanager.view",
        ["小米商城"] = "com.xiaomi.shop",
        ["华为商城"] = "com.vmall.client",
        ["有道翻译官"] = "com.youdao.translator",
        ["喜马拉雅极速版"] = "com.ximalaya.ting.lite",
    };

    private static readonly Regex Noise = new(@"[\s\-_·.，。,：:！!？?（）()]+", RegexOptions.Compiled);

    /// <summary>归一化后的名字 → 别名包名（带空格 / 标点 / 大小写差异的名字也能命中）。</summary>
    private static readonly Dictionary<string, string> NormToPackage = BuildNormalized();

    /// <summary>只有纯英文名才做模糊匹配：中文名短，差一个字往往就是另一个应用，误配代价太高。</summary>
    private static readonly string[] AsciiNames =
        [.. NormToPackage.Keys.Where(IsAscii).OrderBy(n => n, StringComparer.Ordinal)];

    /// <summary>
    /// 应用名末尾的「版本变体」词：别名表里只登记主名，剥掉变体再查一轮
    /// （「哔哩哔哩HD」「抖音极速版」这类名字在全名兜底时会漏，剥后缀后就能对上主名）。
    /// </summary>
    private static readonly string[] VariantSuffixes =
    [
        "极速版", "国际版", "海外版", "专业版", "企业版", "免费版", "精简版", "轻量版",
        "青春版", "标准版", "正式版", "测试版", "体验版", "极简版", "高级版", "纯净版",
        "hd", "pro", "lite", "plus", "premium", "free", "global", "tv",
    ];

    /// <summary>名字归一化：去掉空白与常见标点后转小写。</summary>
    public static string NormalizeAppName(string? name)
        => Noise.Replace((name ?? "").ToLowerInvariant(), "");

    /// <summary>归一化名字的候选序列：先原样，再逐级剥掉末尾的版本变体词。</summary>
    public static List<string> NameVariants(string normalized)
    {
        var variants = new List<string> { normalized };
        while (true)
        {
            bool stripped = false;
            foreach (string suffix in VariantSuffixes)
            {
                if (normalized.Length - suffix.Length >= 2
                    && normalized.EndsWith(suffix, StringComparison.Ordinal))
                {
                    normalized = normalized[..^suffix.Length];
                    variants.Add(normalized);
                    stripped = true;
                    break;
                }
            }
            if (!stripped)
            {
                return variants;
            }
        }
    }

    /// <summary>
    /// 按应用名找别名包名，四级兜底：
    /// 原名直查 → 归一化后查（吃掉空格 / 标点 / 大小写）→ 剥掉末尾版本变体再查
    /// （「哔哩哔哩HD」对「哔哩哔哩」）→ 英文名相似度 ≥0.85（只有纯英文名才做）。
    /// 找不到返回 null。
    /// </summary>
    public static string? Find(string? name)
    {
        string raw = (name ?? "").Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        if (NameToPackage.TryGetValue(raw, out string? alias))
        {
            return alias;
        }

        string normalized = NormalizeAppName(raw);
        if (normalized.Length == 0)
        {
            return null;
        }

        foreach (string candidate in NameVariants(normalized))
        {
            if (NormToPackage.TryGetValue(candidate, out alias))
            {
                return alias;
            }
            if (IsAscii(candidate) && candidate.Length >= 4)
            {
                string? match = SequenceSimilarity.GetCloseMatch(candidate, AsciiNames, 0.85);
                if (match is not null && NormToPackage.TryGetValue(match, out string? fuzzy))
                {
                    return fuzzy;
                }
            }
        }
        return null;
    }

    private static Dictionary<string, string> BuildNormalized()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, string package) in NameToPackage)
        {
            map.TryAdd(NormalizeAppName(name), package);
        }
        return map;
    }

    private static bool IsAscii(string value)
    {
        foreach (char c in value)
        {
            if (c > 127)
            {
                return false;
            }
        }
        return true;
    }
}
