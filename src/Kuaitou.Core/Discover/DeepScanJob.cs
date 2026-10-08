using Kuaitou.Core.Adb;
using Kuaitou.Core.Scrcpy;
using Kuaitou.Interop;

namespace Kuaitou.Core.Discover;

/// <summary>
/// 深度搜索：局域网 + mDNS 先出结果，再对存活主机扫 30000~50000 端口段。
/// 对应 legacy/kuaitou/discover.py 的 <c>_deep_job</c> / <c>deep_discover</c> / <c>deep_cancel</c> /
/// <c>deep_state</c> / <c>_merge_found</c> / <c>_alive_hosts</c> / <c>_try_connect_addr</c>。
///
/// 全程后台线程跑，界面靠 <c>onUpdate</c> 回调拿进度，不会卡住。参数都设了上限，防止在大网络里失控。
/// </summary>
public sealed class DeepScanJob
{
    /// <summary>单次最多扫多少台存活主机（12 × 9s ≈ 110s，留足预算余量）。</summary>
    private const int MaxHosts = 12;

    /// <summary>端口扫描总时间预算（秒），到点收工返回已找到的。</summary>
    private const double Budget = 150.0;

    /// <summary>mDNS 收集时间窗（秒）。</summary>
    private const double MdnsWindow = 6.0;

    /// <summary>
    /// 一段时间内开放端口超过这个数，就当普通电脑（Windows RPC、Steam 之类），不复核：
    /// 它们会一口气开十几个端口，逐个 adb connect 复核又慢又没意义。
    /// </summary>
    private const int PortNoiseMax = 4;

    /// <summary>每台主机最多复核几个候选端口。</summary>
    private const int VerifyMaxPerHost = 4;

    /// <summary>单个候选端口的 adb connect 超时（秒）。开放但不回话的端口要等约 10 秒，必须设上限。</summary>
    private const int VerifyTimeout = 3;

    /// <summary>整轮复核的总时间预算（秒）。</summary>
    private const double VerifyBudget = 25.0;

    private readonly AdbRunner _adb;
    private readonly DeviceInventory _inventory;
    private readonly ReconnectService _reconnect;
    private readonly LanScanner _lan;
    private readonly MdnsScanner _mdns;

    private readonly object _lock = new();
    private DeepState _state = new();
    private Action<DeepState>? _onUpdate;
    private Task? _job;
    private volatile bool _cancelFlag;

    public DeepScanJob(
        AdbRunner adb, DeviceInventory inventory, ReconnectService reconnect,
        LanScanner lan, MdnsScanner mdns)
    {
        _adb = adb;
        _inventory = inventory;
        _reconnect = reconnect;
        _lan = lan;
        _mdns = mdns;
    }

    /// <summary>进度快照（界面轮询 / 回调都用它）。</summary>
    public DeepState Snapshot()
    {
        lock (_lock)
        {
            return _state;
        }
    }

    /// <summary>启动一次深度搜索；已在跑就直接返回当前进度。</summary>
    public DeepState Start(Action<DeepState> onUpdate)
    {
        DeepState snapshot;
        bool launch = false;
        lock (_lock)
        {
            _onUpdate = onUpdate;
            if (!_state.Running)
            {
                _cancelFlag = false;              // 上一轮可能被用户停过，新的一轮从头算
                _state = new DeepState { Running = true, Phase = "lan", Progress = 0, Text = "开始深度搜索 …" };
                launch = true;
            }
            snapshot = _state;
        }

        if (launch)
        {
            _job = Task.Run(Run);
        }
        return snapshot;
    }

    /// <summary>
    /// 请求停止正在跑的深度搜索（没在跑就什么也不做）。
    /// 收尾仍走老办法：等 running 变 false，拿到已经找到的那批结果。
    /// </summary>
    public DeepState Cancel()
    {
        bool running;
        lock (_lock)
        {
            running = _state.Running;
        }
        if (running)
        {
            _cancelFlag = true;
            Publish(s => s with { Text = "正在停止深度搜索 …" });
        }
        return Snapshot();
    }

