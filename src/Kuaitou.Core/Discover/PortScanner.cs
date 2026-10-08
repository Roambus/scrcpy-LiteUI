using System.Collections.Concurrent;

namespace Kuaitou.Core.Discover;

/// <summary>
/// 第三级设备发现：对存活主机扫随机端口段。对应 legacy/kuaitou/discover.py 的
/// <c>_scan_shard</c> / <c>_scan_ports</c> / <c>_deep_threads</c>。
///
/// Android 11+「无线调试」的随机端口落在 30000~50000；mDNS 被防火墙 / 路由器隔离挡住时，
/// 只有这一路能捞到设备。
/// </summary>
public static class PortScanner
{
    public const int DeepPortLo = 30000;

    /// <summary>上界（不含）。旧版注释：Android 11+ 无线调试随机端口区间（不含上界）。</summary>
    public const int DeepPortHi = 50000;

    /// <summary>局域网 RTT 极低，0.12s 足够；缩短能大幅提速。</summary>
    public const double DeepProbeTimeout = 0.12;

    /// <summary>
    /// 每条扫描「线」同时挂起的连接数。两个硬约束照搬旧版实测结论：
    /// 1) 在飞连接超过约 800 个时，手机 / AP 会开始成片丢掉 SYN —— 扫得「越快」反而一个
    ///    adb 端口都扫不到（3840 在飞时命中 0，640 在飞时稳定命中）；
    /// 2) 线程数上限压在 8：在飞连接 = 线程数 × 窗口。
    /// 8 × 80 = 640 在飞，是实测既不丢包又够快的点。
    /// </summary>
    public const int ScanWindow = 80;

    /// <summary>
    /// 一趟扫描约 5% 的开放端口会被随机丢包漏掉（实测 20 趟漏 1 趟）；同一段扫两趟
    /// 几乎不漏（实测 20 趟 0 漏）。两趟的代价是耗时翻倍，所以主机数上限相应收紧。
    /// </summary>
    public const int ScanPasses = 2;

    /// <summary>
    /// 扫描并行度：跟着 CPU 走，下限 4、上限 8。
    /// 每个片内部用异步连接一次挂几十个在飞，几条片就顶替「几百个阻塞线程」：既快，也不会把系统拖垮。
    /// </summary>
    public static int Threads() => Math.Max(4, Math.Min(8, Environment.ProcessorCount / 2));

    /// <summary>扫一台主机的一段端口，返回开放的端口号（升序）。<paramref name="stop"/> 返回 true 时提前收工。</summary>
    public static List<int> ScanHost(string ip, int portLo, int portHi, Func<bool> stop)
    {
        int shards = Threads();
        int total = Math.Max(0, portHi - portLo);
        var found = new HashSet<int>();

        for (int pass = 0; pass < ScanPasses; pass++)
        {
            if (pass > 0 && stop())
            {
                break;
            }

            var bag = new ConcurrentBag<int>();
            var tasks = new List<Task>(shards);
            for (int i = 0; i < shards; i++)
            {
                // 等价 Python 的 ports[i::n]：把整段端口分片给几条线
                var slice = new List<int>();
                for (int p = portLo + i; p < portHi; p += shards)
                {
                    slice.Add(p);
                }
                tasks.Add(ScanShardAsync(ip, slice, stop, bag));
            }

            try
            {
                Task.WhenAll(tasks).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // 单条线异常不影响已收集到的结果
            }
            found.UnionWith(bag);
        }

        return [.. found.Order()];
    }

    /// <summary>一条扫描线：最多同时挂 <see cref="ScanWindow"/> 个连接。</summary>
    private static async Task ScanShardAsync(
        string ip, IReadOnlyList<int> ports, Func<bool> stop, ConcurrentBag<int> found)
    {
        using var gate = new SemaphoreSlim(ScanWindow);
        var running = new List<Task>(ports.Count);
        foreach (int port in ports)
        {
            if (stop())
            {
                break;
            }

            await gate.WaitAsync().ConfigureAwait(false);
            int target = port;
            running.Add(Task.Run(async () =>
            {
                try
                {
                    if (await PortProbe.ProbeAsync(ip, target, DeepProbeTimeout).ConfigureAwait(false))
                    {
                        found.Add(target);
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
            await Task.WhenAll(running).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 同上
        }
    }
}
