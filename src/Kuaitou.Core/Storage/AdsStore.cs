using System.Text;

namespace Kuaitou.Core.Storage;

/// <summary>
/// 宿主文件自身的 NTFS 备用数据流（ADS）存储层，对应 legacy/kuaitou/storage.py。
///
/// 便携目标：应用产生的一切文件（配置 / 应用缓存 / 投屏日志 / 抓取的图标 / 错误日志）
/// 都写进宿主文件自己的数据流里，磁盘上不再散落任何小文件，拷走一个 exe 就带走全部状态。
/// 宿主所在卷不是 NTFS（FAT32 / 网络盘 / 无写权限）时自动退回宿主同目录的普通文件，
/// 功能不受影响；当前实际用的是哪种，<see cref="LocationReport"/> 会如实说明。
///
/// 实测注意：ADS 路径不支持替换式写入（旧版 os.replace 会报 WinError 87），
/// 因此一律直接覆盖写。
/// </summary>
public sealed class AdsStore
{
    /// <summary>
    /// 单个日志流上限。日志藏在数据流里，用户看不见也没法手动清理，长期用会一直涨；
    /// 排查只需要最近一次记录，所以超限就整个丢弃重来。
    /// </summary>
    public const int MaxLogBytes = 1024 * 1024;

    // 流名沿用旧版，保证老用户 exe 数据流里已有的配置 / 图标能被直接读出来
    public const string ConfigStream = "config.json";
    public const string AppsCacheStream = "apps_cache.json";
    public const string LaunchLogStream = "scrcpy_launch.log";
    public const string ScanLogStream = "apps_scan.log";
    public const string ErrorLogStream = "快投_错误日志.txt";
    public const string IconIndexStream = "icon_index.json";
    public const string IconStreamPrefix = "icon_";                    // 图标流：icon_<包名>.png
    public const string IconSyncedStream = "icons_synced.json";
    public const string UnlockPinsStream = "unlock_pins.json";

    private const string ProbeStream = ".ads_probe";

    private static readonly Lazy<AdsStore> LazyDefault = new(() =>
    {
        AppPaths paths = AppPaths.Resolve();
        return new AdsStore(paths.HostPath, paths.DataDir);
    });

    private readonly string _hostPath;
    private readonly string _dataDir;
    private bool? _adsUsable;

    public AdsStore(string hostPath, string dataDir)
    {
        _hostPath = Path.GetFullPath(hostPath);
        _dataDir = Path.GetFullPath(dataDir);
    }

    /// <summary>按当前运行环境解析出来的默认存储（打包态挂 exe，源码态挂仓库根）。</summary>
    public static AdsStore Default => LazyDefault.Value;

    public string HostPath => _hostPath;

    public string DataDir => _dataDir;

    /// <summary>某个流对应的 ADS 路径。</summary>
    public string AdsPath(string stream) => _hostPath + ":" + stream;

    /// <summary>宿主所在卷是否支持 ADS。首次访问做一次读写试错并缓存结果。</summary>
    public bool IsAdsUsable
    {
        get
        {
            _adsUsable ??= Probe();
            return _adsUsable.Value;
        }
    }

