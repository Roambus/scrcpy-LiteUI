using System.Text.Json;
using System.Text.Json.Serialization;
using Kuaitou.Core.Storage;

namespace Kuaitou.Core.Apps;

/// <summary>
/// 图标仓库：索引构建 / 多策略查找 / 落盘 / 版本号。对应 legacy/kuaitou/apps.py 的
/// <c>_icon_index</c>、<c>_icon_index_build</c>、<c>_icon_index_add</c>、<c>find_cached_icon</c>、
/// <c>_write_icon</c>、<c>_icon_ads_keys/register</c> 与 <c>icon_rev/_bump_icon_rev</c>。
///
/// 索引优先级由登记顺序决定，先到先得：手机传回 / 在线抓的图标 &gt; 可写目录 &gt; 内置素材库。
/// 「先到的」在 <see cref="IconIndex.Add"/> 里用「已有键不覆盖」实现，所以重建时必须新建一个
/// 空索引再按顺序灌，拿旧表重建会让新图标顶不掉预置素材。
///
/// 落盘格式：数据流名 <c>icon_&lt;包名&gt;.png</c>（旧版是 .webp，读取时两种都认）。
/// </summary>
public sealed class IconStore
{
    private readonly AdsStore _store;
    private readonly IReadOnlyList<string> _searchDirs;
    private readonly string _writableDir;

    private readonly object _indexLock = new();
    private IconIndex _index = new();

    private readonly object _adsLock = new();
    private HashSet<string>? _adsKeys;

    private int _rev;

    /// <param name="store">数据流存储（图标索引流与运行期抓到的图标）。</param>
    /// <param name="searchDirs">图标查找目录，顺序即优先级（可写目录在前，内置素材库在后）。</param>
    /// <param name="writableDir">数据流不可用时的图标落盘目录。</param>
    public IconStore(AdsStore store, IReadOnlyList<string> searchDirs, string writableDir)
    {
        _store = store;
        _searchDirs = searchDirs;
        _writableDir = writableDir;
        RebuildIndex();
    }

    /// <summary>
    /// 图标库版本号：手机传回新图标或在线抓到时 +1，界面据此重新取图（缓存 URL 里带它）。
    /// </summary>
    public int Rev => Volatile.Read(ref _rev);

    /// <summary>当前索引里的键数（诊断 / 单测用）。</summary>
    public int Count
    {
        get
        {
            lock (_indexLock)
            {
                return _index.Count;
            }
        }
    }

    /// <summary>只读素材库命中就返回图标路径，否则 null。全程只查索引，不碰网络也不碰 adb。</summary>
    public string? FindCached(string? package)
    {
        lock (_indexLock)
        {
            return _index.Find(package);
        }
    }

    /// <summary>
    /// 先按包名查，再按应用名走别名表兜底（同名不同包名的换皮 / 改名应用）。
    /// 别名命中的包名还要再查一次索引：素材库缺这个图标时不能死在这里，得返回 null 去尝试在线抓取。
    /// </summary>
    public string? FindCachedOrAlias(string? name, string? package)
    {
        return FindCached(package) ?? FindCachedAlias(name);
    }

    /// <summary>只走应用名别名这条线。</summary>
    public string? FindCachedAlias(string? name)
    {
        string? alias = IconAliases.Find(name);
        return alias is null ? null : FindCached(alias);
    }

    /// <summary>
    /// 图标落盘：数据流可用就写进 <c>icon_&lt;包名&gt;.png</c> 并登记包名，否则退回
    /// <c>icons/&lt;包名&gt;.png</c>。返回实际路径；彻底失败返回空串。
    ///
    /// 刻意不走 <see cref="AdsStore.Write"/> 的回退路径：它退回的是数据目录根下的
    /// <c>icon_&lt;包名&gt;.png</c>，而索引只扫 <c>icons/</c>，写那儿等于白写。
    /// </summary>
    public string WriteIcon(string package, byte[] data)
    {
        package = (package ?? "").Trim();
        if (package.Length == 0 || data.Length == 0)
        {
            return "";
        }

        if (_store.IsAdsUsable)
        {
            string path = _store.AdsPath(AdsStore.IconStreamPrefix + package + ".png");
            try
            {
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    fs.Write(data, 0, data.Length);
                }
                RegisterAdsKey(package);
                return path;
            }
            catch (Exception)
            {
                // 数据流写失败就退回目录，和旧版一致
            }
        }

