using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Kuaitou.Core.Discover;

/// <summary>
/// TCP 端口探测。对应 legacy/kuaitou/discover.py 的 <c>_probe_port</c> / <c>_probe_5555</c>。
///
/// 旧版用阻塞 socket 的 <c>connect_ex</c> 配 256 个线程池线程；这里改用异步 connect + 信号量限流，
/// 在飞连接数一样是 256，但不占 256 个线程。并发值仍照搬旧版的实测结论：并发过高手机防火墙
/// 会成片丢 SYN，反而一个都扫不到。
/// </summary>
internal static class PortProbe
{
    /// <summary>单个端口的探测超时（秒）。局域网 RTT 极低，0.35s 足够。</summary>
    public const double DefaultTimeout = 0.35;

    /// <summary>并发上限。旧版注释：并发过高手机防火墙会丢弃 SYN，反而一个都扫不到。</summary>
    public const int DefaultWorkers = 256;

    /// <summary>探测单个 TCP 端口是否可连（等价 connect_ex(...) == 0）。</summary>
    public static async Task<bool> ProbeAsync(string ip, int port, double timeoutSeconds)
    {
        if (!IPAddress.TryParse(ip, out IPAddress? address))
        {
            return false;
        }

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cts.Token).ConfigureAwait(false);
            return socket.Connected;
        }
        catch (Exception)
        {
            // 拒绝 / 超时 / DNS 失败都算「不通」
            return false;
        }
    }

    /// <summary>并发探测一批 IP 的同一端口，返回开放的 IP 集合。</summary>
    public static async Task<HashSet<string>> ProbeManyAsync(
        IReadOnlyList<string> ips, int port, double timeoutSeconds, int workers)
    {
        var hits = new HashSet<string>(StringComparer.Ordinal);
        if (ips.Count == 0)
        {
            return hits;
        }

        using var gate = new SemaphoreSlim(Math.Max(1, workers));
        var tasks = new List<Task>(ips.Count);
        foreach (string ip in ips)
        {
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (await ProbeAsync(ip, port, timeoutSeconds).ConfigureAwait(false))
                    {
                        lock (hits)
                        {
                            hits.Add(ip);
                        }
                    }
                }
                finally
                {
                    gate.Release();
                }
            }));
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 单点异常不影响已收集到的结果
        }
        return hits;
    }

    /// <summary>同步包装：局域网扫描本身在后台线程上跑，这里阻塞它是安全的。</summary>
    public static HashSet<string> ProbeMany(IReadOnlyList<string> ips, int port, double timeoutSeconds, int workers)
        => ProbeManyAsync(ips, port, timeoutSeconds, workers).GetAwaiter().GetResult();

    /// <summary>取当前单调时间（秒），用于超时预算与耗时统计。</summary>
    public static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
