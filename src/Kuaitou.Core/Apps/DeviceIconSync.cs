using System.IO.Compression;
using System.Text.Json;
using Kuaitou.Core.Adb;
using Kuaitou.Core.Storage;

namespace Kuaitou.Core.Apps;

/// <summary>一次手机侧取图的结果。</summary>
public sealed record IconSyncResult(bool Ok, string Message, int Imported = 0, int Skipped = 0);

/// <summary>
/// 从手机取图标。对应 legacy/kuaitou/apps.py 的 <c>_dump_icons_on_device</c> /
/// <c>_pull_and_ingest</c> / <c>sync_icons_from_device</c> / <c>auto_sync_icons</c> /
/// <c>resync_icons</c> / <c>forget_icon_sync</c> 与「这台设备取过了」的持久记录。
///
/// 手机上跑的是我们自己编的 android/icondump.dex：用 app_process 直接跑，不装 App、
/// 不要权限、不留任何东西。它遍历手机上所有能启动的应用，把图标渲染成 PNG 打成一个 zip，
/// 我们再拉回来入库（统一转 256×256 PNG）。
///
/// 电脑这边全程自动：设备第一次连上就导一次，之后一直用存下来的那份（想重取就在首页点「刷新」）。
/// 收进来的图标在查找顺序上排在预置素材库前面：手机传回的是这台设备上真实在用的那张图。
/// </summary>
public sealed class DeviceIconSync
{
    private const string RemoteDex = "/data/local/tmp/kuaitou_icondump.dex";
    private const string RemoteZip = "/data/local/tmp/kuaitou_icons.zip";
    private const string DumpClass = "com.kuaitou.icondump.IconDump";

    private const int PushTimeoutSeconds = 60;
    private const int DumpTimeoutSeconds = 180;     // 导一百多个图标三四秒就完了，无线慢些，余量给足
    private const int PullTimeoutSeconds = 300;     // 拉压缩包走 USB 一两秒，无线慢些，耐心给足
    private const int CleanupTimeoutSeconds = 15;

    /// <summary>
    /// 「取图格式」版本：dex 换了渲染方式（比如给图标补圆角遮罩）就来升一版，老记录自然对不上号，
    /// 设备下次连上会自动重取一遍——否则旧图标会一直留在数据流里，用户不点「刷新」就看不到新效果。
    /// </summary>
    private const string SyncFormat = "v2";

    private readonly AdbRunner _adb;
    private readonly AppPaths _paths;
    private readonly AdsStore _store;
    private readonly DeviceInventory _inventory;
    private readonly IconStore _icons;

    private readonly object _syncedLock = new();
    private HashSet<string>? _syncedCache;