        try
        {
            Directory.CreateDirectory(_writableDir);
            string path = Path.Combine(_writableDir, package + ".png");
            File.WriteAllBytes(path, data);
            return path;
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>新抓到的图标登记进索引，后续请求即可立刻命中。</summary>
    public void AddToIndex(string? package, string? path)
    {
        if (string.IsNullOrEmpty(package) || string.IsNullOrEmpty(path))
        {
            return;
        }
        lock (_indexLock)
        {
            _index.Add(package, path);
        }
    }

    /// <summary>图标库有新内容后 +1。返回自增后的版本号。</summary>
    public int BumpRev() => Interlocked.Increment(ref _rev);

    /// <summary>
    /// 重建索引。每次都用一个全新的索引对象（理由见类注释）。登记顺序即优先级：
    /// 1) 数据流里的图标（运行期抓到的；流不在目录里，只能靠索引流找回）
    /// 2) 各查找目录里的图标（可写目录在前，内置只读素材库在后）
    /// </summary>
    public void RebuildIndex()
    {
        var index = new IconIndex();

        foreach (string package in AdsKeys())
        {
            // 旧版本留下的图标是 .webp，新版本写 .png，两种都探一遍
            foreach (string ext in (string[])[".png", ".webp"])
            {
                string path = _store.AdsPath(AdsStore.IconStreamPrefix + package + ext);
                if (AdsStore.FileLength(path) > 0)
                {
                    index.Add(package, path);
                    break;
                }
            }
        }

        foreach (string dir in _searchDirs)
        {
            IEnumerable<string> names;
            try
            {
                names = Directory.EnumerateFiles(dir);
            }
            catch (Exception)
            {
                continue;
            }
            foreach (string path in names)
            {
                if (!IsIconExtension(Path.GetExtension(path)))
                {
                    continue;
                }
                if (AdsStore.FileLength(path) <= 0)
                {
                    continue;
                }
                index.Add(Path.GetFileNameWithoutExtension(path), path);
            }
        }

        lock (_indexLock)
        {
            _index = index;
        }
    }

    /// <summary>已缓存到数据流的图标包名列表。进程内缓存一份，避免反复读流。</summary>
    private HashSet<string> AdsKeys()
    {
        lock (_adsLock)
        {
            _adsKeys ??= LoadAdsKeys();
            return new HashSet<string>(_adsKeys, StringComparer.Ordinal);
        }
    }

    /// <summary>把包名登记进索引流（幂等）。</summary>
    private void RegisterAdsKey(string package)
    {
        lock (_adsLock)
        {
            HashSet<string> keys = _adsKeys ??= LoadAdsKeys();
            if (!keys.Add(package))
            {
                return;
            }
            _store.WriteText(
                AdsStore.IconIndexStream,
                JsonSerializer.Serialize(new IconKeyFile { Packages = [.. keys] }));
        }
    }

    /// <summary>调用方已持有 <see cref="_adsLock"/>。</summary>
    private HashSet<string> LoadAdsKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        string? raw = _store.ReadText(AdsStore.IconIndexStream);
        if (string.IsNullOrEmpty(raw))
        {
            return keys;
        }
        try
        {
            IconKeyFile? file = JsonSerializer.Deserialize<IconKeyFile>(raw);
            if (file?.Packages is not null)
            {
                foreach (string key in file.Packages)
                {
                    if (!string.IsNullOrEmpty(key))
                    {
                        keys.Add(key);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 同上：坏数据当空表
        }
        return keys;
    }

    private static bool IsIconExtension(string ext)
    {
        foreach (string known in AppPaths.IconExtensions)
        {
            if (string.Equals(known, ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>icon_index.json 的落盘结构：<c>{"packages": ["com.foo", ...]}</c>。</summary>
    private sealed class IconKeyFile
    {
        [JsonPropertyName("packages")]
        public List<string> Packages { get; set; } = [];
    }
}
