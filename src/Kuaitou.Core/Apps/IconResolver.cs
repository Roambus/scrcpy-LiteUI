namespace Kuaitou.Core.Apps;

/// <summary>
/// 图标获取调度。对应 legacy/kuaitou/apps.py 的 <c>request_icon</c> / <c>submit_icon</c> /
/// <c>prefetch_icons</c> / <c>fetch_online_icon</c> 与 4 个 worker 的在线队列。
///
/// 关键点：任何请求都不阻塞。命中素材库立即返回路径；否则把包名投进后台队列并立即返回 null，
/// 界面按延迟重试，抓到之后下一次请求自然命中。绝不在 HTTP 上等几十秒把界面卡死。
///
/// 抓不到的包名进 600 秒冷却，避免冷门应用每次刷新都去撞一遍墙。
/// </summary>
public sealed class IconResolver : IDisposable
{
    private const int WorkerCount = 4;
    private const int CooldownSeconds = 600;

    private readonly IconStore _store;
    private readonly OnlineIconSource _source;
    private readonly Func<string, string?> _appName;

    private readonly object _lock = new();
    private readonly Queue<(string Package, string? Name)> _pending = new();
    private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _cooldown = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _workers = [];

    private bool _disposed;

    /// <param name="store">本地图标仓库（缓存命中与落盘）。</param>
    /// <param name="source">在线图标源。</param>
    /// <param name="appName">按包名反查应用名，供 iTunes 源用（可为空）。</param>
    public IconResolver(IconStore store, OnlineIconSource source, Func<string, string?>? appName = null)
    {
        _store = store;
        _source = source;
        _appName = appName ?? (_ => null);

        for (int i = 0; i < WorkerCount; i++)
        {
            _workers.Add(Task.Run(WorkerLoopAsync));
        }
    }

    /// <summary>
    /// 只读素材库：命中返回路径；否则后台补抓并立即返回 null。
    /// </summary>
    public string? RequestIcon(string? package, string? name = null)
    {
        string? cached = _store.FindCached(package);
        cached ??= _store.FindCachedAlias(name);   // 换皮 / 改名包：按应用名兜底命中素材库
        if (cached is not null)
        {
            return cached;
        }
        SubmitIcon(package, name);
        return null;
    }

    /// <summary>把包名投进在线队列。已缓存 / 冷却中 / 已在队列里则跳过。返回是否入队。</summary>
    public bool SubmitIcon(string? package, string? name = null)
    {
        package = (package ?? "").Trim();
        if (package.Length == 0 || _disposed || _store.FindCached(package) is not null)
        {
            return false;
        }

        lock (_lock)
        {
            if (Now() < _cooldown.GetValueOrDefault(package, 0) || !_queued.Add(package))
            {
                return false;
            }
            _pending.Enqueue((package, name));
        }
        _signal.Release();
        return true;
    }

    /// <summary>批量预取：素材库直接命中；缺失的进在线队列，秒级返回。</summary>
    public void Prefetch(IEnumerable<AppEntry> items)
    {
        foreach (AppEntry item in items)
        {
            SubmitIcon(item.Package, item.Name);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cts.Cancel();
        _signal.Release(WorkerCount);       // 唤醒所有 worker 让它们看到取消
        try
        {
            Task.WaitAll([.. _workers], TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // 退出时不等也无所谓
        }
        _signal.Dispose();
        _cts.Dispose();
    }

    private async Task WorkerLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            (string Package, string? Name) item;
            lock (_lock)
            {
                if (_pending.Count == 0)
                {
                    continue;
                }
                item = _pending.Dequeue();
            }

            string? path = null;
            try
            {
                path = FetchOnlineIcon(item.Package, item.Name ?? _appName(item.Package));
            }
            catch (Exception)
            {
                // 单个包抓失败不影响 worker 继续
            }

            lock (_lock)
            {
                _queued.Remove(item.Package);
                if (path is not null)
                {
                    _cooldown.Remove(item.Package);
                }
                else
                {
                    _cooldown[item.Package] = Now() + CooldownSeconds;
                }
            }
        }
    }

    /// <summary>
    /// 按 应用宝 → 小米 → iTunes 顺序在线获取图标，统一成 PNG 永久缓存。
    /// 返回缓存路径或 null。对应旧版 <c>fetch_online_icon</c>。
    /// </summary>
    private string? FetchOnlineIcon(string package, string? name)
    {
        string? cached = _store.FindCachedOrAlias(name, package);
        if (cached is not null)
        {
            return cached;
        }

        byte[]? png = _source.TryFetch(package, name);
        if (png is null)
        {
            return null;
        }

        string path = _store.WriteIcon(package, png);
        if (path.Length == 0)
        {
            return null;
        }
        _store.AddToIndex(package, path);
        return path;
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
}
