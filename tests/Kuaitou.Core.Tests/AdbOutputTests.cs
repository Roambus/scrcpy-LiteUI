using Kuaitou.Core.Adb;
using Kuaitou.Core.Storage;

namespace Kuaitou.Core.Tests;

/// <summary>
/// adb 输出解析与设备选择的纯逻辑测试。对应 legacy/tests/test_device.py 里前一部分
/// （device_states / get_devices / device_info / _usable_ip / _is_disconnect / _serial_args）。
/// 全部不发真 adb 命令：只验证解析与选择规则。
/// </summary>
public sealed class AdbOutputTests
{
    private const string DevicesOut = "List of devices attached\n"
        + "emulator-5554\tdevice\n"
        + "192.168.1.20:5555\tdevice\n"
        + "V2324A\tunauthorized\n"
        + "172.19.163.3:5555\toffline\n\n";

    [Fact]
    public void ParseDeviceStates_ClassifiesAllThree()
    {
        DeviceStates states = AdbOutput.ParseDeviceStates(DevicesOut);

        Assert.Equal(["emulator-5554", "192.168.1.20:5555"], states.Device);
        Assert.Equal(["V2324A"], states.Unauthorized);
        Assert.Equal(["172.19.163.3:5555"], states.Offline);
        Assert.Empty(states.Other);
    }

    [Fact]
    public void ParseDeviceStates_IgnoresBlankAndGarbage()
    {
        DeviceStates states = AdbOutput.ParseDeviceStates("List of devices attached\n\nsome noise\n");

        Assert.Empty(states.Device);
        Assert.Empty(states.Unauthorized);
        Assert.Empty(states.Offline);
        Assert.Empty(states.Other);
    }

    [Fact]
    public void ParseDevices_OnlyOnline()
    {
        Assert.Equal(["emulator-5554", "192.168.1.20:5555"], AdbOutput.ParseDevices(DevicesOut));
    }

    [Fact]
    public void SplitAddress_SplitsIpPort()
    {
        DeviceAddress addr = AdbOutput.SplitAddress("192.168.1.20:5555");

        Assert.Equal("192.168.1.20", addr.Ip);
        Assert.Equal("5555", addr.Port);
    }

    [Fact]
    public void SplitAddress_UsbSerialIsNotSplit()
    {
        // USB 直连的序列号不是 IP（如 V2324A），不能当成 ip:port 拆
        DeviceAddress addr = AdbOutput.SplitAddress("V2324A");

        Assert.Equal("V2324A", addr.Serial);
        Assert.Equal("V2324A", addr.Ip);
        Assert.Equal("", addr.Port);
    }

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.10.1", false)]     // 网卡未连通时的自动地址
    [InlineData("224.0.0.1", false)]
    [InlineData("192.168.1.255", false)]
    [InlineData("192.168.0.0", false)]
    [InlineData("V2324A", false)]
    public void IsUsableIp_FiltersSpecialRanges(string ip, bool expected)
        => Assert.Equal(expected, AdbOutput.IsUsableIp(ip));

    [Theory]
    [InlineData("error: device offline", true)]
    [InlineData("no devices/emulators found", true)]
    [InlineData("Successfully connected", false)]
    public void IsDisconnect_DetectsCommonMessages(string message, bool expected)
        => Assert.Equal(expected, AdbOutput.IsDisconnect(message));

    [Fact]
    public void SelectSerialArgs_ExplicitSerialWins()
        => Assert.Equal(["-s", "V2324A"], AdbOutput.SelectSerialArgs("V2324A", [], AppConfig.CreateDefault()));

    [Fact]
    public void SelectSerialArgs_SingleDeviceNeedsNoFlag()
        => Assert.Empty(AdbOutput.SelectSerialArgs(null, ["192.168.1.20:5555"], AppConfig.CreateDefault()));

    [Fact]
    public void SelectSerialArgs_PrefersUsbWhenMultiple()
        => Assert.Equal(["-s", "V2324A"],
            AdbOutput.SelectSerialArgs(null, ["192.168.1.20:5555", "V2324A"], AppConfig.CreateDefault()));

    [Fact]
    public void SelectSerialArgs_NoUsbFallsBackToConfiguredAddress()
    {
        AppConfig cfg = AppConfig.CreateDefault();
        cfg.Ip = "10.0.0.9";
        cfg.Port = "5555";

        Assert.Equal(["-s", "10.0.0.9:5555"],
            AdbOutput.SelectSerialArgs(null, ["192.168.1.20:5555", "10.0.0.9:5555"], cfg));
    }

    [Fact]
    public void SelectSerialArgs_NoUsbNoConfiguredAddressUsesFirst()
    {
        AppConfig cfg = AppConfig.CreateDefault();
        cfg.Ip = "";
        cfg.Port = "5555";

        Assert.Equal(["-s", "192.168.1.20:5555"],
            AdbOutput.SelectSerialArgs(null, ["192.168.1.20:5555", "10.0.0.9:5555"], cfg));
    }
}
