using Kuaitou.Core.Adb;
using Kuaitou.Core.Discover;
using Kuaitou.Core.Sys;

namespace Kuaitou.Core.Tests;

/// <summary>
/// 设备发现链路的纯逻辑测试：mDNS 输出解析、IP 数值排序、二维码载荷、深度搜索
/// 「状态没变不发」的比较、本机网卡地址过滤。全部不发真 adb 命令、不扫真网络。
/// 对应 legacy/kuaitou/discover.py 与 system.py 的纯函数部分。
/// </summary>
public sealed class DiscoverTests
{
    [Fact]
    public void ParseServices_ReadsTabSeparatedRows()
    {
        const string output = "adb-XXXX-HgzRvA\t_adb-tls-connect._tcp\t192.168.1.39:42865\n"
            + "adb-YYYY-AbCdEf\t_adb-tls-pairing._tcp\t192.168.1.39:37501\n";

        List<MdnsService> rows = MdnsScanner.ParseServices(output);

        Assert.Equal(2, rows.Count);
        Assert.Equal(
            new MdnsService("adb-XXXX-HgzRvA", "_adb-tls-connect._tcp", "192.168.1.39", "42865"),
            rows[0]);
        Assert.Equal(
            new MdnsService("adb-YYYY-AbCdEf", "_adb-tls-pairing._tcp", "192.168.1.39", "37501"),
            rows[1]);
    }

    [Fact]
    public void ParseServices_AcceptsSpaceSeparated_AndSkipsJunk()
    {
        const string output = "adb-A _adb-tls-connect._tcp 10.0.0.5:5555\n"
            + "some-http\t_http._tcp\t10.0.0.9:80\n"                 // 非 adb-tls：跳过
            + "adb-B\t_adb-tls-pairing._tcp\t10.0.0.6:abc\n"        // 端口非数字：跳过
            + "adb-C\t_adb-tls-connect._tcp\t10.0.0.7\n"            // 没有端口：跳过
            + "\n";

        List<MdnsService> rows = MdnsScanner.ParseServices(output);

        Assert.Equal([new MdnsService("adb-A", "_adb-tls-connect._tcp", "10.0.0.5", "5555")], rows);
    }

    [Fact]
    public void IpSortKey_SortsNumerically_AndPushesSerialLast()
    {
        var ips = new List<string> { "192.168.1.100", "V2324A", "10.0.0.2", "192.168.1.9" };

        ips.Sort((a, b) => AdbOutput.IpSortKey(a).CompareTo(AdbOutput.IpSortKey(b)));

        // 数值序而不是字典序：.9 在 .100 之前；USB 序列号不是 IP，排到最后
        Assert.Equal(["10.0.0.2", "192.168.1.9", "192.168.1.100", "V2324A"], ips);
    }

    [Fact]
    public void QrPayload_MatchesLegacyFormat()
    {
        Assert.Equal(
            "WIFI:T:ADB;S:kuaitou-1a2b3c4d;P:abcd1234XYZ;;",
            QrPairingService.Payload("kuaitou-1a2b3c4d", "abcd1234XYZ"));
    }

    [Fact]
    public void DiscoverEntry_IsValueEqual()
    {
        var one = new DiscoverEntry { Ip = "10.0.0.5", Port = "5555", Addr = "10.0.0.5:5555", Source = "同网段" };
        var two = new DiscoverEntry { Ip = "10.0.0.5", Port = "5555", Addr = "10.0.0.5:5555", Source = "同网段" };

        Assert.Equal(one, two);
        Assert.NotEqual(one, two with { Connected = true });
    }

    [Fact]
    public void SameState_IgnoresUnchangedProgress_ButDetectsNewFound()
    {
        var a = new DeepState
        {
            Running = true,
            Phase = "ports",
            Progress = 0.5,
            Found = [new DiscoverEntry { Addr = "10.0.0.5:5555" }],
        };
        var b = new DeepState
        {
            Running = true,
            Phase = "ports",
            Progress = 0.5,
            Found = [new DiscoverEntry { Addr = "10.0.0.5:5555" }],
        };

        Assert.True(DeepScanJob.SameState(a, b));                                  // 没变：不该重发
        Assert.False(DeepScanJob.SameState(a, b with { Scanned = 1 }));            // 计数变了
        Assert.False(DeepScanJob.SameState(                                         // 结果列表变了
            a, b with { Found = [new DiscoverEntry { Addr = "10.0.0.6:5555" }] }));
    }

    [Fact]
    public void LocalIps_ReturnsOnlyUsableIpv4()
    {
        foreach (string ip in LocalIps.Addresses())
        {
            Assert.True(AdbOutput.IsUsableIp(ip), $"不该把 {ip} 当成可扫网段");
        }
    }

    [Fact]
    public async Task ProbeAsync_ReturnsFalseForNonAddress()
    {
        Assert.False(await PortProbe.ProbeAsync("not-an-ip", 5555, 0.1));
        Assert.False(await PortProbe.ProbeAsync("255.255.255.255", 5555, 0.1));
    }
}
