using Kuaitou.Core.Adb;
using Kuaitou.Core.Storage;

namespace Kuaitou.Core.Scrcpy;

/// <summary>
/// 掉线自动重连。对应 legacy/kuaitou/device.py 的 _reconnect_loop / _try_reconnect / _skip_reconnect。
///
/// 只重连「曾经连上、后来掉线」的无线地址（手机熄屏、路由器休眠都会掉）。每台地址独立退避：
/// 6s → 12s → 24s → 60s（之后固定 60s），连续 10 次失败就放弃这一台，等它重新出现在设备列表
/// 里再从头开始，避免无限刷 adb；用户主动「断开」过的地址进黑名单，不自动重连。
/// </summary>
public sealed class ReconnectService : IDisposable
{
    private static readonly int[] Backoff = [6, 12, 24, 60];
    private const int MaxTries = 10;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(6);

    private readonly AdbRunner _adb;
    private readonly ConfigStore _config;
    private readonly object _lock = new();
    private readonly Dictionary<string, (int Tries, double Next)> _state = [];

    /// <summary>用户主动「断开」过的地址：不自动重连，直到重新手动连上。</summary>
    private readonly HashSet<string> _skip = [];

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ReconnectService(AdbRunner adb, ConfigStore config)
    {
        _adb = adb;
        _config = config;
    }

    /// <summary>把某地址加入黑名单：用户主动断开后不该被自动重连拉回来。</summary>
    public void Skip(string addr)
    {
        lock (_lock)
        {
            _skip.Add(addr);
        }
    }

    /// <summary>重新手动连上某地址后，把它从黑名单里放出来。</summary>
    public void Unskip(string addr)
    {
        lock (_lock)
        {
            _skip.Remove(addr);
        }
    }

    /// <summary>启动后台重连守候。</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // 退出时不等守候线程也无所谓
        }
        _cts?.Dispose();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        var lastSeen = new HashSet<string>();
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                if (!_config.Load().ReconnectEnabled)
                {
                    lock (_lock)
                    {
                        _state.Clear();
                    }
                    lastSeen.Clear();
                    continue;
                }

                HashSet<string> online = [.. DeviceInventory.GetDevices(_adb)];
                double now = Environment.TickCount64 / 1000.0;

                List<string> gone;
                lock (_lock)
                {
                    gone = [.. lastSeen
                        .Where(a => !online.Contains(a) && a.Contains(':', StringComparison.Ordinal) && !_skip.Contains(a))];

                    foreach (string a in online)
                    {
                        _state.Remove(a);        // 已连上：清零重连计数
                    }

                    foreach (string a in gone)
                    {
                        (int tries, double next) st = _state.TryGetValue(a, out var existing) ? existing : (0, 0.0);
                        if (st.tries >= MaxTries || now < st.next)
                        {
                            continue;
                        }
                        st.tries++;
                        st.next = now + Backoff[Math.Min(st.tries - 1, Backoff.Length - 1)];
                        _state[a] = st;
                        string addr = a;
                        _ = Task.Run(() => _adb.Run(["connect", addr], timeoutSeconds: 15), token);
                    }
                }

                lastSeen = online;
            }
            catch (Exception)
            {
                // 单轮出错不影响守候，继续下一轮
            }
        }
    }
}