    public void Dispose()
    {
        _cancelFlag = true;
        try
        {
            _job?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // 退出时不等也无所谓
        }
    }

    private bool Stopped(double deadline) => _cancelFlag || PortProbe.Now() > deadline;

    /// <summary>
    /// 更新状态并回调。<b>状态没变不发</b>——旧版 SSE 就是这么做的（<c>if st != last</c>），
    /// 否则界面每 250ms 会被无意义地刷一次。这里用 record 值相等判断，Found 逐条比。
    /// </summary>
    private void Publish(Func<DeepState, DeepState> mutate)
    {
        DeepState next;
        Action<DeepState>? callback = null;
        lock (_lock)
        {
            next = mutate(_state);
            if (!SameState(_state, next))
            {
                _state = next;
                callback = _onUpdate;
            }
        }
        callback?.Invoke(next);
    }

    internal static bool SameState(DeepState a, DeepState b)
    {
        if (a.Running != b.Running || a.Phase != b.Phase || a.Text != b.Text
            || a.Progress != b.Progress || a.Elapsed != b.Elapsed || a.Error != b.Error
            || a.Hosts != b.Hosts || a.Scanned != b.Scanned || a.MdnsCount != b.MdnsCount
            || a.LanCount != b.LanCount || a.PortCount != b.PortCount || a.Canceled != b.Canceled)
        {
            return false;
        }
        if (a.Found.Count != b.Found.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Found.Count; i++)
        {
            if (a.Found[i] != b.Found[i])
            {
                return false;
            }
        }
        return true;
    }