    private readonly object _runLock = new();
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);

    public DeviceIconSync(
        AdbRunner adb,
        AppPaths paths,
        AdsStore store,
        DeviceInventory inventory,
        IconStore icons)
    {
        _adb = adb;
        _paths = paths;
        _store = store;
        _inventory = inventory;
        _icons = icons;
    }

    /// <summary>
    /// 完整走一遍：推 dex → 手机上导一遍 → 拉回来入库。
    /// 任何一步失败都如实返回原因。
    /// </summary>
    public IconSyncResult SyncFromDevice(string? serial = null)
    {
        serial ??= FirstDevice();
        if (string.IsNullOrEmpty(serial))
        {
            return new IconSyncResult(false, "没有已连接的设备");
        }

        (bool ok, string why) = DumpIconsOnDevice(serial);
        if (!ok)
        {
            return new IconSyncResult(false, why);
        }

        IconSyncResult result = PullAndIngest(serial);
        CleanupRemote(serial);
        if (result.Ok)
        {
            MarkSynced(serial);      // 记进数据流：下次连上就直接用这份，不再重取
        }
        return result;
    }

    /// <summary>
    /// 设备连上之后自动取一次图标（每台设备只取一次，之后一直用存下来的那份）。
    /// 整个过程在后台线程里做，界面不等它；失败也不弹提示——图标没取到就继续用预置素材库，
    /// 原因只写进扫描日志。
    /// </summary>
    public void AutoSync(string? serial)
    {
        if (string.IsNullOrEmpty(serial) || IsSyncedBefore(serial))
        {
            return;     // 这台之前取过，图标已经躺在数据流里了
        }
        lock (_runLock)
        {
            if (!_running.Add(serial))
            {
                return;
            }
        }

        StartBackground(() =>
        {
            IconSyncResult r = RunCatching(() => SyncFromDevice(serial));
            if (!r.Ok)
            {
                LogScanFailure($"icon sync {serial}: {r.Message}");
                lock (_runLock)
                {
                    _running.Remove(serial);    // 这次没成，下次连上再试一遍
                }
            }
        });
    }

    /// <summary>
    /// 用户手动要求重取图标（首页「刷新」）：抹掉记录再走一遍，仍在后台跑。
    /// 取回新图标会 bump 图标库版本，界面据此自动换图。
    /// </summary>
    public void Resync(string? serial = null)
    {
        serial ??= FirstDevice();
        if (string.IsNullOrEmpty(serial))
        {
            return;
        }
        Forget(serial);
        UnmarkSynced(serial);

        StartBackground(() =>
        {
            IconSyncResult r = RunCatching(() => SyncFromDevice(serial));
            if (!r.Ok)
            {
                LogScanFailure($"icon resync {serial}: {r.Message}");
            }
        });
    }

    /// <summary>
    /// 设备断开就忘掉进程内的「取过了」：下次连上重新核对一遍持久记录。
    /// 持久记录本身不动——图标已经在数据流里，不必因为一次掉线就重取。
    /// </summary>
    public void Forget(string serial)
    {
        lock (_runLock)
        {
            _running.Remove(serial);
        }
    }

    /// <summary>这台设备之前取过图标没有（持久记录，程序重启也认）。</summary>
    public bool IsSyncedBefore(string? serial)
    {
        string key = SyncKey(serial);
        return key.Length > 0 && SyncedKeys().Contains(key);
    }

    private string? FirstDevice()
    {
        List<string> devices = _inventory.GetDevices();
        return devices.Count > 0 ? devices[0] : null;
    }

    private IconSyncResult RunCatching(Func<IconSyncResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception e)
        {
            return new IconSyncResult(false, $"取图标出错：{e}");
        }
    }

    private static void StartBackground(Action action)
    {
        var thread = new Thread(() => action()) { IsBackground = true, Name = "icon-sync" };
        thread.Start();
    }

    /// <summary>
    /// 推 dex 上去跑一遍，让手机把图标导成 zip。
    /// app_process 是同步跑完才返回的，所以不用靠标记文件轮询进度；非零退出就是真失败。
    /// </summary>
    private (bool Ok, string Why) DumpIconsOnDevice(string serial)
    {
        if (!File.Exists(_paths.IconDexFile))
        {
            return (false, $"取图程序不在：{_paths.IconDexFile} 找不到");
        }

        AdbResult push = _adb.Run(["push", _paths.IconDexFile, RemoteDex], PushTimeoutSeconds, serial);
        if (!push.Ok)
        {
            string text = push.Combined.Trim();
            return (false, $"推送取图程序失败：{(text.Length > 0 ? text : "adb push 出错")}");
        }

        _adb.Run(["shell", "rm", "-f", RemoteZip], CleanupTimeoutSeconds, serial);
        AdbResult dump = _adb.Run(
            ["shell", "CLASSPATH=" + RemoteDex, "app_process", "/system/bin", DumpClass, RemoteZip],
            DumpTimeoutSeconds,
            serial);
        string output = dump.Combined.Trim();
        if (!dump.Ok)
        {
            return (false, $"手机导出图标失败：{(output.Length > 0 ? output : $"app_process 退出码 {dump.ExitCode}")}");
        }
        return (true, output);
    }

    /// <summary>
    /// 把手机上的图标包拉回来入库。包里的条目就是「包名.png」，包名直接就是索引键。
    /// 入库后重建索引让它们排到预置素材库前面。
    /// </summary>
    private IconSyncResult PullAndIngest(string serial)
    {
        string tempDir = Directory.CreateTempSubdirectory("kuaitou_icons_").FullName;
        string zipPath = Path.Combine(tempDir, "icons.zip");
        int imported = 0;
        int skipped = 0;
        try
        {
            AdbResult pull = _adb.Run(["pull", RemoteZip, zipPath], PullTimeoutSeconds, serial);
            if (!pull.Ok)
            {
                string text = pull.Combined.Trim();
                return new IconSyncResult(false, $"拉取图标失败：{(text.Length > 0 ? text : "adb pull 出错")}");
            }

            ZipArchive archive;
            try
            {
                archive = ZipFile.OpenRead(zipPath);
            }
            catch (Exception e)
            {
                return new IconSyncResult(false, $"图标包读不出来：{e.Message}");
            }

            using (archive)
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string package = Path.GetFileNameWithoutExtension(entry.Name);
                    if (!AppsOutputParser.IsPackageName(package))
                    {
                        skipped++;
                        continue;
                    }

                    byte[] raw;
                    try
                    {
                        using Stream input = entry.Open();
                        using var buffer = new MemoryStream();
                        input.CopyTo(buffer);
                        raw = buffer.ToArray();
                    }
                    catch (Exception)
                    {
                        skipped++;
                        continue;
                    }

                    byte[]? png = IconImage.ToPng(raw);
                    if (png is null || _icons.WriteIcon(package, png).Length == 0)
                    {
                        skipped++;
                        continue;
                    }
                    imported++;
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception)
            {
                // 临时目录删不掉不影响结果
            }
        }

        if (imported == 0)
        {
            return new IconSyncResult(false, $"手机传回来 {skipped} 个图标，但没有一个能用", 0, skipped);
        }

        _icons.RebuildIndex();
        _icons.BumpRev();
        string message = $"已从手机取回 {imported} 个图标，应用列表优先用它们";
        if (skipped > 0)
        {
            message += $"；{skipped} 个跳过（图片不合格）";
        }
        return new IconSyncResult(true, message, imported, skipped);
    }

    private void CleanupRemote(string serial)
        => _adb.Run(["shell", "rm", "-f", RemoteDex, RemoteZip], CleanupTimeoutSeconds, serial);

    // ---------- 「这台设备取过图标了」的持久记录 ----------

    /// <summary>
    /// 读「已经取过图标的设备」表。进程内缓存一份：界面每 3 秒轮询一次状态，每次都去读数据流太亏。
    /// 旧格式（改渲染方式之前）的键直接丢掉，不再占着位置。
    /// </summary>
    private HashSet<string> SyncedKeys()
    {
        lock (_syncedLock)
        {
            _syncedCache ??= LoadSyncedKeys();
            return new HashSet<string>(_syncedCache, StringComparer.Ordinal);
        }
    }

    private HashSet<string> LoadSyncedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        string? raw = _store.ReadText(AdsStore.IconSyncedStream);
        if (string.IsNullOrEmpty(raw))
        {
            return keys;
        }
        try
        {
            List<string>? list = JsonSerializer.Deserialize<List<string>>(raw);
            if (list is not null)
            {
                foreach (string key in list)
                {
                    if (!string.IsNullOrEmpty(key) && key.StartsWith(SyncFormat + ":", StringComparison.Ordinal))
                    {
                        keys.Add(key);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 坏了就当空表，下次连上重取
        }
        return keys;
    }

    private void SaveSyncedKeys(HashSet<string> keys)
    {
        lock (_syncedLock)
        {
            _syncedCache = new HashSet<string>(keys, StringComparer.Ordinal);
        }
        _store.WriteText(AdsStore.IconSyncedStream, JsonSerializer.Serialize(keys.OrderBy(k => k, StringComparer.Ordinal)));
    }

    /// <summary>设备身份键：硬件序列号（取不到就退回 adb 序列号），前面带上取图格式版本。</summary>
    private string SyncKey(string? serial)
    {
        string key;
        try
        {
            key = _inventory.GetDeviceKey(serial);
        }
        catch (Exception)
        {
            key = serial ?? "";
        }
        return key.Length > 0 ? $"{SyncFormat}:{key}" : "";
    }

    private void MarkSynced(string serial)
    {
        string key = SyncKey(serial);
        if (key.Length == 0)
        {
            return;
        }
        lock (_syncedLock)
        {
            HashSet<string> keys = _syncedCache ??= LoadSyncedKeys();
            if (!keys.Add(key))
            {
                return;
            }
            SaveSyncedKeys(keys);
        }
    }

    private void UnmarkSynced(string serial)
    {
        string key = SyncKey(serial);
        if (key.Length == 0)
        {
            return;
        }
        lock (_syncedLock)
        {
            HashSet<string> keys = _syncedCache ??= LoadSyncedKeys();
            if (!keys.Remove(key))
            {
                return;
            }
            SaveSyncedKeys(keys);
        }
    }

    private void LogScanFailure(string message)
        => _store.WriteText(AdsStore.ScanLogStream, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}");
}