    /// <summary>
    /// 宿主文件所在卷是否支持 ADS。不用 fsutil（需要管理员），直接一次读写试错。
    /// </summary>
    private bool Probe()
    {
        string probe = AdsPath(ProbeStream);
        try
        {
            using (var fs = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                fs.WriteByte((byte)'1');
            }
            TryDeleteFile(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>写入字节。返回 (实际路径, 是否写进了 ADS)；彻底失败时路径为空串。</summary>
    public (string Path, bool Ads) Write(string stream, byte[] data, bool append = false)
    {
        foreach ((string path, bool ads) in WriteTargets(stream))
        {
            try
            {
                EnsureParentDirectory(path);
                using var fs = new FileStream(path, append ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.Read);
                fs.Write(data, 0, data.Length);
                return (path, ads);
            }
            catch (Exception)
            {
                // ADS 写失败就继续试回退文件；回退也失败时返回空路径，由调用方如实报错。
            }
        }
        return (string.Empty, false);
    }

    /// <summary>写文本（UTF-8 无 BOM，与旧版 open(..., encoding="utf-8") 一致）。</summary>
    public (string Path, bool Ads) WriteText(string stream, string text, bool append = false)
        => Write(stream, Utf8NoBom.GetBytes(text), append);

    /// <summary>读字节。先试 ADS 再试回退文件（兼容旧版本遗留的普通文件）；都没有返回 null。</summary>
    public byte[]? ReadBytes(string stream)
    {
        foreach (string path in ReadCandidates(stream))
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (Exception)
            {
                // 换下一个候选
            }
        }
        return null;
    }

    /// <summary>读文本。非法字节按替换字符处理（UTF8 解码器默认行为，对应旧版 errors="replace"）。</summary>
    public string? ReadText(string stream)
    {
        byte[]? bytes = ReadBytes(stream);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>删掉一个流（ADS 与回退文件都试一遍）。文件本来就不存在时当作已删。</summary>
    public void Delete(string stream)
    {
        foreach (string path in ReadCandidates(stream))
        {
            TryDeleteFile(path);
        }
    }

    /// <summary>
    /// 以追加方式打开一个长期持有的句柄（供子进程 stdout 重定向、日志追加）。
    /// 打开前做日志轮转。全部候选都打不开时返回 null。
    ///
    /// 注：.NET 的 FileStream 一律是字节流，旧版那个 binary 开关在 C# 里没有对应概念，
    /// 文本写入由调用方自行编码。
    /// </summary>
    public FileStream? OpenAppend(string stream)
    {
        foreach (var target in WriteTargets(stream))
        {
            try
            {
                RotateLog(target.Path);
                EnsureParentDirectory(target.Path);
                return new FileStream(target.Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            }
            catch (Exception)
            {
                // 换下一个候选
            }
        }
        return null;
    }

    /// <summary>读日志尾部若干字符，用于把最近一次失败原因回显到界面。</summary>
    public string ReadLogTail(string stream, int limit = 1200)
    {
        string text = ReadText(stream) ?? string.Empty;
        return text.Length <= limit ? text : text[^limit..];
    }

    /// <summary>
    /// 往错误日志追一条。用于「出了事但用户在界面上看不到」的场景（配置写失败、配置读坏了等）：
    /// 这类问题不影响程序继续跑，正因为如此才更需要留痕，否则用户只能看到「设置莫名其妙没生效」。
    /// </summary>
    public void LogError(string text)
    {
        try
        {
            using FileStream? fs = OpenAppend(ErrorLogStream);
            if (fs is null)
            {
                return;
            }
            byte[] line = Utf8NoBom.GetBytes($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\n");
            fs.Write(line, 0, line.Length);
        }
        catch (Exception)
        {
            // 日志写不出来也不能让业务崩
        }
    }

    /// <summary>诊断用：当前实际存储位置一览。诊断报告里原样贴出来。</summary>
    public string LocationReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"宿主文件: {_hostPath}");
        sb.AppendLine($"数据流可用: {(IsAdsUsable ? "是" : "否")}");
        sb.AppendLine($"实际存储方式: {(IsAdsUsable
            ? "宿主文件数据流（NTFS ADS）"
            : "宿主同目录普通文件（所在卷不支持 ADS 或不可写）")}");

        string[] streams =
        [
            ConfigStream, AppsCacheStream, LaunchLogStream,
            IconIndexStream, IconSyncedStream, ScanLogStream, ErrorLogStream,
        ];
        foreach (string stream in streams)
        {
            long adsBytes = FileLength(AdsPath(stream));
            if (adsBytes >= 0)
            {
                sb.AppendLine($"  [ADS] {stream,-22} {adsBytes} 字节");
                continue;
            }
            string fallback = Path.Combine(_dataDir, stream);
            long fileBytes = FileLength(fallback);
            if (fileBytes >= 0)
            {
                sb.AppendLine($"  [文件] {stream,-21} {fileBytes} 字节  {fallback}");
            }
            else
            {
                sb.AppendLine($"  [无]   {stream}");
            }
        }
        return sb.ToString();
    }

    private static UTF8Encoding Utf8NoBom { get; } = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>写入候选，按优先级：ADS 可用时 ADS 优先，其次回退文件。</summary>
    private IEnumerable<(string Path, bool Ads)> WriteTargets(string stream)
    {
        if (IsAdsUsable)
        {
            yield return (AdsPath(stream), true);
        }
        yield return (Path.Combine(_dataDir, stream), false);
    }

    /// <summary>读取候选：ADS 可用时先 ADS，再回退文件。</summary>
    private IEnumerable<string> ReadCandidates(string stream)
    {
        if (IsAdsUsable)
        {
            yield return AdsPath(stream);
        }
        yield return Path.Combine(_dataDir, stream);
    }

    private static void EnsureParentDirectory(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static void RotateLog(string path)
    {
        if (FileLength(path) > MaxLogBytes)
        {
            TryDeleteFile(path);
        }
    }

    /// <summary>
    /// 取文件（或数据流）长度，不存在 / 打不开返回 -1。
    /// 这里用「打开流再读 Length」而不是 FileInfo：FileInfo 对 ADS 路径不可靠，
    /// 而 FileStream 对 ADS 是确定可用的。图标层判断「这个流里到底有没有图」也用它。
    /// </summary>
    public static long FileLength(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return fs.Length;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // 不存在或无权限都当已处理
        }
    }
}