    private void Run()
    {
        double t0 = PortProbe.Now();
        string mdnsError = "";
        try
        {
            // 1) 局域网 + mDNS 并行：先尽快把「看得见」的设备报出来
            Publish(s => s with { Phase = "lan", Text = "正在搜索局域网设备 + mDNS …", Progress = 0.05 });
            (bool mdnsOk, string mdnsMessage) = _mdns.Available();
            Task<DiscoverResult> lanTask = Task.Run(_lan.Scan);
            Task<List<MdnsService>>? mdnsTask = mdnsOk
                ? Task.Run(() => _mdns.Collect(MdnsWindow, 1.2, () => _cancelFlag))
                : null;

            DiscoverResult lan = new([], 0);
            try
            {
                lan = lanTask.Wait(TimeSpan.FromSeconds(60)) ? lanTask.Result : new DiscoverResult([], 0);
            }
            catch (Exception e)
            {
                mdnsError = $"局域网扫描失败：{e.Message}";
            }

            List<MdnsService> mdnsRows = [];
            if (mdnsTask is not null)
            {
                try
                {
                    mdnsRows = mdnsTask.Wait(TimeSpan.FromSeconds(MdnsWindow + 15)) ? mdnsTask.Result : [];
                }
                catch (Exception)
                {
                    mdnsRows = [];
                }
            }
            else if (mdnsError.Length == 0)
            {
                mdnsError = $"adb mDNS 不可用（{(mdnsMessage.Length > 0 ? mdnsMessage : "无输出")}）";
            }

            Dictionary<string, DiscoverEntry> box = MergeFound(lan, mdnsRows);
            int mdnsCount = box.Values.Count(e => e.Instance.Length > 0);
            Publish(s => s with
            {
                Found = [.. box.Values],
                MdnsCount = mdnsCount,
                LanCount = box.Count - mdnsCount,
                Progress = 0.15,
                Elapsed = Math.Round(PortProbe.Now() - t0, 1),
                Text = $"局域网找到 {box.Count} 个目标，开始端口扫描 …",
            });

            // 2) 端口扫描：对所有存活主机扫 30000~50000（已连接的设备跳过，它已经能用）
            var already = box.Values.Where(e => e.Connected).Select(e => e.Ip).ToHashSet(StringComparer.Ordinal);
            (List<string> alive, _) = AliveHosts(already);
            var prefer = box.Values
                .Where(e => AdbOutput.IsUsableIp(e.Ip) && !alive.Contains(e.Ip))
                .Select(e => e.Ip);
            List<string> hosts = [.. prefer.Concat(alive).Where(AdbOutput.IsUsableIp).Take(MaxHosts)];
            double deadline = t0 + Budget;
            int threads = PortScanner.Threads();
            double verifyLeft = VerifyBudget;
            Publish(s => s with { Phase = "ports", Hosts = hosts.Count, Scanned = 0 });

            for (int idx = 0; idx < hosts.Count; idx++)
            {
                if (Stopped(deadline))
                {
                    break;
                }

                string ip = hosts[idx];
                Publish(s => s with
                {
                    Progress = 0.15 + (0.85 * idx / Math.Max(hosts.Count, 1)),
                    Text = $"端口扫描 {idx + 1}/{hosts.Count}：{ip}（{threads} 线程）…",
                });

                List<int> hits;
                try
                {
                    hits = PortScanner.ScanHost(
                        ip, PortScanner.DeepPortLo, PortScanner.DeepPortHi, () => Stopped(deadline));
                }
                catch (Exception)
                {
                    hits = [];
                }

                if (hits.Count > 0 && hits.Count <= PortNoiseMax && verifyLeft > 0)
                {
                    Publish(s => s with { Text = $"复核候选 {ip}（{hits.Count} 个端口）…" });
                    foreach (int port in hits.Take(VerifyMaxPerHost))
                    {
                        if (verifyLeft <= 0 || Stopped(deadline))
                        {
                            break;
                        }
                        double verifyStart = PortProbe.Now();
                        string addr = $"{ip}:{port}";
                        string? state = TryConnectAddr(addr);
                        verifyLeft -= PortProbe.Now() - verifyStart;
                        if (state is not null)
                        {
                            // 复核通过才算真设备：device=可直接用；unauthorized=真端口，
                            // 但手机上还没点「允许调试」，标成待授权而不是已连接
                            box[addr] = new DiscoverEntry
                            {
                                Ip = ip,
                                Port = port.ToString(),
                                Addr = addr,
                                PortScan = true,
                                Pending = state == "unauthorized",
                                Connected = state == "device",
                                Source = "深度搜索（端口扫描）",
                            };
                        }
                    }
                }

                Publish(s => s with
                {
                    Found = [.. box.Values],
                    Scanned = idx + 1,
                    PortCount = box.Values.Count(x => x.PortScan),
                    Elapsed = Math.Round(PortProbe.Now() - t0, 1),
                });
            }

            if (box.Count == 0 && mdnsError.Length > 0
                && !mdnsError.Contains("List of discovered", StringComparison.Ordinal))
            {
                Publish(s => s with { Error = mdnsError });
            }
        }
        catch (Exception e)
        {
            Publish(s => s with { Error = $"深度搜索出错：{e.Message}" });
        }
        finally
        {
            bool canceled = _cancelFlag;
            double elapsed = Math.Round(PortProbe.Now() - t0, 1);
            Publish(s => s with
            {
                Running = false,
                Phase = "done",
                Canceled = canceled,
                Progress = 1.0,
                Elapsed = elapsed,
                Text = $"{(canceled ? "已停止深度搜索" : "深度搜索完成")}：共 {s.Found.Count} 个目标（耗时 {elapsed}s）",
            });
        }
    }

