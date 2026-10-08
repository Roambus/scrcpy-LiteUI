using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Kuaitou.Core.Adb;
using Kuaitou.Core.Storage;

namespace Kuaitou.Core.Apps;

/// <summary>
/// 应用列表：扫描 + 按设备缓存。对应 legacy/kuaitou/apps.py 的 <c>_load_apps_cache</c> /
/// <c>_save_apps_cache</c> / <c>_log_scan_failure</c> / <c>_scan_apps</c> / <c>list_apps</c>。
///
/// 首次扫描要推 scrcpy-server 并在设备端起 Java 进程逐个取应用名，无线 adb 下明显偏慢，
/// 所以扫描结果按 adb 序列号各缓存一份（写回 apps_cache.json 数据流），下次启动直接出列表。
///
/// 「扫描失败」与「扫描成功但设备上确实没有应用」必须区分：前者返回 null，绝不用空结果
/// 顶掉已有缓存——否则一次网络抖动就会让用户的列表变空。
/// </summary>
public sealed class AppCatalog
{
    /// <summary>扫描超时（秒）。首次扫描慢，给足余量避免扫到一半被中断成空结果。</summary>
    private const int ScanTimeoutSeconds = 120;

    private readonly AdbRunner _adb;
    private readonly AppPaths _paths;
    private readonly AdsStore _store;
    private readonly IconResolver? _icons;

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, List<AppEntry>> _cache = new(StringComparer.Ordinal);

    private readonly object _scanLock = new();

    public AppCatalog(AdbRunner adb, AppPaths paths, AdsStore store, IconResolver? icons = null)
    {
        _adb = adb;
        _paths = paths;
        _store = store;
        _icons = icons;
        LoadCache();
    }

    /// <summary>
    /// 获取指定设备的应用列表（按序列号各缓存一份）。并发请求共享同一次扫描（单飞），
    /// 避免重复拉起 scrcpy 把首次连接拖慢。
    /// </summary>
    /// <param name="serial">目标设备；空则取第一台在线设备。</param>
    /// <param name="force">true 时忽略内存缓存重扫（界面点「刷新」）。</param>
    public List<AppEntry> ListApps(string? serial = null, bool force = false)
    {
        serial ??= FirstDevice();
        if (string.IsNullOrEmpty(serial))
        {
            return [];
        }

        if (!force && TryGetCached(serial, out List<AppEntry>? cached))
        {
            return cached;
        }

        lock (_scanLock)
        {
            // 等锁期间其他请求可能已经扫完，直接复用
            if (!force && TryGetCached(serial, out cached))
            {
                return cached;
            }

            List<AppEntry>? scanned = ScanApps(serial);
            if (scanned is null)
            {
                return TryGetCached(serial, out cached) ? cached : [];   // 扫描失败退回已有缓存
            }

            Dictionary<string, DeviceApps> snapshot;
            lock (_cacheLock)
            {
                _cache[serial] = scanned;
                snapshot = _cache.ToDictionary(
                    pair => pair.Key,
                    pair => new DeviceApps { Apps = pair.Value });
            }
            SaveCache(snapshot);
            _icons?.Prefetch(scanned);      // 素材库直接命中；缺失的进在线队列，不阻塞本次返回
            return scanned;
        }
    }

    /// <summary>按包名反查应用名（在线图标源按名字找图时要用）。找不到返回 null。</summary>
    public string? GetAppName(string? package)
    {
        if (string.IsNullOrEmpty(package))
        {
            return null;
        }
        lock (_cacheLock)
        {
            foreach (List<AppEntry> apps in _cache.Values)
            {
                foreach (AppEntry app in apps)
                {
                    if (app.Package == package)
                    {
                        return app.Name;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>第一台在线设备（与旧版 <c>get_devices()[0]</c> 一致）。</summary>
    private string? FirstDevice()
    {
        List<string> devices = DeviceInventory.GetDevices(_adb);
        return devices.Count > 0 ? devices[0] : null;
    }

    private bool TryGetCached(string serial, out List<AppEntry> cached)
    {
        lock (_cacheLock)
        {
            cached = _cache.TryGetValue(serial, out List<AppEntry>? apps) ? apps : [];
        }
        return cached.Count > 0;
    }

    /// <summary>
    /// 真正执行一次扫描。返回 null 表示扫描失败（未连接 / 超时 / 报错），
    /// 与「扫描成功但设备上确实没有应用」（返回空列表）区分开，避免用失败结果覆盖缓存。
    /// </summary>
    private List<AppEntry>? ScanApps(string serial)
    {
        var cmd = new List<string> { _paths.ScrcpyPath };
        cmd.AddRange(_adb.SerialArgs(serial));
        cmd.Add("--list-apps");

        var psi = new ProcessStartInfo
        {
            FileName = cmd[0],
            WorkingDirectory = Path.GetDirectoryName(_paths.ScrcpyPath) ?? ".",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        for (int i = 1; i < cmd.Count; i++)
        {
            psi.ArgumentList.Add(cmd[i]);
        }

        string output;
        int exitCode;
        try
        {
            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("scrcpy 进程启动失败");
            Task<string> outTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(ScanTimeoutSeconds * 1000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // 可能刚好自己退了
                }
                LogScanFailure($"scan timeout after {ScanTimeoutSeconds}s | serial={serial}");
                return null;
            }

            output = outTask.GetAwaiter().GetResult() + errTask.GetAwaiter().GetResult();
            exitCode = process.ExitCode;
        }
        catch (Exception e)
        {
            LogScanFailure($"scan exception: {e}");
            return null;
        }

        List<AppEntry> apps = AppsOutputParser.Parse(output);
        if (apps.Count == 0)
        {
            LogScanFailure($"rc={exitCode} | no apps parsed | tail:\n{Tail(output, 2000)}");
            if (exitCode != 0)
            {
                return null;
            }
        }
        return apps;
    }

    /// <summary>把全部设备的缓存写回存储。传的是已拍好的快照，避免在锁外遍历活字典。</summary>
    private void SaveCache(Dictionary<string, DeviceApps> devices)
        => _store.WriteText(AdsStore.AppsCacheStream, JsonSerializer.Serialize(new AppsCache { Devices = devices }));

    /// <summary>
    /// 启动时读取上次扫描结果；同一台设备可直接出列表，不必每次启动都重扫。
    /// 旧版单设备格式直接忽略（重扫一次即可），不做兼容转换。
    /// </summary>
    private void LoadCache()
    {
        string? raw = _store.ReadText(AdsStore.AppsCacheStream);
        if (string.IsNullOrEmpty(raw))
        {
            return;
        }
        try
        {
            AppsCache? file = JsonSerializer.Deserialize<AppsCache>(raw);
            if (file?.Devices is null)
            {
                return;
            }
            foreach ((string serial, DeviceApps item) in file.Devices)
            {
                if (item.Apps.Count > 0)
                {
                    _cache[serial] = item.Apps;
                }
            }
        }
        catch (JsonException)
        {
            // 缓存坏了就当没有，重扫一次即可
        }
    }

    /// <summary>扫描异常时记一份小日志：窗口程序没有控制台，出问题只能靠日志排查。</summary>
    private void LogScanFailure(string message)
        => _store.WriteText(AdsStore.ScanLogStream, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}");

    private static string Tail(string text, int limit)
        => text.Length <= limit ? text : text[^limit..];
}