    /// <summary>把局域网结果和 mDNS 结果按地址合并去重（mDNS 补充实例名 / 待配对信息）。</summary>
    private Dictionary<string, DiscoverEntry> MergeFound(DiscoverResult lan, List<MdnsService> mdnsRows)
    {
        var box = new Dictionary<string, DiscoverEntry>(StringComparer.Ordinal);
        foreach (DiscoverEntry e in lan.Found)
        {
            box[e.Addr] = e with { Pairing = false, Instance = "", PortScan = false };
        }

        // 必须是「这个地址」真的在线才算已连接：同一 IP 的另一个随机端口并不代表它也连上了
        var online = new HashSet<string>(_inventory.GetDevices(), StringComparer.Ordinal);
        foreach (MdnsService svc in mdnsRows)
        {
            string addr = $"{svc.Ip}:{svc.Port}";
            bool pairing = svc.Service.Contains("pairing", StringComparison.Ordinal);
            var item = new DiscoverEntry
            {
                Ip = svc.Ip,
                Port = svc.Port,
                Addr = addr,
                Instance = svc.Instance,
                Pairing = pairing,
                Source = pairing ? "待配对（mDNS）" : "深度搜索（mDNS）",
                Connected = !pairing && online.Contains(addr),
            };

            if (!box.TryGetValue(addr, out DiscoverEntry? old))
            {
                box[addr] = item;
            }
            else
            {
                // 同一台设备：补上 mDNS 信息，保留原有的来源标记
                bool keepSource = old.Source.StartsWith("USB", StringComparison.Ordinal)
                    || old.Source.StartsWith("已知设备", StringComparison.Ordinal)
                    || old.Source.StartsWith("已连接", StringComparison.Ordinal);
                box[addr] = old with
                {
                    Pairing = pairing,
                    Instance = svc.Instance,
                    Connected = old.Connected || item.Connected,
                    Source = keepSource ? old.Source : item.Source,
                };
            }
        }
        return box;
    }

    /// <summary>
    /// 存活主机清单 = ARP 邻居表里的地址（本机各网卡地址除外）。
    ///
    /// 局域网扫描阶段已经对每个 /24 都发过 TCP SYN，凡是有回应的主机（哪怕端口全关）
    /// 都会进 ARP 表，所以扫完 5555 再读一次 ARP，就等于拿到了「活着的主机」清单。
    /// </summary>
    private static (List<string> Hosts, HashSet<string> Mine) AliveHosts(HashSet<string> exclude)
    {
        IReadOnlyList<string> localIps = LocalIps.Addresses();
        IReadOnlyList<string> neighbors = ArpTable.NeighborIps();
        var mine = new HashSet<string>(localIps, StringComparer.Ordinal);

        var hosts = new List<string>();
        foreach (string ip in neighbors.Concat(localIps))
        {
            if (mine.Contains(ip) || exclude.Contains(ip) || hosts.Contains(ip))
            {
                continue;
            }
            hosts.Add(ip);
        }
        return (hosts, mine);
    }

    /// <summary>
    /// 端口命中后用 adb connect 复核，并以 adb devices 的最终状态为准。
    ///
    /// 不能只看 adb connect 打印什么：只要 TCP 通它就回 "connected to …"，而且上一次失败的
    /// 尝试会在 adb 里留下一条 offline 记录 —— 下一次就直接变成 "already connected to …"，
    /// 于是普通电脑的随机开放端口也被当成「已连接」。所以只有 adb devices 里真是 device 才算连上；
    /// unauthorized 是「真设备但手机上还没点允许」；其余一律 disconnect 摘掉。
    /// </summary>
    private string? TryConnectAddr(string addr)
    {
        AdbResult r = _adb.Run(["connect", addr], timeoutSeconds: VerifyTimeout);
        string text = r.Combined.ToLowerInvariant();
        if (text.Contains("cannot", StringComparison.Ordinal)
            || text.Contains("failed", StringComparison.Ordinal)
            || text.Contains("refused", StringComparison.Ordinal)
            || text.Contains("timeout", StringComparison.Ordinal))
        {
            DropAddr(addr);
            return null;
        }

        DeviceStates states = _inventory.GetStates();
        if (states.Device.Contains(addr))
        {
            _reconnect.Unskip(addr);
            _inventory.RememberDevice(addr);
            return "device";
        }
        if (states.Unauthorized.Contains(addr))
        {
            _inventory.RememberDevice(addr);
            return "unauthorized";
        }

        DropAddr(addr);                 // offline / 不在列表：不是能用的 adb 端点
        return null;
    }

    /// <summary>摘掉一次失败的 connect 尝试，避免它在 adb 设备列表里留下 offline 条目。</summary>
    private void DropAddr(string addr)
    {
        try
        {
            _adb.Run(["disconnect", addr], timeoutSeconds: 5);
        }
        catch (Exception)
        {
            // 摘不掉也无所谓
        }
    }
}
